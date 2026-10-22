using System.Text.Json;
using CanClient.Core.Util;

namespace CanClient.Core.Settings;

/// <summary>开发者手动创建的独立开关文件，不写入普通设置，也不随发布包分发。</summary>
public sealed record DebugOptions(bool ShowServerEndpoints = false)
{
    public const string FileName = "can-pilot.debug.json";

    public static DebugOptions Load(string? directory = null)
    {
        var path = Path.Combine(directory ?? AppPaths.ProgramDirectory, FileName);
        try
        {
            if (!File.Exists(path)) return new();
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return new(document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("show_server_endpoints", out var value) && value.ValueKind == JsonValueKind.True);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.For("debug-options").Warning($"cannot read debug options: {e.Message}");
            return new();
        }
    }
}
