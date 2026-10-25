using Avalonia;
using Avalonia.Media;
using CanClient.Core.Util;

namespace CanClient;

internal static class Program
{
    public static bool Debug { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        Debug = args.Contains("--debug", StringComparer.OrdinalIgnoreCase);
        Log.Setup(AppPaths.LogRoot, Debug);
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            Log.For("uncaught").Critical($"the application crashed: {e}");
            throw;
        }
        finally
        {
            Log.For("shutdown").Info("application exited");
            Log.Close();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = "can-pilot-client", EnableSessionManagement = false })
            .With(new FontManagerOptions { FontFallbacks =
            [
                new() { FontFamily = new FontFamily("Microsoft YaHei UI") },
                new() { FontFamily = new FontFamily("PingFang SC") },
                new() { FontFamily = new FontFamily("Hiragino Sans GB") },
                new() { FontFamily = new FontFamily("Noto Sans CJK SC") },
                new() { FontFamily = new FontFamily("Noto Sans SC") },
                new() { FontFamily = new FontFamily("Source Han Sans SC") },
                new() { FontFamily = new FontFamily("WenQuanYi Micro Hei") },
                new() { FontFamily = new FontFamily("Segoe UI Symbol") },
                new() { FontFamily = new FontFamily("Apple Symbols") },
                new() { FontFamily = new FontFamily("DejaVu Sans") },
                new() { FontFamily = new FontFamily("Noto Sans Symbols 2") }
            ] })
            .LogToTrace();
}
