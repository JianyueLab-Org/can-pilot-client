using System.Runtime.InteropServices;
using CanClient.Core.ModelMatching;
using CanClient.Core.Util;
using static CanClient.Core.Sim.SimConnectNative;

namespace CanClient.Core.Sim;

/// <summary>
/// 和 MSFS 的 SimConnect 连接。对应 Python 版 simlink.py，但换成了一条数据定义 + 每个模拟帧推送：
/// 原来那个包每个 SimVar 都是一次独立的请求再轮询结果，二十几个串着读一轮要半秒多。
///
/// 一条后台线程负责收消息（SimConnect_Open 时交给它一个事件句柄，有消息就被叫醒）。
/// 模拟器没开时 Open 直接失败，每 3 秒再试一次；收到 QUIT 或调用失败就关掉重来。
/// 注入他机（<see cref="TrafficInjector"/>）共用这个句柄，所有调用都过 <see cref="Call"/> 串行化。
/// </summary>
public sealed class SimConnection : IDisposable
{
    private static readonly Logger Log = Util.Log.For("sim");

    private const double RetryInterval = 3.0;
    private const double StaleAfter = 3.0;
    private const uint OwnDefinition = 1;
    private const uint OwnRequest = 1;
    private const uint TitleDefinition = 2;
    private const uint TitleRequest = 2;
    private const uint EventCom1SetHz = 100;
    private const uint EventCom2SetHz = 101;
    private const uint EventXpndrSet = 102;
    private const uint EventTransponderStateSet = 103;

    // 判"停着"的地速门槛（节）
    private const int ParkedSpeedKt = 1;

    /// <summary>本机数据定义里的字段，顺序就是结构体里的顺序（全是 FLOAT64）。</summary>
    private static readonly (string Key, string Name, string Unit)[] OwnFields =
    [
        ("lat", "PLANE LATITUDE", "degrees"),
        ("lon", "PLANE LONGITUDE", "degrees"),
        ("alt", "PLANE ALTITUDE", "feet"),
        ("agl", "PLANE ALT ABOVE GROUND", "feet"),
        ("palt", "PRESSURE ALTITUDE", "feet"),
        ("slp", "SEA LEVEL PRESSURE", "millibars"),
        ("gs", "GROUND VELOCITY", "knots"),
        ("pitch", "PLANE PITCH DEGREES", "degrees"),
        ("bank", "PLANE BANK DEGREES", "degrees"),
        ("hdg", "PLANE HEADING DEGREES TRUE", "degrees"),
        ("squawk", "TRANSPONDER CODE:1", "Bco16"),
        ("xpdr", "TRANSPONDER STATE:1", "enum"),
        ("ident", "TRANSPONDER IDENT:1", "bool"),
        ("com1", "COM ACTIVE FREQUENCY:1", "MHz"),
        ("com2", "COM ACTIVE FREQUENCY:2", "MHz"),
        ("com1power", "COM STATUS:1", "enum"),
        ("com1rx", "COM RECEIVE:1", "bool"),
        ("com1tx", "COM TRANSMIT:1", "bool"),
        ("com2tx", "COM TRANSMIT:2", "bool"),
        ("com2power", "COM STATUS:2", "enum"),
        ("com2rx", "COM RECEIVE:2", "bool"),
        ("ground", "SIM ON GROUND", "bool"),
        ("engine", "GENERAL ENG COMBUSTION:1", "bool"),
        ("gear", "GEAR HANDLE POSITION", "bool"),
        ("flaps", "TRAILING EDGE FLAPS LEFT PERCENT", "percent"),
        ("spoilers", "SPOILERS HANDLE POSITION", "percent"),
        ("beacon", "LIGHT BEACON", "bool"),
        ("landing", "LIGHT LANDING", "bool"),
        ("taxi", "LIGHT TAXI", "bool"),
        ("strobe", "LIGHT STROBE", "bool"),
        ("nav", "LIGHT NAV", "bool"),
        ("logo", "LIGHT LOGO", "bool"),
        ("vx", "VELOCITY WORLD X", "meters per second"),
        ("vy", "VELOCITY WORLD Y", "meters per second"),
        ("vz", "VELOCITY WORLD Z", "meters per second"),
        ("rx", "ROTATION VELOCITY BODY X", "radians per second"),
        ("ry", "ROTATION VELOCITY BODY Y", "radians per second"),
        ("rz", "ROTATION VELOCITY BODY Z", "radians per second"),
        ("steer", "GEAR CENTER STEER ANGLE", "degrees")
    ];

