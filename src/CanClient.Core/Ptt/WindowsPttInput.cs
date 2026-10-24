using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CanClient.Core.Util;
using Microsoft.Win32;

namespace CanClient.Core.Ptt;

/// <summary>
/// Windows：键盘和鼠标用 GetAsyncKeyState（全局，不需要窗口焦点，**不吞键**——PTT 键在模拟器里往往另有用途），
/// 摇杆用 winmm 的 joyGetPosEx。同一摇杆在一轮里只读一次；拔掉的摇杆等到下一次重扫才再试。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPttInput : IPttInput
{
    private static readonly Logger Log = Util.Log.For("ptt");

    private readonly int[] _joystickButtons = new int[16];
    private readonly bool[] _joystickRead = new bool[16];
    private readonly bool[] _joystickPresent = new bool[16];

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

    public void BeginPoll(bool rescan)
    {
        Array.Clear(_joystickRead);
        Array.Clear(_joystickButtons);
        // 拔掉的摇杆每次重扫再看一次（热插拔在飞行摇杆上很常见）
        if (rescan)
            Array.Fill(_joystickPresent, true);
    }

    public bool KeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public int JoystickButtons(int id)
    {
        if (id is < 0 or >= 16)
            return 0;
        if (_joystickRead[id]) return _joystickButtons[id];
        _joystickRead[id] = true;
        if (!_joystickPresent[id])
            return 0;
        var info = new JoyInfoEx { Size = Marshal.SizeOf<JoyInfoEx>(), Flags = JoyReturnButtons };
        var ok = joyGetPosEx(id, ref info) == JoyErrNoError;
        _joystickPresent[id] = ok;
        return _joystickButtons[id] = ok ? info.Buttons : 0;
    }

    public void Dispose()
    {
    }

    // ---------- 摇杆 ----------

    private static readonly Dictionary<int, string> NameCache = new();

    /// <summary>接着的摇杆。名字从注册表的 OEMName 取（winmm 给的通常是驱动名）。</summary>
    public IReadOnlyList<JoystickInfo> Joysticks()
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
