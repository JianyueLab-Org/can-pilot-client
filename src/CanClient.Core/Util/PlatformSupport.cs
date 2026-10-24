namespace CanClient.Core.Util;

/// <summary>
/// 各平台能用什么。Windows 支持 MSFS（SimConnect）和 X-Plane；macOS、Linux 没有 SimConnect，只接 X-Plane。
/// </summary>
public static class PlatformSupport
{
    /// <summary>MSFS 只能在 Windows 上通过 SimConnect 接入。</summary>
    public static bool SupportsMsfs => OperatingSystem.IsWindows();

    /// <summary>界面上的平台名，日志和提示里用。</summary>
    public static string Name =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    /// <summary>XPC_for_CAN 插件包里本平台的插件文件（X-Plane 约定的 64/win.xpl、mac.xpl、lin.xpl）。</summary>
    public static string XPlanePluginBinary =>
        OperatingSystem.IsWindows() ? "win.xpl" : OperatingSystem.IsMacOS() ? "mac.xpl" : "lin.xpl";

    /// <summary>
    /// 设置里的模拟器接入模式落到本平台上实际怎么接。macOS / Linux 不管配置写的是什么都只接 X-Plane，
    /// 这样从 Windows 拷过来的配置也能直接用。
    /// </summary>
    public static string EffectiveSimulatorConnection(string configured) =>
        SupportsMsfs ? configured : "XPlane";
}
