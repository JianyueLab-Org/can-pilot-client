using System.Diagnostics;
using System.Text.Json.Nodes;
using CanClient.Core.Util;

namespace CanClient.Core.Traffic;

/// <summary>渲染端拿到的一架他机：积分到此刻的状态加上记账信息。</summary>
public sealed record TrafficEntry
{
    public required string Callsign { get; init; }
    public int Squawk { get; init; }
    public string Mode { get; init; } = "S";
    public string Equipment { get; init; } = "";
    public string Airline { get; init; } = "";
    public string Livery { get; init; } = "";
    public string Csl { get; init; } = "";
    public bool ModelDirty { get; init; }
    public double VerticalSpeed { get; init; }
    public bool? GearDown { get; init; }
    public double Flaps { get; init; }
    public bool Spoilers { get; init; }
    public IReadOnlyDictionary<string, bool> Lights { get; init; } = new Dictionary<string, bool>();
    public bool EnginesOn { get; init; } = true;
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double Altitude { get; init; }
    public double Pitch { get; init; }
    public double Bank { get; init; }
    public double Heading { get; init; }
    public bool OnGround { get; init; }
    public int Groundspeed { get; init; }
    public double? Agl { get; init; }
    public double NoseWheel { get; init; }
    public double? RangeNm { get; init; }
}

/// <summary>一架他机：记账（机型、配置、应答机）加一个 <see cref="Motion"/>。</summary>
public sealed class TrafficAircraft(string callsign, double created)
{
    public string Callsign { get; } = callsign;
    public int Squawk { get; set; }
    public string TransponderMode { get; set; } = "S";
    public Motion Motion { get; } = new();
    /// <summary>Motion 积分到了哪个时刻。第一个位置样本之前是 null。</summary>
    public double? Time { get; private set; }
    public Sample? Previous { get; private set; }
    public Sample? Latest { get; private set; }
    /// <summary>发过 ^ / #SL / #ST 之后，`@` 就只是心跳。</summary>
    public bool HasVelocity { get; private set; }
    public double Created { get; } = created;
    public double? LastSeen { get; private set; }

    public string Equipment { get; private set; } = "";
    public string Airline { get; private set; } = "";
    public string Livery { get; private set; } = "";
    public string Csl { get; private set; } = "";
    public bool ModelDirty { get; set; } = true;
    public double InfoRequested { get; set; } = double.NegativeInfinity;
    public double ConfigRequested { get; set; } = double.NegativeInfinity;

    public bool? GearDown { get; set; }
    public double Flaps { get; set; }
    public bool Spoilers { get; set; }
    public Dictionary<string, bool> Lights { get; } = new();
    public bool EnginesOn { get; set; } = true;

    public bool HasPlaneInfo => Equipment.Length > 0 || Csl.Length > 0;

    public double VerticalSpeed => Motion.Velocity.B * Motion.FeetPerMetre * 60.0;

    private void AdvanceTo(double now)
    {
        if (Time is not { } time || now <= time)
            return;
        Motion.Advance(now - time);
        Time = now;
    }

    private void ReceiveSample(Sample sample, Vec3 velocity, Vec3 rotation)
    {
        Time ??= sample.Time;
        AdvanceTo(sample.Time);
        Motion.Receive(sample.Latitude, sample.Longitude, sample.Altitude, sample.Pitch, sample.Bank,
            sample.Heading, velocity, rotation);
    }

    public void Update(Sample sample, int? squawk, string? mode)
    {
        LastSeen = LastSeen is null ? sample.Time : Math.Max(LastSeen.Value, sample.Time);
        if (sample.Velocity is not null)
            UpdateFast(sample);
        else if (HasVelocity)
        {
            // 发过快速位置的飞机：`@` 只带来应答机、模式和地速
            if (Latest is not null)
                Latest.Groundspeed = sample.Groundspeed;
        }
        else
            UpdateSlow(sample);
        if (squawk is not null)
            Squawk = squawk.Value;
        if (mode is not null)
            TransponderMode = mode;
    }

