using System.Runtime.InteropServices;
using CanClient.Core.Util;

namespace CanClient.Core.Voice;

/// <summary>
/// RNNoise 麦克风降噪，直接调 xiph RNNoise v0.2 的原生库（由 native/rnnoise 构建，随程序发布；
/// Windows 是 rnnoise.dll，Linux / macOS 是 librnnoise.so / librnnoise.dylib，DllImport 按平台补前后缀）。
/// 库不在、或者设备不是 48 kHz（模型只训练了 48 kHz）时直通不处理——降噪没了可以接受，发不出话不行。
/// </summary>
public sealed class Denoiser : IDisposable
{
    public const int Frame = 480;   // 一次处理 10 ms @ 48 kHz
    public const int Rate = 48000;

    private static readonly Logger Log = Util.Log.For("denoise");
    private static bool? _available;

    [DllImport("rnnoise", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rnnoise_create(IntPtr model);

    [DllImport("rnnoise", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe float rnnoise_process_frame(IntPtr state, float* output, float* input);

    [DllImport("rnnoise", CallingConvention = CallingConvention.Cdecl)]
    private static extern void rnnoise_destroy(IntPtr state);

    private IntPtr _state;
    private readonly float[] _buffer = new float[Frame];

    /// <summary>RNNoise 库能不能用。只试一次。</summary>
    public static bool Available
    {
        get
        {
            if (_available is { } known)
                return known;
            try
            {
                var state = rnnoise_create(IntPtr.Zero);
                if (state != IntPtr.Zero)
                    rnnoise_destroy(state);
                _available = state != IntPtr.Zero;
                Log.Info("loaded RNNoise");
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _available = false;
                Log.Warning($"RNNoise library not found; microphone noise suppression is unavailable ({e.Message})");
            }
            return _available.Value;
        }
    }

    /// <summary>设置开着且库可用，才算降噪在工作。</summary>
    public static bool Effective(bool setting) => setting && Available;

    public Denoiser(int rate)
    {
        RateHz = rate;
        if (!Available)
            return;
        if (rate != Rate)
        {
            Log.Warning($"noise suppression needs 48 kHz; the input runs at {rate} Hz, passing audio through");
            return;
        }
        _state = rnnoise_create(IntPtr.Zero);
    }

    public int RateHz { get; }

    public bool Active => _state != IntPtr.Zero;

    /// <summary>就地处理一帧（长度必须是 480 的整数倍）。</summary>
    public unsafe void Process(Span<short> samples)
    {
        if (!Active || samples.Length == 0 || samples.Length % Frame != 0)
            return;
        fixed (float* buffer = _buffer)
        {
            for (var offset = 0; offset < samples.Length; offset += Frame)
            {
                for (var i = 0; i < Frame; i++)
                    buffer[i] = samples[offset + i];
                rnnoise_process_frame(_state, buffer, buffer);
                for (var i = 0; i < Frame; i++)
                    samples[offset + i] = (short)Math.Clamp(MathF.Round(buffer[i]), -32768, 32767);
            }
        }
    }

    public void Dispose()
    {
        if (_state != IntPtr.Zero)
        {
            rnnoise_destroy(_state);
            _state = IntPtr.Zero;
        }
    }
}
