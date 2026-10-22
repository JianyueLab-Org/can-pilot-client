namespace CanClient.Core.Traffic;

/// <summary>
/// 四元数，逐条对照 xPilot 的 plugin/include/abacus.hpp。(X, Y, Z, W) 对应它的 (I, J, K, U)。
/// 欧拉角是航空的 ZYX：航向绕 z、俯仰绕 y、坡度绕 x，抬头、右坡、右转为正。
/// </summary>
public readonly record struct Quat(double X, double Y, double Z, double W)
{
    public static readonly Quat Identity = new(0, 0, 0, 1);

    /// <summary>Quaternion::CreateFromEuler，弧度。</summary>
    public static Quat FromEuler(double yaw, double pitch, double roll)
    {
        double cy = Math.Cos(yaw * 0.5), sy = Math.Sin(yaw * 0.5);
        double cp = Math.Cos(pitch * 0.5), sp = Math.Sin(pitch * 0.5);
        double cr = Math.Cos(roll * 0.5), sr = Math.Sin(roll * 0.5);
        return new Quat(
            cy * cp * sr - sy * sp * cr,
            sy * cp * sr + cy * sp * cr,
            sy * cp * cr - cy * sp * sr,
            cy * cp * cr + sy * sp * sr);
    }

    /// <summary>Quaternion::ExtractEulerAngles，返回 (俯仰, 航向, 坡度)，弧度。</summary>
    public (double Pitch, double Yaw, double Roll) ToEuler()
    {
        var test = W * Y - Z * X;
        if (test > 0.4999999999999999)
            return (Math.PI / 2, 2.0 * Math.Atan2(X, W), 0.0);
        if (test < -0.4999999999999999)
            return (-Math.PI / 2, -2.0 * Math.Atan2(X, W), 0.0);
        var pitch = Math.Asin(2.0 * test);
        var yaw = Math.Atan2(2.0 * (W * Z + X * Y), 1.0 - 2.0 * (Y * Y + Z * Z));
        var roll = Math.Atan2(2.0 * (W * X + Y * Z), 1.0 - 2.0 * (X * X + Y * Y));
        return (pitch, yaw, roll);
    }

    public static Quat operator *(Quat a, Quat b) => new(
        a.X * b.W + a.W * b.X + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    public Quat Inverse()
    {
        var n = X * X + Y * Y + Z * Z + W * W;
        return new Quat(-X / n, -Y / n, -Z / n, W / n);
    }

    public Quat Normalized()
    {
        var n = Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
        return new Quat(X / n, Y / n, Z / n, W / n);
    }

    /// <summary>
    /// SlerpUnclamped(Identity, q, t)：同一根轴，转 t 倍的角度。不夹 t，
    /// 同一根轴上 Scale(a) * Scale(b) == Scale(a + b)，所以积分结果和帧率无关。
    /// </summary>
    public Quat Scale(double t)
    {
        var dot = W;
        var flip = dot < 0;
        if (flip)
            dot = -dot;
        double n1, n2;
        if (dot > 0.999999)
        {
            n2 = 1.0 - t;
            n1 = flip ? -t : t;
        }
        else
        {
            var theta = Math.Acos(dot);
            var inv = 1.0 / Math.Sin(theta);
            n2 = Math.Sin((1.0 - t) * theta) * inv;
            n1 = Math.Sin(t * theta) * inv;
            if (flip)
                n1 = -n1;
        }
        return new Quat(n1 * X, n1 * Y, n1 * Z, n2 + n1 * W).Normalized();
    }

    /// <summary>度 → 四元数。</summary>
    public static Quat FromAttitude(double pitch, double bank, double heading) =>
        FromEuler(heading * Math.PI / 180.0, pitch * Math.PI / 180.0, bank * Math.PI / 180.0);
}
