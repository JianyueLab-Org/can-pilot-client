using System.Runtime.InteropServices;
using CanClient.Core.Voice;
using CanClient.Tests;

internal static class Program
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly ManualResetEventSlim ProcessEntered = new();
    private static readonly ManualResetEventSlim ResumeProcess = new();
    private static readonly GateCallback Gate = OnProcess;
    private static bool _gateTimedOut;

    private static int Main(string[] args)
    {
        try
        {
            Require(args.Length == 2, "Usage: VoiceLifetimeSmoke <probe library> <real RNNoise library>");
            var probe = NativeLibrary.Load(Path.GetFullPath(args[0]));
            NativeLibrary.SetDllImportResolver(typeof(Denoiser).Assembly,
                (name, _, _) => name == "rnnoise" ? probe : IntPtr.Zero);
            NativeLibrary.SetDllImportResolver(typeof(Program).Assembly,
                (name, _, _) => name == "can_voice_probe" ? probe : IntPtr.Zero);
            Require(probe_open(Path.GetFullPath(args[1]), Gate) != 0, "Cannot load the real RNNoise exports");
            Require(Denoiser.Available, "RNNoise is unavailable");

            DirectDisposeWaitsForProcessing();
            ConcurrentProcessingSharesOneBufferSafely();
            SessionStopWaitsAndRejectsLateCallbacks();
            Require(probe_counter(4) == 0, "RNNoise state was destroyed during an in-flight native call");
            Require(!_gateTimedOut, "Native process gate timed out");
            Console.WriteLine("PASS: real RNNoise processing/disposal, late callbacks, repeated shutdown, meter capture and reconnect");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            ResumeProcess.Set();
            GC.KeepAlive(Gate);
        }
    }

    private static void DirectDisposeWaitsForProcessing()
    {
        using var denoiser = new Denoiser(Denoiser.Rate);
        Require(denoiser.Active, "RNNoise did not create a state");
        var samples = Enumerable.Repeat((short)1000, Denoiser.Frame).ToArray();
        var destroyed = probe_counter(1);
        Arm(1);
        using var frame = new Worker(() => denoiser.Process(samples));
        Worker? dispose = null;
        Worker? repeatedDispose = null;
        try
        {
            WaitForProcessing();
            dispose = new Worker(denoiser.Dispose);
            repeatedDispose = new Worker(denoiser.Dispose);
            dispose.RequireBlocked();
            repeatedDispose.RequireBlocked();
            Require(probe_counter(1) == destroyed, "Dispose freed state before Process returned");
        }
        finally
        {
            ResumeProcess.Set();
            frame.Finish();
            dispose?.Dispose();
            repeatedDispose?.Dispose();
        }
        Require(!samples.All(sample => sample == 1000), "Real RNNoise did not transform the input");
        Require(!denoiser.Active, "Disposed denoiser stayed active");
        Require(probe_counter(1) == destroyed + 1, "Repeated Dispose did not destroy exactly one state");
        var processed = probe_counter(2);
        var late = Enumerable.Repeat((short)1000, Denoiser.Frame).ToArray();
        denoiser.Process(late);
        denoiser.Dispose();
        Require(probe_counter(2) == processed, "Disposed denoiser called native processing again");
        Require(late.All(sample => sample == 1000), "Disposed denoiser changed late input");
    }

    private static void ConcurrentProcessingSharesOneBufferSafely()
    {
        using var denoiser = new Denoiser(Denoiser.Rate);
        var first = Enumerable.Repeat((short)1000, Denoiser.Frame).ToArray();
        var second = Enumerable.Repeat((short)2000, Denoiser.Frame).ToArray();
        var processed = probe_counter(2);
        Arm(1);
        using var frame = new Worker(() => denoiser.Process(first));
        Worker? nextFrame = null;
        try
        {
            WaitForProcessing();
            nextFrame = new Worker(() => denoiser.Process(second));
            nextFrame.RequireBlocked();
            Require(probe_counter(2) == processed + 1, "Concurrent Process reused the native state/buffer");
        }
        finally
        {
            ResumeProcess.Set();
            frame.Finish();
            nextFrame?.Dispose();
        }
        Require(probe_counter(2) == processed + 2, "Serialized frame never reached RNNoise");
        Require(!second.All(sample => sample == 2000), "Second real RNNoise frame was not processed");
    }

    private static void SessionStopWaitsAndRejectsLateCallbacks()
    {
        using var harness = new VoiceCaptureHarness(denoise: true);
        var meterFrames = 0;
        harness.Audio.FrameCaptured += _ => meterFrames++;
        var destroyed = probe_counter(1);
        // AudioEngine delivers 20 ms; gate the second 10 ms RNNoise call.
        Arm(2);
        using var frame = new Worker(() => harness.Feed(VoiceCaptureHarness.Frame()));
        Worker? stop = null;
        try
        {
            WaitForProcessing();
            stop = new Worker(harness.Voice.Stop);
            stop.RequireBlocked();
            Require(probe_counter(1) == destroyed, "Session freed DSP during its capture callback");
        }
        finally
        {
            ResumeProcess.Set();
            frame.Finish();
            stop?.Dispose();
        }
        harness.Voice.Stop();
        harness.Voice.Dispose();
        Require(probe_counter(1) == destroyed + 1, "Session did not dispose exactly one denoiser");
        var created = probe_counter(0);
        var processed = probe_counter(2);
        var late = VoiceCaptureHarness.Frame();
        harness.CapturedCallback(late);
        Require(probe_counter(0) == created, "Late callback recreated session DSP");
        Require(probe_counter(2) == processed, "Late callback used session DSP");
        Require(late.All(sample => sample == 1000), "Stopped session processed late samples");
        harness.Feed(VoiceCaptureHarness.Frame());
        Require(meterFrames == 2 && harness.Audio.Capturing && harness.DeviceDisposals == 0,
            "Disconnect stopped the meter's shared capture");

        // ClientController reconnects with a new VoiceSession on the same AudioEngine.
        using var reconnected = harness.AttachVoice(denoise: true);
        harness.Feed(VoiceCaptureHarness.Frame());
        Require(probe_counter(0) == created + 1 && probe_counter(2) == processed + 2,
            "Reconnected session did not create/process its own DSP");
        harness.CapturedCallback(late);
        Require(probe_counter(2) == processed + 2, "Old callback became active after reconnect");
        reconnected.Stop();
        harness.Audio.ReleaseCapture();
        Require(!harness.Audio.Capturing && harness.DeviceDisposals == 1,
            "Last capture consumer did not close the microphone exactly once");
    }

    private static void Arm(int frames)
    {
        ProcessEntered.Reset();
        ResumeProcess.Reset();
        probe_arm(frames);
    }

    private static void WaitForProcessing()
    {
        Require(ProcessEntered.Wait(Timeout), "Real RNNoise processing did not reach the gate");
        Require(probe_counter(3) == 1, "Expected one in-flight native call");
    }

    private static void OnProcess()
    {
        ProcessEntered.Set();
        if (!ResumeProcess.Wait(Timeout))
            _gateTimedOut = true;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Worker : IDisposable
    {
        private readonly Thread _thread;
        private Exception? _error;

        public Worker(Action action)
        {
            _thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception error) { _error = error; }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void RequireBlocked()
        {
            // Observe an actual monitor wait or completion, not a sleep-based race.
            Require(SpinWait.SpinUntil(() => !_thread.IsAlive ||
                (_thread.ThreadState & ThreadState.WaitSleepJoin) != 0, Timeout), "Worker never settled");
            Require(_thread.IsAlive, "Lifecycle operation completed while native processing was in flight");
        }

        public void Finish()
        {
            Require(_thread.Join(Timeout), "Worker did not finish after native processing resumed");
            if (_error is not null)
                throw new InvalidOperationException("Worker failed", _error);
        }

        public void Dispose() => Finish();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GateCallback();

    [DllImport("can_voice_probe", CallingConvention = CallingConvention.Cdecl)]
    private static extern int probe_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, GateCallback gate);

    [DllImport("can_voice_probe", CallingConvention = CallingConvention.Cdecl)]
    private static extern void probe_arm(int frames);

    [DllImport("can_voice_probe", CallingConvention = CallingConvention.Cdecl)]
    private static extern int probe_counter(int counter);
}
