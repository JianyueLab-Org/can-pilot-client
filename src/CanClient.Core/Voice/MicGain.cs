namespace CanClient.Core.Voice;

/// <summary>
/// 发送端的增益和限幅器，对应 Python 版 micgain.py。电平单位是 dBFS（相对 int16 满量程）。
/// </summary>
public static class MicGain
{
    public const double CeilingDbfs = -1.0;
    public const double ReleaseMs = 50.0;
    public const double FullScale = 32768.0;
    public const double SilenceDbfs = -120.0;

    public static double DbToLinear(double db) => Math.Pow(10.0, db / 20.0);

    public static double FrameRmsDbfs(ReadOnlySpan<short> frame)
    {
        if (frame.IsEmpty)
            return SilenceDbfs;
        double sum = 0;
        foreach (var s in frame)
            sum += (double)s * s;
        var rms = Math.Sqrt(sum / frame.Length);
        if (rms <= 0)
            return SilenceDbfs;
        return Math.Max(SilenceDbfs, 20.0 * Math.Log10(rms / FullScale));
    }

    /// <summary>峰值电平，0..1，设置页的电平表用。</summary>
    public static double Peak(ReadOnlySpan<short> frame)
    {
        var peak = 0;
        foreach (var s in frame)
            peak = Math.Max(peak, Math.Abs((int)s));
        return peak / FullScale;
    }
}

/// <summary>
/// 峰值限幅。每帧算一次所需增益：要压就立即压，放开按 release 指数回升。帧内从上一帧的增益线性
/// 过渡到本帧的增益，避免帧边界上的台阶；仍超上限的采样最后被硬削到上限。
/// </summary>
public sealed class Limiter(int rate, double ceilingDbfs = MicGain.CeilingDbfs, double releaseMs = MicGain.ReleaseMs)
{
    private readonly double _ceiling = (MicGain.FullScale - 1.0) * MicGain.DbToLinear(ceilingDbfs);
    private double _gain = 1.0;

    public int Rate { get; } = rate;

    /// <summary>samples × gain → 限幅 → int16，就地写回。</summary>
    public void Process(Span<short> samples, double gain)
    {
        if (samples.IsEmpty)
            return;
        if (gain <= 0)
        {
            samples.Clear();
            return;
        }
        double peak = 0;
        foreach (var s in samples)
            peak = Math.Max(peak, Math.Abs(s * gain));
        var needed = peak <= _ceiling ? 1.0 : _ceiling / peak;
        double target;
        if (needed < _gain)
            target = needed;
        else
        {
            var frameMs = 1000.0 * samples.Length / Rate;
            var recovered = 1.0 - (1.0 - _gain) * Math.Exp(-frameMs / releaseMs);
            target = Math.Min(needed, recovered);
        }
        var start = _gain;
        _gain = target;
        var n = samples.Length;
        for (var i = 0; i < n; i++)
        {
            var ramp = n == 1 ? target : start + (target - start) * i / (n - 1);
            var y = Math.Clamp(samples[i] * gain * ramp, -_ceiling, _ceiling);
            samples[i] = (short)Math.Round(y, MidpointRounding.ToEven);
        }
    }
}
