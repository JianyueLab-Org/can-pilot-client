using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CanClient.Core.Traffic;
using CanClient.Core.Util;

namespace CanClient.Core.Sim;

/// <summary>复用 XPC_for_CAN 原生插件 v3 协议，传入客户端已经积分好的 pose，由插件匹配 CSL、贴地和填充 TCAS。</summary>
public sealed class XPlaneTrafficBridge : IDisposable
{
    private readonly UdpClient _udp = new(AddressFamily.InterNetwork);
    private readonly object _gate = new();
    private int _sequence;
    private bool _disposed;
    public static byte[][] Encode(object message, int sequence)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(message);
        var total = Math.Max(1, (raw.Length + 5999) / 6000);
        return Enumerable.Range(0, total).Select(i => JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 3, seq = sequence & 65535, part = i, total,
            data = Convert.ToBase64String(raw, i * 6000, Math.Min(6000, raw.Length - i * 6000))
        })).ToArray();
    }

    internal static object Frame(IEnumerable<TrafficEntry> entries, OwnAircraftState? own, string csl) => new
    {
        type = "traffic", csl,
        own = own is null ? null : new { latitude = own.Latitude, longitude = own.Longitude, altitude = own.Altitude },
        aircraft = entries.Take(63).Select(e => new
        {
            callsign = e.Callsign, equipment = e.Equipment, airline = e.Airline, livery = e.Livery,
            // 留空交给原生插件按机型/航司/涂装完整匹配，不能传 MSFS 模型名。
            model = "", pose = new[] { e.Latitude, e.Longitude, e.Altitude, e.Pitch, e.Bank, e.Heading },
            on_ground = e.OnGround, squawk = e.Squawk, mode = e.Mode, agl = e.Agl, nose_wheel = e.NoseWheel,
            gear_down = e.GearDown, flaps = e.Flaps, spoilers = e.Spoilers, engines_on = e.EnginesOn,
            lights = e.Lights.ToDictionary(p => p.Key.EndsWith("_on") ? p.Key : p.Key + "_on", p => p.Value)
        }).ToArray()
    };

    public void Send(IEnumerable<TrafficEntry> entries, OwnAircraftState? own, string csl, string host = "127.0.0.1")
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                foreach (var packet in Encode(Frame(entries, own, csl), ++_sequence))
                    _udp.Send(packet, host, 49900);
            }
            catch (SocketException e) { Log.For("xp-traffic").Debug($"plugin send failed: {e.Message}"); }
        }
    }
    public void Dispose() { lock (_gate) { _disposed = true; _udp.Dispose(); } }
}
