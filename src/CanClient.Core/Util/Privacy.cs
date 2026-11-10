using System.Net;
using System.Text.RegularExpressions;

namespace CanClient.Core.Util;

/// <summary>所有平台共用的数据最小化策略；不记录原始协议、身份或消息内容。</summary>
public static class Privacy
{
    private static readonly object Sync = new();
    private static readonly HashSet<string> Secrets = new(StringComparer.Ordinal);
    public const int LogRetentionDays = 7;

    public static void Protect(params string[] values)
    {
        lock (Sync)
            foreach (var value in values)
                if (!string.IsNullOrWhiteSpace(value)) Secrets.Add(value);
    }

    public static string Sanitize(string text)
    {
        lock (Sync)
            foreach (var value in Secrets.OrderByDescending(v => v.Length))
                text = text.Replace(value, "[redacted]", StringComparison.OrdinalIgnoreCase);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) text = text.Replace(home, "[home]", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "[email]");
        text = Regex.Replace(text, @"(?<![\w:])(?:\d{1,3}\.){3}\d{1,3}(?![\w:])", match =>
            IPAddress.TryParse(match.Value, out _) ? string.Join('.', match.Value.Split('.')[..3]) + ".xxx" : match.Value);
        text = Regex.Replace(text, @"(?<![\w:])(?:[0-9a-fA-F]{0,4}:){2,}[0-9a-fA-F:]{0,39}(?!\w)", match =>
            IPAddress.TryParse(match.Value, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? "[IPv6]" : match.Value);
        return text.Replace('\r', ' ').Replace('\n', ' ');
    }
}
