using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CanClient.Core.Sim;
using CanClient.Core.Traffic;
using CanClient.Core.Util;

namespace CanClient.Core.Fsd;

public enum LinkState
{
    Connecting,
    Online,
    /// <summary>暂时的，还在试。</summary>
    Reconnecting,
    /// <summary>不重试的失败（首连连不上、密码错）。</summary>
    Error,
    /// <summary>重连窗口内都没连回来，这条链路彻底完了。</summary>
    Offline,
    Stopped,
    /// <summary>语音服务器拒绝了某个动作（建频道、进频道）。</summary>
    Denied
}

public sealed record ControllerInfo(string Callsign, string Frequency, int Facility, DateTime Seen)
{
    public string RealName { get; init; } = "";
}

public sealed record FlightPlan
{
    public string Rules { get; init; } = "I";
    public string Aircraft { get; init; } = "";
    public string CruiseSpeed { get; init; } = "";
    public string Departure { get; init; } = "";
    public string DepartureTime { get; init; } = "";
    public string ActualTime { get; init; } = "";
    public string CruiseAltitude { get; init; } = "";
    public string Arrival { get; init; } = "";
    public string EnrouteHours { get; init; } = "0";
    public string EnrouteMinutes { get; init; } = "0";
    public string FuelHours { get; init; } = "0";
    public string FuelMinutes { get; init; } = "0";
    public string Alternate { get; init; } = "";
    public string Remarks { get; init; } = "";
    public string Route { get; init; } = "";
}

/// <summary>
/// 以飞行员身份连接 FSD 服务端（can-fsd）。对应 xPilot 的 fsd 层。
///
/// $ID 的第 9 个字段（challenge）留空，服务端就不会发起 VATSIM 客户端质询。
/// 事件都在后台线程触发，界面要自己切回 UI 线程。
/// </summary>
public sealed class FsdClient : IDisposable
{
    public const int DefaultPort = 6809;

    private const double PositionInterval = 0.2;
    private const double SlowPositionInterval = 5.0;
    private const double FastPositionInterval = 0.2;
    private const double SlowFastInterval = 5.0;
    private const double ConfigBroadcastInterval = 1.0;
    private const double LoginTimeout = 10.0;
    public const double DefaultReconnectWindow = 150.0;
    private static readonly double[] ReconnectDelays = [3.0, 5.0, 10.0, 15.0, 20.0, 30.0];

    // 服务端拒绝登录的错误码。只有这两个值得再试：呼号被旧连接占着、服务器满了。
    private const string ErrCallsignInUse = "1";
    private const string ErrServerFull = "12";

    private enum Failure
    {
        Fatal,
        InUse,
        Transient
    }

    private static readonly Logger Log = Util.Log.For("fsd");

    private readonly string _host;
    private readonly int _port;
    private readonly string _cid;
    private readonly string _password;
    private readonly string _realName;
    private readonly int _rating;
    private readonly int _simulator;
    private readonly TrafficTable? _traffic;
    private readonly double _reconnectWindow;

    private readonly object _lock = new();
    private readonly object _sendLock = new();
    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _thread;
    private Socket? _socket;
    private readonly List<byte> _buffer = new();
    private volatile bool _loggedIn;
    private volatile bool _running;
    private Failure _failure = Failure.Transient;
    private Exception? _broken;
    private volatile bool _retryable;

    private OwnAircraftState? _position;
    private DateTime _identUntil = DateTime.MinValue;
    private int _identChanged;

    public bool IsIdentifying
    {
        get
        {
            lock (_lock)
                return _position?.TransponderIdent == true || DateTime.UtcNow < _identUntil;
        }
    }
    private JsonObject? _configSent;
    private readonly Dictionary<string, ControllerInfo> _controllers = new(StringComparer.Ordinal);
    private TaskCompletionSource<string?>? _metarWaiter;

    public FsdClient(string host, int port, string callsign, string cid, string password, string realName,
        int rating, string aircraft, int simulator, TrafficTable? traffic,
        double reconnectWindow = DefaultReconnectWindow)
    {
        _host = host;
        _port = port > 0 ? port : DefaultPort;
        Callsign = (callsign ?? "").Trim().ToUpperInvariant();
        _cid = FsdProtocol.Sanitize(cid);
        // 密码也要过 Sanitize：带冒号的密码会让 #AP 后面的字段全部错位，而按下标打码的
        // 日志会把冒号后那一截密码原样记下来。
        _password = FsdProtocol.Sanitize(password);
        var name = FsdProtocol.Sanitize(realName);
        _realName = name.Length > 0 ? name : _cid;
        _rating = rating;
        Aircraft = FsdProtocol.Sanitize(aircraft).ToUpperInvariant();
        _simulator = simulator;
        _traffic = traffic;
        _reconnectWindow = reconnectWindow;
        if (traffic is not null)
        {
            traffic.RequestInfo ??= c => RequestPlaneInfo(c);
            traffic.RequestConfig ??= c => RequestConfig(c);
        }
        // 航司码取呼号前三位字母，CCA1501 -> CCA。
        var prefix = Callsign.Length >= 3 ? Callsign[..3] : Callsign;
        Airline = prefix.Length == 3 && prefix.All(char.IsAsciiLetter) ? prefix : "";
    }

