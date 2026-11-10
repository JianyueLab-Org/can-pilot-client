using CanClient.Core.Settings;
using CanClient.Core.Util;
using Xunit;

namespace CanClient.Tests;

public sealed class PrivacyTests
{
    [Fact]
    public void IdentityAndAddressesAreRemovedFromDiagnostics()
    {
        Privacy.Protect("synthetic-password-4f23", "Synthetic Person");
        var result = Privacy.Sanitize("Synthetic Person synthetic-password-4f23 test@example.com 192.0.2.123 2001:db8::1\nforged");
        Assert.DoesNotContain("synthetic-password-4f23", result);
        Assert.DoesNotContain("Synthetic Person", result);
        Assert.DoesNotContain("test@example.com", result);
        Assert.DoesNotContain("2001:db8::1", result);
        Assert.Contains("192.0.2.xxx", result);
        Assert.DoesNotContain('\n', result);
    }

    [Fact]
    public void PasswordPersistenceIsOptIn() => Assert.False(new AppSettings().RememberPassword);

    [Fact]
    public void PersonalDataFilesArePrivateOnUnix()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            PrivateFiles.Write(path, "synthetic-data");
            Assert.Equal("synthetic-data", File.ReadAllText(path));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { File.Delete(path); }
    }
}
