using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CanClient.Views;

public partial class FlightPlanView : UserControl
{
    public FlightPlanView() => InitializeComponent();

    private async void OnOpenWebFlightPlanClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            var opened = top is not null && await top.Launcher.LaunchUriAsync(new Uri("https://ceruleanavi.net/pilots/flightplan"));
            WebFlightPlanError.IsVisible = !opened;
            WebFlightPlanError.Text = "无法打开浏览器，请访问 https://ceruleanavi.net/pilots/flightplan";
        }
        catch (Exception)
        {
            WebFlightPlanError.Text = "无法打开浏览器，请访问 https://ceruleanavi.net/pilots/flightplan";
            WebFlightPlanError.IsVisible = true;
        }
    }
}
