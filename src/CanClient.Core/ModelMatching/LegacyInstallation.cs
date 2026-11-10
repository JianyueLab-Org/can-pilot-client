using System.Xml.Linq;
using CanClient.Core.Util;
using Microsoft.Win32;

namespace CanClient.Core.ModelMatching;

/// <summary>
/// 一套 FSX / FSX:SE / P3D 安装，vPilot Type_02000074 和它的八个子类（Type_0200007E–Type_02000084、Type_02000073）。
///
///   1. 安装目录：注册表（HKCU、HKLM，32 位和 64 位视图都看）里的 AppPath / SetupPath / install_path，
///      目录下要有 FSX.exe / Prepar3D.exe 才算装了；
///   2. SimObjects 路径：P3D v2 起是 ProgramData 下的 simobjects.cfg（[Entry.N] Path=），
///      没有就读 fsx.cfg / Prepar3D.cfg 的 [Main] SimObjectPaths.N；
///   3. P3D v3 起还有 add-ons.cfg 登记的附加包，每个包的 add-on.xml 里 Category 为 SimObjects 的组件；
///      Documents 下的 "Prepar3D vN Add-ons" 是 P3D 自动发现的目录，同样处理；
///   4. Rotorcraft、GroundVehicles 这些不是飞机的类别跳过。
///
/// 找到的每个 SimObjects 目录是扫描结果里的一组，组内每个子目录的 aircraft.cfg（或 sim.cfg）是一个飞机目录。
/// </summary>
public sealed class LegacyInstallation : ISimulatorInstallation
{
    private static readonly Logger Log = Util.Log.For("models");

    private static readonly string[] FsxExcluded = ["Rotorcraft", "GroundVehicles", "Boats", "Animals", "Misc"];
    private static readonly string[] P3dExcluded =
        ["Rotorcraft", "GroundVehicles", "Boats", "Animals", "Misc", "Submersible", "Weapons", "Countermeasures"];
    private static readonly string[] P3dAvatarExcluded = ["Avatars", .. P3dExcluded];

    /// <summary>一个 SimObjects 目录：配置里写的样子（可能是相对安装目录的）和磁盘上的完整路径。</summary>
    public sealed record SimObjectsFolder(string RelativePath, string FullPath);

    private readonly string[] _registryKeys;
    private readonly string[] _registryValues;
    private readonly string _executable;
    private readonly string[] _configFiles;
    private readonly string[] _addOnConfigFiles;
    private readonly string[] _discoveryFolders;
    private readonly string[] _excluded;

    public LegacyInstallation(SimulatorType simulatorType, string? installPathOverride = null)
    {
        if (!simulatorType.IsLegacy())
            throw new NotSupportedException($"Simulator type {simulatorType} is not supported.");
        SimulatorType = simulatorType;
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string[] standardValues = ["AppPath", "SetupPath", "install_path"];
        switch (simulatorType)
        {
            case SimulatorType.Fsx:
                _executable = "FSX.exe";
                _registryKeys = [@"SOFTWARE\Microsoft\Microsoft Games\Flight Simulator\10.0"];
                _registryValues = standardValues;
                _configFiles = [Path.Combine(roaming, "Microsoft", "FSX", "fsx.CFG")];
                _addOnConfigFiles = [];
                _discoveryFolders = [];
                _excluded = FsxExcluded;
                break;
            case SimulatorType.FsxSe:
                _executable = "FSX.exe";
                _registryKeys = [@"SOFTWARE\DovetailGames\FSX", @"Software\Microsoft\Microsoft Games\Flight Simulator - Steam Edition\10.0"];
                _registryValues = ["install_path", "AppPath", "SetupPath"];
                _configFiles = [Path.Combine(roaming, "Microsoft", "FSX-SE", "fsx_se.CFG")];
                _addOnConfigFiles = [];
                _discoveryFolders = [];
                _excluded = FsxExcluded;
                break;
            case SimulatorType.Prepar3d:
                _executable = "Prepar3D.exe";
                _registryKeys = [@"SOFTWARE\LockheedMartin\Prepar3D"];
                _registryValues = standardValues;
                _configFiles = [Path.Combine(roaming, "Lockheed Martin", "Prepar3D", "Prepar3D.CFG")];
                _addOnConfigFiles = [];
                _discoveryFolders = [];
                _excluded = P3dExcluded;
                break;
            default:
            {
                var version = (int)simulatorType - (int)SimulatorType.Prepar3d + 1;
                var folder = $"Prepar3D v{version}";
                _executable = "Prepar3D.exe";
                _registryKeys = [$@"SOFTWARE\Lockheed Martin\{folder}"];
                _registryValues = standardValues;
                _configFiles =
                [
                    Path.Combine(common, "Lockheed Martin", folder, "simobjects.CFG"),
                    Path.Combine(roaming, "Lockheed Martin", folder, "Prepar3D.CFG")
                ];
                _addOnConfigFiles = version >= 3
                    ? [Path.Combine(roaming, "Lockheed Martin", folder, "add-ons.cfg"), Path.Combine(common, "Lockheed Martin", folder, "add-ons.cfg")]
                    : [];
                _discoveryFolders = version >= 3 ? [Path.Combine(documents, $"{folder} Add-ons")] : [];
                _excluded = version >= 3 ? P3dAvatarExcluded : P3dExcluded;
                break;
            }
        }
        PackagesPathOverride = string.IsNullOrWhiteSpace(installPathOverride) ? null : installPathOverride.Trim();
        Validate();
    }

