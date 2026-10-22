using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using CanClient.ViewModels;

namespace CanClient.Views;

public partial class ChatView : UserControl
{
    private ChatTabViewModel? _tab;

    public ChatView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_tab is not null)
            _tab.Messages.CollectionChanged -= OnMessagesChanged;

        _tab = DataContext as ChatTabViewModel;

        if (_tab is not null)
            _tab.Messages.CollectionChanged += OnMessagesChanged;

        ScrollToEnd();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollToEnd();

    // 新消息到达时自动滚动到底部
    private void ScrollToEnd() => Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
}
