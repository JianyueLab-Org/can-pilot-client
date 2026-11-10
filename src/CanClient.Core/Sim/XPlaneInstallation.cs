using System.IO.Compression;
using CanClient.Core.Util;

namespace CanClient.Core.Sim;

/// <summary>来自 can-xplane-client 的安装发现、CSL 扫描和原生插件安装流程。</summary>
public static class XPlaneInstallation
{
    public static bool IsXPlane(string path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(Path.Combine(path, "Resources", "plugins"));
    /// <summary>
    /// X-Plane 安装器记录安装位置的目录：Windows 在 %LOCALAPPDATA%，macOS 在 ~/Library/Preferences，Linux 在 ~/.x-plane。
    /// </summary>
    private static string InstallRecordDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Preferences") : Path.Combine(home, ".x-plane");
        }
    }

    public static string[] FindInstalls()
    {
        var local = InstallRecordDirectory;
        var result = new List<string>();
        foreach (var name in new[] { "x-plane_install_12.txt", "x-plane_install_11.txt" })
        {
            try
            {
                var file = Path.Combine(local, name);
                if (File.Exists(file)) result.AddRange(File.ReadAllLines(file).Select(p => p.Trim()).Where(IsXPlane));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    /// <summary>设置里选的目录有效就用它，否则用 X-Plane 安装器登记的那些。</summary>
    public static string[] Roots(string configured) =>
        IsXPlane(configured) ? [configured.Trim()] : FindInstalls();

    /// <summary>
    /// X-Plane 12 的“设置 → 网络 → 接受传入连接”（Accept incoming connections），存在 X-Plane.prf 里。
    /// 新装的 X-Plane 12 默认关着：多播信标照发，但不回应 RREF 订阅，客户端就一直拿不到机位。
    /// </summary>
    private const string IncomingNetworkingKey = "_disable_incoming_networking";

    public static string PreferencesFile(string xplane) => Path.Combine(xplane, "Output", "preferences", "X-Plane.prf");

    /// <summary>true：传入连接关着；false：开着；null：读不到（没运行过、X-Plane 11 没有这一项）。
    /// X-Plane 退出时才写 prf，运行中在模拟器里改的开关要等退出后才反映到这里。</summary>
    public static bool? IncomingNetworkingDisabled(string xplane)
    {
        try
        {
            var file = PreferencesFile(xplane);
            if (!File.Exists(file)) return null;
            foreach (var line in File.ReadLines(file))
            {
                var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] == IncomingNetworkingKey) return parts[1] != "0";
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>把 prf 里的“接受传入连接”打开。X-Plane 退出时会用内存里的设置覆盖 prf，所以必须先退出 X-Plane。</summary>
    public static void EnableIncomingNetworking(string xplane)
    {
        if (SimulatorProcesses.XPlaneRunning)
            throw new InvalidOperationException("X-Plane 正在运行：请直接在 X-Plane 设置 → 网络 中打开“接受传入连接”，或先退出 X-Plane 再点这里。");
        var file = PreferencesFile(xplane);
        if (!File.Exists(file)) throw new FileNotFoundException("没有找到 X-Plane.prf，请先运行一次 X-Plane 再退出。", file);
        var lines = File.ReadAllText(file).Split('\n');
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var parts = lines[i].Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0] != IncomingNetworkingKey || parts[1] == "0") continue;
            lines[i] = IncomingNetworkingKey + " 0" + (lines[i].EndsWith('\r') ? "\r" : "");
            changed = true;
        }
        if (!changed) return;
        File.Copy(file, file + ".can-backup", true);
        File.WriteAllText(file, string.Join('\n', lines));
        Log.For("xp-install").Info($"enabled incoming networking in {file}");
    }

    public static string FindCsl(string xplane)
    {
        string[] known = ["IVAO_CSL/CSL", "xPilot/Resources/CSL", "LiveTraffic/Resources/CSL", "XSquawkBox/Resources/CSL", "X-CSL", "XPC_for_CAN/CSL"];
        return known.Select(p => Path.Combine(xplane, "Resources", "plugins", p.Replace('/', Path.DirectorySeparatorChar)))
            .FirstOrDefault(p => Directory.Exists(p) && ScanCsl(p).Count > 0) ?? "";
    }
    public static IReadOnlyList<string> ScanCsl(string root)
    {
        var result = new List<string>();
        if (!Directory.Exists(root)) return result;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(root, "*", options).Where(p => Path.GetFileName(p).Equals("xsb_aircraft.txt", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    foreach (var line in File.ReadLines(file))
                    {
                        var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2 && (parts[0].Equals("OBJ8_AIRCRAFT", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("AIRCRAFT", StringComparison.OrdinalIgnoreCase)))
                            result.Add(parts[1].Trim());
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }
    public static string PluginArchive => Path.Combine(AppPaths.ResourceDirectory, "XPC_for_CAN-plugin.zip");
    public static void InstallPlugin(string xplane)
    {
        if (!IsXPlane(xplane)) throw new IOException("请选择包含 Resources/plugins 的 X-Plane 根目录。");
        using var zip = ZipFile.OpenRead(PluginArchive);
        var binary = $"XPC_for_CAN/64/{PlatformSupport.XPlanePluginBinary}";
        if (zip.GetEntry(binary) is null)
            throw new InvalidDataException($"插件包里没有 {PlatformSupport.Name} 版插件（{binary}），请换用包含该平台插件的发布包。");
        var required = new[] { "XPC_for_CAN/LegacyData/Doc8643.txt", "XPC_for_CAN/LegacyData/related.txt" };
        if (required.Any(p => zip.GetEntry(p) is null)) throw new InvalidDataException("原生插件包不完整。");
        var target = Path.GetFullPath(Path.Combine(xplane, "Resources", "plugins")) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!destination.StartsWith(target, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("插件包路径无效。");
            if (entry.Name.Length == 0) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
        }
        var legacy = Path.Combine(target, "PythonPlugins", "PI_XpcTraffic.py");
        if (File.Exists(legacy)) File.Move(legacy, legacy + ".disabled", true);
        Log.For("xp-install").Info($"native plugin installed to {target}");
    }
}
