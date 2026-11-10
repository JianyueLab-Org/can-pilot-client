using System.Runtime.InteropServices;
using CanClient.Core.Traffic;
using CanClient.Core.Util;
using static CanClient.Core.Sim.SimConnectNative;

namespace CanClient.Core.Sim;

/// <summary>一架他机该用什么模型画，交给注入器的那一份。</summary>
public sealed record InjectionTarget(TrafficEntry Entry, string Model);

/// <summary>模拟器里一架 AI 飞机的现状，航空器列表显示用。</summary>
public sealed record InjectedAircraftInfo(string Callsign, string Model, uint? ObjectId, bool Frozen);

/// <summary>
/// 把他机放进 MSFS / FSX / P3D，对应 Python 版 inject.py。SimConnect 允许外部进程直接建 AI 飞机：
///
///     创建   AICreateNonATCAircraft(title, 呼号, 初始位置, requestID)
///     接管   AIReleaseControl + FREEZE_*_SET（objectID 回来之后各一次）
///     地面   RequestDataOnSimObject(objectID, GROUND ALTITUDE / STATIC CG TO GROUND)
///     移动   SetDataOnSimObject(objectID, 位置定义)，跟着模拟器的 Frame 事件
///     装饰   起落架/襟翼/前轮/发动机各一个定义、灯光用 *_SET 事件，变了才写
///     删除   AIRemoveObject
///
/// objectID 是异步回来的，靠 requestID 认领。建模型失败（CREATE_OBJECT_FAILED）靠包号认领到
/// 具体是哪一次创建——和 vPilot 一样，把那个模型报给 <see cref="CreateFailed"/>，由模型匹配记进
/// 拉黑名单并给这架飞机重新挑一个。
/// </summary>
public sealed class TrafficInjector : IDisposable
{
    private static readonly Logger Log = Util.Log.For("inject");

    private const uint RequestBase = 10000;
    private const uint PositionDefinition = 1000;
    private const uint GroundDefinition = 1001;
    private const uint HeightDefinition = 1002;
    private const uint SurfaceDefinitionBase = 1003;
    private const uint FreezeEventBase = 90000;
    private const uint FrameEventId = 90100;
    private const uint LightEventBase = 90200;

    private const double PendingTimeout = 15.0;
    private const double ModelWait = 3.0;
    private const uint GroundInterval = 10;
    private const double MaxModelHeight = 40.0;
    private const double NoseWheelFullDeflection = 60.0;
    private const double SurfaceEpsilon = 0.01;
    private const int PacketsKept = 512;

    private static readonly string[] FreezeEvents =
        ["FREEZE_LATITUDE_LONGITUDE_SET", "FREEZE_ALTITUDE_SET", "FREEZE_ATTITUDE_SET"];

    private static readonly (string Key, string Event)[] LightEvents =
    [
        ("beacon_on", "BEACON_LIGHTS_SET"),
        ("landing_on", "LANDING_LIGHTS_SET"),
        ("taxi_on", "TAXI_LIGHTS_SET"),
        ("nav_on", "NAV_LIGHTS_SET"),
        ("strobe_on", "STROBES_SET"),
        ("logo_on", "LOGO_LIGHTS_SET")
    ];

    private static readonly (string Key, string Name, string Unit)[] SurfaceFields =
    [
        ("gear", "GEAR HANDLE POSITION", "bool"),
        ("flaps_left", "TRAILING EDGE FLAPS LEFT PERCENT", "percent over 100"),
        ("flaps_right", "TRAILING EDGE FLAPS RIGHT PERCENT", "percent over 100"),
        ("nose_wheel", "GEAR CENTER STEER ANGLE", "percent over 100"),
        // AICreateNonATCAircraft 建出来的飞机引擎都在转，对方关着车也听得到引擎声
        ("engine_1", "GENERAL ENG COMBUSTION:1", "bool"),
        ("engine_2", "GENERAL ENG COMBUSTION:2", "bool"),
        ("engine_3", "GENERAL ENG COMBUSTION:3", "bool"),
        ("engine_4", "GENERAL ENG COMBUSTION:4", "bool")
    ];

