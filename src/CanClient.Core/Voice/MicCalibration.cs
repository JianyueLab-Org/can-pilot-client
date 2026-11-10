using System.Text.Json.Nodes;

namespace CanClient.Core.Voice;

/// <summary>
/// 麦克风音量自动校准，对应 Python 版 micgain.py 的 measure / compute_gain 和 calibration.py 的流程：
/// 先录 1 秒静音测底噪，再录 8 秒朗读，按有效语音帧的平均电平算出把语音拉到 -20 dBFS 的增益。
/// 电平单位是 dBFS（相对 int16 满量程）。
/// </summary>
public static class MicCalibration
{
    public const double TargetDbfs = -20.0;
    public const double MinGainDb = -12.0;
    public const double MaxGainDb = 20.0;
    /// <summary>比底噪高这么多才算语音帧。</summary>
    public const double SpeechAboveNoiseDb = 10.0;
    public const double MinSpeechSeconds = 2.0;
    public const double MaxClipRatio = 0.01;
    public const double MinSnrDb = 15.0;
    public const double QuietSeconds = 1.0;
    public const double ReadSeconds = 8.0;

    /// <summary>界面上让用户朗读的那段话。</summary>
    public const string Phrase = "上海进近，东方五三五一，通过六千米下降到四千二百米，航向二七零，建立航向道报告。";

    public static Measurement Measure(IReadOnlyList<short[]> frames, IReadOnlyList<short[]> noiseFrames, int rate,
        IReadOnlyList<short[]>? clipFrames = null)
    {
        var clipSource = clipFrames ?? frames;
        var noise = PowerMeanDbfs(noiseFrames.Select(f => MicGain.FrameRmsDbfs(f)));
        var threshold = noise + SpeechAboveNoiseDb;
        var speechLevels = new List<double>();
        long speechSamples = 0;
        foreach (var frame in frames)
        {
            var level = MicGain.FrameRmsDbfs(frame);
            if (level <= threshold)
                continue;
            speechLevels.Add(level);
            speechSamples += frame.Length;
        }
        var clipRatio = clipSource.Count == 0 ? 0.0 : (double)clipSource.Count(Clipped) / clipSource.Count;
        return new Measurement(PowerMeanDbfs(speechLevels), noise, (double)speechSamples / rate, clipRatio);
    }

    public static GainResult ComputeGain(Measurement m)
    {
        if (m.ClipRatio > MaxClipRatio)
            return new GainResult(null, CalibrationFailure.Clipping);
        if (m.SpeechSeconds < MinSpeechSeconds)
            return new GainResult(null, CalibrationFailure.TooShort);
        if (m.SpeechDbfs - m.NoiseDbfs < MinSnrDb)
            return new GainResult(null, CalibrationFailure.TooNoisy);
        var gain = Math.Clamp(TargetDbfs - m.SpeechDbfs, MinGainDb, MaxGainDb);
        return new GainResult(Math.Round(gain, 1, MidpointRounding.AwayFromZero), null);
    }

    /// <summary>写进设置文件 mic_calibration[设备名] 的那一条，格式和 Python 版一致。</summary>
    public static JsonObject Entry(Measurement m, double gainDb, bool denoise, DateTime? now = null) => new()
    {
        ["gain_db"] = gainDb,
        ["speech_dbfs"] = Math.Round(m.SpeechDbfs, 1),
        ["noise_dbfs"] = Math.Round(m.NoiseDbfs, 1),
        ["denoise"] = denoise,
        ["calibrated_at"] = (now ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
    };

    public static string Describe(CalibrationFailure failure) => failure switch
    {
        CalibrationFailure.Clipping => "输入削波。请把麦克风拿远一点，或调低系统输入音量后重试。",
        CalibrationFailure.TooShort => "没有检测到足够的语音。请完整读出上面的文字后重试。",
        _ => "环境太吵或麦克风声音太小。请换到安静的环境，或调高系统输入音量后重试。",
    };

    private static double PowerMeanDbfs(IEnumerable<double> levels)
    {
        double sum = 0;
        var count = 0;
        foreach (var level in levels)
        {
            sum += Math.Pow(10.0, level / 10.0);
            count++;
        }
        if (count == 0)
            return MicGain.SilenceDbfs;
        return Math.Max(MicGain.SilenceDbfs, 10.0 * Math.Log10(sum / count));
    }

    private static bool Clipped(short[] frame)
    {
        foreach (var s in frame)
            if (s is short.MaxValue or short.MinValue)
                return true;
        return false;
    }
}

public readonly record struct Measurement(double SpeechDbfs, double NoiseDbfs, double SpeechSeconds, double ClipRatio);

public enum CalibrationFailure { Clipping, TooShort, TooNoisy }

public readonly record struct GainResult(double? GainDb, CalibrationFailure? Failure);

public enum CalibrationStage { Quiet, Read, Done }

/// <summary>
/// 校准录音的状态机：喂 20 ms 的原始帧，按帧数（不按墙钟）切换"静音 → 朗读 → 结束"，
/// 所以设备回调卡一下也不会少录。降噪开着时每帧先过 RNNoise，测的是实际发出去的信号；
/// 削波仍按原始信号判。线程安全：设备线程喂帧，界面线程读状态。
/// </summary>
public sealed class MicCalibrator : IDisposable
{
    private readonly object _gate = new();
    private readonly int _rate;
    private readonly int _quietFrames;
    private readonly int _totalFrames;
    private readonly Denoiser? _denoiser;
    private readonly List<short[]> _noise = [];
    private readonly List<short[]> _reading = [];
    private readonly List<short[]> _raw = [];
    private int _frames;

    public MicCalibrator(int rate, int frameSamples, bool denoise)
    {
        _rate = rate;
        var perSecond = (double)rate / frameSamples;
        _quietFrames = (int)Math.Round(MicCalibration.QuietSeconds * perSecond);
        _totalFrames = _quietFrames + (int)Math.Round(MicCalibration.ReadSeconds * perSecond);
        Denoise = Denoiser.Effective(denoise);
        if (Denoise)
            _denoiser = new Denoiser(rate);
    }

    public bool Denoise { get; }

    public CalibrationStage Stage { get; private set; } = CalibrationStage.Quiet;

    /// <summary>整个过程的进度，0..1。</summary>
    public double Progress => Math.Min(1.0, (double)_frames / _totalFrames);

    /// <summary>喂一帧原始采样，返回这一帧（处理后）的 RMS 电平，给电平表用。</summary>
    public double Feed(ReadOnlySpan<short> raw)
    {
        lock (_gate)
        {
            if (Stage == CalibrationStage.Done)
                return MicGain.SilenceDbfs;
            var frame = raw.ToArray();
            _denoiser?.Process(frame);
            if (Stage == CalibrationStage.Quiet)
                _noise.Add(frame);
            else
            {
                _reading.Add(frame);
                _raw.Add(Denoise ? raw.ToArray() : frame);
            }
            _frames++;
            if (_frames >= _totalFrames)
                Stage = CalibrationStage.Done;
            else if (_frames >= _quietFrames)
                Stage = CalibrationStage.Read;
            return MicGain.FrameRmsDbfs(frame);
        }
    }

    public Measurement Measure()
    {
        lock (_gate)
            return MicCalibration.Measure(_reading, _noise, _rate, _raw);
    }

    public void Dispose()
    {
        lock (_gate)
            _denoiser?.Dispose();
    }
}
