using System.Text.Json;
using CanClient.Core.Util;

namespace CanClient.Core.Net;

public sealed record ServerEntry(string Name, string Host, int Port, string Location)
{
    public string Label => $"{Name}{(Location.Length > 0 ? " · " + Location : "")} ({Host}:{Port})";
}

public sealed record ServerList(IReadOnlyList<ServerEntry> Audio, IReadOnlyList<ServerEntry> Fsd);

/// <summary>公共服务器目录；只接收 Mumble 和明文 FSD 端点。对应 Python 版 serverdirectory.py。</summary>
public static class ServerDirectory
{
    public const string DefaultUrl = "https://ceruleanavi.net/api/v1/servers";

    private static readonly Logger Log = Util.Log.For("serverdirectory");

    public static ServerList Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number || status.GetInt32() != 200
            || !payload.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new FormatException("Invalid server directory response");

        List<ServerEntry> Read(string kind)
        {
            var list = new List<ServerEntry>();
            if (!data.TryGetProperty(kind, out var rows) || rows.ValueKind != JsonValueKind.Array)
                return list;
            var seen = new HashSet<(string, int)>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;
                if (!row.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String || k.GetString() != kind)
                    continue;
                var name = row.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "";
                var host = row.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString()!.Trim() : "";
                if (name.Length == 0 || host.Length == 0 || host.Any(char.IsWhiteSpace) || host.Contains("://"))
                    continue;
                if (!row.TryGetProperty("port", out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out var port)
                    || port is < 1 or > 65535)
                    continue;
                if (!seen.Add((host, port)))
                    continue;
                var location = row.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : "";
                list.Add(new ServerEntry(name, host, port, location));
            }
            return list;
        }

        return new ServerList(Read("audio"), Read("fsd"));
    }

    public static async Task<ServerList?> FetchAsync(CancellationToken cancellation = default)
    {
        try
        {
            using var client = Http.Create(TimeSpan.FromSeconds(8));
            using var response = await client.GetAsync(DefaultUrl, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation).ConfigureAwait(false);
            return Parse(document.RootElement);
        }
        catch (Exception e)
        {
            Log.Warning($"could not load server directory: {e.Message}");
            return null;
        }
    }
}

internal static class Http
{
    public const string UserAgent = "Mozilla/5.0 (compatible; CanClient/1.0)";

    public static HttpClient Create(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}
