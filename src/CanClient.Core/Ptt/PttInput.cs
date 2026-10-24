namespace CanClient.Core.Ptt;

/// <summary>
/// PTT 读输入的平台实现。绑定里存的一律是 Windows 虚拟键码（配置文件跨平台通用），
/// macOS / Linux 的实现自己把本平台的键码换算过来；鼠标侧键 X1 / X2 也走虚拟键码 0x05 / 0x06。
/// 所有方法只在 PTT 轮询线程上调用。
/// </summary>
internal interface IPttInput : IDisposable
{
    /// <summary>每轮轮询开头调一次：清掉上一轮的读数；<paramref name="rescan"/> 时重新找设备（热插拔）。</summary>
    void BeginPoll(bool rescan);

    /// <summary>某个虚拟键（含鼠标侧键）现在是不是按着。</summary>
    bool KeyDown(int vk);

    /// <summary>某个摇杆的按钮位图，第 n 位是按钮 n（从 0 数）。摇杆不在返回 0。</summary>
    int JoystickButtons(int id);

    /// <summary>接着的摇杆。</summary>
    IReadOnlyList<JoystickInfo> Joysticks();
}

internal static class PttInputs
{
    public static IPttInput Create()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsPttInput();
        if (OperatingSystem.IsMacOS())
            return new MacPttInput();
        if (OperatingSystem.IsLinux())
            return new LinuxPttInput();
        throw new PlatformNotSupportedException("PTT 只支持 Windows、macOS 和 Linux");
    }
}
