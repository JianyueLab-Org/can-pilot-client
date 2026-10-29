using System.Diagnostics;
using CanClient.Core.ModelMatching;
using CanClient.Core.Settings;
using CanClient.Core.Util;

namespace CanClient.Core.Sim;

/// <summary>
/// 统一模拟器入口：MSFS（x64 SimConnect）、FSX / P3D（32 位 SimConnect 桥接）、X-Plane（UDP）三路。
/// 自动模式锁定先连上的模拟器，联飞中不切换到另一台模拟器；同时开着几个时由用户在主界面选。
/// macOS / Linux 没有 SimConnect，不管设置写的是什么都只接 X-Plane（<see cref="PlatformSupport.EffectiveSimulatorConnection"/>）。
/// </summary>
public sealed class SimulatorConnection : IDisposable
{
    /// <summary>设置里“模拟器接入”的几个取值。</summary>
    public const string Auto = "Auto";
    public const string MsfsOnly = "MSFS";
    public const string LegacyOnly = "FSX/P3D";
    public const string XPlaneOnly = "XPlane";

    private readonly AppSettings _settings;
    private readonly Timer _poll;
    private readonly object _gate = new();
    private bool _started;
    private bool _connected;
    private string _selection = "";
    private SimulatorType _active;
    private int _sourceGeneration;
    private SimulatorType _chosen;

    public SimulatorConnection(AppSettings settings)
    {
        _settings = settings;
        XPlane = new XPlaneConnection(settings);
        // FSX / P3D 也开着管道名一样的 SimConnect 服务：只有它们在跑、MSFS 没在跑时，MSFS 那条先别去连
        Msfs.OpenGate = () => !SimulatorProcesses.LegacyRunning || SimulatorProcesses.MsfsRunning;
        // 桥接进程只在 FSX / P3D 真的开着时才起
        Legacy.OpenGate = () => SimulatorProcesses.LegacyRunning;
        _poll = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public SimConnection Msfs { get; } = new();
    public SimConnection Legacy { get; } = new(legacy: true);
    public XPlaneConnection XPlane { get; }
    public SimulatorType Simulator { get; private set; }
    public bool Connected => Snapshot() is not null;
    public int Generation { get; private set; }
    public bool LockSelection { get; set; }
    public event Action<bool, string>? StateChanged;
    public event Action<SimulatorType, string>? Opened;

    /// <summary>当前模拟器走的 SimConnect 连接（MSFS 或 FSX / P3D），X-Plane 或没连上时是 null。</summary>
    public SimConnection? ActiveSimConnect => SourceOf(_active);

    public bool IsOpen => _active == SimulatorType.XPlane ? XPlane.Connected : ActiveSimConnect?.IsOpen ?? false;

    public string ApplicationName => _active == SimulatorType.XPlane ? "X-Plane 11 / 12" : ActiveSimConnect?.ApplicationName ?? "";

    public bool MultipleSimulators => _selection == Auto && ConnectedSimulators().Count > 1;
    public bool NeedsSelection => MultipleSimulators && _chosen == SimulatorType.None && !LockSelection;
    public IReadOnlyList<SimulatorType> AvailableSimulators => MultipleSimulators ? ConnectedSimulators() : [];

    public bool ChooseSimulator(SimulatorType simulator)
    {
        lock (_gate)
        {
            if (LockSelection || !AvailableSimulators.Contains(simulator)) return false;
            _chosen = simulator;
        }
        Poll();
        return true;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            ApplySelection();
        }
        _poll.Change(0, 200);
    }

    private bool MsfsEnabled => _selection is Auto or MsfsOnly;
    private bool LegacyEnabled => _selection is Auto or LegacyOnly;
    private bool XPlaneEnabled => _selection is Auto or XPlaneOnly;

    private void ApplySelection()
    {
        _selection = PlatformSupport.EffectiveSimulatorConnection(_settings.SimulatorConnection);
        // 设置页的安装检测只决定导航显示，不影响等待模拟器启动和掉线重连。
        if (MsfsEnabled) Msfs.Start();
        if (LegacyEnabled) Legacy.Start();
        if (XPlaneEnabled) XPlane.Start();
    }

    private SimConnection? SourceOf(SimulatorType simulator) =>
        simulator.IsMsfs() ? Msfs : simulator.IsLegacy() ? Legacy : null;

