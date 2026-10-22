using System.Globalization;
using System.Text;
using CanClient.Core.Sim;

namespace CanClient.Core.Fsd;

/// <summary>
/// FSD 包的拼装和拆解，全是纯函数。协议逐条对照 can-fsd 的解析代码和 docs/protocol.md：
///
///     登录   $ID{呼号}:SERVER:{客户端ID}:{客户端名}:{主}:{次}:{CID}:{机器码}
///            #AP{呼号}:SERVER:{CID}:{密码}:{等级}:{协议版本}:{模拟器}:{真实姓名}
///     位置   @{应答机模式}:{呼号}:{squawk}:{等级}:{纬度}:{经度}:{高度}:{地速}:{PBH}:{气压差}
///     快速   ^{呼号}:{纬度}:{经度}:{真高}:{离地高}:{PBH}:{东}:{上}:{北}:{俯仰率}:{航向率}:{坡度率}:{前轮角}
///     计划   $FP{呼号}:SERVER:... 一共 17 段，少一段整包被拒
///     文字   #TM{呼号}:{收件人}:{正文}
///     下线   #DP{呼号}:{CID}
/// </summary>
public static class FsdProtocol
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Encoding LegacyChinese = CreateLegacyEncoding();

    private static Encoding CreateLegacyEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(54936);
    }

    /// <summary>现代客户端优先 UTF-8；兼容旧管制端的 GB18030 / GBK，完整分帧后再解码。</summary>
    internal static string DecodePacket(byte[] bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return LegacyChinese.GetString(bytes); }
    }

    public const int ProtocolRevision = 101;
    public const int RatingObserver = 1;
    public const string ClientId = "0001";
    public const int ClientMajor = 1;
    public const int ClientMinor = 0;

    // 模拟器编号，取自 can-fsd 的 docs/enumerations.md。
    public const int SimulatorMsfs2020 = 10;
    public const int SimulatorMsfs2024 = 11;

    public const int MaxCallsignLength = 12;
    public const string RangedAll = "@94836";
    public const string WallopRecipient = "*S";

    public const double KnotsPerMps = 1.943844492;
    // 速度和角速度都小于这个值就算停着（xPilot 的 POSITIONAL_VELOCITY_ZERO_TOLERANCE）
    public const double StoppedTolerance = 0.005;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// 把俯仰/坡度/航向压成 32 位整数。can-fsd 的拆法：
    ///     pitch = 位 22-31，bank = 位 12-21，heading = 位 2-11，乘 360/1024；位 1 = 在地面。
    /// 角度先折到 0..360 再量化，否则负角度会溢出。
    /// </summary>
    public static uint PackPbh(double pitch, double bank, double heading, bool onGround)
    {
        const double ratio = 1024.0 / 360.0;

        static uint Quantise(double value)
        {
            var wrapped = value % 360.0;
            if (wrapped < 0)
                wrapped += 360.0;
            return (uint)(long)Math.Round(wrapped * ratio, MidpointRounding.ToEven) & 0x3FF;
        }

        var packed = (Quantise(pitch) << 22) | (Quantise(bank) << 12) | (Quantise(heading) << 2);
        if (onGround)
            packed |= 0x2;
        return packed;
    }

    public readonly record struct Attitude(double Pitch, double Bank, double Heading, bool OnGround);

    public static Attitude UnpackPbh(uint packed)
    {
        const double ratio = 360.0 / 1024.0;
        const uint mask = 0x3FF;

        static double Normalise(double value) => value > 180.0 ? value - 360.0 : value;

        return new Attitude(
            Normalise(((packed >> 22) & mask) * ratio),
            Normalise(((packed >> 12) & mask) * ratio),
            ((packed >> 2) & mask) * ratio,
            (packed & 0x2) != 0);
    }

    /// <summary>包是冒号分隔的，正文里的冒号和换行会破坏分帧。</summary>
    public static string Sanitize(string? text) =>
        (text ?? string.Empty).Replace(':', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>呼号不合服务端规矩时返回说明。规则来自 can-fsd 的 IsValidCallsign。</summary>
    public static string? CallsignProblem(string? callsign)
    {
        callsign = (callsign ?? string.Empty).Trim().ToUpperInvariant();
        if (callsign.Length < 2 || callsign.Length > MaxCallsignLength)
            return $"呼号 {callsign} 有 {callsign.Length} 个字符，服务器要求 2 到 {MaxCallsignLength} 个。";
        if (callsign.Any(c => !(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')))
            return $"呼号 {callsign} 只能包含字母、数字、下划线和连字符。";
        return null;
    }

    /// <summary>
    /// 把用户输入的一行翻成 (收件人, 正文)。不是已知点命令返回 (null, 原文)。
    /// 现在只认 .wallop：EuroScope 和 CRC 都是在客户端把它翻成发往 *S 的 #TM。
    /// </summary>
    public static (string? Recipient, string Text) ParseDotCommand(string? text)
    {
        var stripped = (text ?? string.Empty).Trim();
        if (!stripped.StartsWith('.'))
            return (null, stripped);
        var parts = stripped.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && parts[0].Equals(".wallop", StringComparison.OrdinalIgnoreCase))
            return (WallopRecipient, parts.Length > 1 ? parts[1].Trim() : string.Empty);
        return (null, stripped);
    }

    /// <summary>频率 → FSD 收件人：128.500 → @28500。</summary>
    public static string FrequencyRecipient(double frequencyMhz)
    {
        var khz = (int)Math.Round(frequencyMhz * 1000.0);
        return "@" + (khz - 100000).ToString("D5", Inv);
    }

    /// <summary>FSD 的 5 位频率 → MHz 字符串：28500 → 128.500。</summary>
    public static string DisplayFrequency(string wire)
    {
        if (wire.Length == 5 && wire.All(char.IsDigit))
            return $"1{wire[..2]}.{wire[2..]}";
        return wire;
    }

    /// <summary>快照里的机体角速度 → 快速位置包的三个角速度字段（弧度每秒）。
    /// 照 xPilot：发 -Q、R、-P，也就是 MSFS ROTATION VELOCITY BODY X/Y/Z 原样的方向。</summary>
    public static (double X, double Y, double Z) WireRotation(OwnAircraftState s) =>
        (-DegToRad(s.PitchRate), DegToRad(s.HeadingRate), -DegToRad(s.BankRate));

    public static bool IsStopped(OwnAircraftState s)
    {
        var (x, y, z) = WireRotation(s);
        return Math.Abs(s.VelocityEast) < StoppedTolerance
               && Math.Abs(s.VelocityUp) < StoppedTolerance
               && Math.Abs(s.VelocityNorth) < StoppedTolerance
               && Math.Abs(x) < StoppedTolerance
               && Math.Abs(y) < StoppedTolerance
               && Math.Abs(z) < StoppedTolerance;
    }

    /// <summary>
    /// 拼一个 `^` / `#SL` / `#ST`。字段和精度照 xPilot 的 PDUFastPilotPosition。
    /// `#ST` 没有 6-11 那六段，一共 7 段；另外两种 13 段——和 can-fsd 的 minFields 一致。
    /// </summary>
    public static string FastPositionPacket(string kind, string callsign, OwnAircraftState s)
    {
        var pbh = PackPbh(s.Pitch, s.Bank, s.Heading, s.OnGround);
        var fields = new List<string>
        {
            kind + callsign,
            s.Latitude.ToString("F6", Inv),
            s.Longitude.ToString("F6", Inv),
            ((double)s.NetworkAltitude).ToString("F2", Inv),
            ((double)s.Agl).ToString("F2", Inv),
            pbh.ToString(Inv)
        };
        if (kind != "#ST")
        {
            var (x, y, z) = WireRotation(s);
            fields.Add(s.VelocityEast.ToString("F4", Inv));
            fields.Add(s.VelocityUp.ToString("F4", Inv));
            fields.Add(s.VelocityNorth.ToString("F4", Inv));
            fields.Add(x.ToString("F4", Inv));
            fields.Add(y.ToString("F4", Inv));
            fields.Add(z.ToString("F4", Inv));
        }
        fields.Add(s.NoseWheel.ToString("F2", Inv));
        return string.Join(':', fields);
    }

    /// <summary>`@` 位置包。第 7 段是网络高度，最后一段是修正量（气压高度 − 网络高度）。</summary>
    public static string PositionPacket(char mode, string callsign, int rating, OwnAircraftState s)
    {
        var pbh = PackPbh(s.Pitch, s.Bank, s.Heading, s.OnGround);
        return string.Create(Inv,
            $"@{mode}:{callsign}:{s.Squawk:D4}:{rating}:{s.Latitude:F5}:{s.Longitude:F5}:{s.NetworkAltitude}:{s.Groundspeed}:{pbh}:{s.PressureDelta}");
    }

    public static double DegToRad(double degrees) => degrees * Math.PI / 180.0;
    public static double RadToDeg(double radians) => radians * 180.0 / Math.PI;

    public static bool TryDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, Inv, out value);
}
