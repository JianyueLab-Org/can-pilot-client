using System.Reflection;
using CanClient.Core.Voice;

namespace CanClient.Tests;

// Replace physical audio devices. Keep AudioEngine's reference counting,
// PCM assembly, event dispatch and VoiceSession's real callback/shutdown path.
internal sealed class VoiceCaptureHarness : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly CaptureDevice _device = new();

    public VoiceCaptureHarness(bool denoise = false, double gainDb = 6)
    {
        Audio = new AudioEngine();
        Field(typeof(AudioEngine), "_output").SetValue(Audio, new CaptureDevice());
        Field(typeof(AudioEngine), "_input").SetValue(Audio, _device);
        Audio.AcquireCapture(""); // Settings meter owns the first reference.
        Voice = AttachVoice(denoise, gainDb);
        CapturedCallback = Callback(Voice);
    }

    public AudioEngine Audio { get; }
    public VoiceSession Voice { get; }
    public Action<short[]> CapturedCallback { get; }
    public Action? OnDeviceDispose { set => _device.OnDispose = value; }
    public int DeviceDisposals => _device.Disposals;
    public VoiceSession CreateVoice(bool denoise = false, double gainDb = 6) =>
        new("localhost", 64738, "test", "", Audio, "", "",
            new VoiceOptions { Denoise = denoise, MicGainDb = gainDb });

    public VoiceSession AttachVoice(bool denoise = false, double gainDb = 6)
    {
        var voice = CreateVoice(denoise, gainDb);
        // Isolate the capture lifecycle from networking and playback. Start normally
        // transfers this reference and subscribes this same production callback.
        Audio.AcquireCapture("");
        Field(typeof(VoiceSession), "_running").SetValue(voice, true);
        Field(typeof(VoiceSession), "_captureAcquired").SetValue(voice, true);
        Audio.FrameCaptured += Callback(voice);
        return voice;
    }

    public void Feed(short[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        typeof(AudioEngine).GetMethod("OnPcm", PrivateInstance)!.Invoke(Audio, [bytes, bytes.Length]);
    }

    public static short[] Frame() => Enumerable.Repeat((short)1000, AudioEngine.FrameSamples).ToArray();

    public static Action<short[]> Callback(VoiceSession voice) =>
        typeof(VoiceSession).GetMethod("OnFrame", PrivateInstance)!.CreateDelegate<Action<short[]>>(voice);

    public static object ChannelLock(VoiceSession voice) => Field(typeof(VoiceSession), "_channelLock").GetValue(voice)!;

    public void Dispose()
    {
        Voice.Dispose();
        Audio.Dispose();
    }

    private static FieldInfo Field(Type type, string name) => type.GetField(name, PrivateInstance)!;

    private sealed class CaptureDevice : IDisposable
    {
        public int Disposals { get; private set; }
        public Action? OnDispose { get; set; }
        public void Dispose()
        {
            Disposals++;
            OnDispose?.Invoke();
        }
    }
}