    private readonly object _call = new();
    private readonly object _valuesLock = new();
    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _thread;
    private IntPtr _handle;
    private AutoResetEvent? _signal;
    private bool _connected;
    private DateTime _lastUpdate = DateTime.MinValue;
    private Dictionary<string, double> _values = new();
    private string _title = "";

    // 被模拟器拒过的字段（老版本没有 TRANSPONDER STATE 之类），重建定义时跳过
    private readonly HashSet<string> _rejectedFields = new();
    private List<(string Key, string Name, string Unit)> _activeFields = new();
    private readonly Dictionary<uint, int> _definitionPackets = new();

    public SimulatorType Simulator { get; private set; } = SimulatorType.None;
    public string ApplicationName { get; private set; } = "";
    public Version? ApplicationVersion { get; private set; }

    public bool IsOpen => _handle != IntPtr.Zero;

    /// <summary>连着模拟器、而且最近 3 秒内收到过本机数据。</summary>
    public bool Connected => _connected && (DateTime.UtcNow - _lastUpdate).TotalSeconds < StaleAfter;

    /// <summary>每次重新连上都会换一个新号，注入器据此知道要重建。</summary>
    public int Generation { get; private set; }

    /// <summary>(连着没有, 说明)。在 SimConnect 线程上触发。</summary>
    public event Action<bool, string>? StateChanged;

    /// <summary>连上了模拟器（收到 OPEN），带模拟器类型。</summary>
    public event Action<SimulatorType, string>? Opened;

    /// <summary>连接关掉了（模拟器退出、出错、或者我们自己断开）。</summary>
    public event Action? Closed;

    internal event Action<RecvException>? ExceptionReceived;
    internal event Action<uint, uint>? ObjectIdAssigned;
    internal event Action<uint, uint, IntPtr>? SimObjectDataReceived;
    internal event Action<uint, float>? EventReceived;

