using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CanClient.Views;

public enum DialogResult
{
    None,
    Primary,
    Secondary
}

/// <summary>窗口内模态对话框。通过 <see cref="MainWindow.ShowDialogAsync"/> 显示。</summary>
public partial class DialogView : UserControl
{
    private readonly TaskCompletionSource<DialogResult> _result = new();

    public DialogView()
    {
        InitializeComponent();
    }

    public DialogView(string title, object body, string? primaryText, string closeText = "取消", string? secondaryText = null)
        : this()
    {
        TitleText.Text = title;
        BodyPresenter.Content = body;

        PrimaryButton.Content = primaryText;
        PrimaryButton.IsVisible = primaryText is not null;
        CloseButton.Content = closeText;
        SecondaryButton.Content = secondaryText;
        SecondaryButton.IsVisible = secondaryText is not null;
    }

    public Task<DialogResult> Result => _result.Task;

    /// <summary>在按钮上方显示一行提示（比如连接被拦下的原因）。</summary>
    public void SetMessage(string? text)
    {
        MessageText.Text = text;
        MessageText.IsVisible = !string.IsNullOrEmpty(text);
    }

    public bool IsPrimaryEnabled
    {
        get => PrimaryButton.IsEnabled;
        set => PrimaryButton.IsEnabled = value;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Complete(DialogResult.None);
            e.Handled = true;
        }
    }

    private void OnPrimaryClick(object? sender, RoutedEventArgs e) => Complete(DialogResult.Primary);

    private void OnSecondaryClick(object? sender, RoutedEventArgs e) => Complete(DialogResult.Secondary);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Complete(DialogResult.None);

    private void Complete(DialogResult result) => _result.TrySetResult(result);
}
