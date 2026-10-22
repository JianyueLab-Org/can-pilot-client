namespace CanClient.Core.Traffic;

/// <summary>(东, 上, 北) 米每秒，或 (俯仰, 航向, 坡度) 度每秒。</summary>
public readonly record struct Vec3(double A, double B, double C)
{
    public static readonly Vec3 Zero = new(0, 0, 0);
    public bool IsZero => A == 0 && B == 0 && C == 0;
    public bool Any => A != 0 || B != 0 || C != 0;
}

public sealed record MotionState(double Latitude, double Longitude, double Altitude, double Pitch, double Bank, double Heading);

/// <summary>
/// 一架飞机画出来的位置和姿态。xPilot `NetworkAircraft` 的运动部分。
///
/// 每架飞机按上报的速度和角速度逐帧积分，而不是在样本之间插值。新样本到达时不改画出来的
/// 位置，只算一个误差速度 =（上报 − 画出）/ 2 秒，在接下来 2 秒里叠加到速度上，所以飞机
/// 从不跳。角速度 0.5 秒没有更新就清零，姿态落到最后上报的那个。
///
/// 纯状态机，不读时钟：<see cref="Receive"/> 一个样本，<see cref="Advance"/> 往前积分。
/// </summary>
public sealed class Motion
{
    public const double ErrorTime = 2.0;
    public const double RotationHold = 0.5;
    private const double Epsilon = 1e-12;

    public const double FeetPerMetre = 3.280839895;
    public const double MetresPerFoot = 0.3048;
    public const double NmPerDegree = 60.0;
    public const double MetresPerDegree = 1852.0 * NmPerDegree;

    public bool Ready { get; private set; }
    public double Latitude { get; private set; }
    public double Longitude { get; private set; }
    public double Altitude { get; private set; }
    public Quat Orientation { get; private set; } = Quat.Identity;

    private (double Lat, double Lon, double Alt, double Pitch, double Bank, double Heading)? _target;
    private Quat _targetOrientation = Quat.Identity;

    public Vec3 Velocity { get; private set; } = Vec3.Zero;
    public Vec3 Rotation { get; private set; } = Vec3.Zero;

    // 误差速度：(东, 上, 北) 米每秒，和 (俯仰, 航向, 坡度) 弧度每秒
    private Vec3 _errorVelocity = Vec3.Zero;
    private Vec3 _errorRotation = Vec3.Zero;
    private double _errorRemaining;
    private double _sinceUpdate;

    public static double WrapLongitude(double longitude)
    {
        if (longitude > 180.0)
            longitude -= 360.0;
        else if (longitude < -180.0)
            longitude += 360.0;
        return longitude;
    }

    /// <summary>一个位置样本。第一个直接画在那里；之后只改速度和误差速度。</summary>
    public void Receive(double latitude, double longitude, double altitude, double pitch, double bank,
        double heading, Vec3 velocity, Vec3 rotation)
    {
        _target = (latitude, longitude, altitude, pitch, bank, heading);
        _targetOrientation = Quat.FromAttitude(pitch, bank, heading);
        Velocity = velocity;
        Rotation = rotation;
        if (!Ready)
        {
            Ready = true;
            Latitude = latitude;
            Longitude = longitude;
            Altitude = altitude;
            Orientation = _targetOrientation;
            _errorVelocity = Vec3.Zero;
            _errorRotation = Vec3.Zero;
            _errorRemaining = 0;
            _sinceUpdate = 0;
            return;
        }
        if (_sinceUpdate >= RotationHold)
            ClearRotation();
        _sinceUpdate = 0;
        UpdateErrors();
    }

    /// <summary>同一份快照又来了一遍：只换速度，不重算误差。</summary>
    public void Refresh(Vec3 velocity, Vec3 rotation)
    {
        Velocity = velocity;
        Rotation = rotation;
        if (_sinceUpdate >= RotationHold)
            ClearRotation();
        _sinceUpdate = 0;
    }

    private void UpdateErrors()
    {
        var t = _target!.Value;
        var north = (t.Lat - Latitude) * MetresPerDegree;
        var east = WrapLongitude(t.Lon - Longitude) * MetresPerDegree * Math.Cos(t.Lat * Math.PI / 180.0);
        var up = (t.Alt - Altitude) * MetresPerFoot;
        _errorVelocity = new Vec3(east / ErrorTime, up / ErrorTime, north / ErrorTime);
        if (Orientation == _targetOrientation)
        {
            _errorRotation = Vec3.Zero;
        }
        else
        {
            var delta = Orientation.Inverse() * _targetOrientation;
            var (pitch, yaw, roll) = delta.ToEuler();
            _errorRotation = new Vec3(pitch / ErrorTime, yaw / ErrorTime, roll / ErrorTime);
        }
        _errorRemaining = ErrorTime;
    }

    private void ClearRotation()
    {
        Rotation = Vec3.Zero;
        _errorRotation = Vec3.Zero;
        Orientation = _targetOrientation;
    }

