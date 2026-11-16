using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CanClient.Core.Util;

namespace CanClient.Core.Net;

/// <summary>连服务器时优先用哪种 IP。设置 → 网络里选，FSD 和语音共用。</summary>
public enum IpPreference
{
    /// <summary>按系统给的顺序，但两种地址交替着试，一种连不上马上换另一种。</summary>
    Auto,
    IPv4,
    IPv6
}

/// <summary>解析出来的地址按偏好排好的顺序。</summary>
/// <param name="Addresses">要依次尝试的地址。</param>
/// <param name="Preferred">用户指定的地址族；自动时为 null。</param>
/// <param name="PreferredMissing">指定了地址族，但域名没有这种地址，只能回退。</param>
public sealed record ConnectPlan(IReadOnlyList<IPAddress> Addresses, AddressFamily? Preferred, bool PreferredMissing);

/// <summary>
/// 双栈 TCP 连接。.NET 自带的 ConnectAsync(host, port) 会把所有地址按系统顺序挨个试，但外面套的是一个
/// 总超时：IPv6 黑洞（SYN 发出去没回音）会把整段超时耗光，IPv4 一次都没试就报错（can-issues#26）。
/// 这里每个地址单独限时，并按用户选的 IPv4 / IPv6 排序；首选的那种没有地址或连不上就回退到另一种，
/// 回退时通过 notice 告诉界面，同时记日志。
/// </summary>
public static class DualStack
{
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(15);

    private static readonly Logger Log = Util.Log.For("net");

    public static IpPreference Parse(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "ipv4" or "v4" or "4" => IpPreference.IPv4,
        "ipv6" or "v6" or "6" => IpPreference.IPv6,
        _ => IpPreference.Auto
    };

    public static string Name(AddressFamily family) => family == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";

    private static AddressFamily Other(AddressFamily family) =>
        family == AddressFamily.InterNetworkV6 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;

    public static ConnectPlan Plan(IEnumerable<IPAddress> resolved, IpPreference preference)
    {
        var list = resolved
            .Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a)
            .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Distinct()
            .ToList();

        if (preference == IpPreference.Auto)
        {
            // 系统排在第一的那种先试，之后两种交替（RFC 8305 的交错），一种坏了不会把另一种全拖住
            if (list.Count == 0)
                return new ConnectPlan(list, null, false);
            var first = list.Where(a => a.AddressFamily == list[0].AddressFamily).ToList();
            var second = list.Where(a => a.AddressFamily != list[0].AddressFamily).ToList();
            var mixed = new List<IPAddress>(list.Count);
            for (var i = 0; i < Math.Max(first.Count, second.Count); i++)
            {
                if (i < first.Count) mixed.Add(first[i]);
                if (i < second.Count) mixed.Add(second[i]);
            }
            return new ConnectPlan(mixed, null, false);
        }

        var family = preference == IpPreference.IPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        var preferred = list.Where(a => a.AddressFamily == family).ToList();
        var others = list.Where(a => a.AddressFamily != family).ToList();
        return new ConnectPlan([.. preferred, .. others], family, preferred.Count == 0 && others.Count > 0);
    }

    /// <summary>
    /// 按偏好连上 host:port，返回已连好的 socket。全部失败时抛最后一个错误。
    /// notice 在发生回退时调用（不在锁里、可能在后台线程）。
    /// </summary>
    public static Socket Connect(string host, int port, IpPreference preference, Action<string>? notice,
        CancellationToken token)
    {
        host = (host ?? "").Trim();
        return Connect(host, port, Resolve(host, token), preference, notice, ConnectOneAsync,
            AttemptTimeout, TotalTimeout, token);
    }

    /// <summary>
    /// 实际的尝试循环，拆出来是为了测试能换掉解析结果、单个地址的连接方式和时限。
    /// </summary>
    internal static Socket Connect(string host, int port, IReadOnlyList<IPAddress> resolved, IpPreference preference,
        Action<string>? notice, Func<IPEndPoint, CancellationToken, Task<Socket>> connectOne,
        TimeSpan attemptTimeout, TimeSpan totalTimeout, CancellationToken token)
    {
        var plan = Plan(resolved, preference);
        if (plan.Addresses.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        void Tell(string message)
        {
            Log.Info(message);
            try { notice?.Invoke(message); }
            catch (Exception e) { Log.Warning($"notice callback raised: {e.Message}"); }
        }

        if (plan is { PreferredMissing: true, Preferred: { } wanted })
            Tell($"{host} 没有可用的 {Name(wanted)} 地址（服务器或本机网络不支持），已改用 {Name(Other(wanted))} 连接。");

        var clock = Stopwatch.StartNew();
        var tried = new HashSet<AddressFamily>();
        var failed = new HashSet<AddressFamily>();
        Exception? last = null;
        for (var i = 0; i < plan.Addresses.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var address = plan.Addresses[i];
            var family = address.AddressFamily;
            var remaining = totalTimeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;
            // 后面还有一种地址族一次都没试过：给它留出一整次尝试的时间。否则首选的那种有好几个
            // 黑洞地址时，会把总时限用光，回退的那种根本轮不到。
            var otherPending = plan.Addresses.Skip(i + 1).Any(a => a.AddressFamily != family && !tried.Contains(a.AddressFamily));
            var budget = otherPending ? remaining - attemptTimeout : remaining;
            var limit = budget < attemptTimeout ? budget : attemptTimeout;
            if (limit <= TimeSpan.Zero)
            {
                Log.Info($"{host}: skipping {Name(family)} {address} to leave time for the other family");
                continue;
            }
            tried.Add(family);

            Socket socket;
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(limit);
                socket = connectOne(new IPEndPoint(address, port), attempt.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                failed.Add(family);
                last = new TimeoutException($"{Name(family)} 连接超时");
                Log.Info($"{host}: {Name(family)} {address} timed out after {limit.TotalSeconds:F1} s");
                continue;
            }
            catch (Exception e) when (e is SocketException or IOException or NotSupportedException)
            {
                // 也包括本机禁用了 IPv6 时建 socket 就失败（AddressFamilyNotSupported）
                failed.Add(family);
                last = e;
                Log.Info($"{host}: {Name(family)} {address} failed: {e.Message}");
                continue;
            }

            var wantedFamily = plan.Preferred ?? (failed.Count > 0 ? Other(family) : family);
            if (family != wantedFamily && !plan.PreferredMissing && failed.Contains(wantedFamily))
                Tell($"{host} 的 {Name(wantedFamily)} 连不上，已回退到 {Name(family)} 连接。");
            Log.Info($"connected to {host}:{port} over {Name(family)} ({address})");
            return socket;
        }

        throw last ?? new TimeoutException("连接超时");
    }

    /// <summary>建 socket 和连接都在这里，出错时把 socket 关掉，由调用方当作这个地址失败处理。</summary>
    private static async Task<Socket> ConnectOneAsync(IPEndPoint endpoint, CancellationToken token)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket?.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<IPAddress> Resolve(string host, CancellationToken token)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
            return [literal];
        var lookup = Dns.GetHostAddressesAsync(host, token);
        try
        {
            if (!lookup.Wait(TimeSpan.FromSeconds(10), token))
                throw new TimeoutException($"解析 {host} 超时");
        }
        catch (AggregateException e) when (e.InnerException is not null)
        {
            throw e.InnerException;
        }
        return lookup.Result;
    }
}
