using System.Diagnostics;
using System.Runtime.InteropServices;
using CanClient.Core.Util;

namespace CanClient.Core.Sim;

/// <summary>
/// 在自己的线程上调 step()，把他机写进模拟器。
///
/// **平时跟着模拟器的帧走**：注入器订阅了 SimConnect 的 "Frame" 系统事件，每帧在 SimConnect
/// 线程上调一次 <see cref="FrameArrived"/>，那里只记个时间、置个事件；真正的积分和几十次
/// SetDataOnSimObject 在这条线程上做。帧率高于 60 fps 时隔帧写。
///
/// **Frame 不来就退回定时器**（30 Hz）：订阅失败、模拟器暂停（暂停时 MSFS 不发 Frame，可别人的
/// 飞机还在飞）都走这条。
/// </summary>
public sealed class InjectionLoop
{
    private static readonly Logger Log = Util.Log.For("inject");

    private const double FrameRate = 30.0;
    private const double FrameTimeout = 0.25;
    private const double MaxFrameRate = 60.0;

    private readonly Action _step;
    private readonly double _period = 1.0 / FrameRate;
    private readonly double _minFrameInterval = 0.9 / MaxFrameRate;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private volatile bool _stop;
    private Thread? _thread;
    private double _lastFrame = double.NegativeInfinity;
    private string? _mode;

    public InjectionLoop(Action step)
    {
        _step = step;
    }

    public bool Running => _thread is { IsAlive: true };

    /// <summary>模拟器画了一帧。在 SimConnect 线程上调，只做两件小事。</summary>
    public void FrameArrived()
    {
        Volatile.Write(ref _lastFrame, _clock.Elapsed.TotalSeconds);
        _wake.Set();
    }

    public void Start()
    {
        if (Running)
            return;
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "traffic-inject" };
        _thread.Start();
    }

    public void Stop()
    {
        var thread = _thread;
        _stop = true;
        _wake.Set();
        if (thread is not null && thread != Thread.CurrentThread)
        {
            if (!thread.Join(TimeSpan.FromSeconds(2)))
                Log.Warning("the traffic injection loop did not stop within 2 s");
        }
        _thread = null;
        _mode = null;
    }

    private void SetMode(string mode)
    {
        if (mode == _mode)
            return;
        _mode = mode;
        Log.Info(mode == "frame"
            ? "traffic injection follows the simulator's frames"
            : $"traffic injection runs on its own {FrameRate:F0} Hz timer (no frame events from the simulator)");
    }

    private void Step()
    {
        try
        {
            _step();
        }
        catch (Exception e)
        {
            Log.Warning($"injecting traffic into the simulator raised: {e.Message}");
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    private void Run()
    {
        // 定时器模式下 33 ms 的周期要准，Windows 默认 15.6 ms 的粒度会抖成 31/47
        timeBeginPeriod(1);
        try
        {
            var deadline = _clock.Elapsed.TotalSeconds;
            var lastStep = double.NegativeInfinity;
            while (!_stop)
            {
                var now = _clock.Elapsed.TotalSeconds;
                if (now - Volatile.Read(ref _lastFrame) < FrameTimeout)
                {
                    SetMode("frame");
                    var woke = _wake.WaitOne(TimeSpan.FromSeconds(FrameTimeout));
                    if (_stop)
                        break;
                    if (!woke)
                        continue;
                    now = _clock.Elapsed.TotalSeconds;
                    if (now - lastStep < _minFrameInterval)
                        continue;
                    lastStep = now;
                    Step();
                    deadline = _clock.Elapsed.TotalSeconds;
                    continue;
                }

                SetMode("timer");
                lastStep = now;
                Step();
                deadline += _period;
                now = _clock.Elapsed.TotalSeconds;
                if (deadline < now)
                {
                    deadline = now;
                    continue;
                }
                // 期间来了 Frame 的话提前醒，切回跟帧
                _wake.WaitOne(TimeSpan.FromSeconds(deadline - now));
            }
        }
        finally
        {
            timeEndPeriod(1);
        }
    }
}
