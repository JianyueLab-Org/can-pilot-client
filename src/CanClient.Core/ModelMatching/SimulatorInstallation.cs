using CanClient.Core.Util;

namespace CanClient.Core.ModelMatching;

/// <summary>
/// 一套能扫描飞机模型的模拟器安装（vPilot Type_02000072）。MSFS 按包的 layout.json 找配置，
/// FSX / P3D 按 SimObjects 路径列表找，扫描、缓存核对和规则生成走同一套代码。
/// </summary>
public interface ISimulatorInstallation
{
    SimulatorType SimulatorType { get; }
    string Name { get; }

    /// <summary>用户在设置里手动指定的目录，没指定是 null。</summary>
    string? PackagesPathOverride { get; }

    /// <summary>扫描根目录：MSFS 的包目录、FSX / P3D 的安装目录。找不到是 null。</summary>
    string? PackagesPath { get; }

    bool IsValid { get; }

    /// <summary>所有飞机配置文件，PackageRelativePath 相同的归为扫描结果里的一组。</summary>
    List<AircraftConfigFile> AircraftConfigFiles();

    /// <summary>随包附带的 .vmr 规则集。</summary>
    List<string> FindRuleSets();
}

/// <summary>这台机器上装了哪些模拟器，设置页决定显示哪些页面、模型匹配启动时挑默认模拟器都用它。</summary>
public static class SimulatorInstallations
{
    private static readonly Lazy<IReadOnlyList<SimulatorType>> Detected = new(() =>
        PlatformSupport.SupportsMsfs
            ? MsfsInstallation.InstalledSimulators().Concat(LegacyInstallation.InstalledSimulators()).ToArray()
            : []);

    /// <summary>检测到的 MSFS / FSX / P3D（X-Plane 另外检测）。结果在进程内缓存。</summary>
    public static IReadOnlyList<SimulatorType> Installed() => Detected.Value;

    /// <summary>为某个模拟器建一份安装描述。overridePath 是设置里手动指定的目录。</summary>
    public static ISimulatorInstallation? Create(SimulatorType simulator, string? overridePath) => simulator switch
    {
        _ when simulator.IsMsfs() => new MsfsInstallation(simulator, overridePath),
        _ when simulator.IsLegacy() => new LegacyInstallation(simulator, overridePath),
        _ => null
    };
}
