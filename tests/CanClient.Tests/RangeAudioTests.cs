using System.Net;
using System.Text.Json;
using CanClient.Core.Voice;

namespace CanClient.Tests;

public class RangeAudioTests
{
    // ZBAA 附近
    private const double Lat = 40.07;
    private const double Lon = 116.6;

    private static RangeStation Pilot(string cid, double lat, double lon) => new(cid, "CCA" + cid, false, 0, lat, lon);

    private static RangeStation Atc(string cid, string callsign, int facility, double lat, double lon) =>
        new(cid, callsign, true, facility, lat, lon);

    /// <summary>沿经线往北 nm 海里的纬度。</summary>
    private static double North(double nm) => Lat + nm / 60.0;

    [Fact]
    public void PilotInsideRangeIsAllowedOutsideIsMuted()
    {
        var me = Pilot("1", Lat, Lon);
        var policy = RangePolicy.Compute(me, [Pilot("1003", North(150), Lon), Pilot("10013", North(250), Lon)]);
        Assert.True(policy["1003"]);
        Assert.False(policy["10013"]);
    }

    [Theory]
    [InlineData("ZBAA_TWR", 4, 90, true)]
    [InlineData("ZBAA_TWR", 4, 110, false)]
    [InlineData("ZBAA_GND", 3, 99, true)]
    [InlineData("ZBAA_APP", 5, 290, true)]
    [InlineData("ZBAA_APP", 5, 310, false)]
    [InlineData("ZBPE_CTR", 6, 590, true)]
    [InlineData("ZBPE_CTR", 6, 610, false)]
    [InlineData("ZBPE_FSS", 1, 1990, true)]
    [InlineData("ZBPE_FSS", 1, 2010, false)]
    [InlineData("LAX_25_CTR", 6, 590, true)]
    [InlineData("ZBAA_X", 6, 590, true)]       // 后缀认不出来，按席位代码
    public void ControllerRangeFollowsFacility(string callsign, int facility, double nm, bool expected)
    {
        var me = Pilot("1", Lat, Lon);
        var policy = RangePolicy.Compute(me, [Atc("5158", callsign, facility, North(nm), Lon)]);
        Assert.Equal(expected, policy["5158"]);
    }

    [Fact]
    public void ControllerListenerUsesItsOwnRangeForPilots()
    {
        // 和飞行员听塔台一致：塔台听 150 nm 外的飞行员也静音，进近听得见
        var pilots = new[] { Pilot("1003", North(150), Lon) };
        Assert.False(RangePolicy.Compute(Atc("1", "ZBAA_TWR", 4, Lat, Lon), pilots)["1003"]);
        Assert.True(RangePolicy.Compute(Atc("1", "ZBAA_APP", 5, Lat, Lon), pilots)["1003"]);
    }

    [Fact]
    public void ControllersAlwaysHearEachOther()
    {
        var me = Atc("1", "ZBAA_TWR", 4, Lat, Lon);
        var policy = RangePolicy.Compute(me, [Atc("5158", "LAX_25_CTR", 6, 33.94, -118.41), Pilot("1003", 33.94, -118.41)]);
        Assert.True(policy["5158"]);
        Assert.False(policy["1003"]);
    }

    [Fact]
    public void SameFrequencySourcesAreIndependent()
    {
        var me = Pilot("1", Lat, Lon);
        var policy = RangePolicy.Compute(me, [Pilot("1001", North(10), Lon), Pilot("1002", North(500), Lon)]);
        Assert.True(policy["1001"]);
        Assert.False(policy["1002"]);
    }

    [Fact]
    public void UnknownOwnPositionMeansEmptySnapshot()
    {
        Assert.Empty(RangePolicy.Compute(null, [Pilot("1003", North(500), Lon)]));
    }

