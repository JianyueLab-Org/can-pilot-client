using System.Text.Json;
using System.Text.RegularExpressions;
using CanClient.Core.Util;

namespace CanClient.Core.ModelMatching;

/// <summary>一份飞机配置：包内的虚拟路径（SimObjects\Airplanes\X\aircraft.cfg）和磁盘上的真实路径。</summary>
public sealed record AircraftConfigFile(string VirtualPath, string FullPath, string PackageRelativePath, string PackageFullPath);

/// <summary>
/// 一套 MSFS 安装，vPilot Type_02000070（MSFS 2020 / 2024 共用）。
///
/// **不在磁盘上乱走一遍找 aircraft.cfg**，而是照 MSFS 自己的虚拟文件系统来：
///
///   1. 从 UserCfg.opt 读 InstalledPackagesPath（唯一靠谱的来源，安装时可以改到任何地方）；
///   2. 读每个包的 layout.json，只取 SimObjects 下的 aircraft.cfg / sim.cfg；
///   3. Official、Official2020、Official2024 下是 两层目录/包；Community2024 和 Community 下是一层；
///      Community 里和 Community2024 同名的包跳过（2024 用的是后者）；
///   4. 同一个虚拟路径后出现的覆盖先出现的——Community 覆盖 Official，和模拟器的行为一致。
///
/// 这样扫出来的就是模拟器真正能生成的那些，附加件、地景里的配置一个都不会混进来。
/// </summary>
public sealed class MsfsInstallation : ISimulatorInstallation
{
    private static readonly Lazy<IReadOnlyList<SimulatorType>> DetectedInstallations = new(() =>
        new[] { SimulatorType.Msfs2024, SimulatorType.Msfs }
            .Where(type => new MsfsInstallation(type).IsInstalled).ToArray());
    private static readonly Logger Log = Util.Log.For("models");

    // 这些 SimObjects 子目录里的东西不是飞机
    private static readonly string[] ExcludedSimObjectFolders =
        ["Humans", "Landmarks", "Animals", "Boats", "Misc", "GroundVehicles"];

    private static readonly string[] OfficialFolders = ["Official", "Official2020", "Official2024"];

    private readonly List<string> _userConfigFiles = new();

