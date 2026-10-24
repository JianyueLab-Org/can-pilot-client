using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using CanClient.Core.Util;

namespace CanClient.Core.Ptt;

/// <summary>
/// macOS：键盘和鼠标侧键用 CoreGraphics 的 CGEventSourceKeyState / CGEventSourceButtonState
/// （全局、不吞键，和 Windows 的 GetAsyncKeyState 一个意思）；摇杆用 IOKit HID 直接读按钮元素的当前值。
///
/// 系统要求读键盘的程序有"输入监控"权限：第一次用到时请求一次（系统设置 → 隐私与安全性 → 输入监控），
/// 授权后重启客户端生效。摇杆和鼠标侧键不受影响。
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacPttInput : IPttInput
{
    private static readonly Logger Log = Util.Log.For("ptt");

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const int HidSystemState = 1;

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CGEventSourceKeyState(int stateId, ushort key);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CGEventSourceButtonState(int stateId, int button);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CGPreflightListenEventAccess();

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CGRequestListenEventAccess();

    private readonly MacHidJoysticks _joysticks = new();
    private bool _accessChecked;

    public void BeginPoll(bool rescan)
    {
        if (!_accessChecked)
        {
            _accessChecked = true;
            CheckAccess();
        }
        _joysticks.BeginPoll(rescan);
    }

    private static void CheckAccess()
    {
        try
        {
            if (CGPreflightListenEventAccess())
                return;
            Log.Warning("keyboard PTT needs the Input Monitoring permission (System Settings → Privacy & Security → " +
                        "Input Monitoring); requesting it now, restart the client after granting it");
            CGRequestListenEventAccess();
        }
        catch (EntryPointNotFoundException)
        {
            // macOS 10.15 之前没有这道权限
        }
    }

    public bool KeyDown(int vk)
    {
        // CGMouseButton：0 左、1 右、2 中，3 / 4 是侧键（后退 / 前进）
        if (vk == 0x05)
            return CGEventSourceButtonState(HidSystemState, 3);
        if (vk == 0x06)
            return CGEventSourceButtonState(HidSystemState, 4);
        foreach (var code in KeyCodeMap.Mac(vk))
        {
            if (CGEventSourceKeyState(HidSystemState, (ushort)code))
                return true;
        }
        return false;
    }

    public int JoystickButtons(int id) => _joysticks.Buttons(id);

    public IReadOnlyList<JoystickInfo> Joysticks() => _joysticks.List();

    public void Dispose() => _joysticks.Dispose();
}

