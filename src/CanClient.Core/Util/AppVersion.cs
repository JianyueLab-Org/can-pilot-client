using System.Reflection;

namespace CanClient.Core.Util;

/// <summary>
/// 版本号。默认取 Directory.Build.props 里的正式版本，CI 可通过 CAN_VERSION 覆盖后写进程序集。
/// </summary>
public static class AppVersion
{
    /// <summary>FSD $ID 里的客户端名和更新服务器上的包名，沿用 Python 版，服务端靠它们认人。</summary>
    public const string ClientName = "CAN Pilot Client";
    public const string UpdateClientId = "can-pilot";

    private static readonly Assembly Assembly = typeof(AppVersion).Assembly;

    /// <summary>对外报告的版本号（不带构建元数据）。查更新拿的就是这个。</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>标题栏用的版本串。</summary>
    public static string Display => Version;

    public static string Full => $"v{Display}";

    private static string ReadVersion()
    {
        var informational = Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var text = informational ?? Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = text.IndexOf('+');
        return plus >= 0 ? text[..plus] : text;
    }
}
