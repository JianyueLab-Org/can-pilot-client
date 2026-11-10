using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CanClient.Core.Net;
using CanClient.Core.Util;
using CanClient.ViewModels;

namespace CanClient.Views;

public partial class MainWindow : Window
{
    private AircraftWindow? _aircraftWindow;

    public MainWindow()
    {
        InitializeComponent();
        NiriFloating.Apply(this);
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainWindowViewModel vm)
            return;
        vm.AttentionRequested += _ => Flash();
        vm.UpdateFound += update => _ = ShowUpdateAsync(update);
        ApplyAppearance();
    }

    /// <summary>"保持窗口可见"和字体设置。</summary>
    private void ApplyAppearance()
    {
        var settings = Vm.Controller.Settings;
        Topmost = settings.KeepWindowVisible;
        SetFont("Font.Messages", "FontSize.Messages", settings.TextMessagesFont, settings.TextMessagesFontSize);
        SetFont("Font.Controllers", "FontSize.Controllers", settings.ControllerListFont, settings.ControllerListFontSize);
    }

    private void SetFont(string familyKey, string sizeKey, string family, double size)
    {
        if (string.IsNullOrWhiteSpace(family))
            Resources.Remove(familyKey);
        else
            Resources[familyKey] = new FontFamily(family);
        Resources[sizeKey] = size is >= 8 and <= 24 ? size : 12.0;
    }

    /// <summary>在窗口内的遮罩层上显示模态对话框，返回用户点击的按钮。</summary>
    public async Task<DialogResult> ShowDialogAsync(DialogView dialog)
    {
        DialogLayer.Children.Add(dialog);
        DialogLayer.IsVisible = true;
        dialog.Focus();
        try
        {
            return await dialog.Result;
        }
        finally
        {
            DialogLayer.Children.Remove(dialog);
            DialogLayer.IsVisible = DialogLayer.Children.Count > 0;
        }
    }

    // 无系统标题栏：在工具栏空白处按下左键即可拖动窗口
    private void OnToolbarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.IsConnected)
        {
            Vm.Disconnect();
            return;
        }

        var settings = Vm.Controller.Settings;
        var typeCodes = Vm.Controller.Models.Data.TypeCodes.Order(StringComparer.Ordinal).ToList();
        var model = new ConnectViewModel(typeCodes)
        {
            Callsign = settings.Callsign,
            TypeCode = settings.Aircraft,
            Selcal = settings.Selcal,
            IsObserver = settings.ObserverMode
        };
        var view = new ConnectView { DataContext = model };
        var dialog = new DialogView("连接到网络", view, "连接") { IsPrimaryEnabled = model.IsValid };
        model.PropertyChanged += (_, _) => dialog.IsPrimaryEnabled = model.IsValid;

        while (await ShowDialogAsync(dialog) == DialogResult.Primary)
        {
            settings.Selcal = model.Selcal.Trim().ToUpperInvariant();
            var problem = Vm.Connect(model.Callsign, model.TypeCode, model.IsObserver);
            if (problem is null)
                return;
            Vm.AddError(problem);
            view = new ConnectView { DataContext = model };
            dialog = new DialogView("连接到网络", view, "连接") { IsPrimaryEnabled = model.IsValid };
            dialog.SetMessage(problem);
            model.PropertyChanged += (_, _) => dialog.IsPrimaryEnabled = model.IsValid;
        }
    }

    private async void OnFlightPlanClick(object? sender, RoutedEventArgs e)
    {
        var plan = Vm.CreateFlightPlan();
        var view = new FlightPlanView { DataContext = plan };
        if (await ShowDialogAsync(new DialogView("飞行计划", view, "提交")) == DialogResult.Primary)
            Vm.FileFlightPlan(plan);
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        // 模态设置窗口打开期间，主窗口蒙上遮罩，明确区分当前操作的窗口
        DialogLayer.IsVisible = true;
        using var settings = new SettingsViewModel(Vm.Controller, Vm);
        try
        {
            await new SettingsWindow { DataContext = settings }.ShowDialog(this);
        }
        finally
        {
            DialogLayer.IsVisible = DialogLayer.Children.Count > 0;
            ApplyAppearance();
        }
    }

    private void OnAircraftListClick(object? sender, RoutedEventArgs e)
    {
        if (_aircraftWindow is { } open)
        {
            open.Activate();
            return;
        }
        _aircraftWindow = new AircraftWindow(Vm.Controller);
        _aircraftWindow.Closed += (_, _) => _aircraftWindow = null;
        _aircraftWindow.Show(this);
    }

    private void OnClearMessagesClick(object? sender, RoutedEventArgs e) => Vm.ClearMessages();

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e) => await Vm.CheckForUpdateAsync(manual: true);

    private void OnOpenLogClick(object? sender, RoutedEventArgs e) => Log.OpenFolder();

    private async void OnPrivacyClick(object? sender, RoutedEventArgs e)
    {
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var export = new Button { Content = "导出本地个人数据（不含密码）" };
        var erase = new Button { Content = "清除本地账号、飞行计划与日志" };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "连接 CAN 网络会向 FSD/语音服务器发送 CID、姓名、呼号、飞行状态、消息及按下 PTT 时的语音；其他在线成员可能收到公开飞行信息。" +
                   "客户端不录制语音、不上传诊断日志、不含分析追踪。日志仅本机保存，启动和跨日时自动清理超过 7 个日历日的日志。" +
                   "密码默认不保存；macOS 使用钥匙串，Linux 使用 Secret Service（需 secret-tool），不可用时不保存。" +
                   "FSD 当前为明文 TCP，连接前请知悉此传输限制。服务器保存期限、访问/更正/删除请求及法律依据需向 CAN 服务运营方确认。" +
                   "本地导出与清除不代表删除服务器上的数据。"
        });
        panel.Children.Add(export);
        panel.Children.Add(erase);
        panel.Children.Add(status);
        export.Click += (_, _) =>
        {
            try
            {
                var settings = Vm.Controller.Settings;
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    exportedAt = DateTimeOffset.UtcNow, settings.Cid, settings.RealName, settings.Callsign,
                    settings.Aircraft, settings.Selcal, settings.FlightPlan
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                var path = System.IO.Path.Combine(AppPaths.DataDirectory, "personal-data-export.json");
                PrivateFiles.Write(path, json);
                status.Text = $"已导出至 {path}。该文件含个人数据，请妥善保存，用后删除。";
            }
            catch { status.Text = "导出失败，请检查数据目录权限。"; }
        };
        erase.Click += async (_, _) =>
        {
            if (Vm.IsConnected) { status.Text = "请先断开网络，再清除本地数据。"; return; }
            if (await ShowDialogAsync(new DialogView("清除本地数据",
                    new TextBlock { Text = "将清除本地账号信息、系统保存的密码、飞行计划及日志。继续？", TextWrapping = TextWrapping.Wrap }, "清除")) != DialogResult.Primary)
                return;
            try
            {
                var settings = Vm.Controller.Settings;
                CanClient.Core.Settings.CredentialStore.Save(null);
                settings.RememberPassword = false;
                settings.Cid = settings.Password = settings.RealName = settings.Callsign = settings.Aircraft = settings.Selcal = "";
                settings.FlightPlan.Clear();
                settings.Save(throwOnError: true);
                if (File.Exists(settings.Path))
                {
                    var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settings.Path));
                    if (saved?["cid"]?.GetValue<string>() != "") throw new IOException();
                }
                Vm.ClearMessages();
                File.Delete(System.IO.Path.Combine(AppPaths.DataDirectory, "personal-data-export.json"));
                Log.DeleteLocalLogs();
                status.Text = "本地个人数据已清除。请退出并重新打开客户端以释放会话中的临时数据。";
            }
            catch { status.Text = "清除未完成，请检查系统凭据库及数据目录权限后重试。"; }
        };
        await ShowDialogAsync(new DialogView("隐私与本地数据", new ScrollViewer { Content = panel, MaxHeight = 380 }, null, closeText: "关闭"));
    }

    private async Task ShowUpdateAsync(UpdateInfo update)
    {
        var view = new UpdateView(AppVersion.Display, update);
        await ShowDialogAsync(new DialogView("新版本提醒", view, null, closeText: "知道了"));
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var body = new SelectableTextBlock
        {
            Text = $"{AppVersion.ClientName} {AppVersion.Full}\n" +
                   "CAN 连飞网络的 MSFS 2020 / 2024 与 X-Plane 11 / 12 统一飞行员客户端。\n" +
                   "界面布局参考 vPilot，他机模型匹配沿用 vPilot 的规则与模型库。\n" +
                   $"日志：{Log.FilePath ?? "（无）"}",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            MaxWidth = 420
        };
        await ShowDialogAsync(new DialogView("关于", body, null, closeText: "确定"));
    }

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ChatTabViewModel tab)
            Vm.CloseTab(tab);
    }

    private void OnControllerDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ControllerTree.SelectedItem is ControllerStation station)
            Vm.OpenPrivateChatCommand.Execute(station);
    }

    private void OnStationChatClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ControllerStation station)
            Vm.OpenPrivateChatCommand.Execute(station);
    }

    private void OnStationTuneClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ControllerStation station)
            Vm.TuneCom1Command.Execute(station);
    }

    private void OnStationAtisClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ControllerStation station)
            Vm.RequestAtisCommand.Execute(station);
    }

    // ---------- 闪烁任务栏 ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    private void Flash()
    {
        // 任务栏闪烁是 Windows 的做法；macOS / Linux 不闪
        if (!OperatingSystem.IsWindows())
            return;
        if (IsActive || TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero)
            return;
        const uint flashTray = 0x2, flashUntilForeground = 0xC;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Hwnd = hwnd,
            Flags = flashTray | flashUntilForeground,
            Count = uint.MaxValue
        };
        FlashWindowEx(ref info);
    }
}
