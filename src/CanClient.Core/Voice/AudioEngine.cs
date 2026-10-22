using System.Collections.Concurrent;
using CanClient.Core.Util;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CanClient.Core.Voice;

/// <summary>
/// 麦克风和扬声器。48 kHz 单声道 16 位，20 ms 一帧（960 个采样），和 Mumble 的 Opus 帧对齐。
///
/// 采集：WaveIn 的回调凑够 960 个采样就交出一帧（<see cref="FrameCaptured"/>），语音发送和设置页的
/// 电平表都从这里拿。播放：每个说话的人一个缓冲，混到一起再走输出设备；提示音也混在同一路里，
/// 所以响在客户端选的那块输出设备上，而不是系统默认设备。
///
/// 设备的开、关、换都在锁里做——采集回调和播放线程都在别的线程上。
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 48000;
    public const int FrameSamples = 960;

    private static readonly Logger Log = Util.Log.For("audio");
    private static readonly WaveFormat PcmFormat = new(SampleRate, 16, 1);

    private readonly object _lock = new();
    private WaveInEvent? _input;
    private WaveOutEvent? _output;
    private MixingSampleProvider? _mixer;
    private VolumeSampleProvider? _volume;
    private readonly ConcurrentDictionary<uint, BufferedWaveProvider> _talkers = new();
    private readonly short[] _pending = new short[FrameSamples];
    private int _pendingCount;
    private int _captureUsers;
    private string _inputName = "";
    private string _outputName = "";
    private double _outputGainDb;

    /// <summary>一帧麦克风数据（960 个采样）。在采集线程上触发，处理要快。</summary>
    public event Action<short[]>? FrameCaptured;

    public string InputDevice => _inputName;
    public string OutputDevice => _outputName;
    public bool Capturing => _input is not null;
    public bool Playing => _output is not null;

    /// <summary>输出音量（dB），设置里的滑块。</summary>
    public double OutputGainDb
    {
        get => _outputGainDb;
        set
        {
            _outputGainDb = value;
            if (_volume is not null)
                _volume.Volume = (float)MicGain.DbToLinear(value);
        }
    }

    // ---------- 采集 ----------

    /// <summary>要用麦克风（语音连接、设置页电平表各算一个用户）。</summary>
    public void AcquireCapture(string deviceName)
    {
        lock (_lock)
        {
            _captureUsers++;
            if (_input is not null && deviceName == _inputName)
                return;
            OpenInput(deviceName);
        }
    }

    public void ReleaseCapture()
    {
        lock (_lock)
        {
            _captureUsers = Math.Max(0, _captureUsers - 1);
            if (_captureUsers == 0)
                CloseInput();
        }
    }

    /// <summary>设置里换了麦克风：正在用的话当场重开。</summary>
    public void ChangeInput(string deviceName)
    {
        lock (_lock)
        {
            if (deviceName == _inputName && _input is not null)
                return;
            _inputName = deviceName;
            if (_captureUsers > 0)
                OpenInput(deviceName);
        }
    }

    private void OpenInput(string deviceName)
    {
        CloseInput();
        _inputName = deviceName;
        var index = AudioDevices.ResolveInput(deviceName);
        var input = new WaveInEvent
        {
            DeviceNumber = index,
            WaveFormat = PcmFormat,
            BufferMilliseconds = 20,
            NumberOfBuffers = 4
        };
        input.DataAvailable += OnData;
        input.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
                Log.Warning($"the microphone stopped: {e.Exception.Message}");
        };
        try
        {
            input.StartRecording();
            _input = input;
            _pendingCount = 0;
            Log.Info($"microphone open: {(index < 0 ? "[系统默认]" : deviceName)} ({SampleRate} Hz)");
        }
        catch (Exception e)
        {
            input.Dispose();
            throw new InvalidOperationException($"打不开麦克风：{e.Message}", e);
        }
    }

    private void CloseInput()
    {
        var input = _input;
        _input = null;
        if (input is null)
            return;
        input.DataAvailable -= OnData;
        try
        {
            input.StopRecording();
        }
        catch
        {
            // ignore
        }
        input.Dispose();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var count = e.BytesRecorded / 2;
        for (var i = 0; i < count; i++)
        {
            _pending[_pendingCount++] = BitConverter.ToInt16(e.Buffer, i * 2);
            if (_pendingCount == FrameSamples)
            {
                var frame = (short[])_pending.Clone();
                _pendingCount = 0;
                try
                {
                    FrameCaptured?.Invoke(frame);
                }
                catch (Exception ex)
                {
                    Log.Debug($"frame handler raised: {ex.Message}");
                }
            }
        }
    }

    // ---------- 播放 ----------

    public void StartPlayback(string deviceName)
    {
        lock (_lock)
        {
            if (_output is not null && deviceName == _outputName)
                return;
            OpenOutput(deviceName);
        }
    }

    public void StopPlayback()
    {
        lock (_lock)
            CloseOutput();
    }

    public void ChangeOutput(string deviceName)
    {
        lock (_lock)
        {
            if (deviceName == _outputName && _output is not null)
                return;
            _outputName = deviceName;
            if (_output is not null)
                OpenOutput(deviceName);
        }
    }

    private void OpenOutput(string deviceName)
    {
        CloseOutput();
        _outputName = deviceName;
        var index = AudioDevices.ResolveOutput(deviceName);
        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1)) { ReadFully = true };
        var volume = new VolumeSampleProvider(mixer) { Volume = (float)MicGain.DbToLinear(_outputGainDb) };
        var output = new WaveOutEvent { DeviceNumber = index, DesiredLatency = 120, NumberOfBuffers = 3 };
        try
        {
            output.Init(volume);
            output.Play();
        }
        catch (Exception e)
        {
            output.Dispose();
            throw new InvalidOperationException($"打不开扬声器：{e.Message}", e);
        }
        _mixer = mixer;
        _volume = volume;
        _output = output;
        _talkers.Clear();
        Log.Info($"speaker open: {(index < 0 ? "[系统默认]" : deviceName)}");
    }

    private void CloseOutput()
    {
        var output = _output;
        _output = null;
        _mixer = null;
        _volume = null;
        _talkers.Clear();
        if (output is null)
            return;
        try
        {
            output.Stop();
        }
        catch
        {
            // ignore
        }
        output.Dispose();
    }

    /// <summary>
    /// 播一段某个人的语音。每个人一个缓冲；刚开口的那一帧前面垫 40 ms 静音，吸收网络抖动。
    /// </summary>
    public void Play(uint talker, ReadOnlySpan<short> pcm)
    {
        var mixer = _mixer;
        if (mixer is null)
            return;
        var buffer = _talkers.GetOrAdd(talker, _ =>
        {
            var b = new BufferedWaveProvider(PcmFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            mixer.AddMixerInput(b.ToSampleProvider());
            return b;
        });
        // 积压超过 120 ms 就丢掉旧的：网络一抖动攒下的包要是照单全收，这个人之后说的每句话都晚半拍
        // （Python 版每个发话者的队列只留 3 帧，同一个道理）
        if (buffer.BufferedDuration > TimeSpan.FromMilliseconds(120))
            buffer.ClearBuffer();
        if (buffer.BufferedBytes == 0)
        {
            var silence = new byte[SampleRate / 1000 * 40 * 2];
            buffer.AddSamples(silence, 0, silence.Length);
        }
        var bytes = new byte[pcm.Length * 2];
        for (var i = 0; i < pcm.Length; i++)
        {
            bytes[2 * i] = (byte)pcm[i];
            bytes[2 * i + 1] = (byte)(pcm[i] >> 8);
        }
        buffer.AddSamples(bytes, 0, bytes.Length);
    }

    /// <summary>播一段一次性的声音（提示音）。输出没开的话临时开一个。</summary>
    public void PlayClip(short[] pcm)
    {
        var mixer = _mixer;
        if (mixer is null)
        {
            lock (_lock)
            {
                if (_mixer is null)
                {
                    try
                    {
                        OpenOutput(_outputName);
                    }
                    catch (Exception e)
                    {
                        Log.Warning($"could not open the speaker for a chime: {e.Message}");
                        return;
                    }
                }
                mixer = _mixer!;
            }
        }
        var samples = pcm.Select(s => s / 32768f).ToArray();
        mixer.AddMixerInput(new ClipProvider(samples, mixer.WaveFormat));
    }

    private sealed class ClipProvider(float[] samples, WaveFormat format) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = format;

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, samples.Length - _position);
            if (n <= 0)
                return 0;
            Array.Copy(samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            CloseInput();
            CloseOutput();
        }
    }
}
