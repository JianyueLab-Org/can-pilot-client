using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CanClient.Core.Settings;
using CanClient.Core.Util;

namespace CanClient.Core.Sim;

/// <summary>移植 can-xplane-client 的 C# RREF/DREF 接入，复用统一的本机状态和语音引擎。</summary>
public sealed class XPlaneConnection(AppSettings settings) : IDisposable
{
    private static readonly Logger Log = Util.Log.For("xplane");
    internal static readonly (string Key, string Ref)[] Datarefs =
    [
        ("lat", "sim/flightmodel/position/latitude"), ("lon", "sim/flightmodel/position/longitude"),
        ("elevation", "sim/flightmodel/position/elevation"), ("agl", "sim/flightmodel/position/y_agl"),
        ("baro", "sim/weather/barometer_sealevel_inhg"), ("pressure", "sim/flightmodel2/position/pressure_altitude"),
        ("temperature", "sim/weather/aircraft/altimeter_temperature_error"),
        ("gs", "sim/flightmodel/position/groundspeed"), ("pitch", "sim/flightmodel/position/theta"),
        ("bank", "sim/flightmodel/position/phi"), ("heading", "sim/flightmodel/position/true_psi"),
        ("squawk", "sim/cockpit/radios/transponder_code"), ("xpdr", "sim/cockpit/radios/transponder_mode"),
        ("com1", "sim/cockpit2/radios/actuators/com1_frequency_hz_833"),
        ("com2", "sim/cockpit2/radios/actuators/com2_frequency_hz_833"),
        ("com1legacy", "sim/cockpit/radios/com1_freq_hz"), ("com2legacy", "sim/cockpit/radios/com2_freq_hz"),
        ("com1power", "sim/cockpit2/radios/actuators/com1_power"),
        ("com2power", "sim/cockpit2/radios/actuators/com2_power"),
        ("com1rx", "sim/cockpit2/radios/actuators/audio_selection_com1"),
        ("com2rx", "sim/cockpit2/radios/actuators/audio_selection_com2"),
        ("tx", "sim/cockpit2/radios/actuators/audio_com_selection"),
        ("ident", "sim/cockpit2/radios/indicators/transponder_id"),
        ("identlegacy", "sim/cockpit/radios/transponder_id"),
        ("ground", "sim/flightmodel/failures/onground_any"),
        ("east", "sim/flightmodel/position/local_vx"), ("up", "sim/flightmodel/position/local_vy"),
        ("south", "sim/flightmodel/position/local_vz"),
        ("pitchrate", "sim/flightmodel/position/Q"), ("headingrate", "sim/flightmodel/position/R"),
        ("bankrate", "sim/flightmodel/position/P"), ("steer", "sim/flightmodel2/gear/tire_steer_actual_deg[0]"),
        ("beacon", "sim/cockpit/electrical/beacon_lights_on"), ("landing", "sim/cockpit/electrical/landing_lights_on"),
        ("taxi", "sim/cockpit/electrical/taxi_light_on"), ("strobe", "sim/cockpit/electrical/strobe_lights_on"),
        ("nav", "sim/cockpit/electrical/nav_lights_on"), ("gear", "sim/cockpit/switches/gear_handle_status"),
        ("flaps", "sim/flightmodel/controls/flaprat"), ("spoilers", "sim/flightmodel2/controls/speedbrake_ratio"),
        ("engine", "sim/flightmodel/engine/ENGN_running[0]")
    ];

    private readonly object _gate = new();
    private readonly Dictionary<string, double> _values = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private DateTime _last;
    private UdpClient? _udp;
    private IPEndPoint? _address;
    public int Generation { get; private set; }
    public string Address => _address?.ToString() ?? "等待发现 X-Plane";
    public bool Connected => Snapshot() is not null;

    private static bool PositionFresh(IReadOnlyDictionary<string, DateTime> updated, DateTime now) =>
        new[] { "lat", "lon", "elevation" }.All(key => updated.TryGetValue(key, out var time) && now - time < TimeSpan.FromSeconds(3));
    private readonly Dictionary<string, DateTime> _updated = new();

    public void Start() => _worker ??= Task.Run(() => RunAsync(_stop.Token));

    internal static byte[] Subscription(int index, string dataref, int rate = 5)
    {
        var bytes = new byte[413];
        "RREF\0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5), rate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(9), index);
        Encoding.ASCII.GetBytes(dataref).CopyTo(bytes, 13);
        return bytes;
    }

