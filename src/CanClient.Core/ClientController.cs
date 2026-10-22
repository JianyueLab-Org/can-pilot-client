using CanClient.Core.Fsd;
using CanClient.Core.ModelMatching;
using CanClient.Core.Ptt;
using CanClient.Core.Settings;
using CanClient.Core.Sim;
using CanClient.Core.Traffic;
using CanClient.Core.Util;
using CanClient.Core.Voice;

namespace CanClient.Core;

/// <summary>消息类别，对应界面上不同颜色的消息。</summary>
public enum MessageKind
{
    System,
    Radio,
    RadioDirect,
    Private,
    Broadcast,
    Server,
    Error,
    Sent
}

/// <summary>一条要显示的消息。PrivateWith 不为空时显示在和那个呼号的私聊页里。</summary>
public sealed record ClientMessage(MessageKind Kind, string Sender, string Text, string? PrivateWith = null);

/// <summary>航空器列表里的一行：网络上的一架他机、它匹配到的模型，以及在模拟器里的状态。</summary>
public sealed record AircraftRow(
    string Callsign,
    string TypeCode,
    string Airline,
    string ModelName,
    string MatchDescription,
    string MatchSource,
    string SimStatus,
    double Latitude,
    double Longitude,
    int AltitudeMsl,
    int Heading,
    int Groundspeed,
    double? Distance,
    double Pitch,
    double Bank);

/// <summary>
/// 客户端的总调度，对应 Python 版 gui.py 里不碰界面的那一半：
///
///   SimConnect ──本机状态──▶ FSD（5 Hz 位置）、语音（跟着 COM1 切频道）
///   FSD ──他机位置/机型──▶ TrafficTable ──▶ 模型匹配 ──▶ 注入循环 ──▶ SimConnect AI 飞机
///   PTT ──▶ 语音发送
///
/// 事件都在后台线程上触发，界面自己切回 UI 线程。
/// </summary>
public sealed class ClientController : IDisposable
{
    private static readonly Logger Log = Util.Log.For("client");

    private const double TickInterval = 0.2;
    private static readonly TimeSpan ControllerMaxAge = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private readonly Timer _timer;
    private readonly InjectionLoop _injectionLoop;
    private readonly Dictionary<string, (ModelMatchResult Result, string Equipment, string Airline)> _models = new(StringComparer.Ordinal);
    private TrafficInjector? _injector;
    private volatile IReadOnlyList<TrafficEntry>? _plannedEntries;
    private volatile Dictionary<string, string>? _plannedModels;
    private (double Lat, double Lon)? _origin;
    private DateTime _lastChime = DateTime.MinValue;

    // 应答机：默认跟着模拟器，用户点了"模式 C"就以用户为准，直到模拟器那边再变
    private bool _modeC = true;
    private TransponderMode? _lastSimMode;
    private bool _autoModeCDone;

    // 连接意图：用户点了连接、还没点断开
    private bool _connectRequested;
    private bool _observer;
    private string _callsign = "";
    private string _typeCode = "";
    private bool _simWasLost;