    public void Start()
    {
        if (_thread is not null)
            return;
        _stop.Reset();
        _thread = new Thread(Run) { IsBackground = true, Name = "simconnect" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop.Set();
        _signal?.Set();
        var thread = _thread;
        if (thread is not null && thread != Thread.CurrentThread)
            thread.Join(TimeSpan.FromSeconds(3));
        _thread = null;
        CloseHandle();
    }

    public void Dispose() => Stop();

    /// <summary>调一个 SimConnect 函数，串行化。连接没开时返回 E_FAIL。</summary>
    internal int Call(Func<IntPtr, int> call)
    {
        lock (_call)
        {
            if (_handle == IntPtr.Zero)
                return E_FAIL;
            try
            {
                return call(_handle);
            }
            catch (Exception e) when (e is SEHException or AccessViolationException or DllNotFoundException or EntryPointNotFoundException)
            {
                Log.Warning($"a SimConnect call failed: {e.Message}");
                return E_FAIL;
            }
        }
    }

    /// <summary>调一个函数并拿到它发出去的包号（EXCEPTION 回来时靠它认领）。</summary>
    internal int Call(Func<IntPtr, int> call, out uint packetId)
    {
        lock (_call)
        {
            packetId = 0;
            var hr = Call(call);
            if (hr >= 0 && _handle != IntPtr.Zero)
                SimConnect_GetLastSentPacketID(_handle, out packetId);
            return hr;
        }
    }

    private void Run()
    {
        while (!_stop.IsSet)
        {
            if (_handle == IntPtr.Zero && !Open())
            {
                SetState(false, "MSFS 没有运行（等待模拟器启动……）");
                _stop.Wait(TimeSpan.FromSeconds(RetryInterval));
                continue;
            }

            _signal!.WaitOne(TimeSpan.FromMilliseconds(250));
            if (_stop.IsSet)
                break;
            if (!Pump())
            {
                SetState(false, "和 MSFS 的连接断了");
                CloseHandle();
                _stop.Wait(TimeSpan.FromSeconds(RetryInterval));
                continue;
            }

            if (_connected && (DateTime.UtcNow - _lastUpdate).TotalSeconds >= StaleAfter)
                SetState(false, "MSFS 没有数据（是否已进入飞行？）");
        }
        CloseHandle();
    }

    private bool Open()
    {
        _signal ??= new AutoResetEvent(false);
        int hr;
        IntPtr handle;
        try
        {
            hr = SimConnect_Open(out handle, AppVersion.ClientName, IntPtr.Zero, 0,
                _signal.SafeWaitHandle.DangerousGetHandle(), 0);
        }
        catch (DllNotFoundException e)
        {
            Log.Error($"SimConnect.dll is missing next to the program: {e.Message}");
            return false;
        }
        if (hr < 0 || handle == IntPtr.Zero)
        {
            Log.Debug($"SimConnect_Open failed: 0x{hr:X8}");
            return false;
        }
        lock (_call)
            _handle = handle;
        Generation++;
        Log.Info("SimConnect opened, waiting for the simulator to answer");
        Setup();
        return true;
    }

    private void Setup()
    {
        DefineOwnAircraft();
        Call(h => SimConnect_AddToDataDefinition(h, TitleDefinition, "TITLE", null, DataType.String256, 0, Unused));
        Call(h => SimConnect_RequestDataOnSimObject(h, TitleRequest, TitleDefinition, ObjectIdUser, Period.Second,
            DataRequestFlagChanged, 0, 0, 0));
        Call(h => SimConnect_MapClientEventToSimEvent(h, EventCom1SetHz, "COM_RADIO_SET_HZ"));
        Call(h => SimConnect_MapClientEventToSimEvent(h, EventCom2SetHz, "COM2_RADIO_SET_HZ"));
        Call(h => SimConnect_MapClientEventToSimEvent(h, EventXpndrSet, "XPNDR_SET"));
        Call(h => SimConnect_MapClientEventToSimEvent(h, EventTransponderStateSet, "XPNDR_STATE_SET"));
    }

    private void DefineOwnAircraft()
    {
        Call(h => SimConnect_RequestDataOnSimObject(h, OwnRequest, OwnDefinition, ObjectIdUser, Period.Never,
            0, 0, 0, 0));
        Call(h => SimConnect_ClearDataDefinition(h, OwnDefinition));
        _activeFields = OwnFields.Where(f => !_rejectedFields.Contains(f.Key)).ToList();
        _definitionPackets.Clear();
        for (var i = 0; i < _activeFields.Count; i++)
        {
            var field = _activeFields[i];
            var hr = Call(h => SimConnect_AddToDataDefinition(h, OwnDefinition, field.Name, field.Unit,
                DataType.Float64, 0, Unused), out var packet);
            if (hr >= 0)
                _definitionPackets[packet] = i;
        }
        Call(h => SimConnect_RequestDataOnSimObject(h, OwnRequest, OwnDefinition, ObjectIdUser, Period.SimFrame,
            0, 0, 0, 0));
    }

    private void CloseHandle()
    {
        var wasOpen = false;
        lock (_call)
        {
            if (_handle != IntPtr.Zero)
            {
                try
                {
                    SimConnect_Close(_handle);
                }
                catch
                {
                    // ignore
                }
                _handle = IntPtr.Zero;
                wasOpen = true;
            }
        }
        if (wasOpen)
        {
            Simulator = SimulatorType.None;
            lock (_valuesLock)
                _values = new Dictionary<string, double>();
            try
            {
                Closed?.Invoke();
            }
            catch (Exception e)
            {
                Log.Warning($"close callback raised: {e.Message}");
            }
        }
    }

    /// <summary>把排着的消息都处理掉。返回 false 表示连接坏了。</summary>
    private bool Pump()
    {
        for (var guard = 0; guard < 10000; guard++)
        {
            IntPtr data;
            uint size;
            int hr;
            lock (_call)
            {
                if (_handle == IntPtr.Zero)
                    return false;
                hr = SimConnect_GetNextDispatch(_handle, out data, out size);
            }
            if (hr == E_FAIL)
                return true;            // 没有更多消息
            if (hr < 0)
            {
                Log.Warning($"SimConnect_GetNextDispatch failed: 0x{hr:X8}");
                return false;
            }
            try
            {
                if (!Handle(data))
                    return false;
            }
            catch (Exception e)
            {
                Log.Warning($"handling a SimConnect message raised: {e}");
            }
        }
        return true;
    }

    private bool Handle(IntPtr data)
    {
        var header = Marshal.PtrToStructure<Recv>(data);
        switch ((RecvId)header.Id)
        {
            case RecvId.Open:
                OnOpen(Marshal.PtrToStructure<RecvOpen>(data));
                return true;
            case RecvId.Quit:
                Log.Info("the simulator is shutting down (QUIT)");
                return false;
            case RecvId.Exception:
                OnException(Marshal.PtrToStructure<RecvException>(data));
                return true;
            case RecvId.SimObjectData:
            {
                var body = Marshal.PtrToStructure<RecvSimObjectData>(data);
                var payload = data + SimObjectDataOffset;
                if (body.RequestId == OwnRequest)
                    OnOwnData(payload, body.DefineCount);
                else if (body.RequestId == TitleRequest)
                    _title = Marshal.PtrToStringAnsi(payload) ?? "";
                else
                    SimObjectDataReceived?.Invoke(body.RequestId, body.ObjectId, payload);
                return true;
            }
            case RecvId.AssignedObjectId:
            {
                var body = Marshal.PtrToStructure<RecvAssignedObjectId>(data);
                ObjectIdAssigned?.Invoke(body.RequestId, body.ObjectId);
                return true;
            }
            case RecvId.EventFrame:
            {
                var frame = Marshal.PtrToStructure<RecvEventFrame>(data);
                EventReceived?.Invoke(frame.Event.EventId, frame.FrameRate);
                return true;
            }
            case RecvId.Event:
            case RecvId.EventObjectAddRemove:
            {
                var ev = Marshal.PtrToStructure<RecvEvent>(data);
                EventReceived?.Invoke(ev.EventId, 0);
                return true;
            }
            default:
                return true;
        }
    }

    private void OnOpen(RecvOpen open)
    {
        ApplicationName = open.ApplicationName
            .Replace("KittyHawk", "Microsoft Flight Simulator")
            .Replace("SunRise", "Microsoft Flight Simulator 2024");
        ApplicationVersion = new Version((int)open.ApplicationVersionMajor, (int)open.ApplicationVersionMinor,
            (int)open.ApplicationBuildMajor, (int)open.ApplicationBuildMinor);
        Simulator = SimulatorTypeExtensions.FromApplicationVersion((int)open.ApplicationVersionMajor);
        if (Simulator == SimulatorType.None)
        {
            // 认不出来的版本按名字猜，猜不出就当 2020
            Simulator = open.ApplicationName.Contains("SunRise", StringComparison.OrdinalIgnoreCase)
                ? SimulatorType.Msfs2024
                : SimulatorType.Msfs;
        }
        Log.Info($"connected to {ApplicationName} {ApplicationVersion} ({Simulator}), SimConnect {open.SimConnectVersionMajor}.{open.SimConnectVersionMinor}");
        try
        {
            Opened?.Invoke(Simulator, $"{ApplicationName} ({ApplicationVersion})");
        }
        catch (Exception e)
        {
            Log.Warning($"open callback raised: {e.Message}");
        }
    }

    private void OnException(RecvException body)
    {
        // 本机数据定义里有模拟器不认识的字段：去掉它重建，别让一个字段拖垮整条请求
        if (body.Exception == Exceptions.NameUnrecognized && _definitionPackets.TryGetValue(body.SendId, out var index)
                                                          && index < _activeFields.Count)
        {
            var field = _activeFields[index];
            Log.Warning($"the simulator does not know {field.Name}, reading the aircraft without it");
            _rejectedFields.Add(field.Key);
            DefineOwnAircraft();
            return;
        }
        try
        {
            ExceptionReceived?.Invoke(body);
        }
        catch (Exception e)
        {
            Log.Warning($"exception callback raised: {e.Message}");
        }
    }

    private void OnOwnData(IntPtr payload, uint count)
    {
        var fields = _activeFields;
        var values = new Dictionary<string, double>(fields.Count);
        // 定义刚重建过的话，这一包可能还是按旧定义来的，按少的那边读
        var n = count > 0 ? Math.Min(fields.Count, (int)count) : fields.Count;
        for (var i = 0; i < n; i++)
        {
            var value = Marshal.PtrToStructure<double>(payload + i * sizeof(double));
            if (!double.IsNaN(value))
                values[fields[i].Key] = value;
        }
        if (!values.ContainsKey("lat") || !values.ContainsKey("lon"))
            return;
        lock (_valuesLock)
        {
            _values = values;
            _lastUpdate = DateTime.UtcNow;
        }
        if (!_connected)
            SetState(true, $"已连接 {ApplicationName}");
    }

    private void SetState(bool connected, string message)
    {
        if (connected == _connected)
            return;
        _connected = connected;
        Log.Info($"{(connected ? "connected" : "disconnected")}: {message}");
        try
        {
            StateChanged?.Invoke(connected, message);
        }
        catch (Exception e)
        {
            Log.Warning($"status callback raised: {e.Message}");
        }
    }

    /// <summary>当前这一份本机状态。超过 3 秒没有新数据返回 null，免得 FSD 一直广播最后一帧。</summary>
    public OwnAircraftState? Snapshot()
    {
        Dictionary<string, double> raw;
        DateTime last;
        lock (_valuesLock)
        {
            raw = _values;
            last = _lastUpdate;
        }
        if (raw.Count == 0 || (DateTime.UtcNow - last).TotalSeconds >= StaleAfter)
            return null;
        return BuildState(raw, _title);
    }

    internal static OwnAircraftState BuildState(IReadOnlyDictionary<string, double> raw, string title)
    {
        double Get(string key, double fallback = 0) => raw.TryGetValue(key, out var v) ? v : fallback;
        double? Maybe(string key) => raw.TryGetValue(key, out var v) ? v : null;

        var trueAltitude = Get("alt");
        var (network, pressure, error) = Core.Altitude.Msfs(trueAltitude, Maybe("slp"), Maybe("palt"));
        var networkInt = RoundInt(network);
        var pressureInt = RoundInt(pressure);
        var groundspeed = RoundInt(Get("gs"));
        var onGround = Get("ground") != 0;
        var heading = Get("hdg") % 360.0;
        if (heading < 0)
            heading += 360.0;
        const double toDeg = 180.0 / Math.PI;

        return new OwnAircraftState
        {
            Latitude = Get("lat"),
            Longitude = Get("lon"),
            Altitude = RoundInt(trueAltitude),
            NetworkAltitude = networkInt,
            PressureAltitude = pressureInt,
            TemperatureError = RoundInt(error),
            PressureDelta = pressureInt - networkInt,
            Agl = RoundInt(Get("agl")),
            Groundspeed = groundspeed,
            // SimVar 是低头、左坡为正，翻过来
            Pitch = -Get("pitch"),
            Bank = -Get("bank"),
            Heading = heading,
            Squawk = BcdToSquawk(Maybe("squawk")),
            TransponderMode = TransponderModeFor(Maybe("xpdr"), onGround, groundspeed),
            TransponderStateKnown = raw.ContainsKey("xpdr"),
            TransponderIdent = Get("ident") != 0,
            Com1 = Frequency(Maybe("com1")),
            Com2 = Frequency(Maybe("com2")),
            Com1Power = Get("com1power") == 0,
            Com1Receiving = Get("com1rx", 1) != 0,
            Com2Power = Get("com2power") == 0,
            Com2Receiving = Get("com2rx", 1) != 0,
            TransmitRadio = SelectTransmitRadio(Get("com1tx", 1) != 0, Get("com2tx") != 0),
            OnGround = onGround,
            VelocityEast = Get("vx"),
            VelocityUp = Get("vy"),
            VelocityNorth = Get("vz"),
            PitchRate = -Get("rx") * toDeg,
            HeadingRate = Get("ry") * toDeg,
            BankRate = -Get("rz") * toDeg,
            NoseWheel = Get("steer"),
            GearDown = Get("gear", 1) != 0,
            Flaps = Math.Clamp(Get("flaps") / 100.0, 0.0, 1.0),
            Spoilers = Get("spoilers") > 0.5,
            EnginesOn = Get("engine", 1) != 0,
            Lights = new LightsState(Get("beacon") != 0, Get("landing") != 0, Get("taxi") != 0, Get("strobe") != 0,
                Get("nav") != 0, Get("logo") != 0),
            Title = title
        };
    }

    private static int RoundInt(double value) => (int)Math.Round(value, MidpointRounding.ToEven);

    /// <summary>TX 始终互斥。机模短暂同时回报两个 TX 时优先 COM1；都关闭就禁止发送。</summary>
    public static int SelectTransmitRadio(bool com1, bool com2) => com1 ? 1 : com2 ? 2 : 0;

    /// <summary>COM ACTIVE FREQUENCY 已经是 MHz，只要挡掉 0 和异常值。</summary>
    public static double? Frequency(double? raw)
    {
        if (raw is not { } value || value == 0)
            return null;
        value = Math.Round(value, 3);
        return value is >= 118.0 and <= 137.0 ? value : null;
    }

    /// <summary>
    /// 应答机码在 SimVar 里是 BCD 编码的：0x1200 表示 1200。出现 8/9 说明不是 BCD，
    /// 当十进制收，但每一位也得是八进制的。
    /// </summary>
    public static int BcdToSquawk(double? raw)
    {
        if (raw is not { } number || double.IsNaN(number))
            return 2000;
        var value = (int)number;
        var digits = 0;
        foreach (var shift in new[] { 12, 8, 4, 0 })
        {
            var digit = (value >> shift) & 0xF;
            if (digit > 7)
            {
                if (value is >= 0 and <= 7777 && value.ToString("D4").All(c => c is >= '0' and <= '7'))
                    return value;
                return 2000;
            }
            digits = digits * 10 + digit;
        }
        return digits;
    }

    /// <summary>
    /// TRANSPONDER STATE → 位置包要的应答机模式。0 关 1 待机 2 测试 3 开 4 高度(C) 5 地面，
    /// &gt;= 3 才是真的在线。待机和关只在飞机确实停着的时候才当真：很多机模压根没把旋钮接到这个
    /// SimVar 上，它就一直停在 1——让一架在飞的飞机报待机，管制标牌上的高度和地速会一起空掉。
    /// </summary>
    public static TransponderMode TransponderModeFor(double? state, bool onGround, int groundspeed)
    {
        if (state is not { } value)
            return TransponderMode.ModeC;
        if ((int)value >= 3)
            return TransponderMode.ModeC;
        if (onGround && groundspeed < ParkedSpeedKt)
            return TransponderMode.Standby;
        return TransponderMode.ModeC;
    }

    // ---------- 对模拟器的操作 ----------

    /// <summary>调 COM1 / COM2 的有效频率。</summary>
    public bool TuneCom(int radio, double frequencyMhz)
    {
        var hz = (uint)Math.Round(frequencyMhz * 1_000_000);
        var eventId = radio == 2 ? EventCom2SetHz : EventCom1SetHz;
        return Call(h => SimConnect_TransmitClientEvent(h, ObjectIdUser, eventId, hz, GroupPriorityHighest,
            EventFlagGroupIdIsPriority)) >= 0;
    }

    /// <summary>设应答机编码（BCD16）。</summary>
    public bool SetSquawk(int code)
    {
        var text = code.ToString("D4");
        var bcd = 0u;
        foreach (var c in text)
            bcd = (bcd << 4) | (uint)(c - '0');
        return Call(h => SimConnect_TransmitClientEvent(h, ObjectIdUser, EventXpndrSet, bcd, GroupPriorityHighest,
            EventFlagGroupIdIsPriority)) >= 0;
    }

    /// <summary>设应答机状态：1 待机、4 高度。老版本模拟器没有这个事件就算了。</summary>
    public bool SetTransponderState(TransponderMode mode)
    {
        var state = mode == TransponderMode.ModeC ? 4u : 1u;
        return Call(h => SimConnect_TransmitClientEvent(h, ObjectIdUser, EventTransponderStateSet, state,
            GroupPriorityHighest, EventFlagGroupIdIsPriority)) >= 0;
    }
}
