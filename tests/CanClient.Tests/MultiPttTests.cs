using CanClient.Core.Ptt;

namespace CanClient.Tests;

public class MultiPttTests
{
    [Fact]
    public void OverlappingInputsOnlyStopWhenAllReleased()
    {
        PttBinding[] bindings = [PttBinding.Keyboard(65), PttBinding.Mouse("x1"), PttBinding.Joystick(0, 2, "Stick")];
        var keys = new HashSet<int>();
        var buttons = 0;
        bool Down() => PttWatcher.AnyDown(bindings, keys.Contains, _ => buttons);
        Assert.False(Down());
        keys.Add(65);
        Assert.True(Down());
        buttons = 4;
        keys.Clear();
        Assert.True(Down());
        keys.Add(5);
        buttons = 0;
        Assert.True(Down());
        keys.Clear();
        Assert.False(Down());
    }
}
