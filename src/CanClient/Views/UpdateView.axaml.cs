using Avalonia.Controls;
using CanClient.Core.Net;

namespace CanClient.Views;

public partial class UpdateView : UserControl
{
    public UpdateView() => InitializeComponent();

    public UpdateView(string current, UpdateInfo update) : this()
    {
        CurrentText.Text = current;
        NewText.Text = update.Version;
    }
}