    internal bool Handle(byte[] bytes)
    {
        if (bytes.Length < 13 || !bytes.AsSpan(0, 5).SequenceEqual("RREF\0"u8) || (bytes.Length - 5) % 8 != 0)
            return false;
        var valid = false;
        lock (_gate)
        {
            for (var i = 5; i < bytes.Length; i += 8)
            {
                var index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i));
                var value = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i + 4));
                if (index < 0 || index >= Datarefs.Length || !float.IsFinite(value))
                    continue;
                _values[Datarefs[index].Key] = value;
                _updated[Datarefs[index].Key] = DateTime.UtcNow;
                valid = true;
            }
            if (valid) _last = DateTime.UtcNow;
        }
        return valid;
    }

    internal static IPEndPoint? Beacon(byte[] bytes, IPAddress address)
    {
        if (bytes.Length < 21 || !bytes.AsSpan(0, 5).SequenceEqual("BECN\0"u8)) return null;
        // 主机 ID 1 是 X-Plane；排除 Plane Maker 等程序的信标。
        if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(7)) != 1) return null;
        var port = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(19));
        return port > 0 ? new IPEndPoint(address, port) : null;
    }

    private async Task<IPEndPoint?> DiscoverAsync(CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(settings.XPlaneHost))
        {
            var addresses = await Dns.GetHostAddressesAsync(settings.XPlaneHost.Trim(), token);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return address is null ? null : new IPEndPoint(address, Math.Clamp(settings.XPlanePort, 1, 65535));
        }
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 49707));
        udp.JoinMulticastGroup(IPAddress.Parse("239.255.1.1"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            while (true)
            {
                var packet = await udp.ReceiveAsync(timeout.Token);
                if (Beacon(packet.Buffer, packet.RemoteEndPoint.Address) is { } found) return found;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    private async Task RunAsync(CancellationToken token)
    {
        string? reported = null;
        IPEndPoint? silentBeacon = null;
        while (!token.IsCancellationRequested)
        {
            UdpClient? udp = null;
            IPEndPoint? address = null;
            var source = "fallback";
            try
            {
                try { address = await DiscoverAsync(token); }
                catch (SocketException e) { Log.Info($"beacon discovery failed: {e.Message}"); }
                if (address is not null) source = string.IsNullOrWhiteSpace(settings.XPlaneHost) ? "beacon" : "settings";
                // 多网卡时信标可能来自一个收不到回包的虚拟网卡地址；上次它没回数据，这次改走本机回环。
                if (source == "beacon" && address!.Equals(silentBeacon))
                {
                    address = new IPEndPoint(IPAddress.Loopback, address.Port);
                    source = "loopback retry";
                    silentBeacon = null;
                }
                address ??= _address ?? new IPEndPoint(IPAddress.Loopback, 49000);
                udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                lock (_gate)
                {
                    _values.Clear();
                    _updated.Clear();
                    _last = default;
                    _udp = udp;
                    _address = address;
                    Generation++;
                }
                for (var i = 0; i < Datarefs.Length; i++)
                    await udp.SendAsync(Subscription(i, Datarefs[i].Ref), address, token);
                Log.Debug($"subscribed to {Datarefs.Length} datarefs at {address}");
                var silent = DateTime.UtcNow;
                var receiving = false;
                while (DateTime.UtcNow - silent < TimeSpan.FromSeconds(10))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(500);
                    try
                    {
                        // 套接字是本次订阅专用的临时端口，只认 RREF 格式即可；X-Plane 的回包源地址/端口
                        // 不一定等于信标里的地址（多网卡、回包端口不同），严格比对会把有效数据全部丢掉。
                        var packet = await udp.ReceiveAsync(timeout.Token);
                        if (!Handle(packet.Buffer)) continue;
                        silent = DateTime.UtcNow;
                        if (receiving) continue;
                        receiving = true;
                        var state = $"ok {packet.RemoteEndPoint}";
                        if (reported != state)
                            Log.Info($"receiving datarefs from {packet.RemoteEndPoint} (subscribed at {address} via {source})");
                        reported = state;
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                    catch (SocketException) { await Task.Delay(200, token); }
                    token.ThrowIfCancellationRequested();
                }
                // 同一个地址只记一次，模拟器没开时不会每 10 秒刷屏。
                var silence = $"silent {address}";
                if (reported != silence)
                    Log.Info($"no RREF reply from {address} (via {source}) for 10 s; X-Plane not running, or its network/firewall is blocking UDP");
                reported = silence;
                if (source == "beacon") silentBeacon = address;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception e) { Log.Warning($"X-Plane connection: {e.Message}"); }
            finally
            {
                lock (_gate) { _udp = null; _values.Clear(); _updated.Clear(); _last = default; }
                if (udp is not null && address is not null)
                {
                    // 明确取消订阅，避免 X-Plane 继续向已关闭的端口发送数据。
                    try
                    {
                        for (var i = 0; i < Datarefs.Length; i++) udp.Send(Subscription(i, Datarefs[i].Ref, 0), address);
                    }
                    catch (SocketException) { }
                }
                udp?.Dispose();
            }
            try { await Task.Delay(1000, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public OwnAircraftState? Snapshot()
    {
        lock (_gate)
        {
            if (!PositionFresh(_updated, DateTime.UtcNow)) return null;
            return BuildState(_values);
        }
    }

    internal static OwnAircraftState BuildState(IReadOnlyDictionary<string, double> raw)
    {
        double V(string key, double fallback = 0) => raw.TryGetValue(key, out var v) ? v : fallback;
        double? N(string key) => raw.TryGetValue(key, out var v) ? v : null;
        double? Frequency(string key) => SimConnection.Frequency(V(key) > 0 ? V(key) / 1000 : V(key + "legacy") / 100);
        int Round(double value) => (int)Math.Round(value);
        var altitude = V("elevation") / .3048;
        var (network, pressure, error) = Altitude.XPlane(altitude, N("baro") * 33.8639, N("pressure"), N("temperature"));
        var speed = Round(V("gs") * Fsd.FsdProtocol.KnotsPerMps);
        var ground = V("ground") != 0;
        return new OwnAircraftState
        {
            Latitude = V("lat"), Longitude = V("lon"), Altitude = Round(altitude), NetworkAltitude = Round(network),
            PressureAltitude = Round(pressure), TemperatureError = Round(error), PressureDelta = Round(pressure) - Round(network),
            Agl = Round(V("agl") / .3048), Groundspeed = speed, Pitch = V("pitch"), Bank = V("bank"),
            Heading = (V("heading") % 360 + 360) % 360, Squawk = SimConnection.BcdToSquawk(N("squawk")),
            TransponderMode = N("xpdr") is null || V("xpdr") >= 2 || !ground || speed >= 1 ? TransponderMode.ModeC : TransponderMode.Standby,
            TransponderStateKnown = raw.ContainsKey("xpdr"), TransponderIdent = V("ident") != 0 || V("identlegacy") != 0,
            Com1 = Frequency("com1"), Com2 = Frequency("com2"), Com1Power = V("com1power", 1) != 0,
            Com2Power = V("com2power", 1) != 0, Com1Receiving = V("com1rx", 1) != 0, Com2Receiving = V("com2rx", 1) != 0,
            TransmitRadio = (int)V("tx", 6) switch { 6 => 1, 7 => 2, _ => 0 }, OnGround = ground,
            VelocityEast = V("east"), VelocityUp = V("up"), VelocityNorth = -V("south"),
            PitchRate = V("pitchrate"), HeadingRate = V("headingrate"), BankRate = V("bankrate"), NoseWheel = V("steer"),
            GearDown = V("gear", 1) != 0, Flaps = Math.Clamp(V("flaps"), 0, 1), Spoilers = V("spoilers") > .1,
            EnginesOn = V("engine", 1) != 0,
            Lights = new LightsState(V("beacon") != 0, V("landing") != 0, V("taxi") != 0, V("strobe") != 0, V("nav") != 0),
            Title = "X-Plane"
        };
    }

    internal static byte[] WritePacket(string dataref, float value)
    {
        var packet = new byte[509];
        "DREF\0"u8.CopyTo(packet);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(5), value);
        Encoding.ASCII.GetBytes(dataref).CopyTo(packet, 9);
        return packet;
    }

    public bool Write(string dataref, float value)
    {
        lock (_gate)
        {
            if (_udp is null || _address is null || DateTime.UtcNow - _last >= TimeSpan.FromSeconds(3)) return false;
            try { _udp.Send(WritePacket(dataref, value), _address); return true; }
            catch (SocketException e) { Log.Warning($"DREF failed: {e.Message}"); return false; }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _worker?.GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