    public string Callsign { get; }
    public string Aircraft { get; }
    public string Airline { get; }

    /// <summary>服务端的 $SF：附近有别的 101 飞行员，要 5 Hz 的快速位置。</summary>
    public bool SendFast { get; private set; }

    public bool Connected => _loggedIn && _socket is not null;

    public bool GaveUp { get; private set; }

    public bool IsAlive => _thread is { IsAlive: true };

    public event Action<LinkState, string>? StatusChanged;
    public event Action<string, string, string>? TextReceived;
    public event Action<IReadOnlyList<ControllerInfo>>? ControllersChanged;
    /// <summary>某个管制席位回了 ATIS 的一行（sender, line）。</summary>
    public event Action<string, string>? AtisReceived;

    // ---------- 对外 ----------

    public void Start()
    {
        if (_running)
            return;
        _running = true;
        _stop.Reset();
        _thread = new Thread(Run) { IsBackground = true, Name = "fsd" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        // 先把"可以重连"撤掉，否则 Close() 的 stopped 会被翻成 reconnecting
        _retryable = false;
        // 不在这里关 socket：收包循环最多 0.2 秒就会看到停止标记，自己走 Close()，
        // 那里还要发一个 #DP 让服务端立刻放出呼号。
        _stop.Set();
        var thread = _thread;
        if (thread is not null && thread.IsAlive && thread != Thread.CurrentThread)
            thread.Join(TimeSpan.FromSeconds(3));
        _thread = null;
    }

    public void Dispose() => Stop();

    /// <summary>模拟器那边有新数据了。只存下来，实际发包由连接线程按节奏做。</summary>
    public void UpdatePosition(OwnAircraftState? state)
    {
        lock (_lock)
        {
            if ((_position?.TransponderIdent ?? false) != (state?.TransponderIdent ?? false))
            {
                Log.Info($"aircraft IDENT {(state?.TransponderIdent == true ? "on" : "off")}");
                Interlocked.Exchange(ref _identChanged, 1);
            }
            _position = state;
        }
    }

    /// <summary>按下 IDENT，位置包的模式字符临时变成 Y。</summary>
    public void Ident(double seconds = 8.0)
    {
        lock (_lock)
            _identUntil = DateTime.UtcNow.AddSeconds(seconds);
        Interlocked.Exchange(ref _identChanged, 1);
        Log.Info("IDENT");
    }

    /// <summary>给某个呼号发文字消息。recipient 用 @频率 可以发到频率上。</summary>
    public bool SendText(string recipient, string message)
    {
        message = FsdProtocol.Sanitize(message);
        if (message.Length == 0)
            return false;
        return Send($"#TM{Callsign}:{FsdProtocol.Sanitize(recipient)}:{message}");
    }

    /// <summary>提交飞行计划。一共 17 段，收件人是 SERVER。</summary>
    public bool FileFlightPlan(FlightPlan plan)
    {
        static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;

        var rules = FsdProtocol.Sanitize(plan.Rules);
        var fields = new[]
        {
            rules.Length > 0 ? rules[..1] : "I",
            FsdProtocol.Sanitize(Or(plan.Aircraft, Aircraft)),
            FsdProtocol.Sanitize(plan.CruiseSpeed),
            FsdProtocol.Sanitize(plan.Departure).ToUpperInvariant(),
            FsdProtocol.Sanitize(plan.DepartureTime),
            FsdProtocol.Sanitize(plan.ActualTime),
            FsdProtocol.Sanitize(plan.CruiseAltitude),
            FsdProtocol.Sanitize(plan.Arrival).ToUpperInvariant(),
            Or(FsdProtocol.Sanitize(plan.EnrouteHours), "0"),
            Or(FsdProtocol.Sanitize(plan.EnrouteMinutes), "0"),
            Or(FsdProtocol.Sanitize(plan.FuelHours), "0"),
            Or(FsdProtocol.Sanitize(plan.FuelMinutes), "0"),
            FsdProtocol.Sanitize(plan.Alternate).ToUpperInvariant(),
            FsdProtocol.Sanitize(plan.Remarks),
            FsdProtocol.Sanitize(plan.Route).ToUpperInvariant()
        };
        return Send($"$FP{Callsign}:SERVER:" + string.Join(':', fields));
    }

    /// <summary>问某个管制席位要文字通播。</summary>
    public bool RequestAtis(string callsign) =>
        Send($"$CQ{Callsign}:{FsdProtocol.Sanitize(callsign).ToUpperInvariant()}:ATIS");

    /// <summary>问某个呼号的真实姓名（$CQ RN），回答在 $CR 里。</summary>
    public bool RequestRealName(string callsign) =>
        Send($"$CQ{Callsign}:{FsdProtocol.Sanitize(callsign).ToUpperInvariant()}:RN");

    /// <summary>向服务端要 METAR（$AX）。拿不到返回 null。</summary>
    public async Task<string?> RequestMetarAsync(string icao, TimeSpan timeout)
    {
        icao = (icao ?? "").Trim().ToUpperInvariant();
        if (!Connected)
            return null;
        var waiter = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
            _metarWaiter = waiter;
        if (!Send($"$AX{Callsign}:SERVER:METAR:{icao}"))
            return null;
        var finished = await Task.WhenAny(waiter.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return finished == waiter.Task ? waiter.Task.Result : null;
    }

    public IReadOnlyList<ControllerInfo> Controllers
    {
        get
        {
            lock (_lock)
                return _controllers.Values.ToList();
        }
    }

    // ---------- 内部 ----------

    private void Status(LinkState state, string message)
    {
        // 重连期间的失败不是终态
        if (_retryable && state is LinkState.Error or LinkState.Stopped)
            state = LinkState.Reconnecting;
        Log.Info($"connection state: {state}");
        try
        {
            StatusChanged?.Invoke(state, message);
        }
        catch (Exception e)
        {
            Log.Warning($"status callback raised: {e.Message}");
        }
    }

    /// <summary>#AP 的第 4 段是密码，日志里换成星号。</summary>
    internal static string Redact(string packet)
    {
        if (!packet.StartsWith("#AP", StringComparison.Ordinal))
            return packet;
        var fields = packet.Split(':');
        if (fields.Length > 3)
            fields[3] = "***";
        return string.Join(':', fields);
    }

    private bool Send(string packet)
    {
        var socket = _socket;
        if (socket is null)
            return false;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(packet + "\r\n");
            lock (_sendLock)
            {
                var sent = 0;
                while (sent < bytes.Length)
                    sent += socket.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
            }
            if (Log.IsDebugEnabled)
                Log.Debug($"→ protocol packet ({packet.Length} characters; content omitted)");
            return true;
        }
        catch (Exception e)
        {
            SendFailed(socket, e);
            return false;
        }
    }

    /// <summary>发不出去说明连接已经坏了：把 socket 关掉，让收包那边走正常的掉线重连。</summary>
    private void SendFailed(Socket socket, Exception error)
    {
        if (_broken is null)
        {
            _broken = error;
            Log.Warning($"FSD send failed ({error.GetType().Name}): {error.Message}; closing the socket");
        }
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch
        {
            // ignore
        }
    }

    private bool StopRequested => !_running || _stop.IsSet;

    private void Run()
    {
        var establishedOnce = false;
        var attempt = 0;
        DateTime? retrySince = null;

        while (!StopRequested)
        {
            _failure = Failure.Transient;
            try
            {
                if (Connect())
                {
                    establishedOnce = true;
                    attempt = 0;
                    retrySince = null;
                    _retryable = true;
                    Loop();
                }
            }
            catch (Exception e)
            {
                Status(LinkState.Error, $"FSD 连接出错：{e.Message}");
            }
            finally
            {
                Close();
            }

            if (StopRequested)
                return;
            var failure = _failure;
            if (failure == Failure.Fatal)
                return;
            if (failure == Failure.Transient && !establishedOnce)
                return;

            var now = DateTime.UtcNow;
            retrySince ??= now;
            var waited = (now - retrySince.Value).TotalSeconds;
            if (waited >= _reconnectWindow)
            {
                GaveUp = true;
                _retryable = false;
                Log.Warning($"could not get back onto FSD within {_reconnectWindow:F0} s (last failure: {failure}), giving up");
                Status(LinkState.Offline, $"{_reconnectWindow:F0} 秒内没能重新连上 FSD，已断开。");
                return;
            }

            var delay = ReconnectDelays[Math.Min(attempt, ReconnectDelays.Length - 1)];
            delay = Math.Min(delay, _reconnectWindow - waited);
            attempt++;
            Log.Info($"FSD retry {attempt} in {delay:F0} s ({failure}, {waited:F0} of {_reconnectWindow:F0} s used)");
            _retryable = true;
            var message = failure == Failure.InUse
                ? $"呼号 {Callsign} 还被上一条连接占着，{Math.Round(delay):F0} 秒后重试……"
                : $"FSD 连接断开，{Math.Round(delay):F0} 秒后第 {attempt} 次重连……";
            Status(LinkState.Reconnecting, message);
            if (_stop.Wait(TimeSpan.FromSeconds(delay)))
                return;
        }
    }

    private bool Connect()
    {
        _broken = null;
        SendFast = false;
        lock (_buffer)
            _buffer.Clear();
        var problem = FsdProtocol.CallsignProblem(Callsign);
        if (problem is not null)
        {
            _failure = Failure.Fatal;
            _retryable = false;
            Status(LinkState.Error, problem);
            return false;
        }

        Status(LinkState.Connecting, $"正在以 {Callsign} 连接 FSD 服务器 {_host}……");
        try
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            var connect = socket.ConnectAsync(_host, _port);
            if (!connect.Wait(TimeSpan.FromSeconds(10)))
            {
                socket.Dispose();
                throw new TimeoutException("连接超时");
            }
            _socket = socket;
        }
        catch (Exception e)
        {
            var inner = e is AggregateException { InnerException: { } i } ? i : e;
            Status(LinkState.Error, $"连不上 FSD 服务器 {_host}:{_port}：{inner.Message}");
            return false;
        }

        var greeting = ReadPacket(5.0);
        if (!string.IsNullOrEmpty(greeting))
            Log.Info("server greeting received");

        var machineId = Callsign.Sum(c => (int)c) * 7919;
        Send($"$ID{Callsign}:SERVER:{FsdProtocol.ClientId}:{AppVersion.ClientName}:{FsdProtocol.ClientMajor}:{FsdProtocol.ClientMinor}:{_cid}:{machineId}");
        Send($"#AP{Callsign}:SERVER:{_cid}:{_password}:{_rating}:{FsdProtocol.ProtocolRevision}:{_simulator}:{_realName}");
        Send($"$CQ{Callsign}:SERVER:CAPS");

        var deadline = DateTime.UtcNow.AddSeconds(LoginTimeout);
        while (DateTime.UtcNow < deadline)
        {
            if (StopRequested)
                return false;
            var packet = ReadPacket(1.0);
            if (packet is null)
                continue;
            if (packet.Length == 0)
            {
                Status(LinkState.Error, "FSD 服务器关闭了连接。");
                return false;
            }
            if (!HandlePacketSafely(packet))
                return false;
            if (_loggedIn)
            {
                Status(LinkState.Online, $"已以 {Callsign} 登录 FSD。");
                return true;
            }
        }

        Status(LinkState.Error, "登录 FSD 超时，服务器没有回应。");
        return false;
    }

