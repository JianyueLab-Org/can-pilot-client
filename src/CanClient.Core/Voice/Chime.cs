namespace CanClient.Core.Voice;

/// <summary>
/// 收到管制消息时的提示音。飞行员盯着的是窗外，消息区多一行没人看得见。
///
/// 波形现场合成（A5 → E6 两声短促的上行音），不带 wav 资源；声音走客户端选的那块输出设备。
/// </summary>
public static class Chime
{
    private static readonly (double Frequency, double Seconds)[] Tones = [(880.0, 0.085), (1318.5, 0.110)];
    private const double Gap = 0.02;
    private const double Fade = 0.006;
    private const double Gain = 0.22;
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    public static short[] Waveform(int rate = AudioEngine.SampleRate, int volume = 100)
    {
        volume = Math.Clamp(volume, 0, 200);
        var scale = Gain * volume / 100.0;
        var samples = new List<short>();
        foreach (var (frequency, seconds) in Tones)
        {
            var count = Math.Max(1, (int)(rate * seconds));
            var fade = Math.Max(1, (int)(rate * Fade));
            for (var i = 0; i < count; i++)
            {
                var envelope = Math.Min(1.0, Math.Min((double)i / fade, (double)(count - i) / fade));
                var value = Math.Sin(2 * Math.PI * frequency * i / rate) * envelope * scale;
                samples.Add((short)Math.Round(Math.Clamp(value, -1.0, 1.0) * 32767));
            }
            samples.AddRange(new short[(int)(rate * Gap)]);
        }
        return samples.ToArray();
    }

    /// <summary>
    /// 一条 #TM 该不该响，照 xPilot 的规矩：
    ///   私聊（收件人是自己）一定响；广播（*、*S）响；
    ///   频率消息（@xxxxx）默认只有正文里点到自己呼号的才响，打开"所有消息"就都响；
    ///   自己发出去的不响。
    /// </summary>
    public static bool WantsAlert(string ownCallsign, string sender, string recipient, string body, bool everyMessage)
    {
        ownCallsign = (ownCallsign ?? "").Trim().ToUpperInvariant();
        sender = (sender ?? "").Trim().ToUpperInvariant();
        recipient = (recipient ?? "").Trim().ToUpperInvariant();
        if (sender.Length > 0 && sender == ownCallsign)
            return false;
        if (recipient.StartsWith('@'))
            return everyMessage || MentionsCallsign(body, ownCallsign);
        return true;
    }

    /// <summary>
    /// 正文里点到这个呼号没有。前后不能再接字母数字，否则呼号 CCA150 会被 "CCA1501, descend" 点到。
    /// </summary>
    public static bool MentionsCallsign(string? body, string callsign)
    {
        if (string.IsNullOrEmpty(callsign))
            return false;
        var text = (body ?? "").ToUpperInvariant();
        var start = 0;
        while (true)
        {
            var at = text.IndexOf(callsign, start, StringComparison.Ordinal);
            if (at < 0)
                return false;
            var before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            var end = at + callsign.Length;
            var after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (before && after)
                return true;
            start = at + 1;
        }
    }
}
