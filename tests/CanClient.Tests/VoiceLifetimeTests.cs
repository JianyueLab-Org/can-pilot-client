using CanClient.Core.Voice;

namespace CanClient.Tests;

public class VoiceLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void CallbackCapturedBeforeStopCannotProcessAfterStop()
    {
        using var harness = new VoiceCaptureHarness();
        var levels = 0;
        harness.Voice.Level += _ => levels++;
        harness.Feed(VoiceCaptureHarness.Frame());
        Assert.Equal(1, levels);

        harness.Voice.Stop();
        var lateFrame = VoiceCaptureHarness.Frame();
        harness.CapturedCallback(lateFrame);

        Assert.Equal(1, levels);
        Assert.All(lateFrame, sample => Assert.Equal((short)1000, sample));
    }

    [Fact]
    public void DisconnectReleasesOnlyVoiceReferenceAndMeterKeepsReceiving()
    {
        using var harness = new VoiceCaptureHarness();
        var meterFrames = 0;
        harness.Audio.FrameCaptured += _ => meterFrames++;
        harness.Feed(VoiceCaptureHarness.Frame());
        harness.Voice.Stop();
        harness.Voice.Stop();
        harness.Voice.Dispose();
        harness.Feed(VoiceCaptureHarness.Frame());

        Assert.Equal(2, meterFrames);
        Assert.True(harness.Audio.Capturing);
        Assert.Equal(0, harness.DeviceDisposals);
        harness.Audio.ReleaseCapture();
        Assert.False(harness.Audio.Capturing);
        Assert.Equal(1, harness.DeviceDisposals);
    }

    [Fact]
    public void NewSessionProcessesWhileOldCallbackStaysStopped()
    {
        using var harness = new VoiceCaptureHarness();
        harness.Voice.Stop();
        using var reconnected = harness.AttachVoice();
        var levels = 0;
        reconnected.Level += _ => levels++;
        harness.Feed(VoiceCaptureHarness.Frame());
        Assert.Equal(1, levels);

        var oldFrame = VoiceCaptureHarness.Frame();
        harness.CapturedCallback(oldFrame);
        Assert.All(oldFrame, sample => Assert.Equal((short)1000, sample));
        reconnected.Stop();
        VoiceCaptureHarness.Callback(reconnected)(oldFrame);
        Assert.Equal(1, levels);
        Assert.All(oldFrame, sample => Assert.Equal((short)1000, sample));
        Assert.True(harness.Audio.Capturing);
    }

    [Fact]
    public async Task ChannelSwitchLockDoesNotDelayMicrophoneProcessing()
    {
        using var harness = new VoiceCaptureHarness();
        using var level = new ManualResetEventSlim();
        harness.Voice.Level += _ => level.Set();
        Task frame;
        lock (VoiceCaptureHarness.ChannelLock(harness.Voice))
        {
            frame = Task.Run(() => harness.Feed(VoiceCaptureHarness.Frame()));
            Assert.True(level.Wait(Timeout), "Microphone DSP waited for the channel switch lock");
        }
        await frame.WaitAsync(Timeout);
    }

    [Fact]
    public async Task LevelSubscriberCanWaitForShutdownWithoutHoldingDspLock()
    {
        using var harness = new VoiceCaptureHarness();
        using var levelEntered = new ManualResetEventSlim();
        using var leaveLevel = new ManualResetEventSlim();
        harness.Voice.Level += _ =>
        {
            levelEntered.Set();
            leaveLevel.Wait(Timeout);
        };
        var frame = Task.Run(() => harness.Feed(VoiceCaptureHarness.Frame()));
        try
        {
            Assert.True(levelEntered.Wait(Timeout));
            var stop = Task.Run(harness.Voice.Stop);
            await stop.WaitAsync(Timeout);
            Assert.True(harness.Audio.Capturing);
        }
        finally
        {
            leaveLevel.Set();
            await frame.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task ConcurrentShutdownDoesNotReleaseMeterReference()
    {
        using var harness = new VoiceCaptureHarness();
        using var start = new ManualResetEventSlim();
        var stops = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.Wait(Timeout);
            harness.Voice.Stop();
        })).ToArray();
        start.Set();
        await Task.WhenAll(stops).WaitAsync(Timeout);
        Assert.True(harness.Audio.Capturing);
        Assert.Equal(0, harness.DeviceDisposals);
        harness.Audio.ReleaseCapture();
        Assert.Equal(1, harness.DeviceDisposals);
    }

    [Fact]
    public async Task LastCaptureReleaseCanJoinALateCallbackWithoutDspLockInversion()
    {
        using var harness = new VoiceCaptureHarness();
        harness.Audio.ReleaseCapture(); // Meter closes first; voice is now the last owner.
        var joined = false;
        Task? callback = null;
        harness.OnDeviceDispose = () =>
        {
            callback = Task.Run(() => harness.CapturedCallback(VoiceCaptureHarness.Frame()));
            joined = callback.Wait(Timeout);
        };

        harness.Voice.Stop();

        Assert.True(joined, "Capture close waited for a callback blocked by the session DSP lock");
        await callback!.WaitAsync(Timeout);
        Assert.False(harness.Audio.Capturing);
        Assert.Equal(1, harness.DeviceDisposals);
    }

    [Fact]
    public void StopDuringStartupDoesNotTransferAnotherCaptureReference()
    {
        using var harness = new VoiceCaptureHarness();
        harness.Voice.Stop();
        using var starting = harness.CreateVoice();
        starting.StatusChanged += (state, _) =>
        {
            if (state == CanClient.Core.Fsd.LinkState.Connecting)
                starting.Stop();
        };

        starting.Start();
        harness.Audio.ReleaseCapture();

        Assert.False(harness.Audio.Capturing);
        Assert.Equal(1, harness.DeviceDisposals);
        starting.Dispose();
    }

    [Fact]
    public void StoppedSessionCannotRestartItsCaptureLifecycle()
    {
        using var harness = new VoiceCaptureHarness();
        harness.Voice.Stop();
        var updates = 0;
        harness.Voice.StatusChanged += (_, _) => updates++;

        harness.Voice.Start();
        harness.Feed(VoiceCaptureHarness.Frame());

        Assert.Equal(0, updates);
        Assert.True(harness.Audio.Capturing);
        harness.Audio.ReleaseCapture();
        Assert.Equal(1, harness.DeviceDisposals);
    }
}