    public ClientController(AppSettings settings)
    {
        Settings = settings;
        Models = new ModelMatchingService(settings);
        Models.RulesChanged += InvalidateModels;
        Models.Notification += text => Post(MessageKind.System, text);
        Models.ScanCompleted += text => Post(MessageKind.System, text);
        Audio = new AudioEngine { OutputGainDb = settings.OutputGainDb };
        VoiceOptions = new VoiceOptions { Denoise = settings.MicDenoise, MicGainDb = settings.MicGainDb };
        Ptt.SetBindings(settings.PttBindings ?? []);
        Ptt.Changed += down => Voice?.SetTransmitting(down);
        Sim.StateChanged += OnSimState;
        Sim.Opened += (simulator, name) =>
        {
            Post(MessageKind.System, $"已连接到模拟器：{name}");
            _ = Models.SetSimulatorAsync(simulator);
        };
        _injectionLoop = new InjectionLoop(InjectFrame);
        _timer = new Timer(_ => SafeTick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public AppSettings Settings { get; }
    public SimConnection Sim { get; } = new();
    public TrafficTable Traffic { get; } = new();
    public ModelMatchingService Models { get; }
    public AudioEngine Audio { get; }
    public PttWatcher Ptt { get; } = new();
    public VoiceOptions VoiceOptions { get; }
    public FsdClient? Fsd { get; private set; }
    public VoiceSession? Voice { get; private set; }

    public bool IsConnected => _connectRequested;
    public bool IsObserver => _observer;
    public string Callsign => _callsign;
    public string TypeCode => _typeCode;
    public bool ModeC => _modeC;
    public OwnAircraftState? Own { get; private set; }

    public event Action<ClientMessage>? MessageAdded;
    public event Action? ConnectionChanged;
    public event Action<IReadOnlyList<ControllerInfo>>? ControllersChanged;
    public event Action<bool>? TransmittingChanged;
    public event Action<bool>? ReceivingChanged;
    public event Action<OwnAircraftState?>? OwnStateChanged;
    public event Action<bool>? ModeCChanged;
    public event Action<bool, string>? SimStateChanged;
    /// <summary>需要闪任务栏（私聊、无线电消息、断线），参数是原因。</summary>
    public event Action<string>? AttentionRequested;
    public event Action<string, string>? VoiceStatusChanged;
    public event Action<string, string>? FsdStatusChanged;

    public void Start()
    {
        Sim.Start();
        Ptt.Start();
        _ = Models.InitializeAsync();
        _timer.Change(TimeSpan.FromSeconds(TickInterval), TimeSpan.FromSeconds(TickInterval));
    }

    private void Post(MessageKind kind, string text, string sender = "", string? privateWith = null)
    {
        try
        {
            MessageAdded?.Invoke(new ClientMessage(kind, sender, text, privateWith));
        }
        catch (Exception e)
        {
            Log.Warning($"message callback raised: {e.Message}");
        }
    }

    // ---------- 连接 ----------

    /// <summary>连接网络。返回 null 表示开始连了，否则是拦下来的原因。</summary>
    public string? Connect(string callsign, string typeCode, bool observer)
    {
        if (_connectRequested)
            return "已经连接了";
        callsign = callsign.Trim().ToUpperInvariant();
        typeCode = typeCode.Trim().ToUpperInvariant();
        if (!observer && FsdProtocol.CallsignProblem(callsign) is { } problem)
            return problem;
        if (string.IsNullOrWhiteSpace(Settings.Cid) || string.IsNullOrEmpty(Settings.Password))
            return "请先在“设置 → 网络”里填写 CID 和密码。";
        // 没有模拟器位置时不能登录 FSD：否则服务端保留一条没有有效位置的连接
        if (!observer && !Sim.Connected)
            return "还没有连上 MSFS，或者还没进入飞行。观察员模式可以不开模拟器。";

        Settings.Callsign = callsign;
        Settings.Aircraft = typeCode;
        Settings.ObserverMode = observer;
        Settings.Save();

        _callsign = callsign;
        _typeCode = typeCode;
        _observer = observer;
        _connectRequested = true;
        _simWasLost = false;

        if (Settings.ConnectFsd && !observer)
            StartFsd();
        if (Settings.ConnectVoice || observer)
            StartVoice();
        _injectionLoop.Start();
        if (observer)
        {
            Post(MessageKind.System, "观察员模式：只连语音，网络上不会出现这架飞机。");
            if (CurrentFrequency() is null)
                Post(MessageKind.System, "还没有频率：用 .com1 121.800 手动设置，或者在模拟器里调好 COM1。");
        }
        ConnectionChanged?.Invoke();
        return null;
    }

    private void StartFsd()
    {
        Fsd?.Stop();
        var simulator = Sim.Simulator == SimulatorType.Msfs2024 ? FsdProtocol.SimulatorMsfs2024 : FsdProtocol.SimulatorMsfs2020;
        var fsd = new FsdClient(Settings.FsdHostName, Settings.FsdPortNumber, _callsign, Settings.Cid, Settings.Password,
            string.IsNullOrWhiteSpace(Settings.RealName) ? Settings.Cid : Settings.RealName, Settings.Rating, _typeCode,
            simulator, Traffic);
        fsd.StatusChanged += (state, message) => OnFsdStatus(fsd, state, message);
        fsd.TextReceived += OnText;
        fsd.ControllersChanged += list => ControllersChanged?.Invoke(list);
        fsd.AtisReceived += (sender, line) => Post(MessageKind.Radio, line, sender);
        Traffic.RequestInfo = c => fsd.RequestPlaneInfo(c);
        Traffic.RequestConfig = c => fsd.RequestConfig(c);
        Fsd = fsd;
        fsd.UpdatePosition(BuildOwnState());
        fsd.Start();
    }

    private void StartVoice()
    {
        var voice = new VoiceSession(Settings.MumbleHostName, Settings.MumblePortNumber, Settings.Cid, Settings.Password,
            Audio, Settings.InputDevice, Settings.OutputDevice, VoiceOptions);
        voice.StatusChanged += (state, message) => OnVoiceStatus(voice, state, message);
        voice.TransmittingChanged += value => { if (voice == Voice) TransmittingChanged?.Invoke(value); };
        voice.ReceivingChanged += value => { if (voice == Voice) ReceivingChanged?.Invoke(value); };
        voice.ChannelChanged += (frequency, name) => Post(MessageKind.System, $"语音已进入 {frequency:F3}（{name}）");
        Voice = voice;
        if (CurrentFrequency() is { } frequency)
            voice.SetFrequency(frequency);
        ThreadPool.QueueUserWorkItem(_ => voice.Start());
    }

    public void Disconnect()
    {
        _connectRequested = false;
        var fsd = Fsd;
        Fsd = null;
        fsd?.Stop();
        var voice = Voice;
        Voice = null;
        if (voice is not null)
            ThreadPool.QueueUserWorkItem(_ => voice.Stop());
        // 断开网络就没有他机来源了：先停注入循环，再把放进去的清掉，否则会冻在天上
        _injectionLoop.Stop();
        _plannedEntries = null;
        _injector?.Clear();
        Traffic.Clear();
        lock (_lock)
            _models.Clear();
        ControllersChanged?.Invoke([]);
        TransmittingChanged?.Invoke(false);
        ReceivingChanged?.Invoke(false);
        Post(MessageKind.System, "已与网络断开连接。");
        ConnectionChanged?.Invoke();
    }

    private void OnFsdStatus(FsdClient source, LinkState state, string message)
    {
        if (source != Fsd)
            return;
        FsdStatusChanged?.Invoke(state.ToString(), message);
        var kind = state is LinkState.Error or LinkState.Offline ? MessageKind.Error : MessageKind.System;
        if (state != LinkState.Stopped || _connectRequested)
            Post(kind, message);
        if (state == LinkState.Offline)
        {
            Fsd = null;
            LinkGone("网络已下线，语音保持连接；要重新上网络请断开后重新连接。");
        }
        else if (state == LinkState.Error && !source.IsAlive)
        {
            // 首连失败、密码错这类不重试的错误。语音不受影响。
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(200);
                if (Fsd == source && !source.IsAlive)
                {
                    Fsd = null;
                    LinkGone(null);
                }
            });
        }
        if (state is LinkState.Offline or LinkState.Error && Settings.FlashOnDisconnect)
            AttentionRequested?.Invoke("disconnected");
    }

