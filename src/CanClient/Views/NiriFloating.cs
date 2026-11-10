using Avalonia.Controls;
using Avalonia.Threading;

namespace CanClient.Views;

/// <summary>同步 can-client-ui：niri 初次映射为浮动窗口，激活后恢复缩放。</summary>
internal static class NiriFloating
{
    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NIRI_SOCKET")) || !window.CanResize)
            return;
        window.CanResize = false;
        var restored = false;
        void Restore()
        {
            if (restored) return;
            restored = true;
            window.Activated -= Activated;
            window.CanResize = true;
        }
        void Activated(object? sender, EventArgs args) => Restore();
        window.Activated += Activated;
        window.Opened += (_, _) => DispatcherTimer.RunOnce(Restore, TimeSpan.FromSeconds(3));
    }
}
