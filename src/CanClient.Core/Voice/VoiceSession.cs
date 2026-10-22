using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using CanClient.Core.Fsd;
using CanClient.Core.Util;

namespace CanClient.Core.Voice;

/// <summary>发送链路的参数，设置一改就生效。</summary>
public sealed class VoiceOptions
{
    public volatile bool Denoise = true;
    /// <summary>麦克风增益（dB）。</summary>
    public double MicGainDb;
}

/// <summary>
/// 一条 Mumble 语音连接，跟着 COM1 走。对应 Python 版 voice.py。
///
/// 频率到频道的换算是全网统一的约定，改了会同时打断飞行员、管制和 ATIS：
///     FREQ_{int(round(频率 * 1000)):06d}      125.400 → FREQ_125400
///
/// 几条规矩（都是 Python 版踩过的坑）：
///   - 重连有上限：连上过之后掉线，最多再试 3 次；"连上了"以 ServerSync 为准。
///     服务端对认证失败按 CID 限流，一个无限重连的僵尸能把账号的语音锁死。
///   - 被踢了（UserRemove 带 actor/reason/ban）就不连回去：同一账号在别处登录时，两边互相顶会
///     变成死循环，最后被 Murmur 按 autoban 封 IP。
///   - 切频道每秒比对一次目标和服务器上的实际位置，不靠一次性事件：掉线重连后服务器会把人放回
///     根频道，只比对本地记账的话就永远留在根频道里。
///   - 人在根频道就不发：频率频道永远是根的子频道，在根里说话是发给了根频道里的所有人。
/// </summary>
public sealed class VoiceSession : IDisposable
{
    private static readonly Logger Log = Util.Log.For("voice");

    public const int ReconnectLimit = 3;
    private const double ReconnectInterval = 2.0;
    private const double ChannelTimeout = 5.0;
    private const double ChannelRetryInterval = 1.0;
    private const double RxTimeout = 0.5;
    private const double SequenceResetInterval = 5.0;
    private const uint RootChannel = 0;

    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly AudioEngine _audio;
    private readonly string _inputDevice;
    private readonly string _outputDevice;
    private readonly VoiceOptions _options;

    private readonly CancellationTokenSource _cts = new();
    private readonly AutoResetEvent _channelWanted = new(false);
    private readonly object _channelLock = new();
    private readonly ConcurrentDictionary<uint, OpusDecoder> _decoders = new();
    private Thread? _connectionThread;
    private Thread? _channelThread;
    private Thread? _rxThread;
    private volatile MumbleConnection? _connection;
    private volatile bool _running;
    private volatile bool _kicked;
    private bool _established;
    private bool _wasDropped;
    private bool _captureAcquired;
    private string? _denial;
    private string _stuckReason = "";
    private double? _pending;

    // 发送
    private OpusEncoder? _encoder;
    private Denoiser? _denoiser;
    private Limiter? _limiter;
    private volatile bool _transmitting;
    private bool _wasTransmitting;
    private ulong _sequence;
    private long _sequenceStart;
    private long _lastPacket;
    private int _sentFrames;
    private string _skipReason = "";
    private short[]? _lastProcessed;

    // 接收
    private long _lastRx;
    private volatile bool _receiving;
    private string _talker = "";

    public VoiceSession(string host, int port, string username, string password, AudioEngine audio,
        string inputDevice, string outputDevice, VoiceOptions options)
    {
        _host = host;
        _port = port;
        _username = username;
        _password = password;
        _audio = audio;
        _inputDevice = inputDevice;
        _outputDevice = outputDevice;
        _options = options;
    }

    public event Action<LinkState, string>? StatusChanged;
    public event Action<bool>? TransmittingChanged;
    public event Action<bool>? ReceivingChanged;
    /// <summary>进了某个频率的频道（频率, 频道名）。</summary>
    public event Action<double, string>? ChannelChanged;
    /// <summary>处理后的麦克风峰值（0..1），每帧一次。</summary>
    public event Action<double>? Level;

    public double? Frequency { get; private set; }
    public string? Channel { get; private set; }
    public bool GaveUp { get; private set; }
    public bool Transmitting => _transmitting;
    public bool Receiving => _receiving;

    public bool Connected
    {
        get
        {
            var connection = _connection;
            return connection is { Synced: true } && _running;
        }
    }

