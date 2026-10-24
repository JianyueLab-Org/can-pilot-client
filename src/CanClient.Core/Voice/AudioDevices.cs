using System.Runtime.InteropServices;
using CanClient.Core.Util;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CanClient.Core.Voice;

public sealed record AudioDevice(int Index, string Name);

/// <summary>
/// 音频设备列表。Windows 收发用 WinMM（WaveIn/WaveOut）：格式转换交给系统，48 kHz 单声道 16 位直接可用；
/// WinMM 的设备名最多 31 个字符，显示时拿 WASAPI 的完整名字去补。macOS / Linux 用 OpenAL Soft 列出的设备名。
/// 设置里存的是名字（设备序号会随插拔变），打开时再按名字找回序号；找不到就用系统默认设备。
/// </summary>
public static class AudioDevices
{
    public const string SystemDefault = "系统默认设备";

    private static readonly Logger Log = Util.Log.For("audio");

    // NAudio 的 WaveOut 类只在 Windows 专用的目标框架里有，这里直接调 WinMM 列输出设备
    [DllImport("winmm.dll")]
    private static extern int waveOutGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern int waveOutGetDevCaps(nint deviceId, out WaveOutCapabilities caps, int size);

    public static IReadOnlyList<AudioDevice> Inputs()
    {
        if (!OperatingSystem.IsWindows())
            return OpenAlList(Al.CaptureDevices, "input");
        var full = FullNames(DataFlow.Capture);
        var list = new List<AudioDevice>();
        try
        {
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
                list.Add(new AudioDevice(i, Expand(WaveInEvent.GetCapabilities(i).ProductName, full)));
        }
        catch (Exception e)
        {
            Log.Warning($"could not list the input devices: {e.Message}");
        }
        return list;
    }

    public static IReadOnlyList<AudioDevice> Outputs()
    {
        if (!OperatingSystem.IsWindows())
            return OpenAlList(Al.PlaybackDevices, "output");
        var full = FullNames(DataFlow.Render);
        var list = new List<AudioDevice>();
        try
        {
            for (var i = 0; i < waveOutGetNumDevs(); i++)
            {
                if (waveOutGetDevCaps(i, out var caps, Marshal.SizeOf<WaveOutCapabilities>()) == 0)
                    list.Add(new AudioDevice(i, Expand(caps.ProductName, full)));
            }
        }
        catch (Exception e)
        {
            Log.Warning($"could not list the output devices: {e.Message}");
        }
        return list;
    }

    /// <summary>名字 → WinMM 设备号；空名字或找不到返回 -1（系统默认）。</summary>
    public static int ResolveInput(string? name) => Resolve(name, Inputs())?.Index ?? -1;

    public static int ResolveOutput(string? name) => Resolve(name, Outputs())?.Index ?? -1;

    /// <summary>名字 → 实际要打开的设备；空名字或找不到返回 null（系统默认）。</summary>
    internal static AudioDevice? ResolveInputDevice(string? name) => Resolve(name, Inputs());

    internal static AudioDevice? ResolveOutputDevice(string? name) => Resolve(name, Outputs());

    private static AudioDevice? Resolve(string? name, IReadOnlyList<AudioDevice> devices)
    {
        if (string.IsNullOrWhiteSpace(name) || name == SystemDefault)
            return null;
        var exact = devices.FirstOrDefault(d => d.Name == name);
        if (exact is not null)
            return exact;
        var prefix = devices.FirstOrDefault(d => d.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                                                 || name.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase));
        if (prefix is not null)
            return prefix;
        Log.Warning($"the audio device {name} is not connected, using the system default");
        return null;
    }

    private static IReadOnlyList<AudioDevice> OpenAlList(Func<IReadOnlyList<string>> names, string kind)
    {
        try
        {
            return names().Select((name, i) => new AudioDevice(i, name)).ToList();
        }
        catch (Exception e)
        {
            Log.Warning($"could not list the {kind} devices through OpenAL: {e.Message}");
            return [];
        }
    }

    private static List<string> FullNames(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).Select(d => d.FriendlyName).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string Expand(string shortName, List<string> fullNames) =>
        fullNames.FirstOrDefault(f => f.StartsWith(shortName, StringComparison.OrdinalIgnoreCase)) ?? shortName;
}