    private sealed class Record
    {
        public required string Model;
        public required uint RequestId;
        public required DateTime RequestedAt;
        public uint? ObjectId;
        public bool Frozen;
        public GroundClamp? Clamp;
        public double? ClampTime;
        public bool GroundRejected;
        public Dictionary<string, double> Surfaces { get; } = new();
        public Dictionary<string, bool> Lights { get; } = new();
    }

    private readonly SimConnection _sim;
    private readonly int _generation;
    private readonly object _lock = new();
    private readonly object _opLock = new();
    private readonly Dictionary<string, Record> _aircraft = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _pending = new();             // requestID → 呼号
    private readonly Dictionary<uint, uint> _assigned = new();              // requestID → objectID
    private readonly HashSet<uint> _orphaned = new();                       // 不要了但 objectID 还在路上
    private readonly Dictionary<uint, (string Callsign, string Model, uint RequestId)> _createPackets = new();
    private readonly Dictionary<uint, (string Callsign, uint ObjectId, string What)> _controlPackets = new();
    private readonly Dictionary<uint, string> _surfacePackets = new();
    private readonly Dictionary<string, uint> _surfaceDefinitions = new();
    private readonly Dictionary<string, double> _waitingForType = new();
    private readonly Dictionary<uint, (bool Ground, uint ObjectId)> _dataRequests = new();
    private readonly Dictionary<uint, uint> _groundRequests = new();       // objectID → requestID
    private readonly Dictionary<uint, double> _ground = new();
    private readonly Dictionary<uint, double> _modelHeight = new();
    private (uint Id, string Name)[] _freezeEvents = [];
    private (string Key, uint Id, string Name)[] _lightEvents = [];
    private bool _groundReady;
    private bool _heightReady;
    private bool _groundLogged;
    private uint _nextRequest = RequestBase;
    private readonly IntPtr _positionBuffer = Marshal.AllocHGlobal(7 * sizeof(double));
    private readonly IntPtr _oneValue = Marshal.AllocHGlobal(sizeof(double));
    private bool _disposed;

    public TrafficInjector(SimConnection sim)
    {
        _sim = sim;
        _generation = sim.Generation;
        _sim.ExceptionReceived += OnException;
        _sim.ObjectIdAssigned += OnObjectIdAssigned;
        _sim.SimObjectDataReceived += OnData;
        _sim.EventReceived += OnEvent;
        try
        {
            Setup();
            Available = true;
        }
        catch (Exception e)
        {
            Log.Warning($"traffic injection is unavailable: {e.Message}");
        }
    }

    public bool Available { get; }

    public int Generation => _generation;

    /// <summary>注入走的那条 SimConnect 连接（MSFS 或 FSX / P3D）。换了连接就要重建注入器。</summary>
    public SimConnection Connection => _sim;

    public bool FramesSubscribed { get; private set; }

    /// <summary>模拟器每画一帧调一次（在 SimConnect 线程上），只该做很轻的事。</summary>
    public event Action? Frame;

    /// <summary>模拟器建不出这个模型（呼号, 模型）。</summary>
    public event Action<string, string>? CreateFailed;