    /// <summary>"UDP 23 ms" / "TCP 41 ms"，状态栏用。</summary>
    public string TransportDescription
    {
        get
        {
            var connection = _connection;
            if (connection is null)
                return "";
            return connection.UdpActive
                ? $"UDP {connection.UdpPingAverage:F0} ms"
                : $"TCP {connection.TcpPingAverage:F0} ms";
        }
    }

    public static string ChannelName(double frequencyMhz) =>
        "FREQ_" + ((int)Math.Round(frequencyMhz * 1000.0, MidpointRounding.ToEven)).ToString("D6", CultureInfo.InvariantCulture);

    private void Status(LinkState state, string message)
    {
        Log.Info($"{state}: {message}");
        try
        {
            StatusChanged?.Invoke(state, message);
        }
        catch (Exception e)
        {
            Log.Warning($"status callback raised: {e.Message}");
        }
    }

    // ---------- 生命周期 ----------

    public void Start()
    {
        if (_running)
            return;
        _running = true;
        Status(LinkState.Connecting, $"正在连接语音服务器 {_host} …");
        try
        {
            _audio.StartPlayback(_outputDevice);
            _audio.AcquireCapture(_inputDevice);
            _captureAcquired = true;
            _audio.FrameCaptured += OnFrame;
            _encoder = new OpusEncoder();
        }
        catch (Exception e)
        {
            _running = false;
            Release();
            Status(LinkState.Error, $"打不开音频设备：{e.Message}");
            return;
        }

        _connectionThread = new Thread(ConnectionLoop) { IsBackground = true, Name = "mumble" };
        _connectionThread.Start();
        _channelThread = new Thread(ChannelLoop) { IsBackground = true, Name = "mumble-channel" };
        _channelThread.Start();
        _rxThread = new Thread(RxLoop) { IsBackground = true, Name = "mumble-rx" };
        _rxThread.Start();
    }

    public void Stop() => Stop(LinkState.Stopped, "语音已断开");

    private void Stop(LinkState state, string message)
    {
        var wasRunning = _running;
        _running = false;
        _cts.Cancel();
        _channelWanted.Set();
        _connection?.Close();
        foreach (var thread in new[] { _connectionThread, _channelThread, _rxThread })
        {
            if (thread is not null && thread != Thread.CurrentThread && thread.IsAlive)
                thread.Join(TimeSpan.FromSeconds(2));
        }
        Release();
        if (wasRunning)
            Status(state, message);
    }

    private void Release()
    {
        _audio.FrameCaptured -= OnFrame;
        if (_captureAcquired)
        {
            _captureAcquired = false;
            _audio.ReleaseCapture();
        }
        lock (_channelLock)
        {
            _encoder?.Dispose();
            _encoder = null;
            _denoiser?.Dispose();
            _denoiser = null;
        }
        foreach (var decoder in _decoders.Values)
            decoder.Dispose();
        _decoders.Clear();
        if (_transmitting)
        {
            _transmitting = false;
            TransmittingChanged?.Invoke(false);
        }
        if (_receiving)
        {
            _receiving = false;
            ReceivingChanged?.Invoke(false);
        }
    }

    public void Dispose() => Stop();

    // ---------- 连接 ----------