    private void UpdateFast(Sample sample)
    {
        HasVelocity = true;
        var rotation = sample.Rotation ?? Vec3.Zero;
        var latest = Latest;
        if (latest is not null && sample.Time < latest.Time)
            sample.Time = latest.Time;
        if (latest is not null && sample.Velocity!.Value.Any && sample.SamePlace(latest)
            && sample.Time - latest.Time < TrafficTable.DuplicateWindow)
        {
            // 同一份快照又发了一遍，飞机其实在动：只换速度，位置不算数
            AdvanceTo(sample.Time);
            Motion.Refresh(sample.Velocity.Value, rotation);
            latest.Velocity = sample.Velocity;
            latest.Rotation = rotation;
            latest.Groundspeed = sample.Groundspeed;
            return;
        }
        ReceiveSample(sample, sample.Velocity!.Value, rotation);
        Previous = latest;
        Latest = sample;
    }

    private void UpdateSlow(Sample sample)
    {
        var latest = Latest;
        if (latest is null)
        {
            ReceiveSample(sample, Vec3.Zero, Vec3.Zero);
            Latest = sample;
            return;
        }
        Sample? older;
        if (sample.Time <= latest.Time)
        {
            // 时间戳打平：新的那个替换 latest，previous 不动
            sample.Time = latest.Time;
            older = Previous;
            Latest = sample;
        }
        else if (sample.SamePlace(latest) && sample.Time - latest.Time < TrafficTable.DuplicateWindow)
        {
            return;
        }
        else
        {
            older = latest;
            Previous = latest;
            Latest = sample;
        }

        Vec3 velocity, rotation;
        if (older is not null && older.Time < sample.Time)
            (velocity, rotation) = Motion.DeriveRates(older, sample);
        else
            (velocity, rotation) = (Motion.Velocity, Motion.Rotation);
        ReceiveSample(sample, velocity, rotation);
    }

    /// <summary>收到 PI:GEN。有变化才置脏，免得渲染端反复重新加载模型。</summary>
    public bool SetPlaneInfo(string? equipment, string? airline, string? livery, string? csl)
    {
        var changed = false;

        string Apply(string current, string? value)
        {
            if (value is null)
                return current;
            value = value.Trim().ToUpperInvariant();
            if (value.Length == 0 || value == current)
                return current;
            changed = true;
            return value;
        }

        Equipment = Apply(Equipment, equipment);
        Airline = Apply(Airline, airline);
        Livery = Apply(Livery, livery);
        Csl = Apply(Csl, csl);
        if (changed)
            ModelDirty = true;
        return changed;
    }

    /// <summary>积分到 now，返回画出来的状态。没有数据返回 null。</summary>
    public (MotionState State, Sample Latest)? StateAt(double now)
    {
        if (!Motion.Ready || Latest is null)
            return null;
        AdvanceTo(now);
        return (Motion.State()!, Latest);
    }
}

/// <summary>
/// 所有他机。对应 xPilot 的 aircrafts 记账加插件里 NetworkAircraft 的运动模型。
/// FSD 线程写，注入线程和界面读，所以整体上锁。不碰模拟器、不碰网络，只有数据和时间。
/// </summary>
public sealed class TrafficTable
{
    public const double StaleAfter = 15.0;
    public const double PlaneInfoRetry = 30.0;
    public const double ConfigRefresh = 10.0;
    public const double DuplicateWindow = 2.0;

    private static readonly Logger Log = Util.Log.For("traffic");
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly Dictionary<string, TrafficAircraft> _aircraft = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>需要向对方要机型时调用。</summary>
    public Action<string>? RequestInfo { get; set; }

    /// <summary>需要向对方要配置（灯光等）时调用。</summary>
    public Action<string>? RequestConfig { get; set; }

    /// <summary>单调、高分辨率的秒数。这个模块里所有时刻都从这里取。</summary>
    public static double Now() => Clock.Elapsed.TotalSeconds;

    public int Count
    {
        get
        {
            lock (_lock)
                return _aircraft.Count;
        }
    }

    public TrafficAircraft? Get(string callsign)
    {
        lock (_lock)
            return _aircraft.GetValueOrDefault(callsign);
    }

    public IReadOnlyList<string> Callsigns()
    {
        lock (_lock)
            return _aircraft.Keys.ToList();
    }

