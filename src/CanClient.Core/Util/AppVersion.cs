using System.Reflection;

namespace CanClient.Core.Util;

/// <summary>
/// 版本号。正式版本由 CI 在构建时通过 CAN_VERSION 写进程序集；本地构建一律当作开发版。
///
/// 开发版对外报**下一个补丁号**（2.2.0 → 2.2.1），和 Python 版的规矩一致：测试包在排序上
/// 永远新于它基于的正式版，查更新时也不会把它当成旧版去催。
/// </summary>
public static class AppVersion
{
    /// <summary>FSD $ID 里的客户端名和更新服务器上的包名，沿用 Python 版，服务端靠它们认人。</summary>
    public const string ClientName = "MSFS for CAN";
    public const string UpdateClientId = "msfs-for-can";

    private static readonly Assembly Assembly = typeof(AppVersion).Assembly;

    public static bool IsRelease { get; } = Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "CanReleaseBuild" && string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>程序集里写的版本号（不带构建元数据）。</summary>
    public static string ReleaseVersion { get; } = ReadVersion();

    /// <summary>对外报告的版本号。查更新拿的就是这个。</summary>
    public static string Version => IsRelease ? ReleaseVersion : BumpPatch(ReleaseVersion);

    /// <summary>标题栏用的版本串。</summary>
    public static string Display => IsRelease ? Version : $"{Version} (开发版)";

    public static string Full => $"v{Display}";

    private static string ReadVersion()
    {
        var informational = Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var text = informational ?? Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = text.IndexOf('+');
        return plus >= 0 ? text[..plus] : text;
    }

    /// <summary>2.1.1 → 2.1.2。从后往前找到第一段纯数字加一。</summary>
    public static string BumpPatch(string number)
    {
        var pieces = number.Split('.');
        for (var i = pieces.Length - 1; i >= 0; i--)
        {
            if (int.TryParse(pieces[i], out var value))
            {
                pieces[i] = (value + 1).ToString();
                break;
            }
        }
        return string.Join('.', pieces);
    }
}
