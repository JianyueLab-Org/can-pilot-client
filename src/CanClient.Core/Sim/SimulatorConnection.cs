using CanClient.Core.ModelMatching;
using CanClient.Core.Settings;
using CanClient.Core.Util;

namespace CanClient.Core.Sim;

/// <summary>
/// 统一模拟器入口。自动模式锁定先连上的模拟器，联飞中不切换到另一台模拟器。
/// macOS / Linux 没有 SimConnect，不管设置写的是什么都只接 X-Plane（<see cref="PlatformSupport.EffectiveSimulatorConnection"/>）。
/// </summary>
public sealed class SimulatorConnection : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Timer _poll;
    private readonly object _gate = new();
    private bool _started;
    private bool _connected;
    private string _selection = "";
    private SimulatorType _active;
    private int _sourceGeneration;
    private SimulatorType _chosen;
    public bool MultipleSimulators => _selection == "Auto" && Msfs.Connected && XPlane.Connected;
    public bool NeedsSelection => MultipleSimulators && _chosen == SimulatorType.None && !LockSelection;
    public IReadOnlyList<SimulatorType> AvailableSimulators => MultipleSimulators ? [Msfs.Simulator, SimulatorType.XPlane] : [];

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
    public SimulatorConnection(AppSettings settings)
    {
        _settings = settings;
        XPlane = new XPlaneConnection(settings);
        _poll = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }
    public SimConnection Msfs { get; } = new();
    public XPlaneConnection XPlane { get; }
    public SimulatorType Simulator { get; private set; }
    public bool Connected => Snapshot() is not null;
    public bool IsOpen => _active == SimulatorType.XPlane ? XPlane.Connected : Msfs.IsOpen;
    public int Generation { get; private set; }
    public bool LockSelection { get; set; }
    public string ApplicationName => _active == SimulatorType.XPlane ? "X-Plane 11 / 12" : Msfs.ApplicationName;
    public event Action<bool, string>? StateChanged;
    public event Action<SimulatorType, string>? Opened;

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

    private void ApplySelection()
    {
        _selection = PlatformSupport.EffectiveSimulatorConnection(_settings.SimulatorConnection);
        // 设置页的安装检测只决定导航显示，不影响等待模拟器启动和掉线重连。
        if (_selection != "XPlane") Msfs.Start();
        if (_selection != "MSFS") XPlane.Start();
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
                var msfs = _selection != "XPlane" && Msfs.Connected;
                var xp = _selection != "MSFS" && XPlane.Connected;
                if (!msfs || !xp) _chosen = SimulatorType.None;
                if (msfs && xp && _chosen != SimulatorType.None) _active = _chosen;
                if (_active == SimulatorType.None || _active == SimulatorType.XPlane && !xp || _active != SimulatorType.XPlane && !msfs)
                    _active = msfs ? Msfs.Simulator : xp ? SimulatorType.XPlane : SimulatorType.None;
            }
            Simulator = _active == SimulatorType.XPlane ? SimulatorType.XPlane :
                _active != SimulatorType.None ? Msfs.Simulator : SimulatorType.None;
            var connected = Snapshot() is not null;
            var sourceGeneration = _active == SimulatorType.XPlane ? XPlane.Generation : Msfs.Generation;
            if (connected && (previous != _active || !_connected || sourceGeneration != _sourceGeneration))
            {
                _sourceGeneration = sourceGeneration;
                Generation++;
                Opened?.Invoke(Simulator, ApplicationName);
            }
            if (connected != _connected || previous != _active)
            {
                _connected = connected;
                StateChanged?.Invoke(connected, connected ? $"已连接 {ApplicationName}" : PlatformSupport.SupportsMsfs ? "等待 MSFS / X-Plane 进入飞行" : "等待 X-Plane 进入飞行");
            }
        }
    }

    public OwnAircraftState? Snapshot() => _active switch
    {
        SimulatorType.XPlane => XPlane.Snapshot(),
        SimulatorType.Msfs or SimulatorType.Msfs2024 => Msfs.Snapshot(),
        _ => null
    };
    public bool TuneCom(int radio, double frequency) => _active == SimulatorType.XPlane
        ? XPlane.Write($"sim/cockpit2/radios/actuators/com{(radio == 2 ? 2 : 1)}_frequency_hz_833", (float)Math.Round(frequency * 1000))
        : Msfs.TuneCom(radio, frequency);
    public bool SetSquawk(int code) => _active == SimulatorType.XPlane
        ? XPlane.Write("sim/cockpit/radios/transponder_code", Convert.ToInt32(code.ToString("D4"), 16)) : Msfs.SetSquawk(code);
    public bool SetTransponderState(TransponderMode mode) => _active == SimulatorType.XPlane
        ? XPlane.Write("sim/cockpit/radios/transponder_mode", mode == TransponderMode.ModeC ? 2 : 1)
        : Msfs.SetTransponderState(mode);
    public void Dispose()
    {
        lock (_gate) _started = false;
        _poll.Dispose();
        Msfs.Dispose();
        XPlane.Dispose();
    }
}
