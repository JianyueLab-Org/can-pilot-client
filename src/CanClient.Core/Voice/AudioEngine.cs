using CanClient.Core.Util;
using NAudio.Wave;

namespace CanClient.Core.Voice;

/// <summary>
/// 麦克风和扬声器。48 kHz 单声道 16 位，20 ms 一帧（960 个采样），和 Mumble 的 Opus 帧对齐。
///
/// Windows 用 WinMM（WaveIn / WaveOut），macOS / Linux 用 OpenAL Soft（<see cref="OpenAlCapture"/> / <see cref="OpenAlPlayback"/>）。
///
/// 采集：设备回调凑够 960 个采样就交出一帧（<see cref="FrameCaptured"/>），语音发送和设置页的
/// 电平表都从这里拿。播放：每个说话的人一条队列，由输出设备按它要的采样数拉着混（<see cref="RxMixer"/>）；
/// 提示音也混在同一路里，所以响在客户端选的那块输出设备上，而不是系统默认设备。
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
    // Windows 上是 WaveInEvent / WaveOutEvent，其他平台是 OpenAlCapture / OpenAlPlayback
    private IDisposable? _input;
    private IDisposable? _output;
    private readonly RxMixer _mixer = new();
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
            _mixer.Gain = (float)MicGain.DbToLinear(value);
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
            try
            {
                OpenInput(deviceName);
            }
            catch
            {
                // 没开成就不算用户，否则之后 ChangeInput 会以为还有人在用、再去开一次
                _captureUsers--;
                throw;
            }
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
        if (!OperatingSystem.IsWindows())
        {
            var device = AudioDevices.ResolveInputDevice(deviceName);
            try
            {
                _input = new OpenAlCapture(device?.Name, OnPcm);
            }
            catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
            {
                throw new InvalidOperationException($"打不开麦克风：{e.Message}", e);
            }
            _pendingCount = 0;
            Log.Info($"microphone open: {device?.Name ?? "[系统默认]"} ({SampleRate} Hz, OpenAL)");
            return;
        }
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
        if (input is WaveInEvent waveIn)
        {
            waveIn.DataAvailable -= OnData;
            try
            {
                waveIn.StopRecording();
            }
            catch
            {
                // ignore
            }
        }
        input.Dispose();
    }

    private void OnData(object? sender, WaveInEventArgs e) => OnPcm(e.Buffer, e.BytesRecorded);

    private void OnPcm(byte[] buffer, int bytes)
    {
        var count = bytes / 2;
        for (var i = 0; i < count; i++)
        {
            _pending[_pendingCount++] = BitConverter.ToInt16(buffer, i * 2);
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
        _mixer.Clear();
        if (!OperatingSystem.IsWindows())
        {
            var device = AudioDevices.ResolveOutputDevice(deviceName);
            try
            {
                _output = new OpenAlPlayback(device?.Name, _mixer);
            }
            catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
            {
                throw new InvalidOperationException($"打不开扬声器：{e.Message}", e);
            }
            Log.Info($"speaker open: {device?.Name ?? "[系统默认]"} (OpenAL)");
            return;
        }
        var index = AudioDevices.ResolveOutput(deviceName);
        var output = new WaveOutEvent { DeviceNumber = index, DesiredLatency = 120, NumberOfBuffers = 3 };
        try
        {
            output.Init(_mixer);
            output.Play();
        }
        catch (Exception e)
        {
            output.Dispose();
            throw new InvalidOperationException($"打不开扬声器：{e.Message}", e);
        }
        _output = output;
        Log.Info($"speaker open: {(index < 0 ? "[系统默认]" : deviceName)}");
    }

    private void CloseOutput()
    {
        var output = _output;
        _output = null;
        _mixer.Clear();
        if (output is null)
            return;
        try
        {
            (output as WaveOutEvent)?.Stop();
        }
        catch
        {
            // ignore
        }
        output.Dispose();
    }

    /// <summary>播一段某个人的语音。包多长都行，按采样数接在这个人的队列后面。</summary>
    public void Play(uint talker, ReadOnlySpan<short> pcm)
    {
        if (_output is null)
            return;
        _mixer.Enqueue(talker, pcm);
    }

    /// <summary>播一段一次性的声音（提示音）。输出没开的话临时开一个。</summary>
    public void PlayClip(short[] pcm)
    {
        if (_output is null)
        {
            lock (_lock)
            {
                if (_output is null)
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
            }
        }
        _mixer.AddClip(pcm);
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