    private void OnVoiceStatus(VoiceSession source, LinkState state, string message)
    {
        if (source != Voice)
            return;
        VoiceStatusChanged?.Invoke(state.ToString(), message);
        var kind = state is LinkState.Error or LinkState.Offline or LinkState.Denied ? MessageKind.Error : MessageKind.System;
        if (state != LinkState.Stopped)
            Post(kind, message);
        if (state is LinkState.Offline or LinkState.Error)
        {
            Voice = null;
            TransmittingChanged?.Invoke(false);
            ReceivingChanged?.Invoke(false);
            if (Settings.FlashOnDisconnect)
                AttentionRequested?.Invoke("disconnected");
            LinkGone(state == LinkState.Offline ? "语音已下线，网络连接保持；要恢复语音请断开后重新连接。" : null);
        }
    }

    /// <summary>一条链路彻底下线：另一条还在就说一句、留着它；都没了才整个断开。</summary>
    private void LinkGone(string? note)
    {
        if (!_connectRequested)
            return;
        if (Fsd is not null || Voice is not null)
        {
            if (note is not null)
                Post(MessageKind.System, note);
            ConnectionChanged?.Invoke();
        }
        else
            Disconnect();
    }

    private void OnSimState(bool connected, string message)
    {
        Post(connected ? MessageKind.System : MessageKind.Error, $"模拟器：{message}");
        SimStateChanged?.Invoke(connected, message);
        if (!_connectRequested || _observer)
            return;
        if (!connected)
        {
            _simWasLost = true;
            Post(MessageKind.System, "模拟器已断开，网络上的位置暂停更新；模拟器恢复后会自动接上。");
            return;
        }
        if (_simWasLost && (Fsd is null || !Fsd.IsAlive) && Settings.ConnectFsd)
        {
            _simWasLost = false;
            Log.Info("restarting FSD after SimConnect recovered");
            StartFsd();
        }
        _simWasLost = false;
    }

