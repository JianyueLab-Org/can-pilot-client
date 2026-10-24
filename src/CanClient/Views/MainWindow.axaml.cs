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
