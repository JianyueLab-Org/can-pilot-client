using System.Globalization;
using System.Text.Json;
using CanClient.Core.Util;

namespace CanClient.Core.Voice;

/// <summary>data.json 里一个在线用户：CID、呼号、是不是管制员、位置。</summary>
public sealed record RangeStation(string Cid, string Callsign, bool IsController, int Facility, double Latitude, double Longitude);

/// <summary>
/// 距离音频范围：同一频率上离得太远的人静音。和 can-audio for_hjdczy 分支 range_audio_protocol.md 的语义一致：
///   - 策略是 CID → allow 的完整快照，每次整体替换；
///   - 不在快照里的 CID 一律放行（ATIS 机器人、只连语音的观察员、data.json 还没刷到的人）；
///   - 拿不到自己的位置时发空快照，全部放行，宁可多听也不漏听。
///
/// 范围看双方里的管制员，双向一致（"我听得见你，你也听得见我"）：
///   飞行员 ↔ 飞行员 200 nm；
///   飞行员 ↔ 管制员 按管制员席位：TWR 及以下（DEL/GND/TWR）100 nm，APP/DEP 300 nm，CTR 600 nm，FSS 2000 nm；
///   管制员 ↔ 管制员 不设限。
/// </summary>
public static class RangePolicy
{
    public const double PilotRangeNm = 200;
    public const double TowerRangeNm = 100;
    public const double ApproachRangeNm = 300;
    public const double CenterRangeNm = 600;
    public const double FlightServiceRangeNm = 2000;

    // FSD 席位代码：1 FSS, 2 DEL, 3 GND, 4 TWR, 5 APP, 6 CTR
    private const int FacilityFss = 1;
    private const int FacilityApp = 5;
    private const int FacilityCtr = 6;

    private const double EarthRadiusNm = 3440.065;

    public static double RangeNm(RangeStation station)
    {
        if (!station.IsController)
            return PilotRangeNm;
        // 呼号后缀优先（LAX_25_CTR），席位代码兜底
        var suffix = station.Callsign.Split('_')[^1].ToUpperInvariant();
        return suffix switch
        {
            "FSS" => FlightServiceRangeNm,
            "CTR" => CenterRangeNm,
            "APP" or "DEP" => ApproachRangeNm,
            "TWR" or "GND" or "DEL" => TowerRangeNm,
            _ => station.Facility switch
            {
                FacilityFss => FlightServiceRangeNm,
                FacilityCtr => CenterRangeNm,
                FacilityApp => ApproachRangeNm,
                _ => TowerRangeNm
            }
        };
    }

    /// <summary>
    /// 算出完整快照。<paramref name="listener"/> 是自己：位置、是不是管制员；为 null 表示不知道自己在哪，返回空快照。
    /// 同一 CID 出现多次（同时挂了飞行员和管制席位）时，任何一个身份听得见就放行。
    /// </summary>
    public static Dictionary<string, bool> Compute(RangeStation? listener, IEnumerable<RangeStation> stations, string? ownCid = null)
    {
        var policy = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (listener is null)
            return policy;
        foreach (var station in stations)
        {
            if (station.Cid.Length == 0 || station.Cid == ownCid)
                continue;
            bool allow;
            if (listener.IsController && station.IsController)
                allow = true;
            else
            {
                var range = RangeNm(listener.IsController ? listener : station);
                allow = DistanceNm(listener.Latitude, listener.Longitude, station.Latitude, station.Longitude) <= range;
            }
            policy[station.Cid] = policy.TryGetValue(station.Cid, out var previous) ? previous || allow : allow;
        }
        return policy;
    }

    /// <summary>大圆距离（海里）。FSS 两千海里，平面近似不够用。</summary>
    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        const double rad = Math.PI / 180.0;
        var dLat = (lat2 - lat1) * rad;
        var dLon = (lon2 - lon1) * rad;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1 * rad) * Math.Cos(lat2 * rad) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusNm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    /// <summary>解析 data.json 的 pilots 和 controllers；ATIS 不算（语音用户名不是 CID）。坐标缺失或无效的跳过。</summary>
    public static List<RangeStation> Parse(JsonElement root)
    {
        var list = new List<RangeStation>();
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("data.json is not an object");
        Read("pilots", controller: false);
        Read("controllers", controller: true);
        return list;

        void Read(string key, bool controller)
        {
            if (!root.TryGetProperty(key, out var rows) || rows.ValueKind != JsonValueKind.Array)
                return;
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;
                var cid = Text(row, "cid");
                if (cid.Length == 0 || Number(row, "latitude") is not { } lat || Number(row, "longitude") is not { } lon)
                    continue;
                if (lat is < -90 or > 90 || lon is < -180 or > 180 || (lat == 0 && lon == 0))
                    continue;
                var facility = Number(row, "facility") is { } f ? (int)f : 0;
                list.Add(new RangeStation(cid, Text(row, "callsign"), controller, facility, lat, lon));
            }
        }
    }

    // 飞行员的坐标是数字，管制员的是字符串，两种都认
    private static string Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()!.Trim(),
            JsonValueKind.Number => v.GetRawText(),
            _ => ""
        } : "";

    private static double? Number(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            return double.IsFinite(d) ? d : null;
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            return double.IsFinite(d) ? d : null;
        return null;
    }
}

/// <summary>
/// 每 5 秒拉一次 data.json，结合自己的位置算出静音快照。快照通过 <see cref="Policy"/> 给语音会话，
/// 主循环每拍把它交给当前的 <see cref="VoiceSession"/>：新建会话、重连都会自动带上，不需要另外补发。
///
/// 拉取失败时保留上次的在线列表继续按自己的新位置算；超过 <see cref="StaleAfter"/> 还没拉到，就退回空快照全部放行。
/// </summary>
public sealed class RangeAudioService : IDisposable
{
    public const string DataUrl = "https://data.ceruleanavi.net/v1/data.json";
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(1);

