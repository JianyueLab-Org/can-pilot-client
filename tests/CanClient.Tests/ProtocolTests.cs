using CanClient.Core;
using CanClient.Core.Fsd;
using CanClient.Core.Sim;
using CanClient.Core.Voice;

namespace CanClient.Tests;

public class FsdProtocolTests
{
    [Fact]
    public void PackPbhMatchesServerLayout()
    {
        Assert.Equal(256u << 2, FsdProtocol.PackPbh(0, 0, 90, false));
        Assert.Equal((256u << 2) | 2, FsdProtocol.PackPbh(0, 0, 90, true));
        // 负角度先折到 0..360 再量化：-10° → 350° → 996
        Assert.Equal(996u << 22, FsdProtocol.PackPbh(-10, 0, 0, false));
    }

    [Theory]
    [InlineData(5.0, -12.5, 271.0, true)]
    [InlineData(-3.2, 25.0, 0.0, false)]
    [InlineData(0.0, 0.0, 359.9, false)]
    public void PbhRoundTripsWithinQuantisation(double pitch, double bank, double heading, bool onGround)
    {
        var a = FsdProtocol.UnpackPbh(FsdProtocol.PackPbh(pitch, bank, heading, onGround));
        const double step = 360.0 / 1024.0;
        Assert.InRange(a.Pitch, pitch - step, pitch + step);
        Assert.InRange(a.Bank, bank - step, bank + step);
        var dh = Math.Abs(a.Heading - heading) % 360.0;
        Assert.True(Math.Min(dh, 360 - dh) <= step);
        Assert.Equal(onGround, a.OnGround);
    }

    [Fact]
    public void CallsignRules()
    {
        Assert.Null(FsdProtocol.CallsignProblem("CCA1501"));
        Assert.NotNull(FsdProtocol.CallsignProblem("C"));
        Assert.NotNull(FsdProtocol.CallsignProblem("CCA1501ABCDEF"));
        Assert.NotNull(FsdProtocol.CallsignProblem("CCA 15"));
    }

    [Fact]
    public void WallopIsTranslatedAndOtherDotsPassThrough()
    {
        Assert.Equal(("*S", "求助 内容"), FsdProtocol.ParseDotCommand(".WALLOP 求助 内容"));
        Assert.Equal((null, ".hello there"), FsdProtocol.ParseDotCommand(".hello there"));
        Assert.Equal((null, "plain"), FsdProtocol.ParseDotCommand(" plain "));
    }

    [Fact]
    public void Frequencies()
    {
        Assert.Equal("@28500", FsdProtocol.FrequencyRecipient(128.5));
        Assert.Equal("@21800", FsdProtocol.FrequencyRecipient(121.8));
        Assert.Equal("128.500", FsdProtocol.DisplayFrequency("28500"));
        Assert.Equal("FREQ_125400", VoiceSession.ChannelName(125.4));
    }

    [Fact]
    public void FastPositionFieldCounts()
    {
        var state = new OwnAircraftState { Latitude = 31.1, Longitude = 121.3, NetworkAltitude = 3000, VelocityEast = 50 };
        Assert.Equal(13, FsdProtocol.FastPositionPacket("^", "CCA1", state).Split(':').Length);
        Assert.Equal(7, FsdProtocol.FastPositionPacket("#ST", "CCA1", state).Split(':').Length);
        Assert.False(FsdProtocol.IsStopped(state));
        Assert.True(FsdProtocol.IsStopped(new OwnAircraftState()));
    }

    [Fact]
    public void PositionPacketUsesNetworkAltitudeAndDelta()
    {
        var state = new OwnAircraftState
        {
            Latitude = 31.123456, Longitude = 121.5, NetworkAltitude = 5100, PressureDelta = -100, Squawk = 1200,
            Groundspeed = 250
        };
        var fields = FsdProtocol.PositionPacket('N', "CCA1", 1, state).Split(':');
        Assert.Equal("@N", fields[0]);
        Assert.Equal("1200", fields[2]);
        Assert.Equal("31.12346", fields[4]);
        Assert.Equal("5100", fields[6]);
        Assert.Equal("-100", fields[9]);
    }

    [Fact]
    public void RedactHidesThePassword()
    {
        Assert.Equal("#APCCA1:SERVER:123:***:1:101:10:Name", FsdClient.Redact("#APCCA1:SERVER:123:secret:1:101:10:Name"));
    }
}