    private static double Seconds() => Environment.TickCount64 / 1000.0;

    private void Loop()
    {
        // 登录后先报 `@`，再报一个快速位置（停着就是 #ST），和 xPilot 一样
        var now = Seconds();
        var nextPosition = now + SendPosition();
        var nextFast = 0.0;
        var nextSlow = now + SlowFastInterval;
        var nextConfig = now;
        _configSent = null;
        SendFastPosition(slow: false);

        while (!StopRequested)
        {
            // 收包的等待不能盖过下一次该发的时刻，否则 200 ms 的节奏会被拖成 400 ms
            var due = Math.Min(nextPosition, nextSlow);
            if (SendFast)
                due = Math.Min(due, nextFast);
            var timeout = Math.Clamp(due - Seconds(), 0.01, 0.2);
            var packet = _broken is null ? ReadPacket(timeout) : "";
            if (packet is { Length: 0 })
            {
                var broken = _broken;
                Status(LinkState.Error, broken is not null
                    ? $"FSD 发送失败：{broken.Message}"
                    : "FSD 连接断开了。");
                return;
            }
            if (packet is not null && !HandlePacketSafely(packet))
                return;

            now = Seconds();
            if (Interlocked.Exchange(ref _identChanged, 0) != 0 || now >= nextPosition)
                nextPosition = now + SendPosition();
            if (SendFast && now >= nextFast)
            {
                SendFastPosition(slow: false);
                nextFast = now + FastPositionInterval;
            }
            if (now >= nextSlow)
            {
                SendFastPosition(slow: true);
                nextSlow = now + SlowFastInterval;
            }
            if (now >= nextConfig)
            {
                BroadcastConfig();
                nextConfig = now + ConfigBroadcastInterval;
            }
        }
    }

