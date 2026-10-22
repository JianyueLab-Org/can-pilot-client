using System.IO.Compression;
using CanClient.Core.Util;

namespace CanClient.Core.Sim;

/// <summary>来自 can-xplane-client 的安装发现、CSL 扫描和原生插件安装流程。</summary>
public static class XPlaneInstallation
{
    public static bool IsXPlane(string path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(Path.Combine(path, "Resources", "plugins"));
    public static string[] FindInstalls()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
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
    public static string PluginArchive => Path.Combine(AppPaths.ProgramDirectory, "XPC_for_CAN-plugin.zip");
    public static void InstallPlugin(string xplane)
    {
        if (!IsXPlane(xplane)) throw new IOException("请选择包含 Resources/plugins 的 X-Plane 根目录。");
        using var zip = ZipFile.OpenRead(PluginArchive);
        var required = new[] { "XPC_for_CAN/64/win.xpl", "XPC_for_CAN/LegacyData/Doc8643.txt", "XPC_for_CAN/LegacyData/related.txt" };
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
