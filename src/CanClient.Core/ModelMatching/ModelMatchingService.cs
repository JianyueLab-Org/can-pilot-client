using CanClient.Core.Settings;
using CanClient.Core.Util;

namespace CanClient.Core.ModelMatching;

/// <summary>
/// 模型匹配的总管：读 ModelMatchingData、找 MSFS 安装、扫描（带缓存）、自动发现规则集、生成规则、
/// 匹配。界面和他机注入都只跟这个类打交道。
///
/// 启动时和每次连上模拟器时都会核对一遍缓存的扫描结果，飞机装了、删了、改了就自动重扫——
/// 和 vPilot 一样，用户不用记得去点"重新扫描"。
/// </summary>
public sealed class ModelMatchingService
{
    private static readonly Logger Log = Util.Log.For("models");

    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private CancellationTokenSource? _scanCancellation;
    private readonly bool _detached;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly object _dataSync = new();
    private Task? _dataLoaded;

    public ModelMatchingService(AppSettings settings) : this(settings, detached: false)
    {
    }

    private ModelMatchingService(AppSettings settings, bool detached)
    {
        _settings = settings;
        _detached = detached;
        Matcher = new ModelMatcher(ModelMatchingData.Empty);
        Matcher.RulesChanged += () => RulesChanged?.Invoke();
    }

    /// <summary>
    /// 一个不影响他机注入的副本：设置页查看/编辑"另一个"模拟器的模型匹配时用。
    /// 共用同一份设置，但不会把正连着的模拟器的规则和扫描结果换掉，也不改 LastSimulator。
    /// </summary>
    public ModelMatchingService CreateDetached()
    {
        var copy = new ModelMatchingService(_settings, detached: true) { Data = Data, _dataLoaded = Task.CompletedTask };
        copy.Matcher.SetData(Data);
        return copy;
    }

    public ModelMatchingData Data { get; private set; } = ModelMatchingData.Empty;
    public ModelMatcher Matcher { get; }
    public SimulatorType Simulator { get; private set; } = SimulatorType.None;
    public ISimulatorInstallation? Installation { get; private set; }
    public ModelScanResult? ScanResult { get; private set; }
    public bool Scanning { get; private set; }
    public ScanProgress? Progress { get; private set; }

    public event Action<ScanProgress>? ScanProgressChanged;
    /// <summary>扫描结束（成功或失败），带一句给用户看的总结。</summary>
    public event Action<string>? ScanCompleted;
    public event Action? RulesChanged;
    /// <summary>要在消息区告诉用户的事（发现了新的规则集之类）。</summary>
    public event Action<string>? Notification;

    public ModelMatchingSettings Config => _settings.MatchingFor(Simulator);

    public string DefaultModel
    {
        get => Matcher.DefaultModel;
        set
        {
            Config.DefaultModel = value;
            Matcher.DefaultModel = string.IsNullOrWhiteSpace(value) ? Simulator.DefaultModel() : value.Trim();
            _settings.Save();
        }
    }

    /// <summary>启动时调一次：读数据库、挑一个模拟器、核对缓存。</summary>
    public async Task InitializeAsync()
    {
        await EnsureDataLoaded().ConfigureAwait(false);
        var installed = SimulatorInstallations.Installed().ToArray();
        var simulator = installed.Contains(_settings.LastSimulator) ? _settings.LastSimulator : installed.FirstOrDefault();
        if (simulator == SimulatorType.None) return;
        // 启动时模拟器可能已经连上了（Sim.Opened 先到）：那就以连着的为准，别再换成上次用的那个
        await SetSimulatorGatedAsync(simulator, forceValidate: false, startupGuess: true).ConfigureAwait(false);
    }

    /// <summary>读一次 ModelMatchingData；启动和"先连上了模拟器"谁先来谁触发，另一个等着。</summary>
    private Task EnsureDataLoaded()
    {
        lock (_dataSync)
            return _dataLoaded ??= Task.Run(LoadData);
    }

    private void LoadData()
    {
        var path = AppPaths.ModelMatchingDataFile;
        try
        {
            Data = ModelMatchingData.LoadCompressed(path);
            Log.Info($"model matching data {Data.Serial}: {Data.ModelSpecs.Count} known models, {Data.TypeCodes.Count} type codes, " +
                     $"{Data.SimilarTypeCodes.Count} similar groups");
        }
        catch (Exception e)
        {
            Log.Warning($"could not read {path}: {e.Message}; matching will only use the aircraft.cfg files");
            Data = ModelMatchingData.Empty;
        }
        Matcher.SetData(Data);
    }

