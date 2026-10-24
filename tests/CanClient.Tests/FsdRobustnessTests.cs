using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CanClient.Core.Fsd;
using CanClient.Core.Sim;
using CanClient.Core.Traffic;
using CanClient.Core.Voice;

namespace CanClient.Tests;

public class FsdRobustnessTests
{
    private static readonly MethodInfo HandlePacketMethod =
        typeof(FsdClient).GetMethod("HandlePacket", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static bool Handle(FsdClient fsd, string packet) => (bool)HandlePacketMethod.Invoke(fsd, [packet])!;

    private static FsdClient Client(TrafficTable? traffic = null) =>
        new("localhost", 6809, "CCA123", "1", "", "Test", 1, "A320", 10, traffic);

    // ---------- #6 ACC 请求字段类型 ----------

    [Theory]
    [InlineData("""{"request":true}""")]
    [InlineData("""{"request":1}""")]
    [InlineData("""{"request":{}}""")]
    [InlineData("""{"request":[1]}""")]
    [InlineData("""{"request":null}""")]
    [InlineData("""{"request":{"request":"full"}}""")]
    public void AccRequestWithWrongTypeIsIgnored(string json)
    {
        using var fsd = Client();
        Assert.True(Handle(fsd, $"$CQCES456:CCA123:ACC:{json}"));
        Assert.False(FsdClient.IsFullConfigRequest(JsonNode.Parse(json)!.AsObject()));
    }

    [Fact]
    public void AccFullRequestIsStillRecognised() =>
        Assert.True(FsdClient.IsFullConfigRequest(JsonNode.Parse("""{"request":"full"}""")!.AsObject()));

    // ---------- #7 非有限数值 ----------

    private const string Pbh = "0";

    [Theory]
    [InlineData("^CES456:NaN:121:5000:0:0:0:0:0:0:0:0:0")]
    [InlineData("^CES456:31:Infinity:5000:0:0:0:0:0:0:0:0:0")]
    [InlineData("^CES456:31:121:NaN:0:0:0:0:0:0:0:0:0")]
    [InlineData("^CES456:31:121:-Infinity:0:0:0:0:0:0:0:0:0")]
    [InlineData("^CES456:31:121:5000:NaN:0:0:0:0:0:0:0:0")]
    [InlineData("^CES456:31:121:5000:0:0:NaN:0:0:0:0:0:0")]
    [InlineData("^CES456:31:121:5000:0:0:0:0:0:Infinity:0:0:0")]
    [InlineData("^CES456:31:121:5000:0:0:0:0:0:1e308:0:0:0")]
    [InlineData("^CES456:31:121:5000:0:0:0:0:0:0:0:0:NaN")]
    [InlineData("^CES456:91:121:5000:0:0:0:0:0:0:0:0:0")]
    [InlineData("^CES456:31:181:5000:0:0:0:0:0:0:0:0:0")]
    [InlineData("#SLCES456:31:121:NaN:0:0:0:0:0:0:0:0:0")]
    [InlineData("#STCES456:31:121:NaN:0:0:0")]
    [InlineData("#STCES456:-91:121:5000:0:0:0")]
    [InlineData("#STCES456:31:121:5000:0:0:Infinity")]
    [InlineData("@N:CES456:1234:1:NaN:121:5000:250:" + Pbh + ":0")]
    [InlineData("@N:CES456:1234:1:31:-181:5000:250:" + Pbh + ":0")]
    [InlineData("@N:CES456:1234:1:31:121:Infinity:250:" + Pbh + ":0")]
    [InlineData("@N:CES456:1234:1:31:121:5000:NaN:" + Pbh + ":0")]
    [InlineData("@N:CES456:1234:1:x:121:5000:250:" + Pbh + ":0")]
    public void NonFiniteOrOutOfRangeTrafficIsRejected(string packet)
    {
        var traffic = new TrafficTable();
        using var fsd = Client(traffic);
        Assert.True(Handle(fsd, packet));
        Assert.Empty(traffic.Callsigns());
    }

    [Fact]
    public void InvalidUpdateDoesNotOverwriteValidState()
    {
        var traffic = new TrafficTable();
        using var fsd = Client(traffic);
        Assert.True(Handle(fsd, "#STCES456:31:121:5000:0:0:0"));
        Assert.True(Handle(fsd, "#STCES456:31:121:NaN:0:0:0"));
        Assert.True(Handle(fsd, "^CES456:31:121:Infinity:0:0:0:0:0:0:0:0:0"));
        var entry = Assert.Single(traffic.Snapshot());
        Assert.Equal(5000, entry.Altitude, 1);
        Assert.True(XPlaneTrafficBridge.Renderable(entry));
    }

    [Theory]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"-Infinity\"")]
    public void NonFiniteFlapsAreIgnored(string flaps)
    {
        var traffic = new TrafficTable();
        traffic.UpdatePosition("CES456", 31, 121, 5000, 0, 0, 0, false, 250);
        traffic.SetConfig("CES456", JsonNode.Parse($$"""{"flaps_pct":50}""")!.AsObject());
        traffic.SetConfig("CES456", JsonNode.Parse($$"""{"flaps_pct":{{flaps}}}""")!.AsObject());
        Assert.Equal(0.5, traffic.Get("CES456")!.Flaps);
    }

    [Fact]
    public void OneInvalidAircraftDoesNotSinkTheXPlaneFrame()
    {
        var good = new TrafficEntry { Callsign = "CCA123", Latitude = 31, Longitude = 121, Altitude = 5000 };
        var badAltitude = new TrafficEntry { Callsign = "BAD1", Latitude = 31, Longitude = 121, Altitude = double.NaN };
        var badAgl = new TrafficEntry { Callsign = "BAD2", Latitude = 31, Longitude = 121, Altitude = 5000, Agl = double.PositiveInfinity };
        var badFlaps = new TrafficEntry { Callsign = "BAD3", Latitude = 31, Longitude = 121, Altitude = 5000, Flaps = double.NaN };
        var own = new OwnAircraftState { Latitude = double.NaN, Longitude = 121 };

        var packets = XPlaneTrafficBridge.Encode(XPlaneTrafficBridge.Frame([badAltitude, good, badAgl, badFlaps], own, ""), 1);

        using var stream = new MemoryStream();
        foreach (var packet in packets)
        {
            using var part = JsonDocument.Parse(packet);
            stream.Write(Convert.FromBase64String(part.RootElement.GetProperty("data").GetString()!));
        }
        using var frame = JsonDocument.Parse(stream.ToArray());
        var plane = Assert.Single(frame.RootElement.GetProperty("aircraft").EnumerateArray());
        Assert.Equal("CCA123", plane.GetProperty("callsign").GetString());
        Assert.Equal(JsonValueKind.Null, frame.RootElement.GetProperty("own").ValueKind);
    }

    // ---------- #8 降噪器释放 ----------

    [Fact]
    public void DenoiserIgnoresFramesAfterDispose()
    {
        var denoiser = new Denoiser(Denoiser.Rate);
        var frame = new short[Denoiser.Frame * 2];
        denoiser.Process(frame);
        denoiser.Dispose();
        Assert.False(denoiser.Active);
        denoiser.Process(frame);
        denoiser.Dispose();
    }

    [Fact]
    public async Task DenoiserDisposeWhileProcessingDoesNotCrash()
    {
        var denoiser = new Denoiser(Denoiser.Rate);
        var frame = new short[Denoiser.Frame * 2];
        using var stop = new CancellationTokenSource();
        var worker = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                denoiser.Process(frame);
        });
        await Task.Delay(20);
        denoiser.Dispose();
        await Task.Delay(20);
        stop.Cancel();
        await worker.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(denoiser.Active);
    }
}
