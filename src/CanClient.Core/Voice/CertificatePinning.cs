using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CanClient.Core.Util;

namespace CanClient.Core.Voice;

/// <summary>
/// 服务器证书和钉住的指纹对不上。**不能重试**：中间人重试三次还是中间人。
/// </summary>
public sealed class CertificatePinException(string message) : Exception(message);

/// <summary>
/// 语音服务器的证书指纹校验，对应 Python 版 mumblecompat.py。
///
/// Mumble 用的是自签证书，CA 链校验对它没有意义——官方客户端认的是证书指纹。而 Authenticate
/// 消息里的密码是成员的网站密码，所以 audio.ceruleanavi.net 必须按指纹验：校验在 TLS 握手里做，
/// 验不过握手就失败，一个字节的凭据都不会发出去。其他主机只记一行指纹。
/// </summary>
public static class CertificatePinning
{
    public const string PinnedHost = "audio.ceruleanavi.net";
    public const string FingerprintEnvironment = "CAN_MUMBLE_FINGERPRINTS";

    private static readonly string[] PinnedFingerprints =
    [
        "b7b66eeb91d4a1044039263d1a1e10e179cd09abc1664e754c5a3f11c5bb331a"
    ];

    private static readonly Logger Log = Util.Log.For("mumble");

    public static string Normalise(string? value) =>
        new string((value ?? "").Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-').ToArray()).ToLowerInvariant();

    public static string Fingerprint(byte[]? der) => der is { Length: > 0 } ? Convert.ToHexStringLower(SHA256.HashData(der)) : "";

    /// <summary>这次连接认哪些指纹。环境变量设了就用它，否则用源码里那组。</summary>
    public static IReadOnlyList<string> AcceptedFingerprints()
    {
        var raw = (Environment.GetEnvironmentVariable(FingerprintEnvironment) ?? "")
            .Replace(',', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalise)
            .Where(f => f.Length > 0)
            .ToList();
        return raw.Count > 0 ? raw : PinnedFingerprints.Select(Normalise).ToList();
    }

    public static bool IsPinnedHost(string? host) =>
        string.Equals((host ?? "").Trim().TrimEnd('.'), PinnedHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>握手时调用。返回是否接受这张证书。</summary>
    public static bool Check(string host, X509Certificate? certificate, out string seen)
    {
        seen = Fingerprint(certificate?.GetRawCertData());
        if (!IsPinnedHost(host))
        {
            Log.Info($"voice server {host} presented certificate SHA-256 {(seen.Length > 0 ? seen : "(unavailable)")} " +
                     "(not the pinned host, fingerprint not enforced)");
            return true;
        }
        var accepted = AcceptedFingerprints();
        if (seen.Length > 0 && accepted.Contains(seen))
        {
            Log.Debug($"voice server certificate matches the pin (SHA-256 {seen})");
            return true;
        }
        Log.Error($"refusing to log in to {host}: its certificate SHA-256 is {(seen.Length > 0 ? seen : "(unavailable)")}, " +
                  "which is not pinned. The network password is sent on this connection, so it is not being sent. " +
                  $"If the server certificate was replaced on purpose, ship the new fingerprint or set {FingerprintEnvironment}.");
        return false;
    }
}