    /// <summary>发一个位置包，返回下一次的间隔。</summary>
    private double SendPosition()
    {
        OwnAircraftState? snapshot;
        lock (_lock)
            snapshot = _position;
        if (snapshot is null)
            return PositionInterval;

        DateTime identUntil;
        lock (_lock)
            identUntil = _identUntil;
        var mode = PositionMode(snapshot, DateTime.UtcNow < identUntil);
        Send(FsdProtocol.PositionPacket(mode, Callsign, _rating, snapshot));

        // 停在地面上没动就降频，省得刷屏
        if (mode != 'Y' && snapshot.OnGround && snapshot.Groundspeed < 1)
            return SlowPositionInterval;
        return PositionInterval;
    }

    internal static char PositionMode(OwnAircraftState state, bool softwareIdent) =>
        state.TransponderIdent || softwareIdent ? 'Y' : state.TransponderMode == TransponderMode.ModeC ? 'N' : 'S';

    /// <summary>slow=false 是 200 ms 那一拍：在动发 `^`，停着发 `#ST`。slow=true 是 5 秒那一拍：在动才发 `#SL`。</summary>
    private string? SendFastPosition(bool slow)
    {
        OwnAircraftState? snapshot;
        lock (_lock)
            snapshot = _position;
        if (snapshot is null)
            return null;
        var stopped = FsdProtocol.IsStopped(snapshot);
        string kind;
        if (slow)
        {
            if (stopped)
                return null;
            kind = "#SL";
        }
        else
            kind = stopped ? "#ST" : "^";
        Send(FsdProtocol.FastPositionPacket(kind, Callsign, snapshot));
        return kind;
    }

    /// <summary>服务端的 $SF。关掉时补一个包，让别人手里的速度是对的。</summary>
    private void SetSendFast(bool enabled)
    {
        if (enabled == SendFast)
            return;
        SendFast = enabled;
        Log.Info($"fast position updates {(enabled ? "on" : "off")}");
        if (!enabled && SendFastPosition(slow: true) is null)
        {
            OwnAircraftState? snapshot;
            lock (_lock)
                snapshot = _position;
            if (snapshot is not null)
                Send(FsdProtocol.FastPositionPacket("#ST", Callsign, snapshot));
        }
    }

