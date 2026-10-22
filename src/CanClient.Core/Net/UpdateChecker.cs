using System.Text.Json;
using CanClient.Core.Util;

namespace CanClient.Core.Net;

public sealed record UpdateInfo(string Version);

/// <summary>
/// 查有没有新版，对应 Python 版 update.py。查到了也只是告诉用户，绝不自动更新；
/// 任何失败都返回 null，不影响启动。
///
///     GET https://ceruleanavi.net/api/v1/clients/latest?client=msfs-for-can&amp;version=2.2.1
///
/// 回包里顶层的 client 是包名字符串，那一包的信息在 clients[包名]；有没有新版在 update.available。
/// </summary>
public static class UpdateChecker
{
    public const string DefaultUrl = "https://ceruleanavi.net/api/v1/clients/latest";

    private static readonly Logger Log = Util.Log.For("update");

    public static List<int> Parts(string? version)
    {
        var parts = new List<int>();
        var current = "";
        foreach (var c in (version ?? "") + " ")
        {
            if (char.IsAsciiDigit(c))
                current += c;
            else if (current.Length > 0)
            {
                if (int.TryParse(current, out var value))
                    parts.Add(value);
                current = "";
            }
        }
        return parts;
    }

    /// <summary>按数值逐段比，不能按字符串比（2.0.10 &gt; 2.0.9）。</summary>
    public static bool IsNewer(string candidate, string current)
    {
        var a = Parts(candidate);
        var b = Parts(current);
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var left = i < a.Count ? a[i] : 0;
            var right = i < b.Count ? b[i] : 0;
            if (left != right)
                return left > right;
        }
        return false;
    }

    public static UpdateInfo? Read(JsonElement data, string client, string current)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return null;
        JsonElement build = default;
        if (data.TryGetProperty("clients", out var clients) && clients.ValueKind == JsonValueKind.Object
                                                            && clients.TryGetProperty(client, out var b) && b.ValueKind == JsonValueKind.Object)
            build = b;

        string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";

        var offered = "";
        if (data.TryGetProperty("update", out var verdict) && verdict.ValueKind == JsonValueKind.Object)
        {
            if (!verdict.TryGetProperty("available", out var available) || available.ValueKind != JsonValueKind.True)
            {
                Log.Info($"update check: {current} is current");
                return null;
            }
            offered = Str(verdict, "latest");
        }

        var version = (offered.Length > 0 ? offered : Str(build, "version") is { Length: > 0 } v2 ? v2 : Str(data, "version")).Trim();
        if (version.Length == 0 || !IsNewer(version, current))
        {
            Log.Info($"update check: the server offered '{version}' against '{current}', ignoring");
            return null;
        }
        var info = new UpdateInfo(version);
        Log.Info($"update available: {info.Version}");
        return info;
    }

    public static async Task<UpdateInfo?> CheckAsync(string? url = null, CancellationToken cancellation = default)
    {
        var target = string.IsNullOrWhiteSpace(url) ? DefaultUrl : url.Trim();
        var current = AppVersion.Version;
        var joiner = target.Contains('?') ? "&" : "?";
        var query = $"client={Uri.EscapeDataString(AppVersion.UpdateClientId)}&version={Uri.EscapeDataString(current)}";
        try
        {
            using var client = Http.Create(TimeSpan.FromSeconds(10));
            using var response = await client.GetAsync(target + joiner + query, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Info($"update check: the server answered {(int)response.StatusCode}");
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation).ConfigureAwait(false);
            return Read(document.RootElement, AppVersion.UpdateClientId, current);
        }
        catch (Exception e)
        {
            Log.Info($"update check failed: {e.Message}");
            return null;
        }
    }
}
