using Avalonia.Controls;
using Avalonia.Interactivity;
using CanClient.ViewModels;
using CanClient.Views.Settings;
using CanClient.Core.ModelMatching;

namespace CanClient.Views;

public partial class SettingsWindow : Window
{
    private readonly Dictionary<string, Control> _panels = [];

    public SettingsWindow()
    {
        InitializeComponent();
        Menu.SelectedIndex = 0;
    }

    private SettingsViewModel Vm => (SettingsViewModel)DataContext!;

    private async void OnMenuSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Menu.SelectedItem is not ListBoxItem { Tag: string tag })
            return;

        if (tag is "Msfs2020" or "Msfs2024" && DataContext is SettingsViewModel vm)
        {
            await vm.SelectMsfsSettingsAsync(tag == "Msfs2020" ? SimulatorType.Msfs : SimulatorType.Msfs2024);
            if (Menu.SelectedItem is not ListBoxItem { Tag: string current } || current != tag) return;
        }

        if (!_panels.TryGetValue(tag, out var panel))
        {
            panel = CreatePanel(tag);
            _panels[tag] = panel;
        }

        PanelHost.Content = panel;
    }

    private static Control CreatePanel(string tag) => tag switch
    {
        "Network" => new NetworkPage(),
        "Notifications" => new NotificationsPage(),
        "Fonts" => new FontsPage(),
        "Audio" => new AudioPage(),
        "Ptt" => new PttPage(),
        "ModelMatching" => new ModelMatchingPage(),
        "Msfs2020" or "Msfs2024" => new ModelMatchingPage(),
        "SimulatorGeneral" => new SimulatorGeneralPage(),
        "ModelMatchingAdvanced" => new ModelMatchingAdvancedPage(),
        "CustomRules" => new CustomRulesPage(),
        "Performance" => new PerformancePage(),
        "Updates" => new UpdatesPage(),
        "Misc" => new MiscPage(),
        "Information" => new InformationPage(),
        "Simulator" => new SimulatorPage(),
        _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, null)
    };

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.IsDirty)
            Vm.Apply();
        Close(true);
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e) => Vm.Apply();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