    /// <summary>读一个包。超时返回 null，连接关闭返回 ""，空行跳过。</summary>
    private string? ReadPacket(double timeout)
    {
        var socket = _socket;
        if (socket is null)
            return "";
        var deadline = Seconds() + timeout;
        var chunk = new byte[4096];
        while (true)
        {
            lock (_buffer)
            {
                var newline = _buffer.IndexOf((byte)'\n');
                if (newline >= 0)
                {
                    var line = FsdProtocol.DecodePacket(_buffer.GetRange(0, newline).ToArray()).TrimEnd('\r').Trim();
                    _buffer.RemoveRange(0, newline + 1);
                    if (line.Length > 0)
                        return line;
                    continue;
                }
            }

            var remaining = deadline - Seconds();
            if (remaining <= 0)
                return null;
            try
            {
                if (!socket.Poll((int)(remaining * 1_000_000), SelectMode.SelectRead))
                    return null;
                var count = socket.Receive(chunk);
                if (count == 0)
                {
                    Log.Info("FSD receive: the server closed the connection (EOF)");
                    return "";
                }
                lock (_buffer)
                    _buffer.AddRange(chunk.AsSpan(0, count).ToArray());
            }
            catch (Exception e)
            {
                Log.Info($"FSD receive failed: {e.GetType().Name}: {e.Message}");
                return "";
            }
        }
    }

    /// <summary>别人发来的畸形包只丢这一个，不能把整条连接拖下去重连。</summary>
    private bool HandlePacketSafely(string packet)
    {
        try
        {
            return HandlePacket(packet);
        }
        catch (Exception e)
        {
            Log.Warning($"dropped a malformed packet {packet.Split(':')[0]} ({e.GetType().Name}: {e.Message})");
            return true;
        }
    }

    /// <summary>返回 false 表示应当结束连接。</summary>
    private bool HandlePacket(string packet)
    {
        if (Log.IsDebugEnabled)
            Log.Debug($"← protocol packet ({packet.Length} characters; content omitted)");
        var fields = packet.Split(':');
        var head = fields[0];

        if (head.StartsWith("$ER", StringComparison.Ordinal))
        {
            var code = fields.Length > 2 ? fields[2] : "?";
            var message = fields.Length > 4 ? fields[4] : packet;
            if (!_loggedIn)
            {
                if (code == ErrCallsignInUse)
                {
                    // 首连也要等：撞上的多半是自己上一条还没死透的连接
                    _failure = Failure.InUse;
                    _retryable = true;
                }
                else if (code == ErrServerFull)
                    _failure = Failure.Transient;
                else
                {
                    _failure = Failure.Fatal;
                    _retryable = false;
                }
                Log.Info($"login refused with code {code} ({_failure})");
                Status(LinkState.Error, $"FSD 服务器拒绝登录（错误 {code}）：{message}");
                return false;
            }
            Log.Warning($"the server returned an error ({code}); content omitted");
            return true;
        }

        if (head.StartsWith("$AR", StringComparison.Ordinal) && fields.Length >= 4 && fields[2] == "METAR")
        {
            TaskCompletionSource<string?>? waiter;
            lock (_lock)
            {
                waiter = _metarWaiter;
                _metarWaiter = null;
            }
            waiter?.TrySetResult(string.Join(':', fields[3..]).Trim());
            return true;
        }

        if (head.StartsWith("#TM", StringComparison.Ordinal) && fields.Length >= 3)
        {
            var sender = head[3..];
            var recipient = fields[1];
            var body = string.Join(':', fields[2..]);
            try
            {
                TextReceived?.Invoke(sender, recipient, body);
            }
            catch (Exception e)
            {
                Log.Warning($"text-message callback raised: {e.Message}");
            }
            return true;
        }

        if (head.StartsWith('%') && fields.Length >= 3)
        {
            NoteController(head[1..], fields);
            return true;
        }

        if (head.StartsWith('@') && fields.Length >= 9)
        {
            NoteTraffic(fields);
            return true;
        }

        if (head.StartsWith('^') && fields.Length >= 13)
        {
            NoteFastTraffic(head[1..], fields, stopped: false);
            return true;
        }
        if (head.StartsWith("#SL", StringComparison.Ordinal) && fields.Length >= 13)
        {
            NoteFastTraffic(head[3..], fields, stopped: false);
            return true;
        }
        if (head.StartsWith("#ST", StringComparison.Ordinal) && fields.Length >= 7)
        {
            NoteFastTraffic(head[3..], fields, stopped: true);
            return true;
        }

        if (head.StartsWith("$SF", StringComparison.Ordinal))
        {
            // $SFSERVER:{呼号}:{1|0}
            if (fields.Length >= 3 && fields[1].Equals(Callsign, StringComparison.OrdinalIgnoreCase))
                SetSendFast(fields[2].Trim() == "1");
            return true;
        }

        if (head.StartsWith("#SB", StringComparison.Ordinal) && fields.Length >= 3)
        {
            HandlePlaneInfo(head[3..], fields);
            return true;
        }

        if (head.StartsWith("#DP", StringComparison.Ordinal))
        {
            _traffic?.Remove(head[3..]);
            return true;
        }

        if (head.StartsWith("$CR", StringComparison.Ordinal) && fields.Length >= 3)
        {
            var sender = head[3..];
            if (fields[1] == Callsign && fields[2] == "CAPS")
                _loggedIn = true;
            else if (fields[2] == "ACC" && fields.Length > 3 && _traffic is not null)
            {
                // 本客户端早先版本的回答：$CR 加不包 "config" 的 JSON
                var config = ParseJson(string.Join(':', fields[3..]));
                if (config?["config"] is JsonObject inner)
                    config = inner;
                if (config is not null)
                    _traffic.SetConfig(sender, config);
            }
            else if (fields[2] == "RN" && fields.Length > 3)
                NoteRealName(sender, fields[3]);
            else if (fields[2] == "ATIS" && fields.Length > 4)
                NoteAtis(sender, fields[3], string.Join(':', fields[4..]));
            return true;
        }

        if (head.StartsWith("$CQ", StringComparison.Ordinal) && fields.Length >= 3)
        {
            var sender = head[3..];
            var recipient = fields[1];
            var query = fields[2];
            if (query == "ACC" && (recipient == Callsign || recipient == FsdProtocol.RangedAll))
                HandleConfigQuery(sender, recipient, string.Join(':', fields[3..]));
            else if (recipient == Callsign)
            {
                if (query == "CAPS")
                    Send($"$CR{Callsign}:{sender}:CAPS:ATCINFO=0:MODELDESC=1:ACCONFIG=1");
                else if (query == "RN")
                    Send($"$CR{Callsign}:{sender}:RN:{_realName}::{_rating}");
            }
            return true;
        }

        if (head.StartsWith("$PI", StringComparison.Ordinal) && fields.Length >= 3)
        {
            Send($"$PO{Callsign}:{head[3..]}:{string.Join(':', fields[2..])}");
            return true;
        }

        if (head.StartsWith("#DA", StringComparison.Ordinal) || head.StartsWith("#DL", StringComparison.Ordinal))
        {
            if (head.StartsWith("#DA", StringComparison.Ordinal))
                ForgetController(head[3..]);
            _loggedIn = true;
            return true;
        }

        return true;
    }

