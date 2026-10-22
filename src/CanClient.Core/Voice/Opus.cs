using System.Runtime.InteropServices;

namespace CanClient.Core.Voice;

/// <summary>opus.dll（随程序发布）的最小封装。48 kHz 单声道，20 ms 一帧。</summary>
internal static class OpusNative
{
    private const string Dll = "opus.dll";

    public const int ApplicationVoip = 2048;
    public const int ApplicationAudio = 2049;
    public const int SetBitrateRequest = 4002;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr opus_encoder_create(int fs, int channels, int application, out int error);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void opus_encoder_destroy(IntPtr encoder);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe int opus_encode(IntPtr encoder, short* pcm, int frameSize, byte* data, int maxDataBytes);

    // opus_encoder_ctl 是变参函数；Windows x64 上整数变参和普通参数走同一套寄存器，这样声明是对的
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_encoder_ctl(IntPtr encoder, int request, int value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr opus_decoder_create(int fs, int channels, out int error);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void opus_decoder_destroy(IntPtr decoder);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe int opus_decode(IntPtr decoder, byte* data, int length, short* pcm, int frameSize, int decodeFec);
}

public sealed class OpusEncoder : IDisposable
{
    private IntPtr _handle;

    public OpusEncoder(int sampleRate = 48000, int channels = 1, int application = OpusNative.ApplicationAudio)
    {
        _handle = OpusNative.opus_encoder_create(sampleRate, channels, application, out var error);
        if (_handle == IntPtr.Zero || error != 0)
            throw new InvalidOperationException($"opus_encoder_create failed ({error})");
    }

    public int Bitrate
    {
        set => OpusNative.opus_encoder_ctl(_handle, OpusNative.SetBitrateRequest, value);
    }

    public unsafe byte[] Encode(ReadOnlySpan<short> pcm)
    {
        Span<byte> buffer = stackalloc byte[1275];
        int length;
        fixed (short* input = pcm)
        fixed (byte* output = buffer)
            length = OpusNative.opus_encode(_handle, input, pcm.Length, output, buffer.Length);
        if (length < 0)
            throw new InvalidOperationException($"opus_encode failed ({length})");
        return buffer[..length].ToArray();
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            OpusNative.opus_encoder_destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

public sealed class OpusDecoder : IDisposable
{
    // 一个 Opus 包最多 120 ms
    public const int MaxFrameSamples = 48000 * 120 / 1000;

    private IntPtr _handle;

    public OpusDecoder(int sampleRate = 48000, int channels = 1)
    {
        _handle = OpusNative.opus_decoder_create(sampleRate, channels, out var error);
        if (_handle == IntPtr.Zero || error != 0)
            throw new InvalidOperationException($"opus_decoder_create failed ({error})");
    }

    /// <summary>解一个包，返回采样数。data 为空表示丢包补偿。</summary>
    public unsafe int Decode(ReadOnlySpan<byte> data, Span<short> pcm)
    {
        fixed (byte* input = data)
        fixed (short* output = pcm)
        {
            var samples = OpusNative.opus_decode(_handle, data.IsEmpty ? null : input, data.Length, output, pcm.Length, 0);
            return samples < 0 ? 0 : samples;
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            OpusNative.opus_decoder_destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
