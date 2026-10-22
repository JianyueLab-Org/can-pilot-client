using System.Diagnostics;
using System.Text;

namespace CanClient.Core.Util;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>
/// 写到程序目录下的 msfs-for-can.log，滚动保留 4 份。
///
/// 打包出来的程序没有控制台，用户报"连不上"时手里唯一的线索就是这个文件，
/// 所以每个模块都通过 <see cref="For"/> 拿一个带名字的记录器往这里写。
/// 加 --debug 启动会把级别降到 Debug，记下协议层面的细节。
/// </summary>
public static class Log
{
    public const string FileName = "msfs-for-can.log";
    private const long MaxBytes = 1024 * 1024;
    private const int Backups = 3;

    private static readonly object Sync = new();
    private static StreamWriter? _writer;
    private static string? _path;

    public static LogLevel Level { get; set; } = LogLevel.Info;

    public static string? FilePath => _path;

    /// <summary>每写一行就触发一次，测试和调试窗口用。</summary>
    public static event Action<LogLevel, string, string>? Written;

    public static void Setup(string directory, bool debug)
    {
        Level = debug ? LogLevel.Debug : LogLevel.Info;
        lock (Sync)
        {
            _path = Path.Combine(directory, FileName);
            try
            {
                Directory.CreateDirectory(directory);
                OpenWriter();
            }
            catch (Exception e)
            {
                _path = null;
                Trace.WriteLine($"cannot write the log file: {e.Message}");
            }
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            For("uncaught").Critical($"an exception went uncaught: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            For("uncaught").Error($"an unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        For("startup").Info($"log level {Level}, file {_path ?? "(none)"}");
        For("startup").Info($"{Environment.OSVersion} | .NET {Environment.Version} | {AppVersion.Full}");
    }

    public static Logger For(string name) => new(name);

    internal static void Write(LogLevel level, string name, string message)
    {
        if (level < Level)
            return;

        var line = $"{DateTime.Now:HH:mm:ss.fff} {LevelName(level),-7} {name,-12} {message}";
        lock (Sync)
        {
            try
            {
                if (_writer is not null)
                {
                    _writer.WriteLine(line);
                    if (_writer.BaseStream.Length > MaxBytes)
                        Rotate();
                }
            }
            catch
            {
                // 写日志失败不能把调用方拖下水
            }
        }

        Debug.WriteLine(line);
        Written?.Invoke(level, name, message);
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        _ => "CRITICAL"
    };

    private static void OpenWriter()
    {
        var stream = new FileStream(_path!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    private static void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        for (var i = Backups; i >= 1; i--)
        {
            var source = i == 1 ? _path! : $"{_path}.{i - 1}";
            var target = $"{_path}.{i}";
            if (!File.Exists(source))
                continue;
            if (File.Exists(target))
                File.Delete(target);
            File.Move(source, target);
        }
        OpenWriter();
    }

    public static void OpenFolder()
    {
        if (_path is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            For("applog").Warning($"could not open the log folder: {e.Message}");
        }
    }
}

/// <summary>带模块名的记录器。值类型，随手建不花钱。</summary>
public readonly struct Logger(string name)
{
    public string Name { get; } = name;

    public bool IsDebugEnabled => Log.Level <= LogLevel.Debug;

    public void Debug(string message) => Log.Write(LogLevel.Debug, Name, message);
    public void Info(string message) => Log.Write(LogLevel.Info, Name, message);
    public void Warning(string message) => Log.Write(LogLevel.Warning, Name, message);
    public void Error(string message) => Log.Write(LogLevel.Error, Name, message);
    public void Critical(string message) => Log.Write(LogLevel.Critical, Name, message);
}
