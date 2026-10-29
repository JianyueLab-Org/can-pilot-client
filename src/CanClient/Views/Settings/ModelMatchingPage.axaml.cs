using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CanClient.ViewModels;

namespace CanClient.Views.Settings;

public partial class ModelMatchingPage : UserControl
{
    public ModelMatchingPage() => InitializeComponent();

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top || DataContext is not SettingsViewModel vm)
            return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = vm.PackagesPathLabel,
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            vm.PackagesPath = path;
    }
}
