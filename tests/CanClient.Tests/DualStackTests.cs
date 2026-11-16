using System.Net;
using System.Net.Sockets;
using CanClient.Core.Net;
using CanClient.Core.Settings;

namespace CanClient.Tests;

public class DualStackTests
{
    private static readonly IPAddress V4a = IPAddress.Parse("203.0.113.1");
    private static readonly IPAddress V4b = IPAddress.Parse("203.0.113.2");
    private static readonly IPAddress V6a = IPAddress.Parse("2001:db8::1");
    private static readonly IPAddress V6b = IPAddress.Parse("2001:db8::2");

    [Fact]
    public void PreferIPv4PutsIPv4FirstAndKeepsIPv6AsFallback()
    {
        var plan = DualStack.Plan([V6a, V6b, V4a], IpPreference.IPv4);
        Assert.Equal([V4a, V6a, V6b], plan.Addresses);
        Assert.Equal(AddressFamily.InterNetwork, plan.Preferred);
        Assert.False(plan.PreferredMissing);
    }

    [Fact]
    public void PreferIPv6PutsIPv6First()
    {
        var plan = DualStack.Plan([V4a, V6a], IpPreference.IPv6);
        Assert.Equal([V6a, V4a], plan.Addresses);
        Assert.False(plan.PreferredMissing);
    }

    [Fact]
    public void MissingPreferredFamilyIsReportedAsFallback()
    {
        var plan = DualStack.Plan([V4a], IpPreference.IPv6);
        Assert.Equal([V4a], plan.Addresses);
        Assert.True(plan.PreferredMissing);
    }

    [Fact]
    public void AutoInterleavesFamiliesStartingWithSystemOrder()
    {
        var plan = DualStack.Plan([V6a, V6b, V4a, V4b], IpPreference.Auto);
        Assert.Equal([V6a, V4a, V6b, V4b], plan.Addresses);
        Assert.Null(plan.Preferred);
        Assert.False(plan.PreferredMissing);
    }

    [Fact]
    public void MappedAddressesCountAsIPv4AndDuplicatesAreDropped()
    {
        var plan = DualStack.Plan([V4a.MapToIPv6(), V4a], IpPreference.IPv4);
        Assert.Equal([V4a], plan.Addresses);
    }

    [Theory]
    [InlineData("IPv4", IpPreference.IPv4)]
    [InlineData("ipv6", IpPreference.IPv6)]
    [InlineData("Auto", IpPreference.Auto)]
    [InlineData("nonsense", IpPreference.Auto)]
    [InlineData(null, IpPreference.Auto)]
    public void ParseFallsBackToAuto(string? value, IpPreference expected) =>
        Assert.Equal(expected, DualStack.Parse(value));

    [Fact]
    public void SettingRoundTripsAndBadValueDoesNotResetTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"can-ip-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "cid": "", "ip_preference": "what", "traffic_range_nm": 77 }""");
            var loaded = AppSettings.Load(path);
            Assert.Equal(IpPreference.Auto, loaded.IpPreference);
            Assert.Equal(77, loaded.TrafficRangeNm);

            loaded.IpPreference = IpPreference.IPv6;
            loaded.Save(throwOnError: true);
            Assert.Equal(IpPreference.IPv6, AppSettings.Load(path).IpPreference);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static readonly IPAddress V6c = IPAddress.Parse("2001:db8::3");

    [Fact]
    public void BlackholedPreferredAddressesLeaveTimeForFallback()
    {
        // 三个 IPv6 都不回 SYN；每次 200 ms，总共 500 ms。不给 IPv4 留时间的话三次就把总时限用光了
        var attempts = new List<IPAddress>();
        var notices = new List<string>();
        using var socket = DualStack.Connect("example.test", 6809, [V6a, V6b, V6c, V4a], IpPreference.IPv6, notices.Add,
            async (endpoint, token) =>
            {
                lock (attempts) attempts.Add(endpoint.Address);
                if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
                    await Task.Delay(Timeout.Infinite, token);
                return new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            },
            TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.Equal(AddressFamily.InterNetwork, socket.AddressFamily);
        Assert.Equal(V4a, attempts[^1]);
        Assert.Contains(notices, n => n.Contains("IPv6 连不上"));
    }

    [Fact]
    public void UnsupportedFamilyIsTreatedAsAFailedAddress()
    {
        // 本机禁用了 IPv6：建 IPv6 socket 就抛 AddressFamilyNotSupported，也要接着试 IPv4
        var notices = new List<string>();
        using var socket = DualStack.Connect("example.test", 6809, [V6a, V4a], IpPreference.Auto, notices.Add,
            (endpoint, _) => endpoint.AddressFamily == AddressFamily.InterNetworkV6
                ? throw new SocketException((int)SocketError.AddressFamilyNotSupported)
                : Task.FromResult(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.Equal(AddressFamily.InterNetwork, socket.AddressFamily);
        Assert.Single(notices);
    }

    [Fact]
    public void ConnectsToLiteralLoopbackAndFallsBackWhenFamilyMissing()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var notices = new List<string>();
        using var socket = DualStack.Connect("127.0.0.1", port, IpPreference.IPv6, notices.Add, CancellationToken.None);
        Assert.True(socket.Connected);
        Assert.Equal(AddressFamily.InterNetwork, socket.AddressFamily);
        Assert.Single(notices);
        Assert.Contains("IPv6", notices[0]);
    }
}
