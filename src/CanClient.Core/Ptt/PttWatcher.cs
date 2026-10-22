using System.Runtime.InteropServices;
using CanClient.Core.Util;
using Microsoft.Win32;

namespace CanClient.Core.Ptt;

public sealed record JoystickInfo(int Id, string Name);

/// <summary>
/// PTT 的输入源：键盘、鼠标侧键、摇杆按钮，任意一个绑定按住即发话。
///
/// 连接语音时一条线程每 10 ms 轮询已绑定输入；未连接或没有绑定时事件休眠。
/// 键盘和鼠标用 GetAsyncKeyState（全局，不需要窗口焦点，
/// **不吞键**——PTT 键在模拟器里往往另有用途），摇杆用 winmm 的 joyGetPosEx。
/// 状态真的翻转时才触发 <see cref="Changed"/>，在轮询线程上。
///
/// 录制模式（设置页"设置新的 PTT 按键"）：记下当前已经按着的，等第一个新按下的键/按钮。
/// 鼠标只认侧键 X1/X2，左中右键根本不接受。
/// </summary>
public sealed class PttWatcher : IDisposable
{
    private static readonly Logger Log = Util.Log.For("ptt");

    private const int PollMs = 10;
    private readonly AutoResetEvent _wake = new(false);
    private bool _monitoring;
    private readonly int[] _joystickButtons = new int[16];
    private readonly bool[] _joystickRead = new bool[16];
    private IReadOnlyList<JoystickInfo> _captureJoysticks = [];
    private DateTime _nextCaptureScan;
    private const int VkLButton = 0x01, VkRButton = 0x02, VkMButton = 0x04, VkXButton1 = 0x05, VkXButton2 = 0x06;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfoEx
    {
        public int Size;
        public int Flags;
        public int Xpos, Ypos, Zpos, Rpos, Upos, Vpos;
        public int Buttons;
        public int ButtonNumber;
        public int Pov;
        public int Reserved1, Reserved2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JoyCaps
    {
        public ushort Mid;
        public ushort Pid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Pname;
        public int Xmin, Xmax, Ymin, Ymax, Zmin, Zmax;
        public int NumButtons;
        public int PeriodMin, PeriodMax;
        public int Rmin, Rmax, Umin, Umax, Vmin, Vmax;
        public int Caps;
        public int MaxAxes, NumAxes, MaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string RegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string OemVxd;
    }

    [DllImport("winmm.dll")]
    private static extern int joyGetPosEx(int id, ref JoyInfoEx info);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int joyGetDevCapsW(IntPtr id, ref JoyCaps caps, int size);

    [DllImport("winmm.dll")]
    private static extern int joyGetNumDevs();

    private const int JoyReturnButtons = 0x80;
    private const int JoyErrNoError = 0;

    private readonly object _lock = new();
    private List<PttBinding> _bindings = new();
    private Thread? _thread;
    private volatile bool _running;
    private bool _pressed;
    private TaskCompletionSource<PttBinding?>? _capture;
    private HashSet<string>? _captureBaseline;
    private readonly bool[] _joystickPresent = new bool[16];
    private DateTime _nextJoystickScan = DateTime.MinValue;

    /// <summary>按下 / 松开。在轮询线程上触发。</summary>
    public event Action<bool>? Changed;

    public bool Pressed => _pressed;

    public void SetBindings(IEnumerable<PttBinding> bindings)
    {
        lock (_lock)
            _bindings = bindings.DistinctBy(b => (b.Kind, b.VirtualKey, b.MouseButton, b.Device, b.Button)).ToList();
        _wake.Set();
    }

    /// <summary>未连语音时休眠，录制仍可单独唤醒。仅扫描配置的输入。</summary>
    public void SetMonitoring(bool enabled)
    {
        lock (_lock) _monitoring = enabled;
        _wake.Set();
    }

    public void Start()
    {
        if (_thread is not null)
            return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "ptt" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _thread = null;
        if (_pressed)
        {
            _pressed = false;
            Changed?.Invoke(false);
        }
        CancelCapture();
    }

    public void Dispose() => Stop();

    private static bool KeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private int JoystickButtons(int id)
    {
        if (id is < 0 or >= 16)
            return 0;
        if (_joystickRead[id]) return _joystickButtons[id];
        _joystickRead[id] = true;
        if (!_joystickPresent[id] && DateTime.UtcNow < _nextJoystickScan)
            return 0;
        var info = new JoyInfoEx { Size = Marshal.SizeOf<JoyInfoEx>(), Flags = JoyReturnButtons };
        var ok = joyGetPosEx(id, ref info) == JoyErrNoError;
        _joystickPresent[id] = ok;
        return _joystickButtons[id] = ok ? info.Buttons : 0;
    }

    internal static bool AnyDown(IReadOnlyList<PttBinding> bindings, Func<int, bool> keyDown, Func<int, int> joystickButtons)
    {
        for (var i = 0; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            var down = binding.Kind switch
            {
                PttKind.Keyboard => binding.VirtualKey is > 0 and <= 254 && keyDown(binding.VirtualKey),
                PttKind.Mouse => keyDown(binding.MouseButton == "x2" ? VkXButton2 : VkXButton1),
                _ => binding.Button is >= 0 and < 32 && (joystickButtons(binding.Device) & (1 << binding.Button)) != 0
            };
            if (down) return true;
        }
        return false;
    }

    private void Run()
    {
        Func<int, bool> keyDown = KeyDown;
        Func<int, int> joystickButtons = JoystickButtons;
        while (_running)
        {
            var delay = Timeout.Infinite;
            try
            {
                bool monitoring, capturing;
                List<PttBinding> bindings;
                lock (_lock) { monitoring = _monitoring; capturing = _capture is not null; bindings = _bindings; }
                Array.Clear(_joystickRead);
                Array.Clear(_joystickButtons);
                delay = capturing || monitoring && bindings.Count > 0 ? PollMs : Timeout.Infinite;
                // 拔掉的摇杆每 3 秒再看一次（热插拔在飞行摇杆上很常见）
                if (DateTime.UtcNow >= _nextJoystickScan)
                {
                    Array.Fill(_joystickPresent, true);
                    _nextJoystickScan = DateTime.UtcNow.AddSeconds(3);
                }

                if (capturing)
                    PollCapture();

                var down = monitoring && !capturing && AnyDown(bindings, keyDown, joystickButtons);
                if (down != _pressed)
                {
                    _pressed = down;
                    try
                    {
                        Changed?.Invoke(down);
                    }
                    catch (Exception e)
                    {
                        Log.Warning($"PTT callback raised: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Debug($"PTT poll raised: {e.Message}");
            }
            _wake.WaitOne(delay);
        }
    }

    // ---------- 录制 ----------

    /// <summary>等用户按下一个新的键或按钮。取消返回 null。</summary>
    public Task<PttBinding?> CaptureAsync()
    {
        lock (_lock)
        {
            _capture?.TrySetResult(null);
            _capture = new TaskCompletionSource<PttBinding?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _captureBaseline = null;
            _nextCaptureScan = DateTime.MinValue;
            _wake.Set();
            if (_thread is null)
                Start();
            return _capture.Task;
        }
    }

    public void CancelCapture()
    {
        lock (_lock)
        {
            _capture?.TrySetResult(null);
            _capture = null;
            _wake.Set();
        }
    }

    private void PollCapture()
    {
        var down = new Dictionary<string, PttBinding>();
        for (var vk = 0x08; vk <= 0xFE; vk++)
        {
            if (vk is VkLButton or VkRButton or VkMButton or VkXButton1 or VkXButton2)
                continue;
            // Shift/Ctrl/Alt 不分左右的那三个码和左右码会同时亮，只要分左右的
            if (vk is 0x10 or 0x11 or 0x12)
                continue;
            if (KeyDown(vk))
                down[$"k{vk}"] = PttBinding.Keyboard(vk);
        }
        if (KeyDown(VkXButton1))
            down["mx1"] = PttBinding.Mouse("x1");
        if (KeyDown(VkXButton2))
            down["mx2"] = PttBinding.Mouse("x2");
        if (DateTime.UtcNow >= _nextCaptureScan)
        {
            _captureJoysticks = Joysticks();
            _nextCaptureScan = DateTime.UtcNow.AddSeconds(3);
        }
        foreach (var stick in _captureJoysticks)
        {
            var buttons = JoystickButtons(stick.Id);
            for (var b = 0; b < 32; b++)
            {
                if ((buttons & (1 << b)) != 0)
                    down[$"j{stick.Id}:{b}"] = PttBinding.Joystick(stick.Id, b, stick.Name);
            }
        }

        TaskCompletionSource<PttBinding?>? capture;
        PttBinding? captured;
        lock (_lock)
        {
            capture = _capture;
            if (capture is null)
                return;
            if (_captureBaseline is null)
            {
                // 点按钮那一下还按着的（比如回车）不算
                _captureBaseline = down.Keys.ToHashSet();
                return;
            }
            _captureBaseline.IntersectWith(down.Keys);
            captured = down.FirstOrDefault(p => !_captureBaseline.Contains(p.Key)).Value;
            if (captured is null)
                return;
            _capture = null;
        }
        Log.Info($"captured a PTT binding: {captured.Describe()}");
        capture.TrySetResult(captured);
    }

    // ---------- 摇杆 ----------

    private static readonly Dictionary<int, string> NameCache = new();

    /// <summary>接着的摇杆。名字从注册表的 OEMName 取（winmm 给的通常是驱动名）。</summary>
    public static IReadOnlyList<JoystickInfo> Joysticks()
    {
        var list = new List<JoystickInfo>();
        int count;
        try
        {
            count = Math.Min(16, joyGetNumDevs());
        }
        catch
        {
            return list;
        }
        for (var id = 0; id < count; id++)
        {
            var info = new JoyInfoEx { Size = Marshal.SizeOf<JoyInfoEx>(), Flags = JoyReturnButtons };
            if (joyGetPosEx(id, ref info) != JoyErrNoError)
                continue;
            list.Add(new JoystickInfo(id, JoystickName(id)));
        }
        return list;
    }

    private static string JoystickName(int id)
    {
        lock (NameCache)
        {
            if (NameCache.TryGetValue(id, out var cached))
                return cached;
        }
        var name = $"摇杆 {id + 1}";
        try
        {
            var caps = new JoyCaps();
            if (joyGetDevCapsW((IntPtr)id, ref caps, Marshal.SizeOf<JoyCaps>()) == JoyErrNoError)
            {
                name = string.IsNullOrWhiteSpace(caps.Pname) ? name : caps.Pname.Trim();
                var oem = OemName(caps.RegKey, id);
                if (!string.IsNullOrWhiteSpace(oem))
                    name = oem;
            }
        }
        catch (Exception e)
        {
            Log.Debug($"could not read the name of joystick {id}: {e.Message}");
        }
        lock (NameCache)
            NameCache[id] = name;
        return name;
    }

    private static string? OemName(string regKey, int id)
    {
        if (string.IsNullOrEmpty(regKey))
            return null;
        var current = $@"System\CurrentControlSet\Control\MediaResources\Joystick\{regKey}\CurrentJoystickSettings";
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var settings = root.OpenSubKey(current);
            if (settings?.GetValue($"Joystick{id + 1}OEMName") is not string oemKey)
                continue;
            var path = $@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\{oemKey}";
            foreach (var oemRoot in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var oem = oemRoot.OpenSubKey(path);
                if (oem?.GetValue("OEMName") is string name && name.Length > 0)
                    return name;
            }
        }
        return null;
    }
}
