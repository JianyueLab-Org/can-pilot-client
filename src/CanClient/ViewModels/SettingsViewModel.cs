using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Threading;
using CanClient.Core;
using CanClient.Core.ModelMatching;
using CanClient.Core.Net;
using CanClient.Core.Ptt;
using CanClient.Core.Settings;
using CanClient.Core.Sim;
using CanClient.Core.Util;
using CanClient.Core.Voice;
using Connections = CanClient.Core.Sim.SimulatorConnection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CanClient.ViewModels;

/// <summary>服务器下拉框里的一项。Host 为空的是"自定义"。</summary>
public sealed record ServerChoice(string Label, string Host, int Port)
{
    public override string ToString() => Label;
}

/// <summary>自定义规则集列表里的一项。</summary>
public sealed record RuleSetItem(string Path, string Display)
{
    public override string ToString() => Display;
}

/// <summary>
/// 设置窗口，内容与 vPilot SettingsForm（单机模式）对应：网络、通知、字体、音频、按键通话、
/// 模型匹配（高级 / 自定义规则）、性能、更新、杂项。打开时从 <see cref="AppSettings"/> 读，
/// 点"应用"/"确定"时写回并立即生效。
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly ClientController _controller;
    private readonly AppSettings _settings;
    /// <summary>
    /// 设置页正在看的那套模型匹配。默认就是他机注入用的那套；连着模拟器时去看别的模拟器的页面，
    /// 换成一个独立副本，免得把注入用的规则换成别的模拟器的（那样所有他机都建不出来）。
    /// </summary>
    private ModelMatchingService _models;
    private bool _loading = true;
    private bool _meterOn;
    private bool _scanDirty;
    private bool _rulesDirty;

    public MainWindowViewModel? ConnectionInfo { get; }
    public bool ShowServerEndpoints { get; } = DebugOptions.Load().ShowServerEndpoints;

    public SettingsViewModel(ClientController controller, MainWindowViewModel? connectionInfo = null)
    {
        ConnectionInfo = connectionInfo;
        _controller = controller;
        _settings = controller.Settings;
        _models = controller.Models;
        CustomRuleSets.CollectionChanged += (_, _) =>
        {
            if (_loading)
                return;
            _rulesDirty = true;
            IsDirty = true;
        };
        Load();
        _loading = false;
        _models.ScanProgressChanged += OnScanProgress;
        _models.ScanCompleted += OnScanCompleted;
        _ = RefreshServersAsync();
        RefreshXPlaneInfo();
        RefreshSimulatorVisibility();
    }

    /// <summary>有未应用的修改时启用“应用”按钮（同 vPilot BtnApply）。</summary>
    [ObservableProperty]
    private bool _isDirty;

    private static readonly HashSet<string> Transient =
    [
        nameof(IsDirty), nameof(IsWaitingForPtt), nameof(PttButtonText), nameof(CanClearPtt), nameof(InputVolumeText),
        nameof(OutputVolumeText), nameof(SelectedModelFolder), nameof(SelectedRuleSet), nameof(InputLevel),
        nameof(LastModelScan), nameof(ModelsFound), nameof(ScanProgressText), nameof(IsScanning), nameof(MatchTestResult),
        nameof(MatchTestCallsign), nameof(MatchTestType), nameof(ServerStatus), nameof(SimulatorName), nameof(PackagesInfo),
        nameof(DataInfo), nameof(SoundVolumeText), nameof(XPlaneStatus), nameof(CslStatus), nameof(CanChangeSimulator),
        nameof(XPlaneNetworkStatus), nameof(CanEnableXPlaneNetwork),
        nameof(ShowMsfs2020), nameof(ShowMsfs2024), nameof(ShowModelMatching), nameof(ShowXPlane), nameof(ShowFsx), nameof(ShowFsxSe),
        nameof(ShowP3dV1), nameof(ShowP3dV2), nameof(ShowP3dV3), nameof(ShowP3dV4), nameof(ShowP3dV5), nameof(ShowP3dV6),
        nameof(SimulatorConnections), nameof(PackagesPathLabel), nameof(PackagesPathPlaceholder), nameof(PttBindings),
        nameof(SelectedPttBinding), nameof(CanRemovePtt)
    ];

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is not null && !Transient.Contains(e.PropertyName))
            IsDirty = true;
    }

    public void MarkSaved() => IsDirty = false;

    private void Load()
    {
        NetworkLogin = _settings.Cid;
        NetworkPassword = _settings.Password;
        RememberPassword = _settings.RememberPassword;
        FullName = _settings.RealName;
        FsdHost = _settings.FsdHostName;
        FsdPort = _settings.FsdPortNumber;
        VoiceHost = _settings.MumbleHostName;
        VoicePort = _settings.MumblePortNumber;
        SetServerChoices(null);
        SimulatorConnection = PlatformSupport.EffectiveSimulatorConnection(_settings.SimulatorConnection);
        ShowAllSimulators = _settings.ShowAllSimulators;
        XPlanePath = _settings.XPlanePath;
        CslPath = _settings.CslPath;
        XPlaneHost = _settings.XPlaneHost;
        XPlanePort = _settings.XPlanePort;

        FlashTaskbarPrivateMessage = _settings.FlashOnPrivateMessage;
        FlashTaskbarRadioMessage = _settings.FlashOnRadioMessage;
        FlashTaskbarSelcal = _settings.FlashOnSelcal;
        FlashTaskbarDisconnected = _settings.FlashOnDisconnect;
        MessageSound = _settings.MessageSound;
        MessageSoundAll = _settings.MessageSoundAll;
        MessageSoundVolume = _settings.MessageSoundVolume;

        ControllerListFont = _settings.ControllerListFont;
        ControllerListFontSize = (decimal)_settings.ControllerListFontSize;
        TextMessagesFont = _settings.TextMessagesFont;
        TextMessagesFontSize = (decimal)_settings.TextMessagesFontSize;

        InputDevices = [AudioDevices.SystemDefault, .. AudioDevices.Inputs().Select(d => d.Name)];
        OutputDevices = [AudioDevices.SystemDefault, .. AudioDevices.Outputs().Select(d => d.Name)];
        InputDeviceName = string.IsNullOrEmpty(_settings.InputDevice) ? AudioDevices.SystemDefault : _settings.InputDevice;
        OutputDeviceName = string.IsNullOrEmpty(_settings.OutputDevice) ? AudioDevices.SystemDefault : _settings.OutputDevice;
        if (!InputDevices.Contains(InputDeviceName))
            InputDevices = [.. InputDevices, InputDeviceName];
        if (!OutputDevices.Contains(OutputDeviceName))
            OutputDevices = [.. OutputDevices, OutputDeviceName];
        MicDenoise = _settings.MicDenoise;
        RangeAudio = _settings.RangeAudio;
        OutputVolumeDb = _settings.OutputGainDb;
        InputVolumeDb = _settings.MicGainDb;

        _bindings = (_settings.PttBindings ?? []).DistinctBy(b => (b.Kind, b.VirtualKey, b.MouseButton, b.Device, b.Button)).ToList();
        UpdatePttText();

        RenderTraffic = _settings.RenderTraffic;
        AircraftVisualRange = _settings.TrafficRangeNm;
        MaxRenderedAircraft = _settings.MaxAircraft;
        CheckForUpdates = _settings.UpdateCheck;
        AutoSquawkModeC = _settings.AutoSquawkModeC;
        KeepWindowVisible = _settings.KeepWindowVisible;
        DebugLog = _settings.Debug;

        PackagesPath = _settings.PackagesPath;
        DefaultModel = _models.DefaultModel;
        LoadModelMatching();
    }

    private void LoadModelMatching()
    {
        var models = _models;
        var simulator = models.Simulator;
        var folderName = ModelMatchingService.ModelFolderName(simulator);
        SimulatorName = simulator.DisplayName();
        DefaultModel = models.DefaultModel;
        PackagesPath = models.Installation?.PackagesPathOverride ?? "";
        PackagesInfo = models.Installation?.PackagesPath is { } path
            ? $"{folderName}：{path}"
            : simulator.IsLegacy()
                ? $"没有找到 {simulator.DisplayName()} 的安装目录（注册表里没有登记，或者目录下没有主程序）"
                : "没有找到 MSFS 的包目录（UserCfg.opt 里的 InstalledPackagesPath）";
        PackagesPathLabel = simulator.IsLegacy()
            ? $"{simulator.DisplayName()} 安装目录（含 {(simulator.IsFsx() ? "FSX.exe" : "Prepar3D.exe")} 的那一层，留空则读注册表）"
            : "MSFS 包目录（含 Official 和 Community 的那一层，留空则读 UserCfg.opt）";
        PackagesPathPlaceholder = simulator.IsLegacy() ? "自动（注册表）" : "自动";
        DataInfo = models.Data.IsEmpty
            ? "没有读到 ModelMatchingData.bin，只按 aircraft.cfg 匹配"
            : $"模型数据 {models.Data.Serial}：{models.Data.ModelSpecs.Count:N0} 个已知模型，{models.Data.TypeCodes.Count:N0} 个机型";
        UpdateScanSummary();

        ModelFolders.Clear();
        if (models.ScanResult is { } scan)
        {
            foreach (var folder in scan.Folders.OrderBy(f => f.Priority))
                ModelFolders.Add(ModelFolderNode.From(folder, () => { _scanDirty = true; IsDirty = true; }));
        }

        CustomRuleSets.Clear();
        foreach (var ruleSet in models.Config.CustomRuleSets)
            CustomRuleSets.Add(DescribeRuleSet(ruleSet));
    }

    private RuleSetItem DescribeRuleSet(string path)
    {
        var status = _models.Matcher.RuleSetStatus.FirstOrDefault(s => s.Path == path);
        var package = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "";
        var detail = status is null ? "" : status.Error is null ? $"{status.RuleCount:N0} 条规则" : $"读取失败：{status.Error}";
        return new RuleSetItem(path, $"{System.IO.Path.GetFileName(path)}  ·  {package}  {detail}");
    }

    private void UpdateScanSummary()
    {
        var scan = _models.ScanResult;
        LastModelScan = scan is null ? "从未扫描" : scan.ScannedOn.ToString("yyyy 年 M 月 d 日 HH:mm");
        ModelsFound = scan is null
            ? "—"
            : $"找到 {scan.ModelCount:N0} 个模型，已识别 {scan.IdentifiedModelCount:N0} 个（{scan.Folders.Count} 个包）";
        IsScanning = _models.Scanning;
        ScanProgressText = _models.Progress?.Stage ?? "";
    }

    private void OnScanProgress(ScanProgress progress) => Dispatcher.UIThread.Post(() =>
    {
        IsScanning = true;
        ScanProgressText = progress.Stage;
        ScanPercent = progress.Total > 0 ? 100.0 * progress.Done / progress.Total : 0;
    });

    private void OnScanCompleted(string summary) => Dispatcher.UIThread.Post(() =>
    {
        var wasLoading = _loading;
        _loading = true;
        ScanProgressText = summary;
        LoadModelMatching();
        IsScanning = false;
        _loading = wasLoading;
    });

    /// <summary>把改动写回设置并生效。</summary>
    public void Apply()
    {
        _settings.Cid = NetworkLogin.Trim();
        _settings.Password = NetworkPassword;
        _settings.RememberPassword = RememberPassword;
        _settings.RealName = FullName.Trim();
        _settings.FsdHostName = FsdHost.Trim();
        _settings.FsdPortNumber = FsdPort;
        _settings.MumbleHostName = VoiceHost.Trim();
        _settings.MumblePortNumber = VoicePort;
        // macOS / Linux 只有 X-Plane，不改写配置里的接入模式，配置拷回 Windows 还是原样
        if (!_controller.IsConnected && SupportsMsfs) _settings.SimulatorConnection = SimulatorConnection;
        _settings.ShowAllSimulators = ShowAllSimulators;
        _settings.XPlanePath = XPlanePath.Trim();
        _settings.CslPath = CslPath.Trim();
        _settings.XPlaneHost = XPlaneHost.Trim();
        _settings.XPlanePort = XPlanePort;

        _settings.FlashOnPrivateMessage = FlashTaskbarPrivateMessage;
        _settings.FlashOnRadioMessage = FlashTaskbarRadioMessage;
        _settings.FlashOnSelcal = FlashTaskbarSelcal;
        _settings.FlashOnDisconnect = FlashTaskbarDisconnected;
        _settings.MessageSound = MessageSound;
        _settings.MessageSoundAll = MessageSoundAll;
        _settings.MessageSoundVolume = MessageSoundVolume;

        _settings.ControllerListFont = ControllerListFont ?? "";
        _settings.ControllerListFontSize = (double)ControllerListFontSize;
        _settings.TextMessagesFont = TextMessagesFont ?? "";
        _settings.TextMessagesFontSize = (double)TextMessagesFontSize;

        _settings.InputDevice = InputDeviceName == AudioDevices.SystemDefault ? "" : InputDeviceName ?? "";
        _settings.OutputDevice = OutputDeviceName == AudioDevices.SystemDefault ? "" : OutputDeviceName ?? "";
        _settings.MicDenoise = MicDenoise;
        _settings.RangeAudio = RangeAudio;
        _settings.OutputGainDb = OutputVolumeDb;
        _settings.MicGainDb = InputVolumeDb;
        _settings.PttBindings = _bindings.ToList();

        _settings.RenderTraffic = RenderTraffic;
        _settings.TrafficRangeNm = (int)AircraftVisualRange;
        _settings.MaxAircraft = (int)MaxRenderedAircraft;
        _settings.UpdateCheck = CheckForUpdates;
        _settings.AutoSquawkModeC = AutoSquawkModeC;
        _settings.KeepWindowVisible = KeepWindowVisible;
        _settings.Debug = DebugLog;
        Core.Util.Log.Level = DebugLog || Program.Debug ? Core.Util.LogLevel.Debug : Core.Util.LogLevel.Info;

        // 模型目录是按模拟器各存一份的：当前页面属于哪个模拟器就写到哪个
        var simulator = _models.Simulator;
        var packagesChanged = simulator.UsesSimConnect() && (PackagesPath ?? "").Trim() != _settings.SimulatorPathFor(simulator);
        if (packagesChanged)
            _settings.SetSimulatorPath(simulator, PackagesPath ?? "");
        _settings.Save();
        _controller.ApplyAudioSettings();

        var models = _models;
        if ((DefaultModel ?? "").Trim() != models.DefaultModel)
            models.DefaultModel = (DefaultModel ?? "").Trim();
        if (_scanDirty && models.ScanResult is { } scan)
        {
            _scanDirty = false;
            for (var i = 0; i < ModelFolders.Count; i++)
                ModelFolders[i].ApplyTo(i + 1);
            models.ApplyScanPreferences();
        }
        if (_rulesDirty)
        {
            _rulesDirty = false;
            models.SetCustomRuleSets(CustomRuleSets.Select(r => r.Path).ToList());
        }
        if (packagesChanged)
            _ = models.PackagesPathChangedAsync();
        MarkSaved();
    }

    // —— 网络 ——
    /// <summary>
    /// “模拟器接入”的可选项：Auto 总有；MSFS、FSX/P3D、X-Plane 只列出检测到安装的（或者开了“显示所有模拟器”），
    /// 当前选着的那一项不管装没装都留着。macOS / Linux 只有 X-Plane。
    /// </summary>
    [ObservableProperty] private IReadOnlyList<string> _simulatorConnections = PlatformSupport.SupportsMsfs
        ? [Connections.Auto, Connections.MsfsOnly, Connections.LegacyOnly, Connections.XPlaneOnly]
        : [Connections.XPlaneOnly];
    /// <summary>MSFS / FSX / P3D 相关的页面和选项只在 Windows 上显示；macOS / Linux 只有 X-Plane。</summary>
    public bool SupportsMsfs => PlatformSupport.SupportsMsfs;
    [ObservableProperty] private bool _showAllSimulators;
    [ObservableProperty] private bool _showMsfs2020;
    [ObservableProperty] private bool _showMsfs2024;
    [ObservableProperty] private bool _showXPlane;
    [ObservableProperty] private bool _showFsx;
    [ObservableProperty] private bool _showFsxSe;
    [ObservableProperty] private bool _showP3dV1;
    [ObservableProperty] private bool _showP3dV2;
    [ObservableProperty] private bool _showP3dV3;
    [ObservableProperty] private bool _showP3dV4;
    [ObservableProperty] private bool _showP3dV5;
    [ObservableProperty] private bool _showP3dV6;
    /// <summary>至少显示了一个用 SimConnect 模型匹配的模拟器：“高级规则”“自定义规则”跟着它显示。</summary>
    [ObservableProperty] private bool _showModelMatching;
    [ObservableProperty] private string _packagesPathLabel = "";
    [ObservableProperty] private string _packagesPathPlaceholder = "自动";
    private readonly HashSet<SimulatorType> _installedSimulators = [];
    private bool _xplaneInstalled;
    partial void OnShowAllSimulatorsChanged(bool value) => UpdateSimulatorVisibility();

    /// <summary>设置页导航里的模拟器页面：Tag → 模拟器。</summary>
    public static SimulatorType SimulatorForTag(string tag) => tag switch
    {
        "Msfs2020" => SimulatorType.Msfs,
        "Msfs2024" => SimulatorType.Msfs2024,
        _ => Enum.TryParse<SimulatorType>(tag, out var simulator) && simulator.IsLegacy() ? simulator : SimulatorType.None
    };

    private void RefreshSimulatorVisibility()
    {
        _installedSimulators.Clear();
        if (SupportsMsfs)
        {
            _installedSimulators.UnionWith(SimulatorInstallations.Installed());
            // 便携安装：设置里手动指定过、目录也还在的 FSX / P3D 算装了，关掉“显示所有”后页面不会消失
            foreach (var simulator in SimulatorTypeExtensions.Legacy)
            {
                var path = _settings.SimulatorPathFor(simulator);
                if (path.Length > 0 && Directory.Exists(path))
                    _installedSimulators.Add(simulator);
            }
        }
        // 正在连着的模拟器肯定装了（检测不到的便携版之类）
        if (_controller.Sim.Simulator != SimulatorType.None)
            _installedSimulators.Add(_controller.Sim.Simulator);
        _xplaneInstalled = XPlaneInstallation.IsXPlane(XPlanePath) || XPlaneInstallation.FindInstalls().Length > 0 || _controller.Sim.Simulator == SimulatorType.XPlane;
        UpdateSimulatorVisibility();
    }

    private bool ShowSimulator(SimulatorType simulator) =>
        SupportsMsfs && (ShowAllSimulators || _installedSimulators.Contains(simulator));

    private void UpdateSimulatorVisibility()
    {
        ShowMsfs2020 = ShowSimulator(SimulatorType.Msfs);
        ShowMsfs2024 = ShowSimulator(SimulatorType.Msfs2024);
        ShowFsx = ShowSimulator(SimulatorType.Fsx);
        ShowFsxSe = ShowSimulator(SimulatorType.FsxSe);
        ShowP3dV1 = ShowSimulator(SimulatorType.Prepar3d);
        ShowP3dV2 = ShowSimulator(SimulatorType.Prepar3dV2);
        ShowP3dV3 = ShowSimulator(SimulatorType.Prepar3dV3);
        ShowP3dV4 = ShowSimulator(SimulatorType.Prepar3dV4);
        ShowP3dV5 = ShowSimulator(SimulatorType.Prepar3dV5);
        ShowP3dV6 = ShowSimulator(SimulatorType.Prepar3dV6);
        // X-Plane 是 macOS / Linux 唯一的模拟器，页面总是显示
        ShowXPlane = !SupportsMsfs || ShowAllSimulators || _xplaneInstalled;
        var anyMsfs = ShowMsfs2020 || ShowMsfs2024;
        var anyLegacy = ShowFsx || ShowFsxSe || ShowP3dV1 || ShowP3dV2 || ShowP3dV3 || ShowP3dV4 || ShowP3dV5 || ShowP3dV6;
        ShowModelMatching = anyMsfs || anyLegacy;

        if (!SupportsMsfs)
            return;
        var current = SimulatorConnection;
        var connections = new List<string> { Connections.Auto };
        if (anyMsfs || current == Connections.MsfsOnly) connections.Add(Connections.MsfsOnly);
        if (anyLegacy || current == Connections.LegacyOnly) connections.Add(Connections.LegacyOnly);
        if (ShowXPlane || current == Connections.XPlaneOnly) connections.Add(Connections.XPlaneOnly);
        if (!connections.Contains(current)) connections.Add(current);
        if (!SimulatorConnections.SequenceEqual(connections))
            SimulatorConnections = connections;
    }

    // 换选项列表时下拉框可能把选中项清成 null，别让它把设置清掉
    partial void OnSimulatorConnectionChanged(string? oldValue, string newValue)
    {
        if (newValue is null)
            SimulatorConnection = oldValue ?? Connections.Auto;
    }

    /// <summary>切到某个模拟器的模型匹配页面。</summary>
    public async Task SelectModelSettingsAsync(SimulatorType simulator)
    {
        // 切换模型配置前保存当前页面，防止另一模拟器的规则/目录被覆盖。
        if (IsDirty) Apply();
        var live = _controller.Sim.Simulator;
        var models = live.UsesSimConnect() && simulator != live
            ? _models != _controller.Models ? _models : _controller.Models.CreateDetached()
            : _controller.Models;
        if (models != _models)
        {
            _models.ScanProgressChanged -= OnScanProgress;
            _models.ScanCompleted -= OnScanCompleted;
            _models = models;
            _models.ScanProgressChanged += OnScanProgress;
            _models.ScanCompleted += OnScanCompleted;
        }
        await _models.SetSimulatorAsync(simulator);
        var loading = _loading;
        _loading = true;
        LoadModelMatching();
        _loading = loading;
    }
    public bool CanChangeSimulator => !_controller.IsConnected;
    [ObservableProperty] private string _simulatorConnection = "Auto";
    [ObservableProperty] private string _xPlanePath = "";
    [ObservableProperty] private string _cslPath = "";
    [ObservableProperty] private string _xPlaneHost = "";
    [ObservableProperty] private int _xPlanePort = 49000;
    [ObservableProperty] private string _xPlaneStatus = "";
    [ObservableProperty] private string _cslStatus = "";

    [ObservableProperty] private string _xPlaneNetworkStatus = "";
    [ObservableProperty] private bool _canEnableXPlaneNetwork;

    private void RefreshXPlaneInfo()
    {
        XPlaneStatus = XPlaneInstallation.IsXPlane(XPlanePath) ? "已找到 X-Plane 安装目录" : "尚未选择 X-Plane 安装目录";
        CslStatus = string.IsNullOrWhiteSpace(CslPath) ? "尚未选择 CSL 模型库" : "CSL 目录：" + CslPath;
        RefreshXPlaneNetwork();
    }

    /// <summary>X-Plane 12 新装默认关着“接受传入连接”，关着时客户端收不到任何机位数据。</summary>
    private string[] XPlaneRootsWithNetworkingOff() =>
        XPlaneInstallation.Roots(XPlanePath).Where(root => XPlaneInstallation.IncomingNetworkingDisabled(root) == true).ToArray();

    private void RefreshXPlaneNetwork()
    {
        CanEnableXPlaneNetwork = XPlaneRootsWithNetworkingOff().Length > 0;
        XPlaneNetworkStatus = CanEnableXPlaneNetwork
            ? "X-Plane 的“接受传入连接”（设置 → 网络 → Accept incoming connections）是关着的，客户端收不到机位数据。请在 X-Plane 里打开，或退出 X-Plane 后点“开启 X-Plane 网络连接”。"
            : "";
    }

    [RelayCommand]
    private async Task EnableXPlaneNetworkAsync()
    {
        try
        {
            var roots = XPlaneRootsWithNetworkingOff();
            await Task.Run(() => { foreach (var root in roots) XPlaneInstallation.EnableIncomingNetworking(root); });
            RefreshXPlaneNetwork();
            if (!CanEnableXPlaneNetwork) XPlaneNetworkStatus = "已开启 X-Plane 的“接受传入连接”，启动 X-Plane 后客户端会自动连接。";
        }
        catch (Exception e) { XPlaneNetworkStatus = "开启失败：" + e.Message; }
    }

    [RelayCommand]
    private async Task DiscoverXPlaneAsync()
    {
        var found = await Task.Run(() =>
        {
            var root = XPlaneInstallation.IsXPlane(XPlanePath) ? XPlanePath : XPlaneInstallation.FindInstalls().FirstOrDefault() ?? "";
            return (Root: root, Csl: XPlaneInstallation.FindCsl(root));
        });
        XPlanePath = found.Root;
        if (found.Csl.Length > 0) CslPath = found.Csl;
        RefreshXPlaneInfo();
        RefreshSimulatorVisibility();
    }

    [RelayCommand]
    private async Task ScanCslAsync()
    {
        CslStatus = "正在扫描 CSL……";
        var models = await Task.Run(() => XPlaneInstallation.ScanCsl(CslPath));
        CslStatus = $"找到 {models.Count:N0} 个 CSL 模型，运行时由原生插件匹配机型 / 航司 / 涂装。";
    }

    [RelayCommand]
    private async Task InstallXPlanePluginAsync()
    {
        try
        {
            await Task.Run(() => XPlaneInstallation.InstallPlugin(XPlanePath));
            XPlaneStatus = "XPC_for_CAN 插件安装完成，请重启 X-Plane。";
            RefreshXPlaneNetwork();
        }
        catch (Exception e) { XPlaneStatus = "安装失败：" + e.Message; }
    }

    [ObservableProperty] private string _networkLogin = "";
    [ObservableProperty] private string _networkPassword = "";
    [ObservableProperty] private bool _rememberPassword;
    [ObservableProperty] private string _fullName = "";
    [ObservableProperty] private string _fsdHost = "";
    [ObservableProperty] private int _fsdPort;
    [ObservableProperty] private string _voiceHost = "";
    [ObservableProperty] private int _voicePort;
    [ObservableProperty] private IReadOnlyList<ServerChoice> _fsdServers = [];
    [ObservableProperty] private IReadOnlyList<ServerChoice> _voiceServers = [];
    [ObservableProperty] private ServerChoice? _selectedFsdServer;
    [ObservableProperty] private ServerChoice? _selectedVoiceServer;
    [ObservableProperty] private string _serverStatus = "正在获取服务器列表……";

    [RelayCommand]
    private async Task RefreshServersAsync()
    {
        ServerStatus = "正在获取服务器列表……";
        var list = await ServerDirectory.FetchAsync();
        Dispatcher.UIThread.Post(() =>
        {
            var wasLoading = _loading;
            _loading = true;
            ServerStatus = list is null ? "服务器列表获取失败，保留当前可选服务器，可稍后重试。"
                : ShowServerEndpoints ? "选择服务器，或填写调试地址和端口。" : "请选择 FSD 和语音服务器。";
            SetServerChoices(list);
            SelectedFsdServer = FsdServers.FirstOrDefault(s => s.Host == FsdHost && s.Port == FsdPort) ?? FsdServers[0];
            SelectedVoiceServer = VoiceServers.FirstOrDefault(s => s.Host == VoiceHost && s.Port == VoicePort) ?? VoiceServers[0];
            _loading = wasLoading;
        });
    }

    private void SetServerChoices(ServerList? list)
    {
        IReadOnlyList<ServerChoice> Choices(IReadOnlyList<ServerEntry>? rows, string host, int port, string defaultHost, int defaultPort)
        {
            var choices = rows?.Select(s => new ServerChoice(
                ShowServerEndpoints ? s.Label : s.Name + (s.Location.Length > 0 ? " · " + s.Location : ""), s.Host, s.Port)).ToList() ?? [];
            if (choices.Count == 0) choices.Add(new("CAN 默认服务器", defaultHost, defaultPort));
            if (ShowServerEndpoints) choices.Insert(0, new("自定义（调试）", "", 0));
            if (!choices.Any(s => s.Host == host && s.Port == port))
                choices.Add(new(ShowServerEndpoints ? $"当前配置 ({host}:{port})" : "当前已配置服务器", host, port));
            return choices;
        }
        FsdServers = Choices(list?.Fsd, FsdHost, FsdPort, AppSettings.FsdHost, AppSettings.FsdPort);
        VoiceServers = Choices(list?.Audio, VoiceHost, VoicePort, AppSettings.MumbleHost, AppSettings.MumblePort);
        SelectedFsdServer = FsdServers.FirstOrDefault(s => s.Host == FsdHost && s.Port == FsdPort);
        SelectedVoiceServer = VoiceServers.FirstOrDefault(s => s.Host == VoiceHost && s.Port == VoicePort);
    }

    partial void OnSelectedFsdServerChanged(ServerChoice? value)
    {
        if (value is { Host.Length: > 0 })
        {
            FsdHost = value.Host;
            FsdPort = value.Port;
        }
    }

    partial void OnSelectedVoiceServerChanged(ServerChoice? value)
    {
        if (value is { Host.Length: > 0 })
        {
            VoiceHost = value.Host;
            VoicePort = value.Port;
        }
    }

    // —— 通知 ——
    [ObservableProperty] private bool _flashTaskbarPrivateMessage;
    [ObservableProperty] private bool _flashTaskbarRadioMessage;
    [ObservableProperty] private bool _flashTaskbarSelcal;
    [ObservableProperty] private bool _flashTaskbarDisconnected;
    [ObservableProperty] private bool _messageSound;
    [ObservableProperty] private bool _messageSoundAll;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SoundVolumeText))]
    private int _messageSoundVolume;

    public string SoundVolumeText => $"{MessageSoundVolume}%";

    [RelayCommand]
    private void PreviewChime()
    {
        var old = _settings.MessageSoundVolume;
        _settings.MessageSoundVolume = MessageSoundVolume;
        _controller.PlayChime(force: true);
        _settings.MessageSoundVolume = old;
    }

    // —— 字体 ——
    public IReadOnlyList<string> FontFamilies { get; } =
        ["", .. FontManager.Current.SystemFonts.Select(f => f.Name).Order(StringComparer.CurrentCulture)];
    [ObservableProperty] private string? _controllerListFont = "";
    [ObservableProperty] private decimal _controllerListFontSize = 12;
    [ObservableProperty] private string? _textMessagesFont = "";
    [ObservableProperty] private decimal _textMessagesFontSize = 12;

    // —— 音频 ——
    [ObservableProperty] private IReadOnlyList<string> _inputDevices = [];
    [ObservableProperty] private IReadOnlyList<string> _outputDevices = [];
    [ObservableProperty] private string? _inputDeviceName;
    [ObservableProperty] private string? _outputDeviceName;
    [ObservableProperty] private bool _micDenoise;
    [ObservableProperty] private bool _rangeAudio;
    public bool DenoiseAvailable { get; } = Denoiser.Available;
    public string DenoiseNote => DenoiseAvailable ? "" : "（没有找到 rnnoise.dll，降噪不可用）";

    /// <summary>输出音量，单位 dB（vPilot 滑块范围 -18 ~ +18）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputVolumeText))]
    private double _outputVolumeDb;

    /// <summary>麦克风音量，单位 dB（vPilot 滑块范围 -60 ~ +18）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputVolumeText))]
    private double _inputVolumeDb;

    public string OutputVolumeText => ((int)OutputVolumeDb).ToString("+#;-#;0");
    public string InputVolumeText => ((int)InputVolumeDb).ToString("+#;-#;0");

    /// <summary>麦克风峰值电平（0~100），音频页打开时实时刷新。</summary>
    [ObservableProperty] private double _inputLevel;

    private Limiter? _meterLimiter;
    private DateTime _lastMeter;

    /// <summary>音频页显示时开始测电平，离开时停。</summary>
    public void SetMeter(bool on)
    {
        if (on == _meterOn)
            return;
        _meterOn = on;
        var audio = _controller.Audio;
        if (on)
        {
            _meterLimiter = new Limiter(AudioEngine.SampleRate);
            audio.FrameCaptured += OnMeterFrame;
            try
            {
                audio.AcquireCapture(InputDeviceName == AudioDevices.SystemDefault ? "" : InputDeviceName ?? "");
            }
            catch (Exception e)
            {
                audio.FrameCaptured -= OnMeterFrame;
                _meterOn = false;
                Core.Util.Log.For("audio").Warning($"level meter: {e.Message}");
            }
        }
        else
        {
            audio.FrameCaptured -= OnMeterFrame;
            audio.ReleaseCapture();
            InputLevel = 0;
        }
    }

    partial void OnInputDeviceNameChanged(string? value)
    {
        if (_meterOn)
            _controller.Audio.ChangeInput(value == AudioDevices.SystemDefault ? "" : value ?? "");
    }

    private void OnMeterFrame(short[] frame)
    {
        // 语音连着的话发送链路已经处理过一遍了；这里按当前滑块自己算一遍，只为了显示
        var copy = (short[])frame.Clone();
        _meterLimiter?.Process(copy, MicGain.DbToLinear(InputVolumeDb));
        var peak = MicGain.Peak(copy);
        if ((DateTime.UtcNow - _lastMeter).TotalMilliseconds < 50)
            return;
        _lastMeter = DateTime.UtcNow;
        // 电平表按 dB 显示：-60 dBFS 是空，0 dBFS 满
        var db = peak > 0 ? 20 * Math.Log10(peak) : -60;
        var level = Math.Clamp((db + 60) / 60 * 100, 0, 100);
        Dispatcher.UIThread.Post(() => InputLevel = level);
    }

    // —— 按键通话（SettingsPanelPtt） ——
    private List<PttBinding> _bindings = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PttButtonText), nameof(CanClearPtt))]
    private string _pttAssignment = "无";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PttButtonText), nameof(CanClearPtt), nameof(CanRemovePtt))]
    private bool _isWaitingForPtt;

    public string PttButtonText => IsWaitingForPtt ? "取消录制" : "添加 PTT 按键或按钮";
    public ObservableCollection<PttBinding> PttBindings { get; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemovePtt))]
    private PttBinding? _selectedPttBinding;
    public bool CanRemovePtt => !IsWaitingForPtt && SelectedPttBinding is not null;
    public bool CanClearPtt => !IsWaitingForPtt && _bindings.Count > 0;

    private void UpdatePttText()
    {
        PttBindings.Clear();
        foreach (var binding in _bindings) PttBindings.Add(binding);
        PttAssignment = _bindings.Count == 0 ? "无" : $"已设置 {_bindings.Count} 个 PTT，任意一个按住即可发话";
        OnPropertyChanged(nameof(CanClearPtt));
        OnPropertyChanged(nameof(CanRemovePtt));
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SetPtt()
    {
        if (IsWaitingForPtt)
        {
            _controller.Ptt.CancelCapture();
            return;
        }
        IsWaitingForPtt = true;
        try
        {
            var binding = await _controller.Ptt.CaptureAsync();
            if (binding is not null)
            {
                if (!_bindings.Any(b => b.Matches(binding))) _bindings.Add(binding);
                UpdatePttText();
            }
        }
        finally
        {
            IsWaitingForPtt = false;
        }
    }

    [RelayCommand]
    private void ClearPtt()
    {
        _bindings.Clear();
        UpdatePttText();
    }

    [RelayCommand]
    private void RemovePtt()
    {
        if (IsWaitingForPtt || SelectedPttBinding is not { } selected) return;
        _bindings.Remove(selected);
        SelectedPttBinding = null;
        UpdatePttText();
    }

    // —— 模型匹配（SettingsPanelModelMatching） ——
    [ObservableProperty] private string _simulatorName = "";
    [ObservableProperty] private string _packagesInfo = "";
    [ObservableProperty] private string _dataInfo = "";
    [ObservableProperty] private string? _packagesPath = "";
    [ObservableProperty] private string _lastModelScan = "";
    [ObservableProperty] private string _modelsFound = "";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanProgressText = "";
    [ObservableProperty] private double _scanPercent;
    [ObservableProperty] private string? _defaultModel = "";
    [ObservableProperty] private string _matchTestCallsign = "CES5101";
    [ObservableProperty] private string _matchTestType = "A320";
    [ObservableProperty] private string _matchTestResult = "";

    [RelayCommand]
    private void Rescan()
    {
        IsScanning = true;
        ScanProgressText = "正在查找 SimObjects 文件夹……";
        _ = _models.RescanAsync();
    }

    [RelayCommand]
    private void ResetDefaultModel() => DefaultModel = _models.Simulator.DefaultModel();

    [RelayCommand]
    private void TestMatch()
    {
        if (string.IsNullOrWhiteSpace(MatchTestCallsign))
            return;
        var result = _models.Match(MatchTestCallsign, MatchTestType);
        var installed = result.IsDefault || _models.Matcher.IsInstalled(result.ModelName) ? "" : "（未在扫描结果中）";
        MatchTestResult = $"→ {result.ModelName}{installed}\n   {result.Description} · 来源：{result.Source}" +
                          (result.Rule is null ? "" : $" · 规则：{result.Rule}");
    }

    // —— 模型匹配 · 高级（SettingsPanelAdvancedModelMatching） ——
    public ObservableCollection<ModelFolderNode> ModelFolders { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveFolderUpCommand), nameof(MoveFolderDownCommand))]
    private ModelFolderNode? _selectedModelFolder;

    private bool CanMoveFolderUp() => SelectedModelFolder is { IsTopLevel: true } f && ModelFolders.IndexOf(f) > 0;
    private bool CanMoveFolderDown() => SelectedModelFolder is { IsTopLevel: true } f && ModelFolders.IndexOf(f) is >= 0 and var i && i < ModelFolders.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveFolderUp))]
    private void MoveFolderUp()
    {
        MoveSelected(ModelFolders, SelectedModelFolder, -1, MoveFolderUpCommand, MoveFolderDownCommand);
        _scanDirty = true;
    }

    [RelayCommand(CanExecute = nameof(CanMoveFolderDown))]
    private void MoveFolderDown()
    {
        MoveSelected(ModelFolders, SelectedModelFolder, +1, MoveFolderUpCommand, MoveFolderDownCommand);
        _scanDirty = true;
    }

    // —— 模型匹配 · 自定义规则（SettingsPanelCustomRuleSets） ——
    public ObservableCollection<RuleSetItem> CustomRuleSets { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveRuleSetUpCommand), nameof(MoveRuleSetDownCommand), nameof(RemoveRuleSetCommand))]
    private RuleSetItem? _selectedRuleSet;

    private bool CanMoveRuleSetUp() => SelectedRuleSet is { } r && CustomRuleSets.IndexOf(r) > 0;
    private bool CanMoveRuleSetDown() => SelectedRuleSet is { } r && CustomRuleSets.IndexOf(r) is >= 0 and var i && i < CustomRuleSets.Count - 1;
    private bool CanRemoveRuleSet() => SelectedRuleSet is not null;

    [RelayCommand(CanExecute = nameof(CanMoveRuleSetUp))]
    private void MoveRuleSetUp() => MoveSelected(CustomRuleSets, SelectedRuleSet, -1, MoveRuleSetUpCommand, MoveRuleSetDownCommand);

    [RelayCommand(CanExecute = nameof(CanMoveRuleSetDown))]
    private void MoveRuleSetDown() => MoveSelected(CustomRuleSets, SelectedRuleSet, +1, MoveRuleSetUpCommand, MoveRuleSetDownCommand);

    [RelayCommand(CanExecute = nameof(CanRemoveRuleSet))]
    private void RemoveRuleSet()
    {
        if (SelectedRuleSet is not null)
            CustomRuleSets.Remove(SelectedRuleSet);
    }

    public void AddRuleSets(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (CustomRuleSets.Any(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                var count = ModelMatchRuleSet.Load(path).Count;
                var package = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "";
                CustomRuleSets.Add(new RuleSetItem(path, $"{System.IO.Path.GetFileName(path)}  ·  {package}  {count:N0} 条规则"));
            }
            catch (Exception e)
            {
                CustomRuleSets.Add(new RuleSetItem(path, $"{System.IO.Path.GetFileName(path)}  读取失败：{e.Message}"));
            }
        }
    }

    // —— 性能（SettingsPanelPerformance） ——
    [ObservableProperty] private bool _renderTraffic;
    [ObservableProperty] private decimal _aircraftVisualRange = 60;
    [ObservableProperty] private decimal _maxRenderedAircraft = 40;

    // —— 更新 ——
    [ObservableProperty] private bool _checkForUpdates;
    public string VersionText => $"当前版本 {Core.Util.AppVersion.Display}";

    // —— 杂项 ——
    [ObservableProperty] private bool _autoSquawkModeC;
    [ObservableProperty] private bool _keepWindowVisible;
    [ObservableProperty] private bool _debugLog;

    [RelayCommand]
    private static void OpenLogFolder() => Core.Util.Log.OpenFolder();

    private static void MoveSelected<T>(ObservableCollection<T> list, T? item, int offset,
        IRelayCommand up, IRelayCommand down)
    {
        if (item is null)
            return;
        var index = list.IndexOf(item);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= list.Count)
            return;
        list.Move(index, target);
        up.NotifyCanExecuteChanged();
        down.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        SetMeter(false);
        if (IsWaitingForPtt)
            _controller.Ptt.CancelCapture();
        _models.ScanProgressChanged -= OnScanProgress;
        _models.ScanCompleted -= OnScanCompleted;
    }
}

