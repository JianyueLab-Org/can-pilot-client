using Avalonia;
using Avalonia.Controls;
using CanClient.ViewModels;

namespace CanClient.Views.Settings;

public partial class AudioPage : UserControl
{
    public AudioPage() => InitializeComponent();

    // 电平表只在这一页显示的时候开麦克风
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as SettingsViewModel)?.SetMeter(true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        (DataContext as SettingsViewModel)?.SetMeter(false);
        base.OnDetachedFromVisualTree(e);
    }
}