    /// <summary>往前积分 dt 秒。在误差速度到期、角速度到期这两个时刻切段，结果和帧率无关。</summary>
    public void Advance(double dt)
    {
        if (!Ready || dt <= 0)
            return;
        while (dt > Epsilon)
        {
            var step = dt;
            if (_errorRemaining > 0 && _errorRemaining < step)
                step = _errorRemaining;
            var live = _sinceUpdate < RotationHold;
            if (live && RotationHold - _sinceUpdate < step)
                step = RotationHold - _sinceUpdate;
            Integrate(step);
            dt -= step;
            _errorRemaining = Math.Max(0.0, _errorRemaining - step);
            _sinceUpdate += step;
            if (live && _sinceUpdate >= RotationHold - Epsilon)
            {
                _sinceUpdate = Math.Max(_sinceUpdate, RotationHold);
                ClearRotation();
            }
        }
    }

    private void Integrate(double step)
    {
        var (east, up, north) = (Velocity.A, Velocity.B, Velocity.C);
        var pitchRate = Rotation.A * Math.PI / 180.0;
        var headingRate = Rotation.B * Math.PI / 180.0;
        var bankRate = Rotation.C * Math.PI / 180.0;
        if (_errorRemaining > 0)
        {
            east += _errorVelocity.A;
            up += _errorVelocity.B;
            north += _errorVelocity.C;
            pitchRate += _errorRotation.A;
            headingRate += _errorRotation.B;
            bankRate += _errorRotation.C;
        }

        var scale = Math.Max(1e-6, Math.Cos(Latitude * Math.PI / 180.0));
        Longitude = WrapLongitude(Longitude + east * step / (MetresPerDegree * scale));
        Latitude = Math.Clamp(Latitude + north * step / MetresPerDegree, -90.0, 90.0);
        Altitude += up * step * FeetPerMetre;

        if (pitchRate != 0 || headingRate != 0 || bankRate != 0)
        {
            var rotation = Quat.FromEuler(headingRate, pitchRate, bankRate);
            Orientation = (Orientation * rotation.Scale(step)).Normalized();
        }
    }

    /// <summary>画出来的 (俯仰, 坡度, 航向)，度。航向 0..360。</summary>
    public (double Pitch, double Bank, double Heading) Attitude()
    {
        var (pitch, yaw, roll) = Orientation.ToEuler();
        var heading = yaw * 180.0 / Math.PI % 360.0;
        if (heading < 0)
            heading += 360.0;
        return (pitch * 180.0 / Math.PI, roll * 180.0 / Math.PI, heading);
    }

    public MotionState? State()
    {
        if (!Ready)
            return null;
        var (pitch, bank, heading) = Attitude();
        return new MotionState(Latitude, Longitude, Altitude, pitch, bank, heading);
    }

    /// <summary>两个 `@` 样本之间的速度和角速度，给只发 `@` 的客户端用。</summary>
    public static (Vec3 Velocity, Vec3 Rotation) DeriveRates(Sample older, Sample newer)
    {
        var span = newer.Time - older.Time;
        if (span <= 0)
            return (Vec3.Zero, Vec3.Zero);
        var north = (newer.Latitude - older.Latitude) * MetresPerDegree;
        var east = WrapLongitude(newer.Longitude - older.Longitude) * MetresPerDegree
                   * Math.Cos(newer.Latitude * Math.PI / 180.0);
        var up = (newer.Altitude - older.Altitude) * MetresPerFoot;
        var delta = Quat.FromAttitude(older.Pitch, older.Bank, older.Heading).Inverse()
                    * Quat.FromAttitude(newer.Pitch, newer.Bank, newer.Heading);
        var (pitch, yaw, roll) = delta.ToEuler();
        const double toDeg = 180.0 / Math.PI;
        return (new Vec3(east / span, up / span, north / span),
            new Vec3(pitch * toDeg / span, yaw * toDeg / span, roll * toDeg / span));
    }
}

/// <summary>一个时刻上报的位置和姿态。</summary>
public sealed class Sample
{
    public double Time { get; set; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double Altitude { get; init; }
    public double Pitch { get; init; }
    public double Bank { get; init; }
    public double Heading { get; init; }
    public bool OnGround { get; init; }
    public int Groundspeed { get; set; }
    /// <summary>(东, 上, 北) 米每秒。只有快速位置包带；`@` 包是 null。</summary>
    public Vec3? Velocity { get; set; }
    /// <summary>(俯仰, 航向, 坡度) 度每秒，机体轴。只有快速位置包带。</summary>
    public Vec3? Rotation { get; set; }
    public double? Agl { get; init; }
    public double? NoseWheel { get; init; }

    public bool SamePlace(Sample other) =>
        Latitude == other.Latitude && Longitude == other.Longitude && Altitude == other.Altitude
        && Pitch == other.Pitch && Bank == other.Bank && Heading == other.Heading;
}