    /// <summary>
    /// 收到一个位置包。velocity 是 (东, 上, 北) 米每秒，rotation 是 (俯仰, 航向, 坡度) 度每秒
    /// （机体轴，抬头/右转/右坡为正）。只有快速位置包才有。
    /// </summary>
    public TrafficAircraft UpdatePosition(string callsign, double latitude, double longitude, double altitude,
        double pitch, double bank, double heading, bool onGround, int groundspeed, int? squawk = null,
        string? mode = null, double? now = null, Vec3? velocity = null, Vec3? rotation = null,
        double? agl = null, double? noseWheel = null)
    {
        var t = now ?? Now();
        TrafficAircraft aircraft;
        bool needsInfo, needsConfig;
        lock (_lock)
        {
            if (!_aircraft.TryGetValue(callsign, out aircraft!))
            {
                aircraft = new TrafficAircraft(callsign, t);
                _aircraft[callsign] = aircraft;
                Log.Debug($"new aircraft {callsign}");
            }
            aircraft.Update(new Sample
            {
                Time = t, Latitude = latitude, Longitude = longitude, Altitude = altitude, Pitch = pitch,
                Bank = bank, Heading = heading, OnGround = onGround, Groundspeed = groundspeed,
                Velocity = velocity, Rotation = rotation, Agl = agl, NoseWheel = noseWheel
            }, squawk, mode);
            needsInfo = !aircraft.HasPlaneInfo && t - aircraft.InfoRequested > PlaneInfoRetry;
            if (needsInfo)
                aircraft.InfoRequested = t;
            needsConfig = t - aircraft.ConfigRequested > ConfigRefresh;
            if (needsConfig)
                aircraft.ConfigRequested = t;
        }

        // 回调放在锁外面：它会去发包
        if (needsInfo)
            Invoke(RequestInfo, callsign, "ask for its aircraft type");
        if (needsConfig)
            Invoke(RequestConfig, callsign, "ask for its configuration");
        return aircraft;
    }

    private static void Invoke(Action<string>? callback, string callsign, string what)
    {
        if (callback is null)
            return;
        try
        {
            callback(callsign);
        }
        catch (Exception e)
        {
            Log.Warning($"could not {what} from {callsign}: {e.Message}");
        }
    }

    public TrafficAircraft SetPlaneInfo(string callsign, string? equipment = null, string? airline = null,
        string? livery = null, string? csl = null)
    {
        TrafficAircraft aircraft;
        bool changed;
        lock (_lock)
        {
            if (!_aircraft.TryGetValue(callsign, out aircraft!))
            {
                // 机型先于位置到达也要留住，别丢
                aircraft = new TrafficAircraft(callsign, Now());
                _aircraft[callsign] = aircraft;
            }
            changed = aircraft.SetPlaneInfo(equipment, airline, livery, csl);
        }
        if (changed)
            Log.Debug($"{callsign} is a {(aircraft.Equipment.Length > 0 ? aircraft.Equipment : "?")}/{(aircraft.Airline.Length > 0 ? aircraft.Airline : "?")}");
        return aircraft;
    }

    /// <summary>$CQ … ACC 的 config JSON，用来驱动动画。</summary>
    public TrafficAircraft? SetConfig(string callsign, JsonObject config)
    {
        lock (_lock)
        {
            if (!_aircraft.TryGetValue(callsign, out var aircraft))
                return null;
            if (config["gear_down"] is JsonValue gear && TryBool(gear, out var gearDown))
                aircraft.GearDown = gearDown;
            if (config["flaps_pct"] is JsonValue flaps && TryDouble(flaps, out var pct))
                aircraft.Flaps = Math.Clamp(pct / 100.0, 0.0, 1.0);
            if (config["spoilers_out"] is JsonValue spoilers && TryBool(spoilers, out var spoilersOut))
                aircraft.Spoilers = spoilersOut;
            if (config["lights"] is JsonObject lights)
            {
                foreach (var (key, value) in lights)
                {
                    if (value is JsonValue v && TryBool(v, out var on))
                        aircraft.Lights[key] = on;
                }
            }
            if (config["engines"] is JsonObject engines && engines.Count > 0)
            {
                aircraft.EnginesOn = engines.Any(e =>
                    e.Value is JsonObject engine && engine["on"] is JsonValue on && TryBool(on, out var b) && b);
            }
            return aircraft;
        }
    }

    private static bool TryBool(JsonValue value, out bool result)
    {
        if (value.TryGetValue(out result))
            return true;
        if (value.TryGetValue(out double number))
        {
            result = number != 0;
            return true;
        }
        if (value.TryGetValue(out string? text))
        {
            result = text is "1" or "true" or "True";
            return true;
        }
        return false;
    }