    private static readonly Logger Log = Util.Log.For("range");
    private static readonly IReadOnlyDictionary<string, bool> Empty = new Dictionary<string, bool>();

    private readonly Func<(string Cid, double Lat, double Lon)?> _ownPosition;
    private readonly HttpClient _http;
    private readonly object _lifecycleLock = new();
    private RunState? _run;
    private Task _loopCompletion = Task.CompletedTask;
    private bool _disposed;
    private volatile IReadOnlyDictionary<string, bool> _policy = Empty;

    // 每次启动独享缓存，旧请求即使晚于重启完成，也不能污染新一轮的数据。
    private sealed class RunState
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public List<RangeStation> Stations { get; set; } = [];
        public DateTime FetchedAt { get; set; } = DateTime.MinValue;
        public bool Failing { get; set; }
        public bool BuildFailing { get; set; }
    }

    /// <param name="ownPosition">自己的 CID 和模拟器位置；没有模拟器位置时返回 (cid, NaN, NaN)，到 data.json 里按 CID 找；没有 CID 返回 null。</param>
    public RangeAudioService(Func<(string Cid, double Lat, double Lon)?> ownPosition)
        : this(ownPosition, Net.Http.Create(TimeSpan.FromSeconds(8)))
    {
    }

    internal RangeAudioService(Func<(string Cid, double Lat, double Lon)?> ownPosition, HttpClient http)
    {
        _ownPosition = ownPosition;
        _http = http;
    }

    /// <summary>当前快照，只读、整体替换。</summary>
    public IReadOnlyDictionary<string, bool> Policy => _policy;

    public bool Running { get { lock (_lifecycleLock) return _run is not null; } }

    internal Task LoopCompletion { get { lock (_lifecycleLock) return _loopCompletion; } }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_run is not null)
                return;
            var run = new RunState();
            _run = run;
            _loopCompletion = Task.Run(() => LoopAsync(run));
            Log.Info("range audio started");
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            var run = _run;
            _run = null;
            _policy = Empty;
            if (run is null)
                return;
            run.Cancellation.Cancel();
            Log.Info("range audio stopped");
        }
    }

    private async Task LoopAsync(RunState run)
    {
        var cancellation = run.Cancellation.Token;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    await RefreshAsync(run, cancellation).ConfigureAwait(false);
                }
                catch (Exception e) when (!cancellation.IsCancellationRequested)
                {
                    // 兜底：循环只能因 Stop 退出，否则快照会冻在最后一次的结果上
                    Log.Warning($"range refresh raised: {e}");
                }
                await Task.Delay(PollInterval, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            // Stop 只取消；等循环退出才释放 CTS，避免启动中的任务访问已释放的 Token。
            lock (_lifecycleLock)
            {
                run.Cancellation.Dispose();
            }
        }
    }

    private async Task RefreshAsync(RunState run, CancellationToken cancellation)
    {
        try
        {
            using var response = await _http.GetAsync(DataUrl, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation).ConfigureAwait(false);
            run.Stations = RangePolicy.Parse(document.RootElement);
            run.FetchedAt = DateTime.UtcNow;
            if (run.Failing)
                Log.Info("data.json reachable again");
            run.Failing = false;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            if (!run.Failing)
                Log.Warning($"could not load {DataUrl}: {e.Message}");
            run.Failing = true;
        }
        if (cancellation.IsCancellationRequested)
            return;
        IReadOnlyDictionary<string, bool> policy;
        try
        {
            policy = DateTime.UtcNow - run.FetchedAt > StaleAfter ? Empty : Build(run.Stations);
            run.BuildFailing = false;
        }
        catch (Exception e)
        {
            // 算不出来就全部放行，和拿不到位置一样
            if (!run.BuildFailing)
                Log.Warning($"could not build range policy: {e.Message}");
            run.BuildFailing = true;
            policy = Empty;
        }
        lock (_lifecycleLock)
        {
            // 与 Stop 的清空操作原子互斥，并拒绝已经被新一轮启动替换的发布者。
            if (ReferenceEquals(_run, run))
                Publish(policy);
        }
    }

    private Dictionary<string, bool> Build(List<RangeStation> stations)
    {
        if (_ownPosition() is not { } own)
            return [];
        // 自己挂着管制席位（比如用观察员模式听频）就按管制员算，位置取席位坐标；
        // 否则是飞行员，优先用模拟器的实时位置，没有再到 data.json 里找自己
        var listener = stations.FirstOrDefault(s => s.IsController && s.Cid == own.Cid)
                       ?? (!double.IsNaN(own.Lat) && !double.IsNaN(own.Lon)
                           ? new RangeStation(own.Cid, "", false, 0, own.Lat, own.Lon)
                           : stations.FirstOrDefault(s => !s.IsController && s.Cid == own.Cid));
        return RangePolicy.Compute(listener, stations, own.Cid);
    }

    private void Publish(IReadOnlyDictionary<string, bool> policy)
    {
        var old = _policy;
        _policy = policy;
        if (old.Count == policy.Count && policy.All(p => old.TryGetValue(p.Key, out var v) && v == p.Value))
            return;
        var muted = policy.Where(p => !p.Value).Select(p => p.Key).OrderBy(c => c, StringComparer.Ordinal).ToList();
        Log.Debug($"range policy: {policy.Count} entries, muted [{string.Join(", ", muted)}]");
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            Stop();
            _http.Dispose();
        }
    }
}