/// <summary>
/// “高级”页中带复选框的模型树：包 → 飞机目录 → 涂装。取消勾选就不参与自动生成的规则；
/// 涂装后面标出识别状态和推出来的机型/航司。
/// </summary>
public sealed partial class ModelFolderNode : ObservableObject
{
    private readonly Action _changed;
    private ModelScanResultFolder? _folder;
    private ModelScanResultSubfolder? _subfolder;
    private ModelScanResultModel? _model;

    private ModelFolderNode(string name, string detail, bool isChecked, Action changed)
    {
        Name = name;
        Detail = detail;
        _isChecked = isChecked;
        _changed = changed;
    }

    public string Name { get; }
    public string Detail { get; }
    public ObservableCollection<ModelFolderNode> Children { get; } = [];
    public bool IsTopLevel => _folder is not null;

    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => _changed();

    public static ModelFolderNode From(ModelScanResultFolder folder, Action changed)
    {
        var node = new ModelFolderNode(folder.RelativePath,
            $"{folder.ModelCount:N0} 个模型，已识别 {folder.IdentifiedModelCount:N0}", !folder.IsExcluded, changed)
        {
            _folder = folder
        };
        foreach (var sub in folder.Subfolders)
        {
            var subNode = new ModelFolderNode(System.IO.Path.GetFileName(sub.RelativePath),
                $"{sub.ModelCount}", !sub.IsExcluded, changed) { _subfolder = sub };
            foreach (var model in sub.Models)
            {
                var status = model.Status switch
                {
                    ModelStatus.Known => "已知",
                    ModelStatus.Derived => "推导",
                    ModelStatus.Ignored => "忽略",
                    _ => "未识别"
                };
                var what = model.DerivedTypeCodes is null ? "" : $" {model.DerivedTypeCodes}{(model.DerivedAirlines is null ? "" : "/" + model.DerivedAirlines)}";
                subNode.Children.Add(new ModelFolderNode(model.Title ?? "(无标题)", status + what, !model.IsExcluded, changed)
                {
                    _model = model
                });
            }
            node.Children.Add(subNode);
        }
        return node;
    }

    /// <summary>把勾选状态写回扫描结果；priority 只对顶层有效。</summary>
    public void ApplyTo(int priority)
    {
        if (_folder is not null)
        {
            _folder.IsExcluded = !IsChecked;
            _folder.Priority = priority;
        }
        if (_subfolder is not null)
            _subfolder.IsExcluded = !IsChecked;
        if (_model is not null)
            _model.IsExcluded = !IsChecked;
        foreach (var child in Children)
            child.ApplyTo(0);
    }

    public override string ToString() => Name;
}
