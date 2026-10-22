using CanClient.Core.Util;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CanClient.Core.Voice;

public sealed record AudioDevice(int Index, string Name);

/// <summary>
/// 音频设备列表。收发用 WinMM（WaveIn/WaveOut）：格式转换交给系统，48 kHz 单声道 16 位直接可用。
/// WinMM 的设备名最多 31 个字符，显示时拿 WASAPI 的完整名字去补。
/// 设置里存的是名字（设备序号会随插拔变），打开时再按名字找回序号；找不到就用系统默认设备。
/// </summary>
public static class AudioDevices
{
    public const string SystemDefault = "系统默认设备";

    private static readonly Logger Log = Util.Log.For("audio");

    public static IReadOnlyList<AudioDevice> Inputs()
    {
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
        var full = FullNames(DataFlow.Render);
        var list = new List<AudioDevice>();
        try
        {
            for (var i = 0; i < WaveOut.DeviceCount; i++)
                list.Add(new AudioDevice(i, Expand(WaveOut.GetCapabilities(i).ProductName, full)));
        }
        catch (Exception e)
        {
            Log.Warning($"could not list the output devices: {e.Message}");
        }
        return list;
    }

    /// <summary>名字 → WinMM 设备号；空名字或找不到返回 -1（系统默认）。</summary>
    public static int ResolveInput(string? name) => Resolve(name, Inputs());

    public static int ResolveOutput(string? name) => Resolve(name, Outputs());

    private static int Resolve(string? name, IReadOnlyList<AudioDevice> devices)
    {
        if (string.IsNullOrWhiteSpace(name) || name == SystemDefault)
            return -1;
        var exact = devices.FirstOrDefault(d => d.Name == name);
        if (exact is not null)
            return exact.Index;
        var prefix = devices.FirstOrDefault(d => d.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                                                 || name.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase));
        if (prefix is not null)
            return prefix.Index;
        Log.Warning($"the audio device {name} is not connected, using the system default");
        return -1;
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