    private static bool TryDouble(JsonValue value, out double result)
    {
        if (value.TryGetValue(out result))
            return true;
        if (value.TryGetValue(out string? text) && double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result))
            return true;
        result = 0;
        return false;
    }

    public bool Remove(string callsign)
    {
        bool removed;
        lock (_lock)
            removed = _aircraft.Remove(callsign);
        if (removed)
            Log.Info($"{callsign} went offline");
        return removed;
    }

    public void Clear()
    {
        lock (_lock)
            _aircraft.Clear();
    }

    /// <summary>清掉太久没消息的。看的是最近一次收到位置包的时刻，还没收到过位置的按建表时刻算。</summary>
    public IReadOnlyList<string> Prune(double? now = null)
    {
        var t = now ?? Now();
        List<string> gone;
        lock (_lock)
        {
            gone = _aircraft.Where(p => t - (p.Value.LastSeen ?? p.Value.Created) > StaleAfter)
                .Select(p => p.Key).ToList();
            foreach (var callsign in gone)
                _aircraft.Remove(callsign);
        }
        foreach (var callsign in gone)
            Log.Info($"dropped {callsign}, no updates for too long");
        return gone;
    }

    /// <summary>
    /// 每架飞机积分到 now 的状态。给了 origin 就按距离排序并可以截断——远的先扔。
    /// 每次调用都会把飞机往前推到 now，所以 now 要只增不减。
    /// </summary>
    public List<TrafficEntry> Snapshot(double? now = null, (double Lat, double Lon)? origin = null,
        int? limit = null, double? maxRangeNm = null)
    {
        var t = now ?? Now();
        var entries = new List<TrafficEntry>();
        lock (_lock)
        {
            foreach (var one in _aircraft.Values)
            {
                if (one.StateAt(t) is not { } result)
                    continue;
                var (state, latest) = result;
                entries.Add(new TrafficEntry
                {
                    Callsign = one.Callsign,
                    Squawk = one.Squawk,
                    Mode = one.TransponderMode,
                    Equipment = one.Equipment,
                    Airline = one.Airline,
                    Livery = one.Livery,
                    Csl = one.Csl,
                    ModelDirty = one.ModelDirty,
                    VerticalSpeed = one.VerticalSpeed,
                    GearDown = one.GearDown,
                    Flaps = one.Flaps,
                    Spoilers = one.Spoilers,
                    Lights = new Dictionary<string, bool>(one.Lights),
                    EnginesOn = one.EnginesOn,
                    Latitude = state.Latitude,
                    Longitude = state.Longitude,
                    Altitude = state.Altitude,
                    Pitch = state.Pitch,
                    Bank = state.Bank,
                    Heading = state.Heading,
                    OnGround = latest.OnGround,
                    Groundspeed = latest.Groundspeed,
                    Agl = latest.Agl,
                    NoseWheel = latest.NoseWheel ?? 0.0,
                    RangeNm = origin is { } o ? DistanceNm(o.Lat, o.Lon, state.Latitude, state.Longitude) : null
                });
            }
        }

        if (origin is not null)
        {
            entries.Sort((a, b) => a.RangeNm!.Value.CompareTo(b.RangeNm!.Value));
            if (maxRangeNm is { } max)
                entries = entries.Where(e => e.RangeNm <= max).ToList();
        }
        if (limit is { } n && entries.Count > n)
            entries = entries.Take(n).ToList();
        return entries;
    }

    /// <summary>
    /// 渲染端匹配过模型之后回来清标记。带上匹配时用的机型/航司：PI:GEN 的回复如果恰好在快照
    /// 和这里之间落地，无条件清标记会把那次更新吞掉。
    /// </summary>
    public void MarkModelClean(string callsign, string? equipment = null, string? airline = null)
    {
        lock (_lock)
        {
            if (!_aircraft.TryGetValue(callsign, out var aircraft))
                return;
            if (equipment is not null && aircraft.Equipment != equipment)
                return;
            if (airline is not null && aircraft.Airline != airline)
                return;
            aircraft.ModelDirty = false;
        }
    }

    /// <summary>两点距离（海里）。平面近似，几百海里内够用。</summary>
    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        var meanLat = (lat1 + lat2) / 2.0 * Math.PI / 180.0;
        var dy = (lat2 - lat1) * Motion.NmPerDegree;
        var dlon = ((lon2 - lon1 + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        var dx = dlon * Motion.NmPerDegree * Math.Cos(meanLat);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