    private static JsonObject? ParseJson(string text)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>别人的位置包。字段顺序和我们自己发的那个一样。</summary>
    private void NoteTraffic(string[] fields)
    {
        if (_traffic is null)
            return;
        var callsign = fields[1];
        if (callsign == Callsign)
            return;
        try
        {
            var attitude = FsdProtocol.UnpackPbh(unchecked((uint)long.Parse(fields[8])));
            var lat = Parse(fields[4]);
            var lon = Parse(fields[5]);
            CheckCoordinates(lat, lon);
            var alt = Parse(fields[6]);
            var gs = Parse(fields[7]);
            var mode = fields[0].Length > 1 ? fields[0][1..] : "S";
            _traffic.UpdatePosition(callsign, lat, lon, ToInt(AdjustAltitude(alt)), attitude.Pitch, attitude.Bank,
                attitude.Heading, attitude.OnGround, ToInt(gs), int.Parse(fields[2]), mode);
        }
        catch (Exception e) when (e is FormatException or OverflowException or IndexOutOfRangeException)
        {
            Log.Debug($"could not parse the position packet {fields[0]}:{fields.ElementAtOrDefault(1)}: {e.Message}");
        }
    }

    /// <summary>
    /// 别人的快速位置包（`^` / `#SL` / `#ST`）。X 向东、Y 向上、Z 向北；角速度换回抬头/右转/右坡
    /// 为正、度每秒。`#ST` 没有速度段，前轮角在第 6 段。
    /// </summary>
    private void NoteFastTraffic(string callsign, string[] fields, bool stopped)
    {
        if (_traffic is null || callsign == Callsign)
            return;
        try
        {
            var attitude = FsdProtocol.UnpackPbh(unchecked((uint)(long.Parse(fields[5]) & 0xFFFFFFFF)));
            var agl = Parse(fields[4]);
            Vec3 velocity, rotation;
            double noseWheel;
            if (stopped)
            {
                velocity = Vec3.Zero;
                rotation = Vec3.Zero;
                noseWheel = Parse(fields[6]);
            }
            else
            {
                velocity = new Vec3(Parse(fields[6]), Parse(fields[7]), Parse(fields[8]));
                rotation = new Vec3(-FsdProtocol.RadToDeg(Parse(fields[9])), FsdProtocol.RadToDeg(Parse(fields[10])),
                    -FsdProtocol.RadToDeg(Parse(fields[11])));
                noseWheel = Parse(fields[12]);
                // 弧度换度可能把很大的有限值乘成 Infinity
                if (!double.IsFinite(rotation.A) || !double.IsFinite(rotation.B) || !double.IsFinite(rotation.C))
                    throw new FormatException("rotation out of range");
            }
            var groundspeed = ToInt(Math.Round(Math.Sqrt(velocity.C * velocity.C + velocity.A * velocity.A) * FsdProtocol.KnotsPerMps,
                MidpointRounding.ToEven));
            var lat = Parse(fields[1]);
            var lon = Parse(fields[2]);
            CheckCoordinates(lat, lon);
            var altitude = AdjustAltitude(Parse(fields[3]));
            if (!double.IsFinite(altitude))
                throw new FormatException($"altitude out of range: {fields[3]}");
            _traffic.UpdatePosition(callsign, lat, lon, altitude,
                attitude.Pitch, attitude.Bank, attitude.Heading, attitude.OnGround, groundspeed,
                velocity: velocity, rotation: rotation, agl: agl, noseWheel: noseWheel);
        }
        catch (Exception e) when (e is FormatException or OverflowException or IndexOutOfRangeException)
        {
            Log.Debug($"could not parse the fast position packet {fields[0]}: {e.Message}");
        }
    }

