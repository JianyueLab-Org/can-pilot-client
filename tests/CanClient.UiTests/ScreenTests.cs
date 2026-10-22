using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    [AvaloniaFact]
    public void LastMessageStaysVisibleAboveGrowingInput()
    {
        var tab = new ChatTabViewModel("消息", false);
        var view = new ChatView { DataContext = tab };
        var window = new Window { Width = 520, Height = 300, Content = view };
        window.Show();
        for (var i = 0; i < 30; i++)
            tab.Add(new ChatMessage(CanClient.Core.MessageKind.Radio, "ZSSS_TWR", $"第 {i} 行：上海塔台中文管制信息，测试长消息换行后的底部可见性。"));
        tab.Input = string.Join('\n', Enumerable.Repeat("正在输入较长的中文消息", 4));
        Save(window, "chat-last-line");
        var scroller = view.FindControl<ScrollViewer>("MessageScroller")!;
        var footer = view.FindControl<Border>("InputFooter")!;
        Assert.True(scroller.Bounds.Bottom <= footer.Bounds.Top + 1);
        Assert.InRange(scroller.Extent.Height - scroller.Viewport.Height - scroller.Offset.Y, -1, 1);
        var last = view.GetVisualDescendants().OfType<SelectableTextBlock>().Last();
        var bottom = last.TranslatePoint(new Point(0, last.Bounds.Height), view);
        Assert.NotNull(bottom);
        Assert.True(bottom!.Value.Y <= footer.Bounds.Top);
        window.Close();
    }

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
        Assert.Null(main.FindControl<TextBlock>("FsdConnectionStatus"));
        Assert.Null(main.FindControl<TextBlock>("VoiceConnectionStatus"));

        var settings = new SettingsViewModel(controller, vm);
        var detected2020 = settings.ShowMsfs2020;
        var detected2024 = settings.ShowMsfs2024;
        var detectedXp = settings.ShowXPlane;
        settings.ShowAllSimulators = true;
        Assert.True(settings.ShowMsfs2020 && settings.ShowMsfs2024 && settings.ShowXPlane);
        settings.ShowAllSimulators = false;
        Assert.Equal(detected2020, settings.ShowMsfs2020);
        Assert.Equal(detected2024, settings.ShowMsfs2024);
        Assert.Equal(detectedXp, settings.ShowXPlane);
        var window = new SettingsWindow { DataContext = settings };
        window.Show();
        var menu = window.FindControl<ListBox>("Menu")!;
        settings.ShowAllSimulators = true;
        string[] names = ["network", "notifications", "fonts", "audio", "ptt", "simulator-general", "msfs2020", "msfs2024", "simulator", "advanced", "rules", "performance", "updates", "misc", "information"];
        for (var i = 0; i < names.Length; i++)
        {
            menu.SelectedIndex = i;
            if (names[i] == "matching")
                settings.TestMatchCommand.Execute(null);
            Save(window, $"02-settings-{i:D2}-{names[i]}");
            if (names[i] == "network")
            {
                var page = (Control)window.FindControl<ContentControl>("PanelHost")!.Content!;
                Assert.False(page.FindControl<Grid>("FsdEndpointEditor")!.IsVisible);
                Assert.False(page.FindControl<Grid>("VoiceEndpointEditor")!.IsVisible);
                Assert.DoesNotContain(settings.FsdServers, s => s.Host.Length == 0);
                Assert.DoesNotContain(settings.VoiceServers, s => s.Host.Length == 0);
            }
            if (names[i] == "information")
            {
                var page = (Control)window.FindControl<ContentControl>("PanelHost")!.Content!;
                Assert.Equal("FSD：未连接", page.FindControl<TextBlock>("FsdConnectionStatus")!.Text);
                vm.FsdConnectionText = "FSD：已连接";
                vm.VoiceConnectionText = "语音：已连接 · UDP 23 ms";
                Save(window, "02-information-connected");
                Assert.Equal(vm.VoiceConnectionText, page.FindControl<TextBlock>("VoiceConnectionStatus")!.Text);
                vm.VoiceConnectionText = "语音：已连接 · TCP 41 ms";
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(vm.VoiceConnectionText, page.FindControl<TextBlock>("VoiceConnectionStatus")!.Text);
            }
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