    /// <summary>连上了某个模拟器（或启动时猜的那个）。换了模拟器就换一套扫描结果和规则。</summary>
    public async Task SetSimulatorAsync(SimulatorType simulator, bool forceValidate = false) =>
        await SetSimulatorGatedAsync(simulator, forceValidate, startupGuess: false).ConfigureAwait(false);

    private async Task SetSimulatorGatedAsync(SimulatorType simulator, bool forceValidate, bool startupGuess)
    {
        if (!simulator.UsesSimConnect())
            return;
        // 数据库没读完就生成规则，认不出机型；两次切换交错执行，规则和默认模型会混成两个模拟器的
        await EnsureDataLoaded().ConfigureAwait(false);
        await _switchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (startupGuess && Simulator != SimulatorType.None)
                return;
            await SwitchAsync(simulator, forceValidate).ConfigureAwait(false);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    private async Task SwitchAsync(SimulatorType simulator, bool forceValidate)
    {
        if (simulator == Simulator && !forceValidate && ScanResult is not null)
            return;
        Simulator = simulator;
        if (!_detached && _settings.LastSimulator != simulator)
        {
            _settings.LastSimulator = simulator;
            _settings.Save();
        }
        Matcher.SetSimulator(simulator);
        var config = Config;
        Matcher.DefaultModel = string.IsNullOrWhiteSpace(config.DefaultModel) ? simulator.DefaultModel() : config.DefaultModel;
        Matcher.ClearErroneous(config.ErroneousModels);
        var configuredPath = _settings.SimulatorPathFor(simulator);
        // 老版本只有一个 packages_path，两个 MSFS 共用
        if (simulator.IsMsfs() && string.IsNullOrWhiteSpace(configuredPath))
            configuredPath = _settings.PackagesPath;
        var installation = SimulatorInstallations.Create(simulator, configuredPath)!;
        Installation = installation;

        var cached = ModelScanStore.Load(simulator);
        string? reason;
        if (installation.IsValid)
            reason = await Task.Run(() => ModelScanStore.NeedsRescan(cached, installation)).ConfigureAwait(false);
        else
            reason = cached is null ? $"找不到 {simulator.DisplayName()} 的{ModelFolderName(simulator)}" : null;

        ScanResult = cached;
        Matcher.LoadCustomRuleSets(config.CustomRuleSets, raise: false);
        Matcher.GenerateRules(cached, raise: false);
        RulesChanged?.Invoke();

        if (reason is null)
        {
            Log.Info($"using the cached model scan for {simulator}: {cached?.ModelCount ?? 0} models");
            DiscoverRuleSets();
            return;
        }
        Log.Info($"{simulator} needs a model scan: {reason}");
        if (installation.IsValid)
        {
            Notification?.Invoke($"需要扫描已安装的飞机模型（{reason}），正在后台扫描……");
            _ = RescanAsync();
        }
        else
            Notification?.Invoke($"没有找到 {simulator.FullName()} 的{ModelFolderName(simulator)}，他机无法匹配模型。可以在设置的“{simulator.DisplayName()}”页面里手动指定。");
    }

    /// <summary>设置里手动指定的那个目录叫什么：MSFS 是包目录，FSX / P3D 是安装目录。</summary>
    public static string ModelFolderName(SimulatorType simulator) => simulator.IsMsfs() ? "包目录" : "安装目录";

    /// <summary>重新扫描。正在扫的话等它扫完再来一遍。</summary>
    public async Task RescanAsync()
    {
        var installation = Installation;
        if (installation is null)
            return;
        _scanCancellation?.Cancel();
        await _scanGate.WaitAsync().ConfigureAwait(false);
        var cts = new CancellationTokenSource();
        _scanCancellation = cts;
        Scanning = true;
        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                Progress = p;
                ScanProgressChanged?.Invoke(p);
            });
            // Progress<T> 会捕获同步上下文；在后台线程上建的就是线程池回调
            var previous = ScanResult;
            var result = await Task.Run(() => ModelScanner.Scan(installation, Data, progress, cts.Token), cts.Token)
                .ConfigureAwait(false);
            ModelScanStore.CarryOverPreferences(result, previous);
            ModelScanStore.Save(result);
            ScanResult = result;

