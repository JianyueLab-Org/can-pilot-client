using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using NAudio.Wave;

namespace CanClient.Core.Voice;

/// <summary>
/// macOS / Linux 的音频走 OpenAL Soft（随包的 libopenal，来自 Silk.NET.OpenAL.Soft.Native）。
/// 它在 Linux 上接 PipeWire / PulseAudio / ALSA，在 macOS 上接 CoreAudio，采集和播放都支持按名字选设备。
/// 只声明用得到的几个函数；随包的库加载不上时退到系统自带的 OpenAL。
/// </summary>
internal static unsafe class Al
{
    private const string Lib = "openal";

    public const int FormatMono16 = 0x1101;
    public const int SourceState = 0x1010;
    public const int Playing = 0x1012;
    public const int BuffersProcessed = 0x1016;
    public const int AlcFrequency = 0x1007;
    public const int AlcDeviceSpecifier = 0x1005;
    public const int AlcAllDevicesSpecifier = 0x1013;
    public const int AlcCaptureDeviceSpecifier = 0x310;
    public const int AlcCaptureSamples = 0x312;

    static Al()
    {
        // 默认探测（程序目录、单文件解压目录）找不到 libopenal 时才走这里
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) => name == Lib ? LoadSystemOpenAl() : IntPtr.Zero;
    }

    private static IntPtr LoadSystemOpenAl()
    {
        string[] candidates = OperatingSystem.IsMacOS()
            ? ["/System/Library/Frameworks/OpenAL.framework/OpenAL", "libopenal.1.dylib"]
            : ["libopenal.so.1", "libopenal.so"];
        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }
        return IntPtr.Zero;
    }

    [DllImport(Lib)] public static extern IntPtr alcOpenDevice([MarshalAs(UnmanagedType.LPUTF8Str)] string? name);
    [DllImport(Lib)] public static extern byte alcCloseDevice(IntPtr device);
    [DllImport(Lib)] public static extern IntPtr alcCreateContext(IntPtr device, int* attributes);
    [DllImport(Lib)] public static extern byte alcMakeContextCurrent(IntPtr context);
    [DllImport(Lib)] public static extern void alcDestroyContext(IntPtr context);
    [DllImport(Lib)] public static extern IntPtr alcGetString(IntPtr device, int param);
    [DllImport(Lib)] public static extern byte alcIsExtensionPresent(IntPtr device, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] public static extern int alcGetError(IntPtr device);
    [DllImport(Lib)] public static extern IntPtr alcCaptureOpenDevice([MarshalAs(UnmanagedType.LPUTF8Str)] string? name, uint frequency, int format, int bufferSize);
    [DllImport(Lib)] public static extern byte alcCaptureCloseDevice(IntPtr device);
    [DllImport(Lib)] public static extern void alcCaptureStart(IntPtr device);
    [DllImport(Lib)] public static extern void alcCaptureStop(IntPtr device);
    [DllImport(Lib)] public static extern void alcCaptureSamples(IntPtr device, void* buffer, int samples);
    [DllImport(Lib)] public static extern void alcGetIntegerv(IntPtr device, int param, int size, int* values);

    [DllImport(Lib)] public static extern void alGenSources(int n, uint* sources);
    [DllImport(Lib)] public static extern void alDeleteSources(int n, uint* sources);
    [DllImport(Lib)] public static extern void alGenBuffers(int n, uint* buffers);
    [DllImport(Lib)] public static extern void alDeleteBuffers(int n, uint* buffers);
    [DllImport(Lib)] public static extern void alBufferData(uint buffer, int format, void* data, int size, int frequency);
    [DllImport(Lib)] public static extern void alSourceQueueBuffers(uint source, int n, uint* buffers);
    [DllImport(Lib)] public static extern void alSourceUnqueueBuffers(uint source, int n, uint* buffers);
    [DllImport(Lib)] public static extern void alGetSourcei(uint source, int param, int* value);
    [DllImport(Lib)] public static extern void alSourcePlay(uint source);
    [DllImport(Lib)] public static extern void alSourceStop(uint source);
    [DllImport(Lib)] public static extern int alGetError();

    /// <summary>播放设备名。有 ALC_ENUMERATE_ALL_EXT 时列出全部输出（不然只有每个驱动的默认设备）。</summary>
    public static IReadOnlyList<string> PlaybackDevices()
    {
        var all = alcIsExtensionPresent(IntPtr.Zero, "ALC_ENUMERATE_ALL_EXT") != 0;
        return StringList(alcGetString(IntPtr.Zero, all ? AlcAllDevicesSpecifier : AlcDeviceSpecifier));
    }

    public static IReadOnlyList<string> CaptureDevices() =>
        StringList(alcGetString(IntPtr.Zero, AlcCaptureDeviceSpecifier));

    /// <summary>OpenAL 的设备列表是一串以 \0 分隔、\0\0 结尾的 UTF-8 字符串。</summary>
    private static List<string> StringList(IntPtr list)
    {
        var result = new List<string>();
        if (list == IntPtr.Zero)
            return result;
        var p = (byte*)list;
        while (*p != 0)
        {
            var length = 0;
            while (p[length] != 0)
                length++;
            result.Add(Encoding.UTF8.GetString(p, length));
            p += length + 1;
        }
        return result;
    }
}

