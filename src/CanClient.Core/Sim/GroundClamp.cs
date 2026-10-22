namespace CanClient.Core.Sim;

/// <summary>
/// 一架飞机的地面贴合状态，照 xPilot 的 PerformGroundClamping。纯的：不读时钟，不碰模拟器。
///
/// 18000 ft 以下，把两边地面的差作为偏移慢慢叠上去；报告在地面的飞机落在本地地面上；任何时候
/// 不低于本地地面。和 xPilot 的一处不同：它的"近地数据可用"条件永远不成立，这里按它的本意做——
/// AGL 持续 ≤ 100 ft 两秒就用。
/// </summary>
public sealed class GroundClamp
{
    public const double Ceiling = 18000.0;
    public const double MaxUsableAgl = 100.0;
    public const double UsableAfter = 2.0;
    public const double MinAglForClimbout = 50.0;
    public const double WindowLanding = 2.0;
    public const double WindowClimbout = 10.0;
    public const double MinOffsetMagnitude = 0.1;
    public const double MaxSceneryDifference = 1000.0;

    public double Offset { get; private set; }
    public double Target { get; private set; }
    private double _magnitude;
    private double? _lowSince;
    private bool _placed;

    /// <summary>本地地面和对方地面差得太多，这一帧没用本地读数。</summary>
    public bool Rejected { get; private set; }

    /// <summary>目标高度偏移（英尺），加到对方上报的高度上。</summary>
    public static double TargetOffset(double altitude, double? agl, double localGround, bool onGround, bool usable,
        double modelHeight)
    {
        if (onGround)
            return Math.Round(localGround + modelHeight - altitude, 2);
        if (usable && agl is { } a)
            return Math.Round(localGround - (altitude - a), 2);
        return 0.0;
    }

    /// <summary>偏移走完要多少秒：爬升段 10 秒，其余（近地、着陆）2 秒。</summary>
    public static double BlendWindow(bool onGround, double agl, double target) =>
        !onGround && agl >= MinAglForClimbout && target == 0.0 ? WindowClimbout : WindowLanding;

    private static double StepToward(double value, double target, double step)
    {
        if (step >= Math.Abs(target - value))
            return target;
        return target > value ? value + step : value - step;
    }

    /// <summary>返回要写进模拟器的高度（英尺）。localGround 为 null 时原样返回。</summary>
    public double Update(double now, double dt, double altitude, double? agl, bool onGround, double? localGround,
        double modelHeight = 0.0)
    {
        Rejected = false;
        if (localGround is not { } ground || altitude >= Ceiling)
            return altitude;
        if (agl is { } a && Math.Abs(ground - (altitude - a)) > MaxSceneryDifference)
        {
            Rejected = true;
            return altitude;
        }

        bool usable;
        if (agl is { } low && low <= MaxUsableAgl)
        {
            _lowSince ??= now;
            usable = now - _lowSince.Value >= UsableAfter;
        }
        else
        {
            _lowSince = null;
            usable = false;
        }

        var target = TargetOffset(altitude, agl, ground, onGround, usable, modelHeight);
        if (target != Target)
        {
            Target = target;
            _magnitude = Math.Max(Math.Abs(target - Offset), MinOffsetMagnitude);
        }
        if (Offset != Target)
        {
            if (!_placed)
                Offset = Target;
            else
            {
                var localAgl = agl ?? altitude - ground;
                var window = BlendWindow(onGround, localAgl, Target);
                Offset = StepToward(Offset, Target, _magnitude * Math.Max(dt, 0.0) / window);
            }
        }
        _placed = true;
        return Math.Max(altitude + Offset, ground + modelHeight);
    }
}
