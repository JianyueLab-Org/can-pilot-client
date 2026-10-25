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
/// 写到数据目录的 logs/yyyy-MM-dd.log，跨日自动切换文件。
///
/// 打包出来的程序没有控制台，用户报"连不上"时手里唯一的线索就是这个文件，
/// 所以每个模块都通过 <see cref="For"/> 拿一个带名字的记录器往这里写。
/// 加 --debug 启动会把级别降到 Debug，记下协议层面的细节。
/// </summary>
public static class Log
{
    private static readonly object Sync = new();
    private static StreamWriter? _writer;
    private static string? _path;
    private static string? _directory;
    private static DateOnly _date;

    public static LogLevel Level { get; set; } = LogLevel.Info;

    public static string? FilePath => _path;

    /// <summary>每写一行就触发一次，测试和调试窗口用。</summary>
    public static event Action<LogLevel, string, string>? Written;

    public static void Setup(string directory, bool debug)
    {
        Level = debug ? LogLevel.Debug : LogLevel.Info;
        lock (Sync)
        {
            _directory = Path.Combine(directory, "logs");
            try
            {
                _writer?.Dispose();
                _writer = null;
                Directory.CreateDirectory(_directory);
                Prune(_directory);
                _date = DateOnly.FromDateTime(DateTime.Now);
                _path = Path.Combine(_directory, $"{_date:yyyy-MM-dd}.log");
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
        For("startup").Info($"process {Environment.ProcessId} | executable {Environment.ProcessPath} | data {directory}");
    }

    public static Logger For(string name) => new(name);

    internal static void Write(LogLevel level, string name, string message)
    {
        if (level < Level)
            return;
        message = Privacy.Sanitize(message);

        string line;
        lock (Sync)
        {
            var now = DateTime.Now;
            line = $"{now:yyyy-MM-dd HH:mm:ss.fff} {LevelName(level),-7} {name,-12} {message}";
            try
            {
                var date = DateOnly.FromDateTime(now);
                if (_directory is not null && (date != _date || _writer is null))
                {
                    _writer?.Dispose();
                    _writer = null;
                    Directory.CreateDirectory(_directory);
                    Prune(_directory);
                    _path = Path.Combine(_directory, $"{date:yyyy-MM-dd}.log");
                    OpenWriter();
                    _date = date;
                }
                if (_writer is not null)
                    _writer.WriteLine(line);
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
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(_path!, options);
        PrivateFiles.Restrict(_path!);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    private static void Prune(string directory)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.Now).AddDays(-Privacy.LogRetentionDays + 1);
        foreach (var file in Directory.EnumerateFiles(directory, "*.log"))
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", out var date) && date < cutoff)
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static void DeleteLocalLogs()
    {
        lock (Sync)
        {
            _writer?.Dispose();
            _writer = null;
            if (_directory is null) return;
            foreach (var file in Directory.EnumerateFiles(_directory, "*.log"))
                if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", out _)) File.Delete(file);
        }
    }

    public static void OpenFolder()
    {
        if (_path is null)
            return;
        try
        {
            var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = true }
                : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", ["-R", _path])
                : new ProcessStartInfo("xdg-open", [Path.GetDirectoryName(_path)!]);
            Process.Start(start);
        }
        catch (Exception e)
        {
            For("applog").Warning($"could not open the log folder: {e.Message}");
        }
    }

    public static void Close()
    {
        lock (Sync)
        {
            _writer?.Dispose();
            _writer = null;
            _directory = null;
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
