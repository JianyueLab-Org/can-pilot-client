namespace CanClient.Core;

/// <summary>
/// 高度换算。照 xPilot（client/src/network/networkmanager.cpp）：
///
///     网络高度   真高 + 高度表温度误差。`@` 第 7 段、`^`/`#SL`/`#ST` 第 4 段。
///     气压高度   标准气压面（1013.25 hPa）上的高度。
///     修正量     气压高度 − 网络高度。`@` 最后一段。
///     他机高度   收到的高度 − 自己的温度误差 × 权重（<see cref="AdjustIncoming"/>）。
///
/// 海平面气压和高度之间用 ISA 气压公式换算，不用 xPilot 的 30 ft/hPa：线性近似在 FL380、
/// 海压 1030 hPa 时差 168 ft。
/// </summary>
public static class Altitude
{
    public const double IsaSeaLevelHpa = 1013.25;
    private const double IsaScaleFt = 145366.45;
    private const double IsaExponent = 0.190284;

    // 超出这个范围的海平面气压是没读到或读错了，不参与换算。
    private const double SeaLevelMinHpa = 870.0;
    private const double SeaLevelMaxHpa = 1090.0;

    // 超出这个范围的温度误差是没读到或单位错了，按 0 处理。
    public const double MaxTemperatureErrorFt = 5000.0;

    // AdjustIncoming 的权重：垂直距离 3000 ft 以内全额，6000 ft 以外不修。
    private const double AdjustFullFt = 3000.0;
    private const double AdjustNoneFt = 6000.0;

    public static bool SeaLevelValid(double? hpa) => hpa is >= SeaLevelMinHpa and <= SeaLevelMaxHpa;

    public static (double Network, double Pressure, double TemperatureError) XPlane(
        double altitudeFt, double? seaLevelHpa, double? pressureFt, double? temperatureErrorFt)
    {
        var error = temperatureErrorFt is { } e && double.IsFinite(e) && Math.Abs(e) <= MaxTemperatureErrorFt ? e : 0;
        if (pressureFt is { } p && double.IsFinite(p) && Math.Abs(p - altitudeFt - error) <= MaxTemperatureErrorFt)
            return (altitudeFt + error, p, error);
        return (altitudeFt, SeaLevelValid(seaLevelHpa) ? PressureAltitude(altitudeFt, seaLevelHpa!.Value) : altitudeFt, 0);
    }

    private static double Ratio(double seaLevelHpa) => Math.Pow(seaLevelHpa / IsaSeaLevelHpa, IsaExponent);

    /// <summary>ISA 温度、海平面气压为 seaLevelHpa 的大气里，高度 altitudeFt 处的气压高度。</summary>
    public static double PressureAltitude(double altitudeFt, double seaLevelHpa) =>
        IsaScaleFt * (1.0 - Ratio(seaLevelHpa) * (1.0 - altitudeFt / IsaScaleFt));

    /// <summary><see cref="PressureAltitude"/> 的反函数：高度表拨海平面气压时的读数。</summary>
    public static double IsaAltitude(double pressureAltitudeFt, double seaLevelHpa) =>
        IsaScaleFt * (1.0 - (1.0 - pressureAltitudeFt / IsaScaleFt) / Ratio(seaLevelHpa));

    /// <summary>
    /// MSFS：返回 (网络高度, 气压高度, 温度误差)，英尺。
    ///
    /// MSFS 没有温度误差 SimVar。网络高度取高度表拨海平面气压时的读数：
    ///     网络高度 = IsaAltitude(PRESSURE ALTITUDE, SEA LEVEL PRESSURE)
    ///     温度误差 = 网络高度 − PLANE ALTITUDE
    /// PRESSURE ALTITUDE 读不到、或和真高差出 5000 ft 以上：三者都按真高。
    /// 海平面气压读不到：网络高度 = 真高，气压高度 = PRESSURE ALTITUDE。
    /// </summary>
    public static (double Network, double Pressure, double TemperatureError) Msfs(
        double altitudeFt, double? seaLevelHpa, double? pressureAltitudeFt)
    {
        if (pressureAltitudeFt is not { } pressure
            || double.IsNaN(pressure)
            || Math.Abs(pressure - altitudeFt) > MaxTemperatureErrorFt)
            return (altitudeFt, altitudeFt, 0.0);
        if (!SeaLevelValid(seaLevelHpa))
            return (altitudeFt, pressure, 0.0);
        var error = IsaAltitude(pressure, seaLevelHpa!.Value) - altitudeFt;
        if (Math.Abs(error) > MaxTemperatureErrorFt)
            return (altitudeFt, altitudeFt, 0.0);
        return (altitudeFt + error, pressure, error);
    }

    /// <summary>
    /// 他机的网络高度 → 本机模拟器里画它的高度。xPilot 的 AdjustIncomingAltitude。
    /// 垂直距离 3000 ft 以内减去全部温度误差，3000–6000 ft 线性减到 0，6000 ft 以外不改。
    /// </summary>
    public static double AdjustIncoming(double altitudeFt, double? ownNetworkFt, double ownTemperatureErrorFt)
    {
        if (ownNetworkFt is not { } own || ownTemperatureErrorFt == 0)
            return altitudeFt;
        var distance = Math.Abs(own - altitudeFt);
        if (distance > AdjustNoneFt)
            return altitudeFt;
        var weight = 1.0;
        if (distance > AdjustFullFt)
            weight = 1.0 - (distance - AdjustFullFt) / (AdjustNoneFt - AdjustFullFt);
        return altitudeFt - ownTemperatureErrorFt * weight;
    }
}