    /// <summary>选中的接入方式里已经连上的模拟器，顺序就是自动模式的优先级。</summary>
    private List<SimulatorType> ConnectedSimulators()
    {
        var list = new List<SimulatorType>(3);
        if (MsfsEnabled && Msfs.Connected) list.Add(Msfs.Simulator);
        if (LegacyEnabled && Legacy.Connected) list.Add(Legacy.Simulator);
        if (XPlaneEnabled && XPlane.Connected) list.Add(SimulatorType.XPlane);
        return list;
    }

    private void Poll()
    {
        lock (_gate)
        {
            if (!_started) return;
            if (!LockSelection && _selection != PlatformSupport.EffectiveSimulatorConnection(_settings.SimulatorConnection))
            {
                ApplySelection();
                _active = SimulatorType.None;
                _chosen = SimulatorType.None;
            }
            var previous = _active;
            if (!LockSelection || _active == SimulatorType.None)
            {
                var connected = ConnectedSimulators();
                if (connected.Count < 2) _chosen = SimulatorType.None;
                if (connected.Count > 1 && connected.Contains(_chosen)) _active = _chosen;
                if (!connected.Contains(_active))
                    _active = connected.FirstOrDefault();
            }
            Simulator = _active;
            var isConnected = Snapshot() is not null;
            var sourceGeneration = _active == SimulatorType.XPlane ? XPlane.Generation : ActiveSimConnect?.Generation ?? 0;
            if (isConnected && (previous != _active || !_connected || sourceGeneration != _sourceGeneration))
            {
                _sourceGeneration = sourceGeneration;
                Generation++;
                Opened?.Invoke(Simulator, ApplicationName);
            }
            if (isConnected != _connected || previous != _active)
            {
                _connected = isConnected;
                StateChanged?.Invoke(isConnected, isConnected ? $"已连接 {ApplicationName}" : WaitingText);
            }
        }
    }

    private string WaitingText => !PlatformSupport.SupportsMsfs ? "等待 X-Plane 进入飞行" : _selection switch
    {
        MsfsOnly => "等待 MSFS 进入飞行",
        LegacyOnly => "等待 P3D / FSX 进入飞行",
        XPlaneOnly => "等待 X-Plane 进入飞行",
        _ => "等待 MSFS / P3D / FSX / X-Plane 进入飞行"
    };

    public OwnAircraftState? Snapshot() => _active == SimulatorType.XPlane ? XPlane.Snapshot() : ActiveSimConnect?.Snapshot();

    public bool TuneCom(int radio, double frequency) => _active == SimulatorType.XPlane
        ? XPlane.Write($"sim/cockpit2/radios/actuators/com{(radio == 2 ? 2 : 1)}_frequency_hz_833", (float)Math.Round(frequency * 1000))
        : ActiveSimConnect?.TuneCom(radio, frequency) ?? false;
    public bool SetSquawk(int code) => _active == SimulatorType.XPlane
        ? XPlane.Write("sim/cockpit/radios/transponder_code", Convert.ToInt32(code.ToString("D4"), 16))
        : ActiveSimConnect?.SetSquawk(code) ?? false;
    public bool SetTransponderState(TransponderMode mode) => _active == SimulatorType.XPlane
        ? XPlane.Write("sim/cockpit/radios/transponder_mode", mode == TransponderMode.ModeC ? 2 : 1)
        : ActiveSimConnect?.SetTransponderState(mode) ?? false;

    public void Dispose()
    {
        lock (_gate) _started = false;
        _poll.Dispose();
        Msfs.Dispose();
        Legacy.Dispose();
        XPlane.Dispose();
    }
}

/// <summary>哪些模拟器进程开着。按进程名找，结果缓存两秒，SimConnect 线程每三秒问一次。</summary>
internal static class SimulatorProcesses
{
    private static readonly string[] LegacyNames = ["fsx", "Prepar3D"];
    private static readonly string[] MsfsNames = ["FlightSimulator", "FlightSimulator2024"];
    private static readonly object Lock = new();
    private static DateTime _checked = DateTime.MinValue;
    private static bool _legacy, _msfs;

    public static bool LegacyRunning
    {
        get
        {
            Refresh();
            return _legacy;
        }
    }

    public static bool MsfsRunning
    {
        get
        {
            Refresh();
            return _msfs;
        }
    }

    private static void Refresh()
    {
        lock (Lock)
        {
            if ((DateTime.UtcNow - _checked).TotalSeconds < 2)
                return;
            _checked = DateTime.UtcNow;
            _legacy = LegacyNames.Any(Running);
            _msfs = MsfsNames.Any(Running);
        }
    }

    private static bool Running(string name)
    {
        try
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var process in processes)
                process.Dispose();
            return processes.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