public class SimTests
{
    [Theory]
    [InlineData(0x1200, 1200)]
    [InlineData(0x7700, 7700)]
    [InlineData(1200, 1200)]     // 已经是十进制
    [InlineData(4608, 1200)]     // 0x1200 当十进制读出来的样子
    [InlineData(1290, 2000)]     // 不是八进制
    public void SquawkDecoding(int raw, int expected) => Assert.Equal(expected, SimConnection.BcdToSquawk(raw));

    [Fact]
    public void StandbyOnlyCountsWhenParked()
    {
        Assert.Equal(TransponderMode.Standby, SimConnection.TransponderModeFor(1, true, 0));
        Assert.Equal(TransponderMode.ModeC, SimConnection.TransponderModeFor(1, false, 200));
        Assert.Equal(TransponderMode.ModeC, SimConnection.TransponderModeFor(4, true, 0));
        Assert.Equal(TransponderMode.ModeC, SimConnection.TransponderModeFor(null, true, 0));
    }

    [Fact]
    public void ComFrequencyFilter()
    {
        Assert.Equal(121.8, SimConnection.Frequency(121.8000001));
        Assert.Null(SimConnection.Frequency(0));
        Assert.Null(SimConnection.Frequency(108.0));
    }

    [Fact]
    public void BuildStateFlipsSimVarSigns()
    {
        var state = SimConnection.BuildState(new Dictionary<string, double>
        {
            ["lat"] = 31, ["lon"] = 121, ["alt"] = 1000, ["pitch"] = -5, ["bank"] = 10, ["hdg"] = -90,
            ["rx"] = 0.1, ["ry"] = 0.2, ["rz"] = 0.3
        }, "Test");
        Assert.Equal(5, state.Pitch);
        Assert.Equal(-10, state.Bank);
        Assert.Equal(270, state.Heading);
        Assert.True(state.PitchRate < 0 && state.HeadingRate > 0 && state.BankRate < 0);
    }

    [Fact]
    public void GroundClampKeepsAircraftOnLocalGround()
    {
        var clamp = new GroundClamp();
        // 对方在地面，报 500 ft；本地地面 520 ft，模型离地 5 ft → 525
        Assert.Equal(525, clamp.Update(0, 0, 500, 0, true, 520, 5), 3);
        // 高空不管地面
        Assert.Equal(20000, new GroundClamp().Update(0, 0, 20000, 15000, false, 520), 3);
    }
}

public class AltitudeTests
{
    [Fact]
    public void StandardDayHasNoTemperatureError()
    {
        var (network, pressure, error) = Altitude.Msfs(5000, 1013.25, 5000);
        Assert.Equal(5000, network, 3);
        Assert.Equal(5000, pressure, 3);
        Assert.Equal(0, error, 3);
    }

    [Fact]
    public void BadPressureAltitudeFallsBackToTrueAltitude()
    {
        Assert.Equal((5000.0, 5000.0, 0.0), Altitude.Msfs(5000, 1013.25, 5000 * 0.3048 * 100));
    }

    [Fact]
    public void IsaRoundTrip()
    {
        var p = Altitude.PressureAltitude(8000, 1030);
        Assert.Equal(8000, Altitude.IsaAltitude(p, 1030), 6);
    }

    [Fact]
    public void IncomingAdjustmentWeights()
    {
        Assert.Equal(4800, Altitude.AdjustIncoming(5000, 5000, 200), 6);
        Assert.Equal(4900, Altitude.AdjustIncoming(5000, 500, 200), 6);    // 4500 ft 外，权重一半
        Assert.Equal(5000, Altitude.AdjustIncoming(5000, -2000, 200), 6);  // 7000 ft 外不修
        Assert.Equal(5000, Altitude.AdjustIncoming(5000, null, 200), 6);
    }
}

public class ObserverAndChimeTests
{
    [Theory]
    [InlineData("121.8", 121.8)]
    [InlineData("121800", 121.8)]
    [InlineData("136.9754", 136.975)]
    [InlineData("137.0004", null)]
    [InlineData("abc", null)]
    public void ParseFrequency(string text, double? expected) => Assert.Equal(expected, Observer.ParseFrequency(text));

