namespace CanClient.Core.Util;

/// <summary>
/// 文件放在哪。和 Python 版一样，设置、日志、扫描结果都跟着程序目录走——
/// 用户把整个目录拷到别的机器上，配置一起带过去。
/// 配置目录不可写时退到 %LOCALAPPDATA%\CAN Pilot Client；日志始终使用程序目录的 logs。
/// </summary>
public static class AppPaths
{
    private static string? _dataDirectory;

    public static string ProgramDirectory => AppContext.BaseDirectory;

    public static string DataDirectory => _dataDirectory ??= ChooseDataDirectory();

    public static string SettingsFile => Path.Combine(DataDirectory, "msfs_settings.json");

    /// <summary>模型扫描结果，按模拟器分文件，和 vPilot 的命名一致。</summary>
    public static string ModelScanResultFile(string simulator) =>
        Path.Combine(DataDirectory, $"ModelScanResult-{simulator}.xml");

    /// <summary>随程序发布的模型匹配数据（vPilot 的 ModelMatchingData.bin）。</summary>
    public static string ModelMatchingDataFile => Path.Combine(ProgramDirectory, "ModelMatchingData.bin");

    /// <summary>测试用：把数据目录换到临时目录。</summary>
    public static void OverrideDataDirectory(string directory) => _dataDirectory = directory;

    private static string ChooseDataDirectory()
    {
        var program = ProgramDirectory;
        if (IsWritable(program))
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