    [Fact]
    public void OwnCidIsLeftOutAndDuplicateCidAllowsIfAnyRoleAllows()
    {
        var me = Pilot("1", Lat, Lon);
        var policy = RangePolicy.Compute(me,
            [Pilot("1", Lat, Lon), Pilot("7", North(500), Lon), Atc("7", "ZBPE_CTR", 6, North(500), Lon)], ownCid: "1");
        Assert.False(policy.ContainsKey("1"));
        Assert.True(policy["7"]);
    }

    [Fact]
    public void GreatCircleDistance()
    {
        // ZBAA → KLAX 约 5400 nm
        var nm = RangePolicy.DistanceNm(40.07, 116.6, 33.94, -118.41);
        Assert.InRange(nm, 5300, 5500);
        Assert.Equal(60, RangePolicy.DistanceNm(0, 0, 1, 0), 0);
    }

    [Fact]
    public void ParsesDataJson()
    {
        const string json = """
            {"general":{},"pilots":[{"cid":"1003","callsign":"CCA1658","latitude":40.07733,"longitude":116.61185}],
             "controllers":[{"cid":"5158","callsign":"LAX_25_CTR","facility":6,"latitude":"33.9425","longitude":"-118.408074"},
                            {"cid":"9","callsign":"BAD_TWR","facility":4,"latitude":"","longitude":"1"}],
             "atis":[{"cid":"10047","callsign":"ZBAA_ATIS","facility":7,"latitude":"40.07","longitude":"116.59"}]}
            """;
        using var doc = JsonDocument.Parse(json);
        var stations = RangePolicy.Parse(doc.RootElement);
        Assert.Equal(2, stations.Count);
        Assert.Contains(stations, s => s is { Cid: "1003", IsController: false });
        var ctr = Assert.Single(stations, s => s.IsController);
        Assert.Equal("5158", ctr.Cid);
        Assert.Equal(6, ctr.Facility);
        Assert.Equal(33.9425, ctr.Latitude, 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppedRefreshCannotPublishAfterStopOrRestart(bool restart)
    {
        using var enteredBuild = new ManualResetEventSlim();
        using var resumeBuild = new ManualResetEventSlim();
        var calls = 0;
        using var http = new HttpClient(new RangeDataHandler());
        using var service = new RangeAudioService(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                enteredBuild.Set();
                if (!resumeBuild.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Old refresh was not released");
                // 旧刷新会生成 deny，新刷新会生成 allow，便于检查是否被覆盖。
                return ("1", Lat, Lon);
            }
            return ("1", 50, Lon);
        }, http);

        service.Start();
        var oldLoop = service.LoopCompletion;
        try
        {
            Assert.True(enteredBuild.Wait(TimeSpan.FromSeconds(10)), "Refresh did not enter Build");
            service.Stop();
            Assert.False(service.Running);
            Assert.Empty(service.Policy);

            IReadOnlyDictionary<string, bool>? restartedPolicy = null;
            if (restart)
            {
                service.Start();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!service.Policy.ContainsKey("2"))
                    await Task.Delay(10, timeout.Token);
                restartedPolicy = service.Policy;
                Assert.True(restartedPolicy["2"]);
            }

            resumeBuild.Set();
            await oldLoop.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(restart, service.Running);
            if (restart)
                Assert.Same(restartedPolicy, service.Policy);
            else
                Assert.Empty(service.Policy);
        }
        finally
        {
            resumeBuild.Set();
            service.Stop();
            await oldLoop.WaitAsync(TimeSpan.FromSeconds(10));
            await service.LoopCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ImmediateStopDoesNotFaultStartingLoop()
    {
        using var http = new HttpClient(new RangeDataHandler());
        using var service = new RangeAudioService(() => ("1", Lat, Lon), http);
        for (var i = 0; i < 50; i++)
        {
            service.Start();
            service.Stop();
            await service.LoopCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(service.Running);
            Assert.Empty(service.Policy);
        }
    }

    private sealed class RangeDataHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"pilots":[{"cid":"2","callsign":"CCA2","latitude":50,"longitude":116.6}],"controllers":[]}""")
            });
    }
}