    [Fact]
    public void ManualFrequencyOnlyForObservers()
    {
        Assert.Equal(122.8, Observer.FrequencyFor(121.8, true, "122.8", true));
        Assert.Equal(121.8, Observer.FrequencyFor(121.8, true, "122.8", false));
        Assert.Null(Observer.FrequencyFor(null, true, "", false));
    }

    [Fact]
    public void ChimeRules()
    {
        Assert.True(Chime.WantsAlert("CCA150", "ZSSS_APP", "CCA150", "hello", false));
        Assert.False(Chime.WantsAlert("CCA150", "ZSSS_APP", "@28500", "CCA1501 descend", false));
        Assert.True(Chime.WantsAlert("CCA150", "ZSSS_APP", "@28500", "CCA150, descend", false));
        Assert.True(Chime.WantsAlert("CCA150", "ZSSS_APP", "@28500", "anything", true));
        Assert.False(Chime.WantsAlert("CCA150", "CCA150", "@28500", "CCA150", true));
        Assert.True(Chime.WantsAlert("CCA150", "SUP", "*", "broadcast", false));
    }
}

public class CryptTests
{
    private static readonly byte[] RawKey = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] Nonce = Enumerable.Range(0, 16).Select(i => (byte)(0xff - i * 0x11)).ToArray();

    [Fact]
    public void KrovetzTestVectors()
    {
        using var cs = new CryptStateOcb2();
        cs.SetKey(RawKey, RawKey, RawKey);
        var tag = new byte[16];
        Assert.True(cs.OcbEncrypt([], [], RawKey, tag));
        Assert.Equal(Convert.FromHexString("BF310813077 3AD5EC70EC69E7875A7B0".Replace(" ", "")), tag);

        var source = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        var crypt = new byte[40];
        Assert.True(cs.OcbEncrypt(source, crypt, RawKey, tag));
        Assert.Equal(Convert.FromHexString("9DB0CDF880F73E3E10D4EB3217766688"), tag);
        Assert.Equal(Convert.FromHexString("F75D6BC8B4DC8D66B836A2B08B32A6369F1CD3C5228D79FD6C267F5F6AA7B231C7DFB9D59951AE9C"), crypt);
    }

    [Fact]
    public void AuthCryptRoundTrip()
    {
        using var cs = new CryptStateOcb2();
        for (var length = 0; length < 128; length++)
        {
            cs.SetKey(RawKey, Nonce, Nonce);
            var src = Enumerable.Range(1, length).Select(i => (byte)i).ToArray();
            var encrypted = new byte[length];
            var encTag = new byte[16];
            Assert.True(cs.OcbEncrypt(src, encrypted, Nonce, encTag));
            var decrypted = new byte[length];
            var decTag = new byte[16];
            Assert.True(cs.OcbDecrypt(encrypted, decrypted, Nonce, decTag));
            Assert.Equal(encTag, decTag);
            Assert.Equal(src, decrypted);
        }
    }

    [Fact]
    public void PacketsRoundTripAndRejectReplayAndTampering()
    {
        using var client = new CryptStateOcb2();
        using var server = new CryptStateOcb2();
        var a = Enumerable.Repeat((byte)0x55, 16).ToArray();
        var b = Enumerable.Repeat((byte)0x22, 16).ToArray();
        client.SetKey(RawKey, a, b);
        server.SetKey(RawKey, b, a);
        var message = "It was a funky funky town!"u8.ToArray();
        var packet = client.Encrypt(message)!;
        var copy = (byte[])packet.Clone();
        copy[6] ^= 1;
        Assert.Null(server.Decrypt(copy));
        Assert.Equal(message, server.Decrypt(packet));
        Assert.Null(server.Decrypt(packet));    // 重放

        for (var i = 0; i < 16; i++)
            packet = client.Encrypt(message)!;
        Assert.Equal(message, server.Decrypt(packet));    // 丢了 15 个也能接上
    }

    [Fact]
    public void XexStarAttackIsRefused()
    {
        using var cs = new CryptStateOcb2();
        cs.SetKey(RawKey, Nonce, Nonce);
        var src = new byte[32];
        src[15] = 16 * 8;
        for (var i = 16; i < 32; i++)
            src[i] = 42;
        Assert.False(cs.OcbEncrypt(src, new byte[32], Nonce, new byte[16]));
    }
}
