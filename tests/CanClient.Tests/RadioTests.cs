using System.Reflection;
using CanClient.Core.Sim;
using CanClient.Core.Voice;
using Google.Protobuf;

namespace CanClient.Tests;

public class RadioTests
{
    [Fact]
    public void SwitchingTxSwapsPrimaryAndListenerAndClearsLostAircraft()
    {
        using var audio = new AudioEngine();
        using var voice = new VoiceSession("localhost", 64738, "test", "", audio, "", "", new VoiceOptions());
        voice.SetRadios(121.8, true, 125.4, true, 1);
        AssertRadioTargets(voice, 1, 121.8, 125.4);
        voice.SetRadios(121.8, true, 125.4, true, 2);
        AssertRadioTargets(voice, 2, 125.4, 121.8);
        voice.SetRadios(121.8, false, 125.4, true, 2);
        AssertRadioTargets(voice, 2, 125.4, null);
        voice.SetRadios(121.8, true, 125.4, true, 0);
        AssertRadioTargets(voice, 0, 121.8, 125.4);
        voice.SetRadios(null, false, null, false, 0);
        AssertRadioTargets(voice, 0, null, null);
    }

    private static void AssertRadioTargets(VoiceSession voice, int tx, double? primary, double? listener)
    {
        Assert.Equal(tx, voice.TransmitRadio);
        Assert.Equal(primary, typeof(VoiceSession).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(voice));
        Assert.Equal(listener, typeof(VoiceSession).GetField("_pendingListener", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(voice));
    }

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 2)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 0)]
    public void AircraftTransmitSelectionIsExclusive(bool tx1, bool tx2, int expected)
    {
        var state = SimConnection.BuildState(new Dictionary<string, double>
        {
            ["com1"] = 121.8, ["com2"] = 125.4,
            ["com1tx"] = tx1 ? 1 : 0, ["com2tx"] = tx2 ? 1 : 0,
            ["com1rx"] = 1, ["com2rx"] = 0,
            ["com1power"] = 0, ["com2power"] = 1
        }, "Test aircraft");
        Assert.Equal(expected, state.TransmitRadio);
        Assert.Equal(121.8, state.Com1);
        Assert.Equal(125.4, state.Com2);
        Assert.True(state.Com1Receiving);
        Assert.False(state.Com2Receiving);
        Assert.True(state.Com1Power);
        Assert.False(state.Com2Power);
    }

    [Fact]
    public void MissingOptionalSimVarsKeepLegacyAircraftUsable()
    {
        var state = SimConnection.BuildState(new Dictionary<string, double>(), "Legacy");
        Assert.Equal(1, state.TransmitRadio);
        Assert.True(state.Com1Receiving && state.Com2Receiving);
        Assert.True(state.Com1Power && state.Com2Power);
    }

    [Fact]
    public void LegacySimVarsCannotDisableVoiceReception()
    {
        var state = SimConnection.BuildState(new Dictionary<string, double>
        {
            ["com1"] = 121.8, ["com2"] = 125.4,
            ["com1rx"] = 0, ["com2rx"] = 0
        }, "P3D", legacy: true);

        Assert.True(state.Com1Receiving);
        Assert.True(state.Com2Receiving);
    }

    [Fact]
    public void ModernSimVarsStillControlVoiceReception()
    {
        var state = SimConnection.BuildState(new Dictionary<string, double>
        {
            ["com1"] = 121.8, ["com2"] = 125.4,
            ["com1rx"] = 0, ["com2rx"] = 1
        }, "MSFS");

        Assert.False(state.Com1Receiving);
        Assert.True(state.Com2Receiving);
    }

    [Fact]
    public void ServerAcknowledgedListenersSurviveMovesButNotRemovalOrReconnect()
    {
        using var connection = new MumbleConnection("localhost", 64738, "test", "");
        Handle(connection, 5, new MumbleProto.ServerSync { Session = 42 });
        var other = new MumbleProto.UserState { Session = 99 };
        other.ListeningChannelAdd.Add(10);
        Handle(connection, 9, other);
        Assert.Empty(connection.ListeningChannels);

        var mine = new MumbleProto.UserState { Session = 42, ChannelId = 1 };
        mine.ListeningChannelAdd.Add(10);
        mine.ListeningChannelAdd.Add(11);
        Handle(connection, 9, mine);
        Handle(connection, 9, new MumbleProto.UserState { Session = 42, ChannelId = 2 });
        Assert.Equal(new uint[] { 10, 11 }, connection.ListeningChannels.Order().ToArray());
        Assert.Equal(2u, connection.Myself?.ChannelId);

        var remove = new MumbleProto.UserState { Session = 42 };
        remove.ListeningChannelRemove.Add(10);
        Handle(connection, 9, remove);
        Assert.Equal(new uint[] { 11 }, connection.ListeningChannels);
        Handle(connection, 6, new MumbleProto.ChannelRemove { ChannelId = 11 });
        Assert.Empty(connection.ListeningChannels);

        using var reconnected = new MumbleConnection("localhost", 64738, "test", "");
        Handle(reconnected, 5, new MumbleProto.ServerSync { Session = 43 });
        Assert.Empty(reconnected.ListeningChannels);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(3u)]
    public void AudioPreservesReceiveContextForRadioIndicators(uint context)
    {
        using var connection = new MumbleConnection("localhost", 64738, "test", "");
        uint? received = null;
        connection.AudioReceived += (session, frame, opus, end, actualContext) => received = actualContext;
        var audio = new MumbleUDP.Audio
        {
            SenderSession = 99, Context = context, OpusData = ByteString.CopyFrom(new byte[] { 1, 2 })
        };
        var packet = new byte[audio.CalculateSize() + 1];
        audio.ToByteArray().CopyTo(packet, 1);
        typeof(MumbleConnection).GetMethod("HandleMedia", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(connection, [packet]);
        Assert.Equal(context, received);
    }

    private static void Handle(MumbleConnection connection, ushort type, IMessage message)
    {
        var method = typeof(MumbleConnection).GetMethod("Handle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var messageType = Enum.ToObject(method.GetParameters()[0].ParameterType, type);
        method.Invoke(connection, [messageType, message.ToByteArray()]);
    }
}
