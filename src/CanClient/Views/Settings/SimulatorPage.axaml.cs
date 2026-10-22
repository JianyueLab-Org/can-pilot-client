using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CanClient.ViewModels;

namespace CanClient.Views.Settings;

public partial class SimulatorPage : UserControl
{
    public SimulatorPage() => InitializeComponent();

    private async void OnBrowseXPlaneClick(object? sender, RoutedEventArgs e) => await BrowseAsync(false);
    private async void OnBrowseCslClick(object? sender, RoutedEventArgs e) => await BrowseAsync(true);
    private async Task BrowseAsync(bool csl)
    {
        if (TopLevel.GetTopLevel(this) is not { } top || DataContext is not SettingsViewModel vm) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = csl ? "选择 CSL 模型库目录" : "选择 X-Plane 安装目录", AllowMultiple = false
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
        if (csl) vm.CslPath = path;
        else vm.XPlanePath = path;
    }
}
