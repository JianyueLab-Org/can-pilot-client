using Avalonia.Controls;
using Avalonia.Input;

namespace CanClient.Views;

public partial class ResizeGrips : UserControl
{
    public ResizeGrips() => InitializeComponent();

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window { CanResize: true } window
            || sender is not Control { Tag: string tag }
            || !Enum.TryParse<WindowEdge>(tag, out var edge)
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        window.BeginResizeDrag(edge, e);
        e.Handled = true;
    }
}
