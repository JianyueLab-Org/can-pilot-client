using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CanClient.Core.Util;

namespace CanClient.Core.Ptt;

/// <summary>
/// Linux：直接读 evdev（/dev/input/event*）。X11 和 Wayland 下都是全局的、不需要窗口焦点、不吞键。
/// 每轮对每个设备做一次 EVIOCGKEY，拿到整块按键位图，之后的查询都查这份快照。
///
/// 键盘和鼠标的设备节点通常只有 input 组能读（摇杆一般由 logind 给当前用户放行），
/// 读不到时在日志里提示把用户加进 input 组。
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class LinuxPttInput : IPttInput
{
    private static readonly Logger Log = Util.Log.For("ptt");

    private const int EvKey = 0x01;
    private const int KeyMax = 0x2ff;
    private const int KeyBytes = KeyMax / 8 + 1;
    private const int ORdOnly = 0, ONonBlock = 0x800, OCloExec = 0x80000;
    private const int EAcces = 13;

    // 鼠标侧键：不同鼠标报 SIDE/EXTRA 或 BACK/FORWARD
    private const int BtnSide = 0x113, BtnExtra = 0x114, BtnForward = 0x115, BtnBack = 0x116;
    private const int BtnMisc = 0x100, BtnJoystick = 0x120, BtnGamepadEnd = 0x13f;
    private const int BtnTriggerHappy = 0x2c0, BtnTriggerHappyEnd = 0x2e7;

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc")]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, byte* argument);

    private static nuint IocRead(int number, int size) =>
        (nuint)((2u << 30) | ((uint)size << 16) | ((uint)'E' << 8) | (uint)number);

    private static nuint EviocGName(int size) => IocRead(0x06, size);
    private static nuint EviocGKey(int size) => IocRead(0x18, size);
    private static nuint EviocGBit(int type, int size) => IocRead(0x20 + type, size);

    private sealed class Device(string path, int fd, string name, byte[] caps, int[] joystickButtons)
    {
        public string Path { get; } = path;
        public int Fd { get; } = fd;
        public string Name { get; } = name;
        public byte[] Caps { get; } = caps;
        public byte[] State { get; } = new byte[KeyBytes];
        public bool Read { get; set; }
        /// <summary>摇杆的按钮键码，按 joydev 的顺序排好；不是摇杆为空。</summary>
        public int[] JoystickButtons { get; } = joystickButtons;
        public bool IsJoystick => JoystickButtons.Length > 0;
    }

    private readonly List<Device> _devices = new();
    private readonly List<Device> _joysticks = new();
    private bool _scanned;
    private bool _warnedPermission;

    public void BeginPoll(bool rescan)
    {
        if (rescan || !_scanned)
            Scan();
        foreach (var device in _devices)
            device.Read = false;
    }

    private bool ReadState(Device device)
    {
        if (device.Read)
            return true;
        device.Read = true;
        fixed (byte* state = device.State)
        {
            if (ioctl(device.Fd, EviocGKey(KeyBytes), state) >= 0)
                return true;
        }
        // 拔掉了（ENODEV）：清零，下次重扫时关掉
        Array.Clear(device.State);
        return false;
    }

    private static bool Bit(byte[] bits, int code) => code is >= 0 and <= KeyMax && (bits[code >> 3] & (1 << (code & 7))) != 0;

    public bool KeyDown(int vk)
    {
        ReadOnlySpan<int> codes = vk switch
        {
            0x05 => [BtnSide, BtnBack],
            0x06 => [BtnExtra, BtnForward],
            _ => KeyCodeMap.Evdev(vk)
        };
        if (codes.IsEmpty)
            return false;
        foreach (var device in _devices)
        {
            if (device.IsJoystick || !ReadState(device))
                continue;
            foreach (var code in codes)
            {
                if (Bit(device.State, code))
                    return true;
            }
        }
        return false;
    }

    public int JoystickButtons(int id)
    {
        if (id < 0 || id >= _joysticks.Count)
            return 0;
        var device = _joysticks[id];
        if (!ReadState(device))
            return 0;
        var buttons = 0;
        for (var b = 0; b < device.JoystickButtons.Length && b < 32; b++)
        {
            if (Bit(device.State, device.JoystickButtons[b]))
                buttons |= 1 << b;
        }
        return buttons;
    }

    public IReadOnlyList<JoystickInfo> Joysticks()
    {
        if (!_scanned)
            Scan();
        return _joysticks.Select((d, i) => new JoystickInfo(i, d.Name)).ToList();
    }

    private void Scan()
    {
        _scanned = true;
        string[] paths;
        try
        {
            paths = Directory.GetFiles("/dev/input", "event*").OrderBy(EventNumber).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"could not list /dev/input: {e.Message}");
            return;
        }

        // 已经拔掉的、或者节点没了的关掉
        for (var i = _devices.Count - 1; i >= 0; i--)
        {
            var device = _devices[i];
            fixed (byte* state = device.State)
            {
                if (paths.Contains(device.Path) && ioctl(device.Fd, EviocGKey(KeyBytes), state) >= 0)
                    continue;
            }
            close(device.Fd);
            _devices.RemoveAt(i);
        }

        var denied = 0;
        foreach (var path in paths)
        {
            if (_devices.Any(d => d.Path == path))
                continue;
            var fd = open(path, ORdOnly | ONonBlock | OCloExec);
            if (fd < 0)
            {
                if (Marshal.GetLastPInvokeError() == EAcces)
                    denied++;
                continue;
            }
            var caps = new byte[KeyBytes];
            fixed (byte* p = caps)
            {
                if (ioctl(fd, EviocGBit(EvKey, KeyBytes), p) < 0 || caps.All(b => b == 0))
                {
                    // 没有按键的设备（加速度计、触摸板的位置流之类）用不上
                    close(fd);
                    continue;
                }
            }
            _devices.Add(new Device(path, fd, DeviceName(fd), caps, JoystickButtonCodes(caps)));
        }

        _joysticks.Clear();
        // 摇杆编号按 eventN 的顺序，和 Windows 版一样最多 16 个
        _joysticks.AddRange(_devices.Where(d => d.IsJoystick).OrderBy(d => EventNumber(d.Path)).Take(16));

        if (denied > 0 && !_devices.Any(d => !d.IsJoystick) && !_warnedPermission)
        {
            _warnedPermission = true;
            Log.Warning($"no permission to read {denied} input devices under /dev/input; keyboard and mouse PTT need the user " +
                        "in the 'input' group (sudo usermod -aG input $USER, then log out and back in)");
        }
    }

    private static int EventNumber(string path) =>
        int.TryParse(Path.GetFileName(path).AsSpan(5), out var n) ? n : int.MaxValue;

    private static string DeviceName(int fd)
    {
        var buffer = stackalloc byte[256];
        var length = ioctl(fd, EviocGName(256), buffer);
        if (length <= 0)
            return "";
        var span = new ReadOnlySpan<byte>(buffer, Math.Min(length, 256));
        var end = span.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(end >= 0 ? span[..end] : span).Trim();
    }

    /// <summary>
    /// 摇杆 / 手柄的按钮：按内核 joydev 的顺序编号——先 BTN_JOYSTICK 往后（含 BTN_TRIGGER_HAPPY），
    /// 再补 BTN_MISC 段，这样按钮号和 jstest 里看到的一致。不带摇杆 / 手柄按钮的设备返回空。
    /// </summary>
    private static int[] JoystickButtonCodes(byte[] caps)
    {
        var isJoystick = false;
        for (var code = BtnJoystick; code <= BtnGamepadEnd && !isJoystick; code++)
            isJoystick = Bit(caps, code);
        for (var code = BtnTriggerHappy; code <= BtnTriggerHappyEnd && !isJoystick; code++)
            isJoystick = Bit(caps, code);
        if (!isJoystick)
            return [];
        var buttons = new List<int>();
        for (var code = BtnJoystick; code <= KeyMax; code++)
        {
            if (Bit(caps, code))
                buttons.Add(code);
        }
        for (var code = BtnMisc; code < BtnJoystick; code++)
        {
            if (Bit(caps, code))
                buttons.Add(code);
        }
        return buttons.ToArray();
    }

    public void Dispose()
    {
        foreach (var device in _devices)
            close(device.Fd);
        _devices.Clear();
        _joysticks.Clear();
    }
}
