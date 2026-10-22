using CanClient.Core.Settings;

namespace CanClient.Tests;

public class DebugOptionsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("{}", false)]
    [InlineData("{\"show_server_endpoints\":true}", true)]
    [InlineData("{\"show_server_endpoints\":false}", false)]
    [InlineData("{\"show_server_endpoints\":\"true\"}", false)]
    [InlineData("{broken", false)]
    public void ServerEditorsRequireExplicitBooleanInSeparateFile(string? json, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "can-debug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (json is not null) File.WriteAllText(Path.Combine(root, DebugOptions.FileName), json);
            Assert.Equal(expected, DebugOptions.Load(root).ShowServerEndpoints);
        }
        finally { Directory.Delete(root, true); }
    }
}
