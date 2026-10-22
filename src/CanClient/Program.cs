using Avalonia;
using CanClient.Core.Util;

namespace CanClient;

internal static class Program
{
    public static bool Debug { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        Debug = args.Contains("--debug", StringComparer.OrdinalIgnoreCase);
        Log.Setup(AppPaths.ProgramDirectory, Debug);
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
            .LogToTrace();
}