    /// <summary>NaN、±Infinity 也算解析失败：进了交通表会一路积分下去，最后让整帧 X-Plane 数据序列化失败。</summary>
    private static double Parse(string text) =>
        FsdProtocol.TryDouble(text, out var value) && double.IsFinite(value)
            ? value
            : throw new FormatException($"not a finite number: {text}");

    private static void CheckCoordinates(double lat, double lon)
    {
        if (Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
            throw new FormatException($"position out of range: {lat}, {lon}");
    }

    /// <summary>超出 int 范围的有限值按边界截断，别让强转溢出成负数。</summary>
    private static int ToInt(double value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);

    /// <summary>他机高度按本机温度误差修正后再进运动模型。没有本机快照时不改。</summary>
    private double AdjustAltitude(double altitude)
    {
        OwnAircraftState? own;
        lock (_lock)
            own = _position;
        if (own is null)
            return altitude;
        return Core.Altitude.AdjustIncoming(altitude, own.NetworkAltitude, own.TemperatureError);
    }

    /// <summary>问对方的机型，用于模型匹配。</summary>
    public bool RequestPlaneInfo(string callsign) => Send($"#SB{Callsign}:{callsign}:PIR");

    /// <summary>问对方的配置（灯光/襟翼/起落架）。请求体是 {"request":"full"}。</summary>
    public bool RequestConfig(string callsign) => Send($"$CQ{Callsign}:{callsign}:ACC:{{\"request\":\"full\"}}");

    /// <summary>$CQ … ACC：别人要我们的配置，或者别人发来他的配置。</summary>
    private void HandleConfigQuery(string sender, string recipient, string body)
    {
        JsonObject? payload = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            payload = ParseJson(body);
            if (payload is null)
            {
                Log.Debug("unreadable ACC payload (content omitted)");
                return;
            }
        }
        if (payload?["config"] is JsonObject config)
        {
            _traffic?.SetConfig(sender, config);
            return;
        }
        if (recipient != Callsign)
            return;
        if (payload is null)
            Send($"$CR{Callsign}:{sender}:ACC:{WireJson(OwnConfig(full: false))}");
        else if (IsFullConfigRequest(payload))
            Send($"$CQ{Callsign}:{sender}:ACC:{WireJson(new JsonObject { ["config"] = OwnConfig(full: true) })}");
    }

    /// <summary>{"request":"full"}。request 不是字符串（布尔、数字、对象、数组）一律不算。</summary>
    internal static bool IsFullConfigRequest(JsonObject payload) =>
        payload["request"] is JsonValue value && value.TryGetValue<string>(out var request) && request == "full";

    private static string WireJson(JsonNode? value) =>
        (value ?? new JsonObject()).ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    /// <summary>自己的配置，键名照 protocol.md 的 ACC。没有快照返回 null。</summary>
    private JsonObject? OwnConfig(bool full)
    {
        OwnAircraftState? s;
        lock (_lock)
            s = _position;
        if (s is null)
            return null;
        var config = new JsonObject();
        if (full)
            config["is_full_data"] = true;
        config["lights"] = new JsonObject
        {
            ["beacon_on"] = s.Lights.Beacon,
            ["landing_on"] = s.Lights.Landing,
            ["taxi_on"] = s.Lights.Taxi,
            ["strobe_on"] = s.Lights.Strobe,
            ["nav_on"] = s.Lights.Nav,
            ["logo_on"] = s.Lights.Logo
        };
        config["engines"] = new JsonObject { ["1"] = new JsonObject { ["on"] = s.EnginesOn, ["is_reversing"] = false } };
        config["gear_down"] = s.GearDown;
        config["flaps_pct"] = (int)Math.Round(s.Flaps * 100.0, MidpointRounding.ToEven);
        config["spoilers_out"] = s.Spoilers;
        config["on_ground"] = s.OnGround;
        return config;
    }

