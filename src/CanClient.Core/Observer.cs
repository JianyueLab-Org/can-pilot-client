using System.Globalization;

namespace CanClient.Core;

/// <summary>
/// 观察员模式：双人机组里坐在右座的那位。只连语音、进和机长同一个 FREQ_* 频道，网络上不会多出
/// 一架飞机。所以收不到发给机长呼号的文字消息——这是这个模式的代价，不是 bug。
///
/// 手输频率只在观察员模式下存在：正常连着 FSD 的飞行员要是能把语音频率和座舱 COM1 分开设，
/// 迟早会出现"管制以为你在 121.8、你人在别的频道"。
/// </summary>
public static class Observer
{
    public const double MinFrequency = 118.0;
    public const double MaxFrequency = 136.975;

    /// <summary>认 121.8、121.800 和六位千赫 121800（Mumble 频道名里的写法）。先量化到千赫再判范围。</summary>
    public static double? ParseFrequency(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var cleaned = text.Trim().Replace(" ", "");
        if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        if (!cleaned.Contains('.') && Math.Abs(value) >= 1000)
            value /= 1000.0;
        value = Math.Round(value, 3, MidpointRounding.ToEven);
        return value is >= MinFrequency and <= MaxFrequency ? value : null;
    }

    /// <summary>语音这一刻该待在哪个频率上。观察员手输的压过模拟器；非观察员一律看模拟器。</summary>
    public static double? FrequencyFor(double? com1, bool com1Power, string? manual, bool observer)
    {
        if (observer && ParseFrequency(manual) is { } chosen)
            return chosen;
        if (com1 is { } value && value != 0 && com1Power)
            return Math.Round(value, 3);
        return null;
    }

    public static string FormatFrequency(double? frequency) =>
        frequency is { } value ? value.ToString("F3", CultureInfo.InvariantCulture) : "";
}
