using CanClient.Core.Fsd;
using CanClient.Core.Sim;

namespace CanClient.Tests;

public class IdentTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void AircraftIdentIsReadFromSimConnect(double ident, bool expected)
    {
        var state = SimConnection.BuildState(new Dictionary<string, double> { ["ident"] = ident }, "Test");
        Assert.Equal(expected, state.TransponderIdent);
    }

    [Theory]
    [InlineData(true, false, TransponderMode.ModeC, 'Y')]
    [InlineData(false, true, TransponderMode.ModeC, 'Y')]
    [InlineData(true, false, TransponderMode.Standby, 'Y')]
    [InlineData(false, false, TransponderMode.ModeC, 'N')]
    [InlineData(false, false, TransponderMode.Standby, 'S')]
    public void NetworkModeHonoursBothIdentSources(bool aircraft, bool software, TransponderMode mode, char expected)
    {
        var state = new OwnAircraftState { TransponderIdent = aircraft, TransponderMode = mode };
        var actual = FsdClient.PositionMode(state, software);
        Assert.Equal(expected, actual);
        Assert.StartsWith($"@{expected}:", FsdProtocol.PositionPacket(actual, "CCA123", 1, state));
    }

    [Fact]
    public void CockpitAndSoftwareIdentShareIndicatorState()
    {
        using var fsd = new FsdClient("localhost", 6809, "CCA123", "1", "", "Test", 1, "A320", 10, null);
        fsd.UpdatePosition(new OwnAircraftState { TransponderIdent = true });
        Assert.True(fsd.IsIdentifying);
        fsd.UpdatePosition(new OwnAircraftState { TransponderIdent = false });
        Assert.False(fsd.IsIdentifying);
        fsd.Ident();
        fsd.UpdatePosition(new OwnAircraftState { TransponderIdent = false });
        Assert.True(fsd.IsIdentifying);
        fsd.Ident(seconds: -1);
        Assert.False(fsd.IsIdentifying);
    }
}