    /// <summary>配置变了就向附近广播（$CQ…:@94836:ACC），只带变了的键。连上后的第一次发全量。</summary>
    private void BroadcastConfig()
    {
        var config = OwnConfig(full: false);
        if (config is null)
            return;
        JsonObject changed;
        var last = _configSent;
        if (last is null)
        {
            changed = new JsonObject { ["is_full_data"] = true };
            foreach (var (key, value) in config)
                changed[key] = value?.DeepClone();
        }
        else
        {
            changed = new JsonObject { ["is_full_data"] = false };
            var any = false;
            foreach (var (key, value) in config)
            {
                if (JsonNode.DeepEquals(last[key], value))
                    continue;
                changed[key] = value?.DeepClone();
                any = true;
            }
            if (!any)
                return;
        }
        _configSent = config;
        Send($"$CQ{Callsign}:{FsdProtocol.RangedAll}:ACC:{WireJson(new JsonObject { ["config"] = changed })}");
    }

    /// <summary>#SB。别人问我们机型要答，别人报机型要记下来。</summary>
    private void HandlePlaneInfo(string sender, string[] fields)
    {
        var kind = fields.Length > 2 ? fields[2] : "";
        if (kind == "PIR")
        {
            var reply = $"#SB{Callsign}:{sender}:PI:GEN:EQUIPMENT={(Aircraft.Length > 0 ? Aircraft : "B738")}";
            if (Airline.Length > 0)
                reply += $":AIRLINE={Airline}";
            Send(reply);
            return;
        }

        if (_traffic is null)
            return;

        if (kind == "PI" && fields.Length > 3 && fields[3] == "GEN")
        {
            string? equipment = null, airline = null, livery = null, csl = null;
            foreach (var field in fields[4..])
            {
                var eq = field.IndexOf('=');
                if (eq < 0)
                    continue;
                var key = field[..eq].ToUpperInvariant();
                var value = field[(eq + 1)..];
                if (value.Length == 0)
                    continue;
                switch (key)
                {
                    case "EQUIPMENT": equipment = value; break;
                    case "AIRLINE": airline = value; break;
                    case "LIVERY": livery = value; break;
                    case "CSL": csl = value; break;
                }
            }
            if (equipment is not null || airline is not null || livery is not null || csl is not null)
                _traffic.SetPlaneInfo(sender, equipment, airline, livery, csl);
            return;
        }

        if (kind == "PI" && fields.Length > 3 && fields[3] == "X")
        {
            // 老式：#SB发方:收方:PI:X:0:发动机类型:CSL=名字（有的客户端写成 ~名字）
            foreach (var field in fields[4..])
            {
                if (field.StartsWith("CSL=", StringComparison.OrdinalIgnoreCase))
                    _traffic.SetPlaneInfo(sender, csl: field[4..]);
                else if (field.StartsWith('~'))
                    _traffic.SetPlaneInfo(sender, csl: field[1..]);
            }
        }
    }

    /// <summary>记下一个在线管制席位：%呼号:频率:席位类型:可视范围:等级:纬度:经度:0</summary>
    private void NoteController(string callsign, string[] fields)
    {
        bool isNew;
        lock (_lock)
        {
            if (!int.TryParse(fields.Length > 2 ? fields[2] : "0", out var facility))
                facility = 0;
            isNew = !_controllers.TryGetValue(callsign, out var existing);
            _controllers[callsign] = new ControllerInfo(callsign, FsdProtocol.DisplayFrequency(fields[1]), facility,
                DateTime.UtcNow) { RealName = existing?.RealName ?? "" };
        }
        if (isNew)
            RequestRealName(callsign);
        RaiseControllers();
    }

    private void NoteRealName(string callsign, string name)
    {
        lock (_lock)
        {
            if (!_controllers.TryGetValue(callsign, out var existing))
                return;
            _controllers[callsign] = existing with { RealName = name };
        }
        RaiseControllers();
    }

    private void NoteAtis(string sender, string kind, string text)
    {
        // T 是正文行，E 是结束，V/Z 是语音地址和下线时间
        if (kind == "T" && text.Length > 0)
            AtisReceived?.Invoke(sender, text);
    }

    private void ForgetController(string callsign)
    {
        bool removed;
        lock (_lock)
            removed = _controllers.Remove(callsign);
        if (removed)
            RaiseControllers();
    }

    /// <summary>太久没有位置包的管制席位从列表里拿掉（服务端只在范围内周期性广播）。</summary>
    public void PruneControllers(TimeSpan maxAge)
    {
        bool changed;
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - maxAge;
            var gone = _controllers.Where(c => c.Value.Seen < cutoff).Select(c => c.Key).ToList();
            foreach (var key in gone)
                _controllers.Remove(key);
            changed = gone.Count > 0;
        }
        if (changed)
            RaiseControllers();
    }

    private void RaiseControllers()
    {
        try
        {
            ControllersChanged?.Invoke(Controllers);
        }
        catch (Exception e)
        {
            Log.Warning($"controller-list callback raised: {e.Message}");
        }
    }

    private void Close()
    {
        var socket = _socket;
        if (socket is not null)
        {
            try
            {
                Send($"#DP{Callsign}:{_cid}");
            }
            catch
            {
                // ignore
            }
            try
            {
                socket.Dispose();
            }
            catch
            {
                // ignore
            }
            _socket = null;
        }
        _loggedIn = false;
        lock (_lock)
            _controllers.Clear();
        RaiseControllers();
        Status(LinkState.Stopped, "FSD 已断开。");
    }
}
