using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Avalonia.Platform;
using CanClient.Core;
using CanClient.Core.Settings;
using CanClient.Core.Util;
using CanClient.ViewModels;
using CanClient.Views;

namespace CanClient;

public partial class App : Application
{
    public static ClientController Controller { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        using var logo = AssetLoader.Open(new Uri("avares://can-pilot/app-logo.png"));
        Resources["CanLogo"] = new Avalonia.Media.Imaging.Bitmap(logo);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load();
            if (Program.Debug)
                Log.Level = LogLevel.Debug;
            else if (settings.Debug)
                Log.Level = LogLevel.Debug;
            Controller = new ClientController(settings);
            var viewModel = new MainWindowViewModel(Controller);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.ShutdownRequested += (_, _) => Controller.Dispose();
            Controller.Start();
            viewModel.Started();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