    private void Setup()
    {
        string[] position =
        [
            "PLANE LATITUDE", "PLANE LONGITUDE", "PLANE ALTITUDE", "PLANE PITCH DEGREES", "PLANE BANK DEGREES",
            "PLANE HEADING DEGREES TRUE", "AIRSPEED TRUE"
        ];
        string[] units = ["degrees", "degrees", "feet", "degrees", "degrees", "degrees", "knots"];
        // 这里不能放 SIM ON GROUND：它不可写，混进来整条 SetDataOnSimObject 都会失败，飞机冻在原地
        for (var i = 0; i < position.Length; i++)
        {
            var name = position[i];
            var unit = units[i];
            var hr = _sim.Call(h => SimConnect_AddToDataDefinition(h, PositionDefinition, name, unit, DataType.Float64, 0, Unused));
            if (hr < 0)
                throw new InvalidOperationException($"AddToDataDefinition({name}) failed: 0x{hr:X8}");
        }

        var freeze = new List<(uint, string)>();
        for (var i = 0; i < FreezeEvents.Length; i++)
        {
            var id = FreezeEventBase + (uint)i;
            var name = FreezeEvents[i];
            if (_sim.Call(h => SimConnect_MapClientEventToSimEvent(h, id, name)) < 0)
            {
                Log.Warning($"could not map {name}, injected traffic will not be frozen");
                freeze.Clear();
                break;
            }
            freeze.Add((id, name));
        }
        _freezeEvents = freeze.ToArray();

        var lights = new List<(string, uint, string)>();
        for (var i = 0; i < LightEvents.Length; i++)
        {
            var (key, name) = LightEvents[i];
            var id = LightEventBase + (uint)i;
            if (_sim.Call(h => SimConnect_MapClientEventToSimEvent(h, id, name)) >= 0)
                lights.Add((key, id, name));
            else
                Log.Warning($"could not map {name}, that light will not be switched on injected traffic");
        }
        _lightEvents = lights.ToArray();

        _groundReady = _sim.Call(h => SimConnect_AddToDataDefinition(h, GroundDefinition, "GROUND ALTITUDE", "feet",
            DataType.Float64, 0, Unused)) >= 0;
        _heightReady = _groundReady && _sim.Call(h => SimConnect_AddToDataDefinition(h, HeightDefinition,
            "STATIC CG TO GROUND", "feet", DataType.Float64, 0, Unused)) >= 0;

        for (var i = 0; i < SurfaceFields.Length; i++)
        {
            var (key, name, unit) = SurfaceFields[i];
            var definition = SurfaceDefinitionBase + (uint)i;
            if (_sim.Call(h => SimConnect_AddToDataDefinition(h, definition, name, unit, DataType.Float64, 0, Unused)) >= 0)
                _surfaceDefinitions[key] = definition;
        }

        if (_sim.Call(h => SimConnect_SubscribeToSystemEvent(h, FrameEventId, "Frame")) >= 0)
            FramesSubscribed = true;
        else
            Log.Warning("could not subscribe to the simulator's Frame event, traffic injection stays on its timer");
    }

    private uint NextRequest()
    {
        lock (_lock)
            return ++_nextRequest;
    }

    // ---------- SimConnect 线程上的回调 ----------

    private void OnEvent(uint eventId, float frameRate)
    {
        if (eventId == FrameEventId)
            Frame?.Invoke();
    }

    private void OnObjectIdAssigned(uint requestId, uint objectId)
    {
        lock (_lock)
            _assigned[requestId] = objectId;
    }

    private void OnData(uint requestId, uint objectId, IntPtr payload)
    {
        (bool Ground, uint ObjectId) request;
        lock (_lock)
        {
            if (!_dataRequests.TryGetValue(requestId, out request))
                return;
        }
        var value = Marshal.PtrToStructure<double>(payload);
        lock (_lock)
        {
            if (request.Ground)
            {
                _ground[request.ObjectId] = value;
                if (!_groundLogged)
                {
                    _groundLogged = true;
                    Log.Info($"the simulator reports the ground under injected traffic (object {request.ObjectId}: {value:F0} ft)");
                }
            }
            else
                _modelHeight[request.ObjectId] = value is >= 0 and <= MaxModelHeight ? value : 0.0;
        }
    }

    private void OnException(RecvException body)
    {
        string? failedCallsign = null, failedModel = null;
        lock (_lock)
        {
            if (_surfacePackets.Remove(body.SendId, out var surfaceKey))
            {
                if (_surfaceDefinitions.Remove(surfaceKey))
                    Log.Warning($"the simulator refused writing {surfaceKey} on injected traffic ({Exceptions.Name(body.Exception)}); it will not be written again");
                return;
            }
            if (_controlPackets.Remove(body.SendId, out var control))
            {
                Log.Warning($"the simulator refused {control.What} for {control.Callsign} (object {control.ObjectId}): {Exceptions.Name(body.Exception)}");
                return;
            }

            if (body.Exception == Exceptions.CreateObjectFailed)
            {
                (string Callsign, string Model, uint RequestId) create;
                if (!_createPackets.Remove(body.SendId, out create))
                {
                    // 对不上包号：只有一个在建的时候才能指认（vPilot 拿队首的那个）
                    if (_pending.Count != 1)
                    {
                        Log.Warning($"the simulator refused to create traffic, but it is not clear which request failed ({_pending.Count} pending)");
                        return;
                    }
                    var only = _pending.First();
                    create = (only.Value, _aircraft.TryGetValue(only.Value, out var r) ? r.Model : "", only.Key);
                }
                _pending.Remove(create.RequestId);
                if (_aircraft.TryGetValue(create.Callsign, out var record) && record.RequestId == create.RequestId)
                    _aircraft.Remove(create.Callsign);
                failedCallsign = create.Callsign;
                failedModel = create.Model;
            }
            else if (body.Exception == Exceptions.ObjectOutsideRealityBubble)
            {
                // 暂时的：等 PendingTimeout 自己重来
                Log.Info("an aircraft is outside the simulator's reality bubble, it will be retried");
                return;
            }
            else if (body.Exception is Exceptions.ObjectContainer or Exceptions.DataError)
            {
                Log.Warning($"SimConnect exception for traffic: {Exceptions.Name(body.Exception)} (packet {body.SendId})");
                return;
            }
            else
            {
                Log.Debug($"SimConnect exception {Exceptions.Name(body.Exception)} (packet {body.SendId}, index {body.Index})");
                return;
            }
        }

        Log.Warning($"the simulator could not create {failedCallsign} with model {failedModel}");
        if (failedCallsign is not null && !string.IsNullOrEmpty(failedModel))
            CreateFailed?.Invoke(failedCallsign, failedModel);
    }