    public SimulatorType SimulatorType { get; }
    public string Name => SimulatorType.FullName();
    public string? PackagesPathOverride { get; }

    /// <summary>安装目录。</summary>
    public string? PackagesPath { get; private set; }

    public bool IsValid => PackagesPath is not null;

    /// <summary>侧栏安装检测：注册表登记的目录里确实有模拟器主程序。卸载残留的注册表和配置不算。</summary>
    public bool IsInstalled => RegistryInstallPath() is { } path && File.Exists(Path.Combine(path, _executable));

    private void Validate()
    {
        if (PackagesPathOverride is not null)
        {
            if (Directory.Exists(PackagesPathOverride))
            {
                PackagesPath = PackagesPathOverride;
                return;
            }
            Log.Warning($"the configured {Name} directory {PackagesPathOverride} does not exist, falling back to the registry");
        }
        var path = RegistryInstallPath();
        if (path is not null && File.Exists(Path.Combine(path, _executable)))
        {
            PackagesPath = path;
            Log.Info($"{Name}: installed at {path}");
            return;
        }
        Log.Debug($"{Name}: no installation was found in the registry");
    }

    private string? RegistryInstallPath()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                foreach (var keyName in _registryKeys)
                {
                    using var key = root.OpenSubKey(keyName);
                    if (key is null)
                        continue;
                    foreach (var valueName in _registryValues)
                    {
                        var value = (key.GetValue(valueName) as string)?.Replace("\0", "").Trim();
                        if (!string.IsNullOrEmpty(value) && Directory.Exists(value))
                            return value;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Debug($"cannot read the registry for {Name}: {e.Message}");
            }
        }
        return null;
    }

    /// <summary>模拟器登记的所有 SimObjects 目录（去掉非飞机类别和不存在的），vPilot Type_02000074.p()。</summary>
    public List<SimObjectsFolder> SimObjectsFolders()
    {
        var result = new List<SimObjectsFolder>();
        if (PackagesPath is null)
            return result;
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            if (path.Length > 0 && seen.Add(path))
                paths.Add(path);
        }

        try
        {
            var config = _configFiles.FirstOrDefault(File.Exists);
            if (config is null)
            {
                // 配置文件不在（便携安装、没启动过）：模拟器默认只有这一个飞机目录
                Log.Info($"{Name}: no {string.Join(" / ", _configFiles.Select(Path.GetFileName))} was found, scanning SimObjects\\Airplanes only");
                Add(@"SimObjects\Airplanes");
            }
            else
            {
                foreach (var path in ConfiguredSimObjectsPaths(config))
                    Add(path);
            }

            var packages = new List<string>();
            foreach (var file in _addOnConfigFiles)
                packages.AddRange(AddOnPackages(file, PackagesPath));
            foreach (var folder in _discoveryFolders)
                packages.AddRange(SafeDirectories(folder));
            foreach (var package in packages)
            {
                foreach (var path in AddOnSimObjects(package))
                    Add(path);
            }
        }
        catch (Exception e)
        {
            Log.Warning($"{Name}: reading the SimObjects paths failed: {e.Message}");
        }

        var fullPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var trimmed = path.TrimEnd('\\', '/');
            if (_excluded.Any(category => trimmed.EndsWith("SimObjects\\" + category, StringComparison.OrdinalIgnoreCase)))
                continue;
            var full = Path.IsPathRooted(path) ? path : Path.Combine(PackagesPath, path);
            if (Directory.Exists(full) && fullPaths.Add(Path.GetFullPath(full)))
                result.Add(new SimObjectsFolder(path, full));
        }
        return result;
    }

    /// <summary>
    /// 模拟器配置里登记的 SimObjects 路径：simobjects.cfg 是 [Entry.N] Path=（Active=False 的跳过），
    /// fsx.cfg / Prepar3D.cfg 是 [Main] SimObjectPaths.N=。
    /// </summary>
    internal static IEnumerable<string> ConfiguredSimObjectsPaths(string config)
    {
        var data = AircraftConfigParser.ReadIni(config);
        if (Path.GetFileName(config).Equals("simobjects.cfg", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (name, keys) in data.Sections)
            {
                if (name.StartsWith("entry.", StringComparison.OrdinalIgnoreCase) && !IsInactive(keys)
                    && keys.TryGetValue("path", out var value))
                    yield return CleanPath(value);
            }
            yield break;
        }
        foreach (var (_, keys) in data.Named("Main"))
        {
            foreach (var (key, value) in keys)
            {
                if (key.StartsWith("simobjectpaths.", StringComparison.OrdinalIgnoreCase))
                    yield return CleanPath(value);
            }
        }
    }

    private static bool IsInactive(Dictionary<string, string> keys) =>
        keys.TryGetValue("active", out var active) &&
        AircraftConfigParser.CleanValue(active).Equals("false", StringComparison.OrdinalIgnoreCase);

    private static string CleanPath(string value) => AircraftConfigParser.CleanValue(value).Trim('"').Trim();

    /// <summary>add-ons.cfg 里启用的包目录：[Package.N] PATH=，以及 [DiscoveryPath.N] 下的每个子目录。</summary>
    internal static IEnumerable<string> AddOnPackages(string addOnsConfig, string installPath)
    {
        if (!File.Exists(addOnsConfig))
            yield break;
        foreach (var (name, keys) in AircraftConfigParser.ReadIni(addOnsConfig).Sections)
        {
            var package = name.StartsWith("package.", StringComparison.OrdinalIgnoreCase);
            var discovery = name.StartsWith("discoverypath.", StringComparison.OrdinalIgnoreCase);
            if (!package && !discovery || IsInactive(keys) || !keys.TryGetValue("path", out var value))
                continue;
            var path = CleanPath(value);
            if (path.Length == 0)
                continue;
            path = Path.IsPathRooted(path) ? path : Path.Combine(installPath, path);
            if (package)
                yield return path;
            else
            {
                foreach (var child in SafeDirectories(path))
                    yield return child;
            }
        }
    }

    /// <summary>一个附加包的 add-on.xml 里 Category 为 SimObjects 的组件路径（相对的按包目录算）。</summary>
    internal static IEnumerable<string> AddOnSimObjects(string packageDirectory)
    {
        var file = Path.Combine(packageDirectory, "add-on.xml");
        if (!File.Exists(file))
            return [];
        try
        {
            var document = XDocument.Load(file);
            return document.Descendants()
                .Where(e => e.Name.LocalName.Equals("AddOn.Component", StringComparison.OrdinalIgnoreCase))
                .Where(e => e.Elements().Any(c => c.Name.LocalName.Equals("Category", StringComparison.OrdinalIgnoreCase)
                                                  && c.Value.Trim().Equals("SimObjects", StringComparison.OrdinalIgnoreCase)))
                .SelectMany(e => e.Elements().Where(c => c.Name.LocalName.Equals("Path", StringComparison.OrdinalIgnoreCase)))
                .Select(p => p.Value.Trim())
                .Where(p => p.Length > 0)
                .Select(p => Path.IsPathRooted(p) ? p : Path.Combine(packageDirectory, p))
                .ToList();
        }
        catch (Exception e)
        {
            Log.Debug($"cannot read {file}: {e.Message}");
            return [];
        }
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception e)
        {
            Log.Debug($"cannot list {path}: {e.Message}");
            return [];
        }
    }

    /// <summary>每个 SimObjects 目录下一层子目录里的 aircraft.cfg（没有就 sim.cfg），vPilot Type_02000075。</summary>
    public List<AircraftConfigFile> AircraftConfigFiles()
    {
        var result = new List<AircraftConfigFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in SimObjectsFolders())
        {
            var directories = SafeDirectories(folder.FullPath).ToList();
            directories.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in directories)
            {
                var config = Path.Combine(directory, "aircraft.cfg");
                if (!File.Exists(config))
                    config = Path.Combine(directory, "sim.cfg");
                if (!File.Exists(config))
                    continue;
                var virtualPath = $"{folder.RelativePath.TrimEnd('\\', '/')}\\{Path.GetFileName(directory)}\\{Path.GetFileName(config)}";
                if (seen.Add(virtualPath))
                    result.Add(new AircraftConfigFile(virtualPath, config, folder.RelativePath, folder.FullPath));
            }
        }
        return result;
    }

    /// <summary>vPilot 不在 FSX / P3D 里自动找规则集，AI 包的 .vmr 由用户在“自定义规则”里添加。</summary>
    public List<string> FindRuleSets() => [];

    /// <summary>这台机器上装了哪些 FSX / P3D。</summary>
    public static IEnumerable<SimulatorType> InstalledSimulators() =>
        SimulatorTypeExtensions.Legacy.Where(type => new LegacyInstallation(type).IsInstalled);
}
