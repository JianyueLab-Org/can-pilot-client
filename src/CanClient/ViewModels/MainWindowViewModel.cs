using System.Collections.ObjectModel;
using CanClient.Core.ModelMatching;
using Avalonia.Threading;
using CanClient.Core;
using CanClient.Core.Fsd;
using CanClient.Core.Net;
using CanClient.Core.Sim;
using CanClient.Core.Util;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CanClient.ViewModels;

/// <summary>
/// 主界面状态。布局参考 vPilot 的 FullInterfaceForm：顶部操作按钮、呼号与 COM1/COM2，
/// 左侧范围内管制员，右侧消息标签页。所有数据来自 <see cref="ClientController"/>，
/// 它的事件在后台线程上触发，这里一律 Post 回 UI 线程。
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly ClientController _controller;

    public MainWindowViewModel(ClientController controller)
    {
        _controller = controller;
        Com1 = new RadioViewModel("COM1", "---.---");
        Com2 = new RadioViewModel("COM2", "---.---") { IsTransmitEnabled = false };

        foreach (var name in ControllerGroup.Names)
            ControllerGroups.Add(new ControllerGroup(name));

        MainTab = CreateTab("消息", isClosable: false);
        NoteTab = CreateTab("Note", isClosable: false, isLocalNote: true);
        SelectedTab = MainTab;
        Callsign = controller.Settings.Callsign;
        TypeCode = controller.Settings.Aircraft;

        controller.MessageAdded += m => Ui(() => OnMessage(m));
        controller.ConnectionChanged += () => Ui(OnConnectionChanged);
        controller.ControllersChanged += list => Ui(() => OnControllers(list));
        controller.TransmittingChanged += v => Ui(() => UpdateTransmitIndicators(v));
        controller.RadioReceivingChanged += (radio, v) => Ui(() =>
        {
            if (radio == 1) Com1.IsReceiving = v;
            else if (radio == 2) Com2.IsReceiving = v;
        });
        controller.OwnStateChanged += s => Ui(() => OnOwnState(s));
        controller.ModeCChanged += v => Ui(() => SetModeCFromController(v));
        controller.FsdStatusChanged += (state, message) => Ui(() =>
        {
            _fsdLinkState = state;
            FsdConnectionDetail = message;
            RefreshConnectionStatus();
        });
        controller.VoiceStatusChanged += (state, message) => Ui(() =>
        {
            _voiceLinkState = state;
            VoiceConnectionDetail = message;
            RefreshConnectionStatus();
        });
        controller.SimStateChanged += (connected, _) => Ui(() => SimConnected = connected);
        controller.Sim.XPlane.ProblemChanged += problem => Ui(() =>
        {
            XPlaneProblem = problem;
            if (problem is not null) AddSystem(problem);
        });
        controller.AttentionRequested += reason => Ui(() => AttentionRequested?.Invoke(reason));
        controller.Models.ScanProgressChanged += p => Ui(() => ScanStatus = p.Stage);
        controller.Models.ScanCompleted += _ => Ui(() => ScanStatus = "");

        AddSystem($"欢迎使用 {AppVersion.ClientName} {AppVersion.Display}。");
        if (controller.Settings.PasswordMigrated)
            AddSystem("旧版本把网络密码明文存在了配置文件里，已经从文件中清除（本次仍可连接）。需要的话可以在设置里勾选“记住密码”。");
        AddSystem("点击“连接”登录网络；输入 .help 查看可用命令。");
    }

    private static void Ui(Action action) => Dispatcher.UIThread.Post(action);

    /// <summary>闪任务栏的请求，窗口那边处理。</summary>
    public event Action<string>? AttentionRequested;

    /// <summary>需要弹出更新提示。</summary>
    public event Action<UpdateInfo>? UpdateFound;

    public ClientController Controller => _controller;
    public RadioViewModel Com1 { get; }
    public RadioViewModel Com2 { get; }

    public ObservableCollection<ControllerGroup> ControllerGroups { get; } = [];
    public ObservableCollection<ChatTabViewModel> Tabs { get; } = [];
    public ChatTabViewModel MainTab { get; }
    public ChatTabViewModel NoteTab { get; }

    [ObservableProperty] private ChatTabViewModel? _selectedTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText), nameof(CallsignText))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CallsignText))]
    private string _callsign = string.Empty;

    [ObservableProperty] private string _typeCode = string.Empty;
    [ObservableProperty] private bool _isModeC = true;
    [ObservableProperty] private bool _isIdent;
    [ObservableProperty] private bool _showSimulatorChoice;
    [ObservableProperty] private bool _canChooseSimulator;
    [ObservableProperty] private IReadOnlyList<string> _runningSimulatorChoices = [];
    [ObservableProperty] private string? _selectedRunningSimulator;
    private bool _updatingSimulatorChoice;

    partial void OnSelectedRunningSimulatorChanged(string? value)
    {
        if (_updatingSimulatorChoice || value is null) return;
        var simulator = _controller.Sim.AvailableSimulators.FirstOrDefault(s => s.DisplayName() == value);
        if (simulator != SimulatorType.None) _controller.Sim.ChooseSimulator(simulator);
    }

    private void RefreshSimulatorChoice()
    {
        var sim = _controller.Sim;
        ShowSimulatorChoice = sim.MultipleSimulators;
        CanChooseSimulator = !_controller.IsConnected;
        var choices = sim.AvailableSimulators.Select(s => s.DisplayName()).ToArray();
        _updatingSimulatorChoice = true;
        if (!RunningSimulatorChoices.SequenceEqual(choices)) RunningSimulatorChoices = choices;
        SelectedRunningSimulator = sim.NeedsSelection || !ShowSimulatorChoice ? null : sim.Simulator.DisplayName();
        _updatingSimulatorChoice = false;
    }
    [ObservableProperty] private string _fsdConnectionText = "FSD：未连接";
    [ObservableProperty] private string _voiceConnectionText = "语音：未连接";
    [ObservableProperty] private string _fsdConnectionDetail = "尚未连接 FSD 服务器";
    [ObservableProperty] private string _voiceConnectionDetail = "尚未连接语音服务器";
    private string _fsdLinkState = "Stopped";
    private string _voiceLinkState = "Stopped";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _simConnected;
    [ObservableProperty] private string _transponderCode = "----";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _scanStatus = "";

    /// <summary>发现了 X-Plane 但收不到数据的原因，完整说明同时发在消息区。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _xPlaneProblem;

    /// <summary>呼号旁边那行小字：模型扫描进度优先，其次是模拟器连没连上。</summary>
    public string StatusText => ScanStatus.Length > 0 ? ScanStatus
        : SimConnected ? $"{_controller.Sim.ApplicationName}"
        : XPlaneProblem is not null ? "X-Plane 没有数据（见消息）" : "等待模拟器";
    [ObservableProperty] private string _windowTitle = $"{AppVersion.ClientName} {AppVersion.Display}";

    private bool _suppressModeC;

    public string ConnectButtonText => IsConnected ? "断开" : "连接";
    public string CallsignText => IsConnected && Callsign.Length > 0 ? Callsign : "--------";

    public void Started()
    {
        if (_controller.Settings.UpdateCheck)
            _ = CheckForUpdateAsync(manual: false);
    }

    // ---------- 连接 ----------

    public string? Connect(string callsign, string typeCode, bool observer)
    {
        var problem = _controller.Connect(callsign, typeCode, observer);
        if (problem is not null)
            return problem;
        Callsign = observer && callsign.Trim().Length == 0 ? "观察员" : callsign.Trim().ToUpperInvariant();
        TypeCode = typeCode.Trim().ToUpperInvariant();
        IsConnected = true;
        return null;
    }

    public void Disconnect() => _controller.Disconnect();

    private void OnConnectionChanged()
    {
        IsConnected = _controller.IsConnected;
        if (!IsConnected)
        {
            _fsdLinkState = "Stopped";
            _voiceLinkState = "Stopped";
            FsdConnectionDetail = "FSD 连接已断开";
            VoiceConnectionDetail = "语音连接已断开";
        }
        RefreshConnectionStatus();
        if (!IsConnected)
        {
            IsIdent = false;
            Com1.IsTransmitting = false;
            Com1.IsReceiving = false;
            Com2.IsTransmitting = false;
            Com2.IsReceiving = false;
            foreach (var group in ControllerGroups)
                group.Stations.Clear();
            RefreshGroups();
        }
    }

    partial void OnIsModeCChanged(bool value)
    {
        if (!_suppressModeC)
            _controller.SetModeC(value);
    }

    private void SetModeCFromController(bool value)
    {
        _suppressModeC = true;
        IsModeC = value;
        _suppressModeC = false;
    }

    [RelayCommand]
    private void Ident()
    {
        if (IsIdent)
            return;
        if (!_controller.Ident())
        {
            AddError("尚未连接到网络，IDENT 无效。");
            return;
        }
        IsIdent = true;
    }

    private void OnOwnState(OwnAircraftState? state)
    {
        RefreshSimulatorChoice();
        RefreshConnectionStatus();
        IsIdent = _controller.IsIdentifying;
        UpdateTransmitIndicators(_controller.Voice?.Transmitting ?? false);
        SimConnected = _controller.Sim.Connected;
        if (state is null)
        {
            if (!(_controller.IsObserver && _controller.Settings.ObserverFrequency.Length > 0))
                Com1.Frequency = "---.---";
            else
                Com1.Frequency = _controller.Settings.ObserverFrequency;
            Com2.Frequency = "---.---";
            return;
        }
        Com1.Frequency = _controller.IsObserver && Observer.ParseFrequency(_controller.Settings.ObserverFrequency) is { } manual
            ? manual.ToString("F3")
            : state.Com1?.ToString("F3") ?? "---.---";
        Com2.Frequency = state.Com2?.ToString("F3") ?? "---.---";
        TransponderCode = state.Squawk.ToString("D4");
    }

    private void UpdateTransmitIndicators(bool transmitting)
    {
        var radio = _controller.TransmitRadio;
        // 先熄灭旧灯，切换 TX 时也不会出现两个同时亮起。
        Com1.IsTransmitting = false;
        Com2.IsTransmitting = false;
        Com1.IsTransmitEnabled = radio == 1;
        Com2.IsTransmitEnabled = radio == 2;
        if (transmitting && radio == 1) Com1.IsTransmitting = true;
        else if (transmitting && radio == 2) Com2.IsTransmitting = true;
    }

    private void RefreshConnectionStatus()
    {
        var fsd = _controller.Fsd;
        var voice = _controller.Voice;
        FsdConnectionText = _controller.IsConnected && _controller.IsObserver
            ? "FSD：观察员模式"
            : _controller.IsConnected && !_controller.Settings.ConnectFsd ? "FSD：未启用"
            : $"FSD：{ConnectionStateText(fsd?.Connected == true ? "Online" : _fsdLinkState == "Online" && fsd is not null ? "Reconnecting" : _fsdLinkState, fsd is not null)}";
        var state = ConnectionStateText(voice?.Connected == true ? "Online" : _voiceLinkState == "Online" && voice is not null ? "Reconnecting" : _voiceLinkState, voice is not null);
        VoiceConnectionText = _controller.IsConnected && !_controller.IsObserver && !_controller.Settings.ConnectVoice
            ? "语音：未启用"
            : $"语音：{state}" + (voice?.Connected == true ? $" · {voice.TransportDescription}" : "");
    }

    internal static string ConnectionStateText(string state, bool present) => state switch
    {
        "Online" => present ? "已连接" : "已断开",
        "Connecting" => "连接中",
        "Reconnecting" => "重连中",
        "Error" => "连接错误",
        "Offline" => "已下线",
        "Denied" => present ? "服务器拒绝操作" : "已断开",
        _ => present ? "连接中" : "未连接"
    };

    // ---------- 管制员 ----------

    private void OnControllers(IReadOnlyList<ControllerInfo> list)
    {
        foreach (var group in ControllerGroups)
            group.Stations.Clear();
        foreach (var info in list.OrderBy(c => c.Callsign, StringComparer.Ordinal))
            ControllerGroups[ControllerGroup.IndexFor(info)].Stations
                .Add(new ControllerStation(info.Callsign, info.Frequency, info.RealName));
        RefreshGroups();
    }

    private void RefreshGroups()
    {
        // ControllerGroup.Header 依赖 Stations.Count，重建集合以刷新树。
        var groups = ControllerGroups.ToList();
        ControllerGroups.Clear();
        foreach (var group in groups)
            ControllerGroups.Add(group);
    }

    [RelayCommand]
    private void OpenPrivateChat(ControllerStation? station)
    {
        if (station is not null)
            SelectedTab = GetOrCreatePrivateTab(station.Callsign);
    }

    [RelayCommand]
    private void TuneCom1(ControllerStation? station)
    {
        if (station is null)
            return;
        var problem = _controller.TuneCom(1, station.Frequency);
        if (problem is not null)
            AddError(problem);
        else
            AddSystem($"COM1 已调谐至 {station.Frequency}（{station.Callsign}）");
    }

    [RelayCommand]
    private void RequestAtis(ControllerStation? station)
    {
        if (station is null)
            return;
        if (_controller.Fsd?.RequestAtis(station.Callsign) != true)
            AddError("尚未连接到网络。");
    }

    // ---------- 标签页 ----------

    public void CloseTab(ChatTabViewModel tab)
    {
        if (!tab.IsClosable)
            return;
        Tabs.Remove(tab);
        if (SelectedTab is null || SelectedTab == tab)
            SelectedTab = MainTab;
    }

    public void ClearMessages() => SelectedTab?.Messages.Clear();

    partial void OnSelectedTabChanged(ChatTabViewModel? value)
    {
        if (value is not null)
            value.HasUnread = false;
    }

    private ChatTabViewModel CreateTab(string header, bool isClosable, bool isLocalNote = false)
    {
        var tab = new ChatTabViewModel(header, isClosable, isLocalNote);
        tab.Submitted += OnTabSubmitted;
        Tabs.Add(tab);
        return tab;
    }

    private ChatTabViewModel GetOrCreatePrivateTab(string callsign)
        => Tabs.FirstOrDefault(t => t.IsClosable && t.Header.Equals(callsign, StringComparison.OrdinalIgnoreCase))
           ?? CreateTab(callsign.ToUpperInvariant(), isClosable: true);

    private void OnMessage(ClientMessage message)
    {
        var chat = new ChatMessage(message.Kind, message.Sender, message.Text);
        if (message.PrivateWith is { } who)
        {
            var tab = GetOrCreatePrivateTab(who);
            tab.Add(chat);
            if (SelectedTab != tab)
                tab.HasUnread = true;
            return;
        }
        MainTab.Add(chat);
    }

    private void OnTabSubmitted(ChatTabViewModel tab, string text)
    {
        if (tab.IsLocalNote)
        {
            // 在命令解析和所有网络路径之前拦截，连 .wallop / .com1 也只作为笔记文字。
            tab.Add(new ChatMessage(MessageKind.System, string.Empty, text));
            return;
        }
        if (text.StartsWith('.') && !text.StartsWith(".wallop", StringComparison.OrdinalIgnoreCase))
        {
            HandleCommand(text);
            return;
        }

        string? problem;
        string shown;
        if (tab.IsClosable)
        {
            problem = _controller.SendText(tab.Header, text);
            shown = text;
        }
        else
        {
            problem = _controller.SendRadioText(text);
            var (recipient, body) = FsdProtocol.ParseDotCommand(text);
            shown = recipient is not null ? $"（督导）{body}" : text;
        }
        if (problem is not null)
            tab.Add(new ChatMessage(MessageKind.Error, string.Empty, problem));
        else
            tab.Add(new ChatMessage(MessageKind.Sent, _controller.Callsign, shown));
    }

    private void HandleCommand(string text)
    {
        var parts = text[1..].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var command = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

        switch (command)
        {
            case "com1" or "com2" when parts.Length > 1:
            {
                var radio = command == "com2" ? 2 : 1;
                var problem = _controller.TuneCom(radio, parts[1]);
                if (problem is null)
                    AddSystem($"{command.ToUpperInvariant()} 已调谐至 {parts[1]}");
                else
                    AddError(problem);
                break;
            }
            case "x" or "xpdr" or "sq" when parts.Length > 1:
            {
                var problem = _controller.SetSquawk(parts[1]);
                if (problem is null)
                    AddSystem($"应答机已设置为 {parts[1]}");
                else
                    AddError(problem);
                break;
            }
            case "chat" when parts.Length > 1:
                SelectedTab = GetOrCreatePrivateTab(parts[1]);
                break;
            case "msg" when parts.Length > 2:
            {
                var tab = GetOrCreatePrivateTab(parts[1]);
                SelectedTab = tab;
                var problem = _controller.SendText(tab.Header, parts[2]);
                tab.Add(problem is null
                    ? new ChatMessage(MessageKind.Sent, _controller.Callsign, parts[2])
                    : new ChatMessage(MessageKind.Error, "", problem));
                break;
            }
            case "atis" when parts.Length > 1:
                if (_controller.Fsd?.RequestAtis(parts[1]) != true)
                    AddError("尚未连接到网络。");
                break;
            case "metar" when parts.Length > 1:
                _ = MetarAsync(parts[1]);
                break;
            case "match" when parts.Length > 2:
            {
                // 模型匹配测试：.match <呼号> <机型>
                var result = _controller.Models.Match(parts[1], parts[2]);
                AddSystem($"{parts[1].ToUpperInvariant()} {parts[2].ToUpperInvariant()} → {result.ModelName}" +
                          $"（{result.Description}，规则来源：{result.Source}{(result.Rule is null ? "" : "，" + result.Rule)}）");
                break;
            }
            case "clear":
                ClearMessages();
                break;
            case "help":
                AddSystem("可用命令：.com1 <频率>  .com2 <频率>  .x <应答机>  .chat <呼号>  .msg <呼号> <内容>  " +
                          ".atis <呼号>  .metar <机场>  .wallop <内容>  .match <呼号> <机型>  .clear");
                break;
            default:
                AddError($"未知命令：{text}");
                break;
        }
    }

    private async Task MetarAsync(string icao)
    {
        var fsd = _controller.Fsd;
        if (fsd is null)
        {
            AddError("尚未连接到网络。");
            return;
        }
        var metar = await fsd.RequestMetarAsync(icao, TimeSpan.FromSeconds(20));
        Ui(() =>
        {
            if (metar is null)
                AddError($"没有拿到 {icao.ToUpperInvariant()} 的 METAR。");
            else
                MainTab.Add(new ChatMessage(MessageKind.Server, "METAR", metar));
        });
    }

    public void AddSystem(string text) => MainTab.Add(new ChatMessage(MessageKind.System, string.Empty, text));
    public void AddError(string text) => MainTab.Add(new ChatMessage(MessageKind.Error, string.Empty, text));

    // ---------- 飞行计划 ----------

    public FlightPlanViewModel CreateFlightPlan() =>
        FlightPlanViewModel.FromJson(_controller.Settings.FlightPlan, _controller.Settings.Aircraft);

    public void FileFlightPlan(FlightPlanViewModel plan)
    {
        _controller.Settings.FlightPlan = plan.ToJson();
        _controller.Settings.Save();
        if (_controller.Fsd is { Connected: true } fsd && fsd.FileFlightPlan(plan.ToPlan()))
            AddSystem($"飞行计划已提交：{plan.Departure.ToUpperInvariant()} → {plan.Arrival.ToUpperInvariant()}");
        else
            AddSystem("尚未连接到网络，飞行计划只保存在本地。");
    }

    // ---------- 更新 ----------

    public async Task CheckForUpdateAsync(bool manual)
    {
        var update = await UpdateChecker.CheckAsync(_controller.Settings.UpdateUrl);
        Ui(() =>
        {
            if (update is null)
            {
                if (manual)
                    AddSystem($"已经是最新版本（{AppVersion.Display}）。");
                return;
            }
            AddSystem($"[版本提醒] 当前版本 {AppVersion.Display}，有新版本 {update.Version}，请通过独立更新器更新。");
            UpdateFound?.Invoke(update);
        });
    }
}