    // ---------- 定时 ----------

    private void SafeTick()
    {
        try
        {
            Tick();
        }
        catch (Exception e)
        {
            Log.Warning($"tick raised: {e}");
        }
    }

    private OwnAircraftState? BuildOwnState()
    {
        var snapshot = Sim.Snapshot();
        if (snapshot is null)
            return null;
        var simMode = snapshot.TransponderMode;
        if (_lastSimMode != simMode)
        {
            _lastSimMode = simMode;
            SetModeCInternal(simMode == TransponderMode.ModeC);
        }
        if (Settings.AutoSquawkModeC && !snapshot.OnGround && !_modeC && !_autoModeCDone)
        {
            _autoModeCDone = true;
            SetModeCInternal(true);
            Post(MessageKind.System, "已离地，应答机自动切到模式 C。");
        }
        if (snapshot.OnGround && snapshot.Groundspeed < 1)
            _autoModeCDone = false;
        return snapshot with { TransponderMode = _modeC ? TransponderMode.ModeC : TransponderMode.Standby };
    }

    private void SetModeCInternal(bool value)
    {
        if (_modeC == value)
            return;
        _modeC = value;
        ModeCChanged?.Invoke(value);
    }

    /// <summary>界面上的"模式 C"按钮。</summary>
    public void SetModeC(bool value)
    {
        SetModeCInternal(value);
        Sim.SetTransponderState(value ? TransponderMode.ModeC : TransponderMode.Standby);
    }

    private double? CurrentFrequency() =>
        Observer.FrequencyFor(Own?.Com1, Own?.Com1Power ?? true, Settings.ObserverFrequency, _observer);

    private void Tick()
    {
        var own = BuildOwnState();
        Own = own;
        OwnStateChanged?.Invoke(own);

        var fsd = Fsd;
        fsd?.UpdatePosition(own);
        fsd?.PruneControllers(ControllerMaxAge);
        // 语音频率每一拍都跟一次，不挂在"读到了模拟器数据"下面：观察员多半没开模拟器
        Voice?.SetFrequency(CurrentFrequency());

        foreach (var callsign in Traffic.Prune())
        {
            lock (_lock)
                _models.Remove(callsign);
        }

        if (!Settings.RenderTraffic || own is null)
        {
            _plannedEntries = null;
            return;
        }
        if (_injector is null || _injector.Generation != Sim.Generation)
        {
            _injector?.Dispose();
            _injector = null;
            if (Sim.IsOpen && Sim.Simulator != SimulatorType.None)
            {
                var injector = new TrafficInjector(Sim);
                injector.Frame += _injectionLoop.FrameArrived;
                injector.CreateFailed += OnCreateFailed;
                _injector = injector;
                if (!injector.Available)
                    Post(MessageKind.Error, "他机注入不可用，详见日志。");
            }
        }

        _origin = (own.Latitude, own.Longitude);
        var entries = Traffic.Snapshot(origin: _origin, limit: Settings.MaxAircraft, maxRangeNm: Settings.TrafficRangeNm);
        var models = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
            models[entry.Callsign] = ModelFor(entry).ModelName;
        _plannedModels = models;
        _plannedEntries = entries;
    }

    /// <summary>给一架飞机挑模型，结果缓存住；机型/航司变了、规则变了、模型被拉黑才重新匹配。</summary>
    private ModelMatchResult ModelFor(TrafficEntry entry)
    {
        lock (_lock)
        {
            if (_models.TryGetValue(entry.Callsign, out var cached) && !entry.ModelDirty
                && cached.Equipment == entry.Equipment && cached.Airline == entry.Airline
                && !Models.Matcher.IsErroneous(cached.Result.ModelName))
                return cached.Result;
        }
        var previous = _models.GetValueOrDefault(entry.Callsign).Result?.ModelName;
        var result = Models.Match(entry.Callsign, entry.Equipment, previous);
        lock (_lock)
            _models[entry.Callsign] = (result, entry.Equipment, entry.Airline);
        Traffic.MarkModelClean(entry.Callsign, entry.Equipment, entry.Airline);
        var line = $"{entry.Callsign} ({(entry.Equipment.Length > 0 ? entry.Equipment : "?")}) → {result.ModelName}: {result.Description} [{result.Source}]";
        if (entry.Equipment.Length > 0)
            Log.Info(line);
        else
            Log.Debug(line);
        return result;
    }

