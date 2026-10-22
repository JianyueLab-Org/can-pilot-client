using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CanClient.ViewModels;

/// <summary>消息区中的一个标签页：主消息页或与某呼号的私聊页。</summary>
public sealed partial class ChatTabViewModel(string header, bool isClosable, bool isLocalNote = false) : ViewModelBase
{
    private const int MaxMessages = 2000;

    public string Header { get; } = header;
    public bool IsClosable { get; } = isClosable;
    public bool IsLocalNote { get; } = isLocalNote;
    public ObservableCollection<ChatMessage> Messages { get; } = [];

    [ObservableProperty]
    private string _input = string.Empty;

    [ObservableProperty]
    private bool _hasUnread;

    public string Placeholder => IsLocalNote ? "本地随手记，回车添加；不发送到网络，重启后清空"
        : IsClosable
        ? $"发送私聊消息给 {Header}"
        : "输入文字消息发到 COM1 频率，或以 . 开头输入命令（.help 查看）";

    public event Action<ChatTabViewModel, string>? Submitted;

    public void Add(ChatMessage message)
    {
        Messages.Add(message);
        while (Messages.Count > MaxMessages)
            Messages.RemoveAt(0);
    }

    [RelayCommand]
    private void Submit()
    {
        var text = Input.Trim();
        if (text.Length == 0)
            return;

        Input = string.Empty;
        Submitted?.Invoke(this, text);
    }
}
