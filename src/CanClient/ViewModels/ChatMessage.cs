using CanClient.Core;

namespace CanClient.ViewModels;

public sealed class ChatMessage(MessageKind kind, string sender, string text, DateTime? time = null)
{
    public MessageKind Kind { get; } = kind;
    public string Sender { get; } = sender;
    public string Text { get; } = text;
    public DateTime Time { get; } = time ?? DateTime.UtcNow;

    public string TimeText => Time.ToString("HH:mm:ss") + "Z";
    public string SenderPrefix => string.IsNullOrEmpty(Sender) ? string.Empty : Sender + ": ";
}