    // ---------- 同步 ----------

    /// <summary>让模拟器里的 AI 飞机和这份快照一致。entries 已经按距离排好、截断过。</summary>
    public void Sync(IReadOnlyList<InjectionTarget> targets, double now)
    {
        if (!Available || _disposed || _sim.Generation != _generation)
            return;
        lock (_opLock)
        {
            CollectAssigned();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in targets)
            {
                seen.Add(target.Entry.Callsign);
                try
                {
                    SyncOne(target, now);
                }
                catch (Exception e)
                {
                    Log.Debug($"updating {target.Entry.Callsign} raised: {e.Message}");
                }
            }

            List<string> gone;
            lock (_lock)
                gone = _aircraft.Keys.Where(c => !seen.Contains(c)).ToList();
            foreach (var callsign in gone)
                RemoveLocked(callsign);
            foreach (var callsign in _waitingForType.Keys.Where(c => !seen.Contains(c)).ToList())
                _waitingForType.Remove(callsign);
        }
    }

    private void CollectAssigned()
    {
        var claimed = new List<(string Callsign, uint ObjectId)>();
        var strays = new List<uint>();
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            foreach (var (requestId, objectId) in _assigned.ToList())
            {
                if (_pending.Remove(requestId, out var callsign))
                {
                    _assigned.Remove(requestId);
                    if (_aircraft.TryGetValue(callsign, out var record) && record.RequestId == requestId)
                    {
                        record.ObjectId = objectId;
                        claimed.Add((callsign, objectId));
                        Log.Info($"{callsign} is in the simulator (object {objectId}, model {record.Model})");
                    }
                    else
                        strays.Add(objectId);
                }
                else if (_orphaned.Remove(requestId))
                {
                    _assigned.Remove(requestId);
                    strays.Add(objectId);
                }
            }

            // 等了太久还没等到 objectID 的：多半是那次创建静默失败了（比如位置太远）
            foreach (var (callsign, record) in _aircraft.ToList())
            {
                if (record.ObjectId is null && (now - record.RequestedAt).TotalSeconds > PendingTimeout)
                {
                    if (_pending.Remove(record.RequestId))
                        _orphaned.Add(record.RequestId);
                    _aircraft.Remove(callsign);
                    Log.Debug($"gave up waiting for {callsign}'s objectID, will recreate");
                }
            }
        }

        foreach (var (callsign, objectId) in claimed)
        {
            var frozen = TakeControl(callsign, objectId);
            lock (_lock)
            {
                if (_aircraft.TryGetValue(callsign, out var record))
                    record.Frozen = frozen;
            }
            RequestGround(callsign, objectId);
        }
        foreach (var objectId in strays)
        {
            _sim.Call(h => SimConnect_AIRemoveObject(h, objectId, NextRequest()));
            Log.Info($"swept up an aircraft that was no longer needed (object {objectId})");
        }
    }

    private void SyncOne(InjectionTarget target, double now)
    {
        var entry = target.Entry;
        var callsign = entry.Callsign;
        Record? record;
        lock (_lock)
            _aircraft.TryGetValue(callsign, out record);

        if (record is not null && target.Model.Length > 0 && record.Model != target.Model)
        {
            // 机型问到了或者变了，换模型只能删了重建
            RemoveLocked(callsign);
            record = null;
        }

        if (record is null)
        {
            if (target.Model.Length == 0)
                return;
            if (!TypeKnownOrWaited(callsign, entry, now))
                return;
            _waitingForType.Remove(callsign);
            Create(callsign, entry, target.Model);
            return;
        }

        if (record.ObjectId is not { } objectId)
            return;
        Move(objectId, entry, GroundAltitude(callsign, record, objectId, entry, now));
        WriteSurfaces(record, objectId, entry);
        if (_lightEvents.Length > 0)
            WriteLights(callsign, record, objectId, entry);
    }

    /// <summary>机型问到了，或者已经等够 3 秒了。先拿通用模型建、半秒后再换，画面上就是一架飞机闪一下。</summary>
    private bool TypeKnownOrWaited(string callsign, TrafficEntry entry, double now)
    {
        if (entry.Equipment.Length > 0 || entry.Csl.Length > 0)
            return true;
        if (!_waitingForType.TryGetValue(callsign, out var first))
            _waitingForType[callsign] = first = now;
        return now - first >= ModelWait;
    }

    private void Create(string callsign, TrafficEntry entry, string model)
    {
        var init = new InitPosition
        {
            Latitude = entry.Latitude,
            Longitude = entry.Longitude,
            Altitude = entry.Altitude,
            // FSD 的姿态是抬头、右滚为正；MSFS 的 PLANE PITCH/BANK 正好反过来
            Pitch = -entry.Pitch,
            Bank = -entry.Bank,
            Heading = entry.Heading,
            OnGround = entry.OnGround ? 1u : 0u,
            Airspeed = (uint)Math.Max(0, entry.Groundspeed)
        };
        var requestId = NextRequest();
        var tail = callsign.Length > 12 ? callsign[..12] : callsign;
        lock (_lock)
        {
            _pending[requestId] = callsign;
            _aircraft[callsign] = new Record { Model = model, RequestId = requestId, RequestedAt = DateTime.UtcNow };
        }
        var hr = _sim.Call(h => SimConnect_AICreateNonATCAircraft(h, model, tail, init, requestId), out var packet);
        if (hr < 0)
        {
            // HRESULT 直接失败是连接/参数层面的问题，不是这个模型的错，不拉黑
            Log.Warning($"creating {callsign} failed (model {model}): 0x{hr:X8}");
            lock (_lock)
            {
                _pending.Remove(requestId);
                _aircraft.Remove(callsign);
            }
            return;
        }
        lock (_lock)
        {
            _createPackets[packet] = (callsign, model, requestId);
            Trim(_createPackets);
        }
        if (entry.Equipment.Length > 0)
            Log.Info($"asking the simulator to create {callsign}: model {model}, request {requestId}");
        else
            Log.Debug($"asking the simulator to create {callsign}: model {model}, request {requestId}");
    }

    private static void Trim<T>(Dictionary<uint, T> packets)
    {
        while (packets.Count > PacketsKept)
            packets.Remove(packets.Keys.Min());
    }

    /// <summary>把刚建好的飞机从 MSFS 的 AI 手里拿过来：释放 AI 控制，冻结位置和姿态。</summary>
    private bool TakeControl(string callsign, uint objectId)
    {
        var ok = true;
        if (_sim.Call(h => SimConnect_AIReleaseControl(h, objectId, NextRequest()), out var packet) < 0)
            ok = false;
        else
            RememberControl(packet, callsign, objectId, "AIReleaseControl");
        foreach (var (id, name) in _freezeEvents)
        {
            if (_sim.Call(h => SimConnect_TransmitClientEvent(h, objectId, id, 1, GroupPriorityHighest,
                    EventFlagGroupIdIsPriority), out packet) < 0)
                ok = false;
            else
                RememberControl(packet, callsign, objectId, name);
        }
        if (!ok)
            Log.Warning($"could not take {callsign} (object {objectId}) off AI control");
        else if (_freezeEvents.Length == 0)
        {
            Log.Warning($"{callsign} (object {objectId}) is released from AI control but not frozen");
            return false;
        }
        else
            Log.Info($"{callsign} (object {objectId}) is frozen and released from AI control");
        return ok;
    }

    private void RememberControl(uint packet, string callsign, uint objectId, string what)
    {
        lock (_lock)
        {
            _controlPackets[packet] = (callsign, objectId, what);
            Trim(_controlPackets);
        }
    }

    private void RequestGround(string callsign, uint objectId)
    {
        if (!_groundReady)
            return;
        var requestId = NextRequest();
        lock (_lock)
            _dataRequests[requestId] = (true, objectId);
        if (_sim.Call(h => SimConnect_RequestDataOnSimObject(h, requestId, GroundDefinition, objectId, Period.SimFrame,
                0, 0, GroundInterval, 0)) < 0)
        {
            lock (_lock)
                _dataRequests.Remove(requestId);
            Log.Warning($"could not request the ground elevation for {callsign} (object {objectId})");
        }
        else
        {
            lock (_lock)
                _groundRequests[objectId] = requestId;
        }

        if (!_heightReady)
            return;
        var heightRequest = NextRequest();
        lock (_lock)
            _dataRequests[heightRequest] = (false, objectId);
        if (_sim.Call(h => SimConnect_RequestDataOnSimObject(h, heightRequest, HeightDefinition, objectId, Period.Once,
                0, 0, 0, 0)) < 0)
        {
            lock (_lock)
                _dataRequests.Remove(heightRequest);
        }
    }

    private void ForgetGround(uint objectId)
    {
        uint? requestId;
        lock (_lock)
        {
            requestId = _groundRequests.Remove(objectId, out var rid) ? rid : null;
            foreach (var key in _dataRequests.Where(r => r.Value.ObjectId == objectId).Select(r => r.Key).ToList())
                _dataRequests.Remove(key);
            _ground.Remove(objectId);
            _modelHeight.Remove(objectId);
        }
        if (requestId is { } id)
            _sim.Call(h => SimConnect_RequestDataOnSimObject(h, id, GroundDefinition, objectId, Period.Never, 0, 0, 0, 0));
    }

    private double GroundAltitude(string callsign, Record record, uint objectId, TrafficEntry entry, double now)
    {
        if (!_groundReady)
            return entry.Altitude;
        double? localGround;
        double modelHeight;
        lock (_lock)
        {
            localGround = _ground.TryGetValue(objectId, out var g) ? g : null;
            modelHeight = _modelHeight.GetValueOrDefault(objectId);
        }
        record.Clamp ??= new GroundClamp();
        var dt = record.ClampTime is { } last ? now - last : 0.0;
        record.ClampTime = now;
        var adjusted = record.Clamp.Update(now, dt, entry.Altitude, entry.Agl, entry.OnGround, localGround, modelHeight);
        if (record.Clamp.Rejected && !record.GroundRejected)
        {
            record.GroundRejected = true;
            Log.Warning($"the simulator's ground under {callsign} (object {objectId}) is {localGround:F0} ft, " +
                        $"more than {GroundClamp.MaxSceneryDifference:F0} ft from the sender's; not using it");
        }
        return adjusted;
    }

    private void Move(uint objectId, TrafficEntry entry, double altitude)
    {
        unsafe
        {
            var values = (double*)_positionBuffer;
            values[0] = entry.Latitude;
            values[1] = entry.Longitude;
            values[2] = altitude;
            values[3] = -entry.Pitch;
            values[4] = -entry.Bank;
            values[5] = entry.Heading;
            values[6] = entry.Groundspeed;
        }
        _sim.Call(h => SimConnect_SetDataOnSimObject(h, PositionDefinition, objectId, 0, 0, 7 * sizeof(double),
            _positionBuffer));
    }

    private void WriteSurfaces(Record record, uint objectId, TrafficEntry entry)
    {
        // 定义表会被异常回调删掉，快照必须在锁内取
        KeyValuePair<string, uint>[] definitions;
        lock (_lock)
            definitions = _surfaceDefinitions.ToArray();
        if (definitions.Length == 0)
            return;

        var gear = entry.GearDown;
        if (gear is null && entry.OnGround)
            gear = true;
        var wheel = Math.Clamp(entry.NoseWheel / NoseWheelFullDeflection, -1.0, 1.0);
        var engines = entry.EnginesOn ? 1.0 : 0.0;
        var wanted = new Dictionary<string, double?>
        {
            ["gear"] = gear is null ? null : gear.Value ? 1.0 : 0.0,
            ["flaps_left"] = entry.Flaps,
            ["flaps_right"] = entry.Flaps,
            ["nose_wheel"] = entry.OnGround ? wheel : 0.0,
            ["engine_1"] = engines,
            ["engine_2"] = engines,
            ["engine_3"] = engines,
            ["engine_4"] = engines
        };
        foreach (var (key, definition) in definitions)
        {
            if (wanted.GetValueOrDefault(key) is not { } value)
                continue;
            if (record.Surfaces.TryGetValue(key, out var last) && Math.Abs(last - value) < SurfaceEpsilon)
                continue;
            record.Surfaces[key] = value;
            Marshal.StructureToPtr(value, _oneValue, false);
            var hr = _sim.Call(h => SimConnect_SetDataOnSimObject(h, definition, objectId, 0, 0, sizeof(double), _oneValue),
                out var packet);
            if (hr < 0)
            {
                lock (_lock)
                {
                    if (_surfaceDefinitions.Remove(key))
                        Log.Warning($"could not write {key} on injected traffic, it will not be written again");
                }
                continue;
            }
            lock (_lock)
            {
                _surfacePackets[packet] = key;
                Trim(_surfacePackets);
            }
        }
    }

    private void WriteLights(string callsign, Record record, uint objectId, TrafficEntry entry)
    {
        foreach (var (key, id, name) in _lightEvents)
        {
            var wanted = entry.Lights.TryGetValue(key, out var on) && on;
            if (record.Lights.TryGetValue(key, out var written) && written == wanted)
                continue;
            record.Lights[key] = wanted;
            if (_sim.Call(h => SimConnect_TransmitClientEvent(h, objectId, id, wanted ? 1u : 0u, GroupPriorityHighest,
                    EventFlagGroupIdIsPriority), out var packet) < 0)
                Log.Warning($"could not switch {name} on {callsign} (object {objectId})");
            else
                RememberControl(packet, callsign, objectId, name);
        }
    }

    public void Remove(string callsign)
    {
        lock (_opLock)
            RemoveLocked(callsign);
    }

    private void RemoveLocked(string callsign)
    {
        Record? record;
        lock (_lock)
        {
            if (!_aircraft.Remove(callsign, out record))
                return;
            if (record.ObjectId is null && _pending.Remove(record.RequestId))
            {
                // 号码还没回来。飞机在模拟器里可能已经建出来了，等号码回来补删
                _orphaned.Add(record.RequestId);
            }
        }
        if (record.ObjectId is not { } objectId)
            return;
        ForgetGround(objectId);
        _sim.Call(h => SimConnect_AIRemoveObject(h, objectId, NextRequest()));
        Log.Info($"removed {callsign} from the simulator");
    }

    /// <summary>全部清掉。断开网络和退出时都要调，否则飞机会冻在天上。</summary>
    public void Clear()
    {
        lock (_opLock)
        {
            List<string> all;
            lock (_lock)
                all = _aircraft.Keys.ToList();
            foreach (var callsign in all)
                RemoveLocked(callsign);
            _waitingForType.Clear();
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
                return _aircraft.Count;
        }
    }

    public IReadOnlyDictionary<string, InjectedAircraftInfo> Describe()
    {
        lock (_lock)
        {
            return _aircraft.ToDictionary(p => p.Key,
                p => new InjectedAircraftInfo(p.Key, p.Value.Model, p.Value.ObjectId, p.Value.Frozen));
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        try
        {
            if (_sim.Generation == _generation)
                Clear();
        }
        catch (Exception e)
        {
            Log.Debug($"clearing traffic on dispose raised: {e.Message}");
        }
        _disposed = true;
        _sim.ExceptionReceived -= OnException;
        _sim.ObjectIdAssigned -= OnObjectIdAssigned;
        _sim.SimObjectDataReceived -= OnData;
        _sim.EventReceived -= OnEvent;
        lock (_opLock)
        {
            Marshal.FreeHGlobal(_positionBuffer);
            Marshal.FreeHGlobal(_oneValue);
        }
    }
}