/// <summary>
/// IOKit HID 摇杆：IOHIDManager 按"摇杆 / 手柄 / 多轴控制器"匹配设备，按钮元素（Usage Page 9）按 Usage 排序编号。
/// 不挂 RunLoop，直接用 IOHIDDeviceGetValue 读元素当前值；热插拔靠定期重建 manager。
/// 摇杆按 LocationID 排序编号，同一个 USB 口的设备编号不变。
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacHidJoysticks : IDisposable
{
    private static readonly Logger Log = Util.Log.For("ptt");

    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string IoKitLib = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const uint Utf8 = 0x08000100;
    private const int SInt32Type = 3;
    private const uint ButtonPage = 0x09;
    private const int GenericDesktopPage = 0x01, Joystick = 0x04, GamePad = 0x05, MultiAxisController = 0x08;

    [DllImport(CoreFoundationLib)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFNumberCreate(IntPtr allocator, int type, int* value);
    [DllImport(CoreFoundationLib)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CFNumberGetValue(IntPtr number, int type, int* value);
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);
    [DllImport(CoreFoundationLib)] private static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFArrayCreateMutable(IntPtr allocator, nint capacity, IntPtr callbacks);
    [DllImport(CoreFoundationLib)] private static extern void CFArrayAppendValue(IntPtr array, IntPtr value);
    [DllImport(CoreFoundationLib)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CoreFoundationLib)] private static extern nint CFSetGetCount(IntPtr set);
    [DllImport(CoreFoundationLib)] private static extern void CFSetGetValues(IntPtr set, IntPtr* values);
    [DllImport(CoreFoundationLib)] private static extern nuint CFGetTypeID(IntPtr value);
    [DllImport(CoreFoundationLib)] private static extern nuint CFStringGetTypeID();
    [DllImport(CoreFoundationLib)] private static extern nuint CFNumberGetTypeID();
    [DllImport(CoreFoundationLib)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CFStringGetCString(IntPtr text, byte* buffer, nint size, uint encoding);
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFRetain(IntPtr value);
    [DllImport(CoreFoundationLib)] private static extern void CFRelease(IntPtr value);

    [DllImport(IoKitLib)] private static extern IntPtr IOHIDManagerCreate(IntPtr allocator, uint options);
    [DllImport(IoKitLib)] private static extern void IOHIDManagerSetDeviceMatchingMultiple(IntPtr manager, IntPtr matching);
    [DllImport(IoKitLib)] private static extern int IOHIDManagerOpen(IntPtr manager, uint options);
    [DllImport(IoKitLib)] private static extern int IOHIDManagerClose(IntPtr manager, uint options);
    [DllImport(IoKitLib)] private static extern IntPtr IOHIDManagerCopyDevices(IntPtr manager);
    [DllImport(IoKitLib)] private static extern IntPtr IOHIDDeviceGetProperty(IntPtr device, IntPtr key);
    [DllImport(IoKitLib)] private static extern IntPtr IOHIDDeviceCopyMatchingElements(IntPtr device, IntPtr matching, uint options);
    [DllImport(IoKitLib)] private static extern int IOHIDDeviceGetValue(IntPtr device, IntPtr element, IntPtr* value);
    [DllImport(IoKitLib)] private static extern uint IOHIDElementGetUsagePage(IntPtr element);
    [DllImport(IoKitLib)] private static extern uint IOHIDElementGetUsage(IntPtr element);
    [DllImport(IoKitLib)] private static extern nint IOHIDValueGetIntegerValue(IntPtr value);

    private sealed class Stick(IntPtr device, IntPtr elements, IntPtr[] buttons, string name, int location)
    {
        public IntPtr Device { get; } = device;
        public IntPtr Elements { get; } = elements;
        public IntPtr[] Buttons { get; } = buttons;
        public string Name { get; } = name;
        public int Location { get; } = location;
        public int Value { get; set; }
        public bool Read { get; set; }
        public bool Gone { get; set; }
    }

    private IntPtr _manager;
    private List<Stick> _sticks = new();
    private bool _scanned;
    private bool _failed;
    private bool _missing;

    public void BeginPoll(bool rescan)
    {
        // 重建 manager 不便宜：只在有摇杆掉了、绑定的摇杆还没插上、或者还没扫过的时候重扫
        if (!_scanned || rescan && (_missing || _sticks.Any(s => s.Gone)))
        {
            _missing = false;
            Refresh();
        }
        foreach (var stick in _sticks)
            stick.Read = false;
    }

    public int Buttons(int id)
    {
        if (id < 0 || id >= _sticks.Count)
        {
            _missing = id is >= 0 and < 16;
            return 0;
        }
        var stick = _sticks[id];
        if (stick.Read || stick.Gone)
            return stick.Gone ? 0 : stick.Value;
        stick.Read = true;
        var value = 0;
        for (var b = 0; b < stick.Buttons.Length; b++)
        {
            IntPtr hidValue;
            if (IOHIDDeviceGetValue(stick.Device, stick.Buttons[b], &hidValue) != 0)
            {
                // kIOReturnNotAttached 之类：设备拔掉了
                stick.Gone = true;
                return stick.Value = 0;
            }
            if (hidValue != IntPtr.Zero && IOHIDValueGetIntegerValue(hidValue) != 0)
                value |= 1 << b;
        }
        return stick.Value = value;
    }

    public IReadOnlyList<JoystickInfo> List()
    {
        // 录制时每 3 秒调一次，顺便把新插上的摇杆找出来
        Refresh();
        return _sticks.Select((s, i) => new JoystickInfo(i, s.Name)).ToList();
    }

    private void Refresh()
    {
        _scanned = true;
        Release();
        if (_failed)
            return;
        try
        {
            _manager = IOHIDManagerCreate(IntPtr.Zero, 0);
            if (_manager == IntPtr.Zero)
                return;
            var matching = MatchingArray();
            IOHIDManagerSetDeviceMatchingMultiple(_manager, matching);
            CFRelease(matching);
            var result = IOHIDManagerOpen(_manager, 0);
            if (result != 0)
                Log.Debug($"IOHIDManagerOpen returned 0x{result:x8}");
            var set = IOHIDManagerCopyDevices(_manager);
            if (set == IntPtr.Zero)
                return;
            var count = (int)CFSetGetCount(set);
            var devices = new IntPtr[count];
            fixed (IntPtr* p = devices)
                CFSetGetValues(set, p);
            var sticks = new List<Stick>();
            foreach (var device in devices)
            {
                var elements = IOHIDDeviceCopyMatchingElements(device, IntPtr.Zero, 0);
                if (elements == IntPtr.Zero)
                    continue;
                var buttons = new List<(uint Usage, IntPtr Element)>();
                var elementCount = CFArrayGetCount(elements);
                for (nint i = 0; i < elementCount; i++)
                {
                    var element = CFArrayGetValueAtIndex(elements, i);
                    if (IOHIDElementGetUsagePage(element) == ButtonPage)
                        buttons.Add((IOHIDElementGetUsage(element), element));
                }
                if (buttons.Count == 0)
                {
                    CFRelease(elements);
                    continue;
                }
                var ordered = buttons.OrderBy(b => b.Usage).DistinctBy(b => b.Usage).Take(32).Select(b => b.Element).ToArray();
                CFRetain(device);
                sticks.Add(new Stick(device, elements, ordered, StringProperty(device, "Product"), IntProperty(device, "LocationID")));
            }
            CFRelease(set);
            _sticks = sticks.OrderBy(s => s.Location).ThenBy(s => s.Name, StringComparer.Ordinal).Take(16).ToList();
            foreach (var extra in sticks.Except(_sticks))
                ReleaseStick(extra);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _failed = true;
            Log.Warning($"IOKit HID is unavailable, joystick PTT is disabled: {e.Message}");
        }
    }

    private static IntPtr MatchingArray()
    {
        var cf = NativeLibrary.Load(CoreFoundationLib);
        var keyCallbacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks");
        var valueCallbacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks");
        var arrayCallbacks = NativeLibrary.GetExport(cf, "kCFTypeArrayCallBacks");
        var pageKey = CfString("DeviceUsagePage");
        var usageKey = CfString("DeviceUsage");
        var array = CFArrayCreateMutable(IntPtr.Zero, 0, arrayCallbacks);
        foreach (var usage in new[] { Joystick, GamePad, MultiAxisController })
        {
            var dictionary = CFDictionaryCreateMutable(IntPtr.Zero, 0, keyCallbacks, valueCallbacks);
            var page = GenericDesktopPage;
            var usageValue = usage;
            var pageNumber = CFNumberCreate(IntPtr.Zero, SInt32Type, &page);
            var usageNumber = CFNumberCreate(IntPtr.Zero, SInt32Type, &usageValue);
            CFDictionarySetValue(dictionary, pageKey, pageNumber);
            CFDictionarySetValue(dictionary, usageKey, usageNumber);
            CFRelease(pageNumber);
            CFRelease(usageNumber);
            CFArrayAppendValue(array, dictionary);
            CFRelease(dictionary);
        }
        CFRelease(pageKey);
        CFRelease(usageKey);
        return array;
    }

    private static IntPtr CfString(string text) => CFStringCreateWithCString(IntPtr.Zero, text, Utf8);

    private static string StringProperty(IntPtr device, string key)
    {
        var cfKey = CfString(key);
        try
        {
            var value = IOHIDDeviceGetProperty(device, cfKey);
            if (value == IntPtr.Zero || CFGetTypeID(value) != CFStringGetTypeID())
                return "";
            var buffer = stackalloc byte[256];
            return CFStringGetCString(value, buffer, 256, Utf8)
                ? Encoding.UTF8.GetString(buffer, new ReadOnlySpan<byte>(buffer, 256).IndexOf((byte)0)).Trim()
                : "";
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    private static int IntProperty(IntPtr device, string key)
    {
        var cfKey = CfString(key);
        try
        {
            var value = IOHIDDeviceGetProperty(device, cfKey);
            var result = 0;
            if (value != IntPtr.Zero && CFGetTypeID(value) == CFNumberGetTypeID())
                CFNumberGetValue(value, SInt32Type, &result);
            return result;
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    private static void ReleaseStick(Stick stick)
    {
        CFRelease(stick.Elements);
        CFRelease(stick.Device);
    }

    private void Release()
    {
        foreach (var stick in _sticks)
            ReleaseStick(stick);
        _sticks = new List<Stick>();
        if (_manager != IntPtr.Zero)
        {
            IOHIDManagerClose(_manager, 0);
            CFRelease(_manager);
            _manager = IntPtr.Zero;
        }
    }

    public void Dispose() => Release();
}