/// <summary>OpenAL 麦克风：后台线程每 5 ms 看一眼，凑够 20 ms 就交出去，和 WaveIn 的回调用法一样。</summary>
internal sealed unsafe class OpenAlCapture : IDisposable
{
    private const int ChunkSamples = AudioEngine.FrameSamples;

    private readonly IntPtr _device;
    private readonly Action<byte[], int> _onData;
    private readonly Thread _thread;
    private volatile bool _stop;

    public OpenAlCapture(string? deviceName, Action<byte[], int> onData)
    {
        _onData = onData;
        // 环形缓冲给半秒，轮询线程偶尔被耽搁也不丢采样
        _device = Al.alcCaptureOpenDevice(deviceName, AudioEngine.SampleRate, Al.FormatMono16, AudioEngine.SampleRate / 2);
        if (_device == IntPtr.Zero)
            throw new InvalidOperationException($"OpenAL 打不开采集设备 {deviceName ?? "（默认）"}");
        Al.alcCaptureStart(_device);
        _thread = new Thread(Run) { IsBackground = true, Name = "audio-in", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        var buffer = new byte[ChunkSamples * 2];
        while (!_stop)
        {
            int available;
            Al.alcGetIntegerv(_device, Al.AlcCaptureSamples, 1, &available);
            if (available < ChunkSamples)
            {
                Thread.Sleep(5);
                continue;
            }
            fixed (byte* p = buffer)
                Al.alcCaptureSamples(_device, p, ChunkSamples);
            _onData(buffer, buffer.Length);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(1));
        Al.alcCaptureStop(_device);
        Al.alcCaptureCloseDevice(_device);
    }
}

/// <summary>
/// OpenAL 扬声器：一个音源排 6 块 20 ms 的缓冲（约 120 ms，和 Windows 版 WaveOut 的延迟一样），
/// 后台线程把播完的缓冲从 <see cref="IWaveProvider"/>（<see cref="RxMixer"/>）重新填满再排回去。
/// 同一时间只开一个输出，所以直接用进程级的当前上下文。
/// </summary>
internal sealed unsafe class OpenAlPlayback : IDisposable
{
    private const int BufferCount = 6;
    private const int BufferSamples = AudioEngine.FrameSamples;

    private readonly IntPtr _device;
    private readonly IntPtr _context;
    private readonly IWaveProvider _source;
    private readonly uint[] _buffers = new uint[BufferCount];
    private readonly byte[] _pcm = new byte[BufferSamples * 2];
    private uint _sourceId;
    private readonly Thread _thread;
    private volatile bool _stop;

    public OpenAlPlayback(string? deviceName, IWaveProvider source)
    {
        _source = source;
        _device = Al.alcOpenDevice(deviceName);
        if (_device == IntPtr.Zero)
            throw new InvalidOperationException($"OpenAL 打不开播放设备 {deviceName ?? "（默认）"}");
        var attributes = stackalloc int[] { Al.AlcFrequency, AudioEngine.SampleRate, 0 };
        _context = Al.alcCreateContext(_device, attributes);
        if (_context == IntPtr.Zero || Al.alcMakeContextCurrent(_context) == 0)
        {
            if (_context != IntPtr.Zero)
                Al.alcDestroyContext(_context);
            Al.alcCloseDevice(_device);
            throw new InvalidOperationException("OpenAL 建不了播放上下文");
        }

        fixed (uint* source0 = &_sourceId)
            Al.alGenSources(1, source0);
        fixed (uint* buffers = _buffers)
        {
            Al.alGenBuffers(BufferCount, buffers);
            for (var i = 0; i < BufferCount; i++)
                Fill(buffers[i]);
            Al.alSourceQueueBuffers(_sourceId, BufferCount, buffers);
        }
        Al.alSourcePlay(_sourceId);
        _thread = new Thread(Run) { IsBackground = true, Name = "audio-out", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Fill(uint buffer)
    {
        _source.Read(_pcm, 0, _pcm.Length);
        fixed (byte* p = _pcm)
            Al.alBufferData(buffer, Al.FormatMono16, p, _pcm.Length, AudioEngine.SampleRate);
    }

    private void Run()
    {
        while (!_stop)
        {
            int processed;
            Al.alGetSourcei(_sourceId, Al.BuffersProcessed, &processed);
            for (var i = 0; i < processed; i++)
            {
                uint buffer;
                Al.alSourceUnqueueBuffers(_sourceId, 1, &buffer);
                Fill(buffer);
                Al.alSourceQueueBuffers(_sourceId, 1, &buffer);
            }
            // 线程被耽搁到缓冲全播完时音源会自己停下，重新开播
            int state;
            Al.alGetSourcei(_sourceId, Al.SourceState, &state);
            if (state != Al.Playing)
                Al.alSourcePlay(_sourceId);
            Thread.Sleep(5);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(1));
        Al.alSourceStop(_sourceId);
        fixed (uint* source0 = &_sourceId)
            Al.alDeleteSources(1, source0);
        fixed (uint* buffers = _buffers)
            Al.alDeleteBuffers(BufferCount, buffers);
        Al.alcMakeContextCurrent(IntPtr.Zero);
        Al.alcDestroyContext(_context);
        Al.alcCloseDevice(_device);
    }
}
