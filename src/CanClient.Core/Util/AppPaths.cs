namespace CanClient.Core.Util;

/// <summary>
/// 文件放在哪。和 Python 版一样，设置、日志、扫描结果都跟着程序目录走——
/// 用户把整个目录拷到别的机器上，配置一起带过去。
/// 程序目录不可写时（比如 Linux 包装到 /opt）退到 %LOCALAPPDATA%\CAN Pilot Client
/// （macOS 是 ~/Library/Application Support，Linux 是 $XDG_DATA_HOME 或 ~/.local/share），日志也跟过去。
///
/// macOS 的 .app 包例外：程序在 CAN Pilot.app/Contents/MacOS 里，不该往包里写东西，
/// 设置和日志都放 ~/Library/Application Support/CAN Pilot Client，随包文件在 Contents/Resources。
/// </summary>
public static class AppPaths
{
    private static string? _dataDirectory;

    public static string ProgramDirectory => AppContext.BaseDirectory;

    /// <summary>是不是从 macOS 的 .app 包里运行。</summary>
    public static bool InMacAppBundle =>
        OperatingSystem.IsMacOS() && Path.TrimEndingDirectorySeparator(ProgramDirectory)
            .EndsWith(Path.Combine(".app", "Contents", "MacOS"), StringComparison.OrdinalIgnoreCase);

    /// <summary>随程序发布的数据文件（模型匹配数据、X-Plane 插件包、许可证）所在目录。</summary>
    public static string ResourceDirectory => InMacAppBundle
        ? Path.GetFullPath(Path.Combine(ProgramDirectory, "..", "Resources"))
        : ProgramDirectory;

    /// <summary>logs 目录放在哪下面：和设置同一个目录（程序目录可写时就是程序目录）。</summary>
    public static string LogRoot => DataDirectory;

    public static string DataDirectory => _dataDirectory ??= ChooseDataDirectory();

    public static string SettingsFile => Path.Combine(DataDirectory, "msfs_settings.json");

    /// <summary>模型扫描结果，按模拟器分文件，和 vPilot 的命名一致。</summary>
    public static string ModelScanResultFile(string simulator) =>
        Path.Combine(DataDirectory, $"ModelScanResult-{simulator}.xml");

    /// <summary>随程序发布的模型匹配数据（vPilot 的 ModelMatchingData.bin）。</summary>
    public static string ModelMatchingDataFile => Path.Combine(ResourceDirectory, "ModelMatchingData.bin");

    /// <summary>测试用：把数据目录换到临时目录。</summary>
    public static void OverrideDataDirectory(string directory) => _dataDirectory = directory;

    private static string ChooseDataDirectory()
    {
        var program = ProgramDirectory;
        if (!InMacAppBundle && IsWritable(program))
            return program;

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CAN Pilot Client");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