            // 重扫之后清空拉黑名单（vPilot 也是这样）：装了新包，以前建不出来的可能已经好了
            Config.ErroneousModels.Clear();
            Matcher.ClearErroneous();
            if (!result.IsEmpty && !Matcher.IsInstalled(Matcher.DefaultModel) && !result.ContainsTitle(Matcher.DefaultModel))
            {
                Config.DefaultModel = "";
                Matcher.DefaultModel = Simulator.DefaultModel();
            }
            _settings.Save();
            // 新发现的规则集在 DiscoverRuleSets 里已经读过了，别再把十几万条规则读第二遍
            if (!DiscoverRuleSets(raise: false))
                Matcher.LoadCustomRuleSets(Config.CustomRuleSets, raise: false);
            Matcher.GenerateRules(result);
            var summary = $"模型扫描完成：找到 {result.ModelCount:N0} 个模型，已识别 {result.IdentifiedModelCount:N0} 个（{result.Folders.Count} 个包）。";
            ScanCompleted?.Invoke(summary);
        }
        catch (OperationCanceledException)
        {
            Log.Info("the model scan was cancelled");
        }
        catch (Exception e)
        {
            Log.Error($"the model scan failed: {e}");
            ScanCompleted?.Invoke($"模型扫描失败：{e.Message}");
        }
        finally
        {
            Scanning = false;
            Progress = null;
            _scanGate.Release();
        }
    }

    /// <summary>
    /// 在已安装的包里找 .vmr 规则集（FSLTL、AIG 等会随包带一个），没加过、也没被用户移除过的自动加上。
    /// 这是 vPilot 没有的：vPilot 要用户自己去"自定义规则"里添加。
    /// </summary>
    /// <returns>有没有新加规则集（加了的话规则已经重新读过）。</returns>
    public bool DiscoverRuleSets(bool raise = true)
    {
        var installation = Installation;
        if (installation is null || !installation.IsValid)
            return false;
        var config = Config;
        var added = new List<string>();
        foreach (var path in installation.FindRuleSets())
        {
            if (config.CustomRuleSets.Contains(path, StringComparer.OrdinalIgnoreCase)
                || config.DismissedRuleSets.Contains(path, StringComparer.OrdinalIgnoreCase))
                continue;
            config.CustomRuleSets.Add(path);
            added.Add(path);
        }
        if (added.Count == 0)
            return false;
        _settings.Save();
        Matcher.LoadCustomRuleSets(config.CustomRuleSets, raise);
        foreach (var path in added)
        {
            var status = Matcher.RuleSetStatus.FirstOrDefault(s => s.Path == path);
            var package = Path.GetFileName(Path.GetDirectoryName(path));
            Notification?.Invoke(status?.Error is null
                ? $"发现并启用了 {package} 的模型匹配规则集 {Path.GetFileName(path)}（{status?.RuleCount ?? 0:N0} 条规则）。"
                : $"发现了规则集 {path}，但读不了：{status.Error}");
        }
        return true;
    }

    /// <summary>设置页改了自定义规则集列表（顺序、增删）。</summary>
    public void SetCustomRuleSets(IReadOnlyList<string> paths)
    {
        var config = Config;
        foreach (var removed in config.CustomRuleSets.Except(paths, StringComparer.OrdinalIgnoreCase))
        {
            if (!config.DismissedRuleSets.Contains(removed, StringComparer.OrdinalIgnoreCase))
                config.DismissedRuleSets.Add(removed);
        }
        config.DismissedRuleSets.RemoveAll(d => paths.Contains(d, StringComparer.OrdinalIgnoreCase));
        config.CustomRuleSets = paths.ToList();
        _settings.Save();
        Matcher.LoadCustomRuleSets(config.CustomRuleSets);
    }

    /// <summary>重新读自定义规则集（文件在外面改过）。</summary>
    public void ReloadRules()
    {
        Matcher.LoadCustomRuleSets(Config.CustomRuleSets, raise: false);
        Matcher.GenerateRules(ScanResult);
    }

    /// <summary>"高级"页里改了排除和优先级：存下来、重新生成规则。</summary>
    public void ApplyScanPreferences()
    {
        if (ScanResult is null)
            return;
        ModelScanStore.NormalisePriorities(ScanResult);
        ModelScanStore.Save(ScanResult);
        Matcher.GenerateRules(ScanResult);
    }

    /// <summary>包目录改了：重新找安装、重新核对。</summary>
    public Task PackagesPathChangedAsync() => SetSimulatorAsync(Simulator, forceValidate: true);

    public ModelMatchResult Match(string callsign, string? typeCode, string? previousModel = null) =>
        Matcher.Match(callsign, typeCode, previousModel);

    /// <summary>模拟器建不出这个模型：记进拉黑名单（存盘），之后不再用它。</summary>
    /// <returns>是不是第一次拉黑这个模型。</returns>
    public bool MarkErroneous(string model)
    {
        var added = Matcher.MarkErroneous(model);
        var config = Config;
        if (!config.ErroneousModels.Contains(model, StringComparer.OrdinalIgnoreCase))
        {
            config.ErroneousModels.Add(model);
            _settings.Save();
        }
        return added;
    }
}