    private void InvalidateModels()
    {
        lock (_lock)
            _models.Clear();
    }

    /// <summary>重新匹配所有他机（规则改了以后，或者用户点了"重新加载模型匹配规则"）。</summary>
    public void RematchAll()
    {
        Models.ReloadRules();
        InvalidateModels();
        Post(MessageKind.System, "模型匹配规则已重新加载，所有他机会重新匹配。");
    }

    private void OnCreateFailed(string callsign, string model)
    {
        Models.MarkErroneous(model);
        lock (_lock)
            _models.Remove(callsign);
        Post(MessageKind.Error, $"模拟器无法生成模型 {model}（{callsign}），已改用其他模型。");
    }

    /// <summary>注入循环的一帧：积分到此刻，写进模拟器。在注入线程上。</summary>
    private void InjectFrame()
    {
        var injector = _injector;
        if (injector is null)
            return;
        if (!Settings.RenderTraffic)
        {
            if (injector.Count > 0)
                injector.Clear();
            return;
        }
        var models = _plannedModels;
        if (_plannedEntries is null || models is null || _origin is null)
            return;
        var entries = Traffic.Snapshot(origin: _origin, limit: Settings.MaxAircraft, maxRangeNm: Settings.TrafficRangeNm);
        var targets = entries
            .Select(e => new InjectionTarget(e, models.GetValueOrDefault(e.Callsign, "")))
            .ToList();
        injector.Sync(targets, TrafficTable.Now());
    }

    /// <summary>删除模拟器里的所有他机（航空器列表里的按钮）。网络上的飞机下一帧会重新建出来。</summary>
    public void RemoveAllAircraft()
    {
        _injector?.Clear();
        InvalidateModels();
    }

    // ---------- 航空器列表 ----------

    public IReadOnlyList<AircraftRow> AircraftList()
    {
        var origin = _origin;
        var entries = Traffic.Snapshot(origin: origin);
        var injected = _injector?.Describe() ?? new Dictionary<string, InjectedAircraftInfo>();
        var rows = new List<AircraftRow>();
        foreach (var e in entries)
        {
            // 没开模拟器（或者还没轮到这架）时注入那边不会去匹配，这里现算一次，列表上照样看得到映射
            var result = ModelFor(e);
            var match = (Result: result, e.Equipment, e.Airline);
            var status = injected.TryGetValue(e.Callsign, out var info)
                ? info.ObjectId is { } id ? $"已生成 #{id}" : "生成中"
                : e.RangeNm > Settings.TrafficRangeNm ? "超出显示距离" : "未生成";
            rows.Add(new AircraftRow(e.Callsign, e.Equipment, e.Airline,
                match.Result?.ModelName ?? "", match.Result?.Description ?? "（还没匹配）", match.Result?.Source ?? "",
                status, e.Latitude, e.Longitude, (int)Math.Round(e.Altitude), (int)Math.Round(e.Heading) % 360,
                e.Groundspeed, e.RangeNm, e.Pitch, e.Bank));
        }
        return rows;
    }

    // ---------- 文字消息 ----------

