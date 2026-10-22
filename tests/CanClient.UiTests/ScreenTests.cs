using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CanClient.Core;
using CanClient.Core.Settings;
using CanClient.Core.Util;
using CanClient.ViewModels;
using CanClient.Views;

[assembly: AvaloniaTestApplication(typeof(CanClient.UiTests.TestApp))]

namespace CanClient.UiTests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// 把主窗口、连接对话框、设置的每一页、航空器列表真实渲染一遍，存成 PNG（bin/.../screens）。
/// 用的是真的 ClientController：模型匹配会扫这台机器上的 MSFS（有的话），他机是造的。
/// </summary>
public class ScreenTests(ITestOutputHelper output)
{
    private static readonly string ScreenDirectory = Path.Combine(AppContext.BaseDirectory, "screens");

    private static ClientController CreateController()
    {
        var data = Path.Combine(Path.GetTempPath(), "canclient-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        AppPaths.OverrideDataDirectory(data);
        var settings = AppSettings.Load(Path.Combine(data, "msfs_settings.json"));
        settings.Callsign = "CCA1501";
        settings.Aircraft = "A20N";
        return new ClientController(settings);
    }

    private void Save(TopLevel window, string name)
    {
        Directory.CreateDirectory(ScreenDirectory);
        for (var i = 0; i < 5; i++)
            Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(ScreenDirectory, name + ".png");
#pragma warning disable CS0618 // 新的 Save 重载要编码器选项，这里默认 PNG 就够了
        frame!.Save(path);
#pragma warning restore CS0618
        output.WriteLine(path);
    }

    private static void Feed(ClientController controller)
    {
        var traffic = controller.Traffic;
        void Add(string callsign, string type, double lat, double lon, double alt, double hdg, int gs)
        {
            traffic.UpdatePosition(callsign, lat, lon, alt, 0, 0, hdg, alt < 100, gs);
            traffic.SetPlaneInfo(callsign, type, callsign[..3]);
        }
        Add("CES5101", "A320", 31.15, 121.80, 3200, 170, 182);
        Add("CSN3101", "A21N", 31.30, 121.60, 8900, 12, 265);
        Add("CHH7178", "B738", 31.05, 121.40, 11800, 185, 290);
        Add("CCA1501", "A20N", 31.19, 121.33, 0, 178, 0);
        Add("ANA12", "B789", 31.45, 122.10, 21600, 220, 380);
        Add("CDG4321", "C919", 31.00, 121.20, 6200, 90, 210);
    }

    [AvaloniaFact]
    public async Task RendersAllWindows()
    {
        var controller = CreateController();
        await controller.Models.InitializeAsync();
        // 扫描在后台跑，等它完成（没装 MSFS 就直接过）
        for (var i = 0; i < 200 && controller.Models.Scanning; i++)
            await Task.Delay(100);
        Feed(controller);

        var vm = new MainWindowViewModel(controller);
        var main = new MainWindow { DataContext = vm };
        main.Show();
        vm.AddSystem(".match CES5101 A320 → " + controller.Models.Match("CES5101", "A320").ModelName);
        Save(main, "01-main");
        Assert.Equal("FSD：未连接", main.FindControl<TextBlock>("FsdConnectionStatus")!.Text);
        Assert.Equal("语音：未连接", main.FindControl<TextBlock>("VoiceConnectionStatus")!.Text);
        vm.FsdConnectionText = "FSD：已连接";
        vm.VoiceConnectionText = "语音：已连接 · UDP 23 ms";
        Save(main, "01-main-connected");
        Assert.Equal(vm.VoiceConnectionText, main.FindControl<TextBlock>("VoiceConnectionStatus")!.Text);
        vm.VoiceConnectionText = "语音：已连接 · TCP 41 ms";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(vm.VoiceConnectionText, main.FindControl<TextBlock>("VoiceConnectionStatus")!.Text);
        vm.FsdConnectionText = "FSD：未连接";
        vm.VoiceConnectionText = "语音：未连接";

        var settings = new SettingsViewModel(controller);
        var window = new SettingsWindow { DataContext = settings };
        window.Show();
        var menu = window.FindControl<ListBox>("Menu")!;
        string[] names = ["network", "notifications", "fonts", "audio", "ptt", "matching", "advanced", "rules", "performance", "updates", "misc"];
        for (var i = 0; i < names.Length; i++)
        {
            menu.SelectedIndex = i;
            if (names[i] == "matching")
                settings.TestMatchCommand.Execute(null);
            Save(window, $"02-settings-{i:D2}-{names[i]}");
        }
        settings.SetMeter(false);
        window.Close();

        var aircraft = new AircraftWindow(controller);
        aircraft.Show();
        await Task.Delay(1300);
        Save(aircraft, "03-aircraft");
        var rows = controller.AircraftList();
        foreach (var row in rows)
            output.WriteLine($"{row.Callsign} {row.TypeCode} → {row.ModelName} [{row.MatchDescription} / {row.MatchSource}] {row.SimStatus}");
        Assert.Equal(6, rows.Count);
        aircraft.Close();

        var connect = new ConnectViewModel(controller.Models.Data.TypeCodes.Order().ToList())
        {
            Callsign = "CCA1501", TypeCode = "A20N"
        };
        _ = main.ShowDialogAsync(new DialogView("连接到网络", new ConnectView { DataContext = connect }, "连接"));
        Save(main, "04-connect");

        main.Close();
        controller.Dispose();
    }
}