    private void ConnectionLoop()
    {
        var attempts = 0;
        while (_running)
        {
            if (_kicked)
                return;
            if (_established)
            {
                if (attempts >= ReconnectLimit)
                {
                    GaveUp = true;
                    Log.Warning($"{attempts} reconnect attempts after the drop all failed, giving up");
                    ThreadPool.QueueUserWorkItem(_ => Stop(LinkState.Offline, $"掉线后重连 {ReconnectLimit} 次都没成功，语音已下线"));
                    return;
                }
                attempts++;
                Status(LinkState.Reconnecting, $"语音连接断开，正在重连（{attempts}/{ReconnectLimit}）");
            }

            var connection = new MumbleConnection(_host, _port, _username, _password);
            connection.Connected += () =>
            {
                // 计数只在这里清零：TLS 建好、Authenticate 发出去不代表认证通过
                attempts = 0;
                var first = !_established;
                _established = true;
                if (first)
                    Status(LinkState.Online, $"语音已连接（{_username}）");
                else if (_wasDropped)
                {
                    _wasDropped = false;
                    Status(LinkState.Online, $"语音已重连（{_username}）");
                }
                _channelWanted.Set();
            };
            connection.PermissionDenied += OnPermissionDenied;
            connection.UserRemoved += (session, kicked, reason) => OnUserRemoved(connection, session, kicked, reason);
            connection.AudioReceived += OnAudio;
            _connection = connection;

            try
            {
                connection.Run(_cts.Token);
            }
            catch (CertificatePinException e)
            {
                ThreadPool.QueueUserWorkItem(_ => Stop(LinkState.Error, e.Message));
                return;
            }
            catch (MumbleRejectedException e)
            {
                if (!_established)
                {
                    ThreadPool.QueueUserWorkItem(_ => Stop(LinkState.Error, $"语音服务器拒绝了 {_username}（{e.Message}）"));
                    return;
                }
                Log.Warning($"the server rejected the reconnection: {e.Message}");
            }
            catch (Exception e) when (e is not ThreadInterruptedException)
            {
                var inner = e is AggregateException { InnerException: { } i } ? i : e;
                if (!_established && _running)
                {
                    ThreadPool.QueueUserWorkItem(_ => Stop(LinkState.Error, $"语音服务器连接失败：{inner.Message}"));
                    return;
                }
                Log.Info($"voice connection attempt failed: {inner.Message}");
            }
            finally
            {
                connection.Dispose();
                _connection = null;
            }

            if (!_running || _kicked)
                return;
            if (!_established)
            {
                ThreadPool.QueueUserWorkItem(_ => Stop(LinkState.Error, "语音服务器断开了连接"));
                return;
            }
            if (!_wasDropped)
            {
                _wasDropped = true;
                Log.Warning($"voice connection dropped, reconnecting (up to {ReconnectLimit} attempts)");
                Status(LinkState.Reconnecting, $"语音连接断开，正在重连（最多 {ReconnectLimit} 次）");
            }
            Frequency = null;
            Channel = null;
            if (_cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(ReconnectInterval)))
                return;
        }
    }

    private void OnPermissionDenied(string kind, string reason)
    {
        var text = kind switch
        {
            "Permission" => "没有权限（建频率频道要根频道的 MakeTempChannel，进频道要 Enter）",
            "ChannelName" => "频道名不合服务器的规矩",
            "NestingLimit" => "频道层级超过了服务器上限",
            "ChannelCountLimit" => "服务器上的频道数已达上限",
            "UserListenerLimit" => "服务器限制了每个用户能监听的频道数",
            _ => $"服务器拒绝了操作：{kind}"
        };
        if (reason.Length > 0)
            text = $"{text}（{reason}）";
        _denial = text;
        NoteStuck(text);
        Status(LinkState.Denied, text);
    }

    private void OnUserRemoved(MumbleConnection connection, uint session, bool kicked, string reason)
    {
        if (connection.MySession != session || !kicked)
            return;
        _kicked = true;
        Log.Warning($"kicked by the server: {(reason.Length > 0 ? reason : "no reason given")}");
        var why = reason.Length > 0 ? reason : "账号可能在其他位置登录了";
        ThreadPool.QueueUserWorkItem(_ => Stop(LinkState.Offline, $"被服务器断开：{why}。不会自动重连。"));
    }

    // ---------- 频率 ----------

    /// <summary>COM1 变了。立刻返回，真正的切换在工作线程上。</summary>
    public void SetFrequency(double? frequency)
    {
        if (frequency is null)
            return;
        var value = Math.Round(frequency.Value, 3);
        if (_pending == value)
            return;
        _pending = value;
        _channelWanted.Set();
    }

    private void NoteStuck(string reason)
    {
        if (reason == _stuckReason)
            return;
        _stuckReason = reason;
        Log.Warning($"still not in the frequency channel: {reason}");
    }

    private void ChannelLoop()
    {
        while (_running)
        {
            _channelWanted.WaitOne(TimeSpan.FromSeconds(ChannelRetryInterval));
            if (!_running)
                break;
            var target = _pending;
            var connection = _connection;
            if (target is null)
            {
                NoteStuck("no COM1 frequency yet");
                continue;
            }
            if (connection is not { Synced: true })
            {
                NoteStuck("the voice server is not connected yet");
                continue;
            }
            if (target == Frequency)
            {
                if (InExpectedChannel(connection))
                {
                    _stuckReason = "";
                    continue;
                }
                Log.Warning($"the server no longer has us in {Channel} (most likely a reconnect), rejoining");
                Frequency = null;
                Channel = null;
            }
            try
            {
                SwitchChannel(connection, target.Value);
            }
            catch (Exception e)
            {
                Log.Warning($"channel switch raised: {e.Message}");
            }
        }
    }

    private bool InExpectedChannel(MumbleConnection connection)
    {
        if (Channel is null)
            return false;
        var channel = connection.FindChannel(Channel);
        var myself = connection.Myself;
        return channel is not null && myself is not null && myself.ChannelId == channel.Id;
    }

    private void SwitchChannel(MumbleConnection connection, double frequency)
    {
        lock (_channelLock)
        {
            if (frequency == Frequency)
                return;
            var name = ChannelName(frequency);
            var channel = connection.FindChannel(name);
            if (channel is null)
            {
                Log.Info($"channel {name} does not exist, creating a temporary one");
                _denial = null;
                connection.CreateChannel(name);
                channel = WaitForChannel(connection, name);
            }
            if (channel is null)
            {
                Log.Warning($"channel {name} did not appear within {ChannelTimeout:F0} s of being created, retrying");
                Diagnose(connection, name);
                return;
            }
            var myself = connection.Myself;
            if (myself is null)
            {
                Log.Warning($"myself is still unknown, skipping the switch to {name} this round");
                return;
            }
            if (myself.ChannelId != channel.Id)
            {
                connection.MoveTo(channel.Id);
                if (!WaitUntilIn(connection, channel.Id))
                {
                    Log.Warning($"sent the request to join {name} but it did not take effect within {ChannelTimeout:F0} s, will retry");
                    Diagnose(connection, name);
                    return;
                }
            }
            Frequency = frequency;
            Channel = name;
            _stuckReason = "";
            Log.Info($"switched to {name} ({frequency:F3} MHz)");
            ChannelChanged?.Invoke(frequency, name);
        }
    }

    private MumbleChannel? WaitForChannel(MumbleConnection connection, string name)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed.TotalSeconds < ChannelTimeout && _running)
        {
            if (_denial is not null)
            {
                Log.Warning($"the server refused while waiting for {name}: {_denial}");
                return null;
            }
            var channel = connection.FindChannel(name);
            if (channel is not null)
            {
                Log.Info($"channel {name} appeared after {stopwatch.Elapsed.TotalSeconds:F1} s (id={channel.Id})");
                return channel;
            }
            Thread.Sleep(100);
        }
        return connection.FindChannel(name);
    }

    private bool WaitUntilIn(MumbleConnection connection, uint channelId)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed.TotalSeconds < ChannelTimeout && _running)
        {
            if (connection.Myself?.ChannelId == channelId)
                return true;
            Thread.Sleep(100);
        }
        return false;
    }

    private static void Diagnose(MumbleConnection connection, string name)
    {
        var myself = connection.Myself;
        var channels = connection.Channels;
        Log.Warning($"diagnosis: target={name} my session={connection.MySession} my channel={myself?.ChannelId} " +
                    $"channel table={channels.Count} entries target present={channels.Any(c => c.Name == name)}");
    }

    // ---------- 发送 ----------

    /// <summary>PTT。界面和快捷键都走这里。</summary>
    public void SetTransmitting(bool value)
    {
        if (value == _transmitting)
            return;
        _transmitting = value;
        if (value)
        {
            _sentFrames = 0;
            _skipReason = "";
            Log.Info($"PTT down: channel {Channel ?? "(none)"}, frequency {Frequency?.ToString("F3") ?? "(none)"}");
        }
        else if (_sentFrames > 0)
            Log.Info($"PTT up, sent {_sentFrames} frames (about {_sentFrames * 0.02:F1} s)");
        else
            Log.Warning($"PTT up but not a single frame was sent: {(_skipReason.Length > 0 ? _skipReason : "reason unknown")}");
        TransmittingChanged?.Invoke(value);
    }

    private void Skip(string reason)
    {
        if (reason == _skipReason)
            return;
        _skipReason = reason;
        Log.Debug($"not transmitting for now: {reason}");
    }

    /// <summary>一帧麦克风数据：降噪 → 增益 → 限幅 → Opus → 发出去。采集线程上调。</summary>
    private void OnFrame(short[] frame)
    {
        var samples = frame;
        if (_options.Denoise && Denoiser.Available)
        {
            _denoiser ??= new Denoiser(AudioEngine.SampleRate);
            _denoiser.Process(samples);
        }
        _limiter ??= new Limiter(AudioEngine.SampleRate);
        _limiter.Process(samples, MicGain.DbToLinear(_options.MicGainDb));
        _lastProcessed = samples;
        Level?.Invoke(MicGain.Peak(samples));

        var transmitting = _transmitting;
        if (!transmitting)
        {
            if (_wasTransmitting)
            {
                // 松开 PTT：补一个结束包，对面立刻停止播放
                _wasTransmitting = false;
                SendFrame(new short[AudioEngine.FrameSamples], terminator: true);
            }
            return;
        }

        var connection = _connection;
        if (connection is not { Synced: true })
        {
            Skip("语音服务器未连接");
            return;
        }
        var myself = connection.Myself;
        if (myself is null)
        {
            Skip("服务器还没回报我们自己的用户信息");
            return;
        }
        if (myself.ChannelId is null)
        {
            Skip("还没有进入任何频道");
            return;
        }
        if (myself.ChannelId == RootChannel)
        {
            Skip("还留在根频道（频率频道没切成功），发出去没人听得到");
            return;
        }

        if (!_wasTransmitting)
        {
            // 新的一段话：按 pymumble 的规矩接上帧号（停顿超过 5 秒从 0 开始）
            _wasTransmitting = true;
            var now = Stopwatch.GetTimestamp();
            if (_lastPacket == 0 || Stopwatch.GetElapsedTime(_lastPacket).TotalSeconds > SequenceResetInterval)
            {
                _sequenceStart = now;
                _sequence = 0;
            }
            else
                _sequence = (ulong)(Stopwatch.GetElapsedTime(_sequenceStart).TotalSeconds / 0.01);
        }
        if (SendFrame(samples, terminator: false))
            _sentFrames++;
    }

    private bool SendFrame(short[] samples, bool terminator)
    {
        var connection = _connection;
        if (connection is not { Synced: true })
            return false;
        byte[] opus;
        lock (_channelLock)
        {
            if (_encoder is null)
                return false;
            _encoder.Bitrate = connection.EncoderBitrate();
            opus = _encoder.Encode(samples);
        }
        try
        {
            connection.SendAudio(opus, _sequence, terminator);
            _sequence += 2;     // 一帧 20 ms，帧号按 10 ms 计
            _lastPacket = Stopwatch.GetTimestamp();
            return true;
        }
        catch (Exception e)
        {
            Skip($"发送失败：{e.Message}");
            return false;
        }
    }

    // ---------- 接收 ----------

    private void OnAudio(uint session, ulong frameNumber, byte[] opus, bool terminator)
    {
        var connection = _connection;
        if (connection is null || session == connection.MySession)
            return;
        try
        {
            var decoder = _decoders.GetOrAdd(session, _ => new OpusDecoder());
            var pcm = new short[OpusDecoder.MaxFrameSamples];
            var samples = decoder.Decode(opus, pcm);
            if (samples <= 0)
                return;
            Interlocked.Exchange(ref _lastRx, Stopwatch.GetTimestamp());
            if (!_receiving)
            {
                _receiving = true;
                _talker = connection.User(session)?.Name ?? session.ToString();
                Log.Debug($"receiving audio from {_talker}");
                ReceivingChanged?.Invoke(true);
            }
            _audio.Play(session, pcm.AsSpan(0, samples));
        }
        catch (Exception e)
        {
            Log.Warning($"failed to play the received audio: {e.Message}");
        }
    }

    private void RxLoop()
    {
        while (_running)
        {
            Thread.Sleep(50);
            if (!_receiving)
                continue;
            var last = Interlocked.Read(ref _lastRx);
            if (Stopwatch.GetElapsedTime(last).TotalSeconds > RxTimeout)
            {
                _receiving = false;
                Log.Debug($"stopped hearing {_talker}");
                ReceivingChanged?.Invoke(false);
            }
        }
    }
}
