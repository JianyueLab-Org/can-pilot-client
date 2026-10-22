using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CanClient.ViewModels;

namespace CanClient.Views.Settings;

public partial class CustomRulesPage : UserControl
{
    public CustomRulesPage() => InitializeComponent();

    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top || DataContext is not SettingsViewModel vm)
            return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "添加模型匹配规则集",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("模型匹配规则集 (*.vmr)") { Patterns = ["*.vmr"] }]
        });
        vm.AddRuleSets(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }
}