    public MsfsInstallation(SimulatorType simulatorType, string? packagesPathOverride = null)
    {
        SimulatorType = simulatorType;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        switch (simulatorType)
        {
            case SimulatorType.Msfs:
                _userConfigFiles.Add(Path.Combine(local, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"));
                _userConfigFiles.Add(Path.Combine(roaming, "Microsoft Flight Simulator", "UserCfg.opt"));
                break;
            case SimulatorType.Msfs2024:
                _userConfigFiles.Add(Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"));
                _userConfigFiles.Add(Path.Combine(roaming, "Microsoft Flight Simulator 2024", "UserCfg.opt"));
                break;
            default:
                throw new NotSupportedException($"Simulator type {simulatorType} is not supported.");
        }
        PackagesPathOverride = string.IsNullOrWhiteSpace(packagesPathOverride) ? null : packagesPathOverride.Trim();
        Validate();
    }

    public SimulatorType SimulatorType { get; }
    public string Name => SimulatorType.FullName();
    public string? PackagesPathOverride { get; }

    /// <summary>InstalledPackagesPath，读不到就是 null。</summary>
    public string? PackagesPath { get; private set; }

    public bool IsValid => PackagesPath is not null;

    /// <summary>侧栏安装检测：配置残留和手工填过的共享目录不能单独证明对应版本已安装。</summary>
    public bool IsInstalled => _userConfigFiles.Any(file =>
        ReadInstalledPackagesPath(file) is { } path && HasVersionPackages(path, SimulatorType));

    internal static bool HasVersionPackages(string path, SimulatorType simulator) => simulator switch
    {
        SimulatorType.Msfs => Directory.Exists(Path.Combine(path, "Official")) &&
            !Directory.Exists(Path.Combine(path, "Official2024")),
        SimulatorType.Msfs2024 => Directory.Exists(Path.Combine(path, "Official2024")) ||
            Directory.Exists(Path.Combine(path, "Community2024")),
        _ => false
    };

    /// <summary>读 UserCfg.opt。用户在设置里手动指定了包目录的话以那个为准。</summary>
    private void Validate()
    {
        if (PackagesPathOverride is not null)
        {
            if (Directory.Exists(PackagesPathOverride))
            {
                PackagesPath = PackagesPathOverride;
                return;
            }
            Log.Warning($"the configured package directory {PackagesPathOverride} does not exist, falling back to UserCfg.opt");
        }

        foreach (var file in _userConfigFiles)
        {
            var path = ReadInstalledPackagesPath(file);
            if (path is null)
                continue;
            PackagesPath = path;
            Log.Info($"{Name}: InstalledPackagesPath = {path} (from {file})");
            return;
        }
        Log.Warning($"{Name}: no UserCfg.opt with a usable InstalledPackagesPath was found");
    }

    /// <summary>格式是一行 `InstalledPackagesPath "D:\MSFS2022"`，键和值之间可能是任意空白。</summary>
    public static string? ReadInstalledPackagesPath(string userCfg)
    {
        try
        {
            if (!File.Exists(userCfg))
                return null;
            var match = Regex.Match(File.ReadAllText(userCfg), "InstalledPackagesPath\\s+\"(.+?)\"");
            if (!match.Success)
                return null;
            var value = match.Groups[1].Value;
            if (Directory.Exists(value))
                return value;
            Log.Warning($"{userCfg} points at {value}, which does not exist");
        }
        catch (Exception e)
        {
            Log.Warning($"error reading {userCfg}: {e.Message}");
        }
        return null;
    }

    /// <summary>所有包的 layout.json，按 Official → Community2024 → Community 的顺序（后者覆盖前者）。</summary>
    public List<string> LayoutFiles()
    {
        var result = new List<string>();
        if (PackagesPath is null)
            return result;

        var official = new List<string>();
        foreach (var folder in OfficialFolders)
        {
            var root = Path.Combine(PackagesPath, folder);
            if (!Directory.Exists(root))
                continue;
            foreach (var store in SafeDirectories(root))
            foreach (var package in SafeDirectories(store))
            {
                var layout = Path.Combine(package, "layout.json");
                if (File.Exists(layout))
                    official.Add(layout);
            }
        }
        official.Sort(StringComparer.OrdinalIgnoreCase);

        var community = new List<string>();
        var community2024 = Path.Combine(PackagesPath, "Community2024");
        if (Directory.Exists(community2024))
        {
            foreach (var package in SafeDirectories(community2024))
            {
                var layout = Path.Combine(package, "layout.json");
                if (File.Exists(layout))
                    community.Add(layout);
            }
        }
        var community2020 = Path.Combine(PackagesPath, "Community");
        if (Directory.Exists(community2020))
        {
            foreach (var package in SafeDirectories(community2020))
            {
                var name = Path.GetFileName(package);
                if (Directory.Exists(Path.Combine(community2024, name)))
                    continue;
                var layout = Path.Combine(package, "layout.json");
                if (File.Exists(layout))
                    community.Add(layout);
            }
        }
        community.Sort(StringComparer.OrdinalIgnoreCase);

        result.AddRange(official);
        result.AddRange(community);
        return result;
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception e)
        {
            Log.Debug($"cannot list {path}: {e.Message}");
            return [];
        }
    }

    /// <summary>
    /// 所有飞机配置文件。同一个虚拟路径只留最后一个（Community 覆盖 Official）。
    /// </summary>
    public List<AircraftConfigFile> AircraftConfigFiles()
    {
        var byVirtualPath = new Dictionary<string, AircraftConfigFile>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var layout in LayoutFiles())
        {
            var packageDir = Path.GetDirectoryName(layout)!;
            var packageRelative = Path.GetRelativePath(PackagesPath!, packageDir);
            try
            {
                foreach (var virtualPath in ReadLayout(layout))
                {
                    var full = Path.Combine(packageDir, virtualPath.Replace('\\', Path.DirectorySeparatorChar));
                    if (!File.Exists(full))
                    {
                        Log.Debug($"File does not exist or path is too long: {full}");
                        continue;
                    }
                    if (!byVirtualPath.ContainsKey(virtualPath))
                        order.Add(virtualPath);
                    byVirtualPath[virtualPath] = new AircraftConfigFile(virtualPath, full, packageRelative, packageDir);
                }
            }
            catch (Exception e)
            {
                Log.Debug($"Error reading layout file {layout}: {e.Message}");
            }
        }
        return order.Select(v => byVirtualPath[v]).ToList();
    }

    /// <summary>layout.json 里 SimObjects 下的 aircraft.cfg / sim.cfg，去掉不是飞机的那几类。</summary>
    public static IEnumerable<string> ReadLayout(string layoutFile)
    {
        using var stream = File.OpenRead(layoutFile);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });
        if (!document.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String)
                continue;
            var path = pathElement.GetString() ?? "";
            if (!path.StartsWith("SimObjects", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!path.EndsWith("aircraft.cfg", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith("sim.cfg", StringComparison.OrdinalIgnoreCase))
                continue;
            var normalised = path.Replace('/', '\\');
            if (ExcludedSimObjectFolders.Any(f =>
                    normalised.Contains($"SimObjects\\{f}\\", StringComparison.OrdinalIgnoreCase)))
                continue;
            yield return normalised;
        }
    }

    /// <summary>在已安装的包里找 .vmr 规则集（FSLTL、AIG 等 AI 包会随包附带）。</summary>
    public List<string> FindRuleSets()
    {
        var found = new List<string>();
        if (PackagesPath is null)
            return found;
        foreach (var folder in new[] { "Community2024", "Community" })
        {
            var root = Path.Combine(PackagesPath, folder);
            if (!Directory.Exists(root))
                continue;
            foreach (var package in SafeDirectories(root))
            {
                try
                {
                    found.AddRange(Directory.GetFiles(package, "*.vmr", SearchOption.TopDirectoryOnly));
                }
                catch (Exception e)
                {
                    Log.Debug($"cannot search {package} for rule sets: {e.Message}");
                }
            }
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    /// <summary>这台机器上装了哪些 MSFS（读得到 InstalledPackagesPath 的）。</summary>
    public static IEnumerable<SimulatorType> InstalledSimulators()
    {
        return DetectedInstallations.Value;
    }
}