    private void OnText(string sender, string recipient, string body)
    {
        var own = Own;
        var mine = _callsign;
        if (recipient.StartsWith('@'))
        {
            var direct = Chime.MentionsCallsign(body, mine);
            var tuned = own is null || IsTuned(recipient, own.Com1) || IsTuned(recipient, own.Com2)
                        || (_observer && Observer.ParseFrequency(Settings.ObserverFrequency) is { } manual
                            && FsdProtocol.FrequencyRecipient(manual) == recipient);
            if (!tuned)
                return;
            Post(direct ? MessageKind.RadioDirect : MessageKind.Radio, body, sender);
            if (Settings.FlashOnRadioMessage || direct)
                AttentionRequested?.Invoke("radio");
        }
        else if (recipient.Equals(mine, StringComparison.OrdinalIgnoreCase))
        {
            if (sender.Equals("SERVER", StringComparison.OrdinalIgnoreCase))
                Post(MessageKind.Server, body, sender);
            else
            {
                Post(MessageKind.Private, body, sender, privateWith: sender);
                if (Settings.FlashOnPrivateMessage)
                    AttentionRequested?.Invoke("private");
            }
        }
        else
            Post(MessageKind.Broadcast, body, sender);

        if (Settings.MessageSound && Chime.WantsAlert(mine, sender, recipient, body, Settings.MessageSoundAll))
            PlayChime();
    }

    private static bool IsTuned(string recipient, double? frequency) =>
        frequency is { } f && FsdProtocol.FrequencyRecipient(f) == recipient;

    public void PlayChime(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastChime < Chime.MinInterval)
            return;
        _lastChime = DateTime.UtcNow;
        var volume = Settings.MessageSoundVolume;
        if (volume <= 0)
            return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (!Audio.Playing)
                    Audio.StartPlayback(Settings.OutputDevice);
                Audio.PlayClip(Chime.Waveform(volume: volume));
            }
            catch (Exception e)
            {
                Log.Info($"could not play the message chime: {e.Message}");
            }
        });
    }

    /// <summary>发到 COM1 频率上（主消息页的输入）。返回错误说明，成功返回 null。</summary>
    public string? SendRadioText(string text)
    {
        var (recipient, body) = FsdProtocol.ParseDotCommand(text);
        if (recipient is not null)
            return SendText(recipient, body, wallop: true);
        if (Own?.Com1 is not { } com1)
            return "读不到 COM1 频率，消息没有发出去。";
        return SendText(FsdProtocol.FrequencyRecipient(com1), body);
    }

    public string? SendText(string recipient, string body, bool wallop = false)
    {
        if (Fsd is not { Connected: true } fsd)
            return "尚未连接到网络，消息没有发出去。";
        if (body.Trim().Length == 0)
            return wallop ? ".wallop 后面要写内容，例如 .wallop 请求协助" : null;
        if (!fsd.SendText(recipient, body))
            return "消息发送失败。";
        return null;
    }

    public bool Ident()
    {
        if (Fsd is not { Connected: true } fsd)
            return false;
        fsd.Ident();
        return true;
    }

    /// <summary>调 COM1。观察员没开模拟器时就是手输频率。</summary>
    public string? TuneCom(int radio, string text)
    {
        if (Observer.ParseFrequency(text) is not { } frequency)
            return $"频率 {text} 读不出来，写成 121.800 这样。";
        if (_observer || !Sim.Connected)
        {
            if (radio != 1)
                return "没有连模拟器，只能设置 COM1 的手动频率。";
            Settings.ObserverFrequency = Observer.FormatFrequency(frequency);
            Settings.Save();
            Voice?.SetFrequency(CurrentFrequency());
            return null;
        }
        return Sim.TuneCom(radio, frequency) ? null : "调频失败（模拟器没有响应）。";
    }

    public string? SetSquawk(string text)
    {
        if (text.Length != 4 || !text.All(c => c is >= '0' and <= '7'))
            return $"应答机编码 {text} 不对，要四位 0-7 的数字。";
        if (!Sim.Connected)
            return "没有连上模拟器。";
        return Sim.SetSquawk(int.Parse(text)) ? null : "设置应答机失败。";
    }

    public void ApplyAudioSettings()
    {
        VoiceOptions.Denoise = Settings.MicDenoise;
        VoiceOptions.MicGainDb = Settings.MicGainDb;
        Audio.OutputGainDb = Settings.OutputGainDb;
        Audio.ChangeInput(Settings.InputDevice);
        try
        {
            Audio.ChangeOutput(Settings.OutputDevice);
        }
        catch (Exception e)
        {
            Post(MessageKind.Error, e.Message);
        }
        Ptt.SetBindings(Settings.PttBindings ?? []);
    }

    public void Dispose()
    {
        _timer.Dispose();
        if (_connectRequested)
            Disconnect();
        _injectionLoop.Stop();
        _injector?.Dispose();
        Ptt.Dispose();
        Sim.Dispose();
        Audio.Dispose();
    }
}
