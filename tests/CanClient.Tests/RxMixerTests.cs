using CanClient.Core.Voice;

namespace CanClient.Tests;

public class RxMixerTests
{
    private const int Lead = RxMixer.LeadInSamples;

    private static short[] Pull(RxMixer mixer, int samples)
    {
        var bytes = new byte[samples * 2];
        Assert.Equal(bytes.Length, mixer.Read(bytes, 0, bytes.Length));
        var pcm = new short[samples];
        Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
        return pcm;
    }

    private static short[] Fill(int length, short value) => Enumerable.Repeat(value, length).ToArray();

    [Fact]
    public void SilenceWhenNobodyTalks()
    {
        var mixer = new RxMixer();
        Assert.All(Pull(mixer, 960), s => Assert.Equal(0, s));
    }

    [Fact]
    public void LongPacketKeepsItsTailForTheNextRead()
    {
        var mixer = new RxMixer();
        var packet = Enumerable.Range(1, 2880).Select(i => (short)i).ToArray();   // 60 ms
        mixer.Enqueue(1, packet);

        Pull(mixer, Lead);                          // 开口垫的静音
        var first = Pull(mixer, 960);
        var rest = Pull(mixer, 1920);
        Assert.Equal(packet[..960], first);
        Assert.Equal(packet[960..], rest);
        Assert.Equal(0, mixer.TalkerCount);
    }

    [Fact]
    public void ShortPacketsAreJoinedBackToBack()
    {
        var mixer = new RxMixer();
        mixer.Enqueue(1, Fill(480, 100));           // 10 ms
        mixer.Enqueue(1, Fill(480, 200));
        Pull(mixer, Lead);
        var frame = Pull(mixer, 960);
        Assert.All(frame[..480], s => Assert.Equal(100, s));
        Assert.All(frame[480..], s => Assert.Equal(200, s));
    }

    [Fact]
    public void ShorterTalkerDoesNotTruncateTheOther()
    {
        var mixer = new RxMixer();
        mixer.Enqueue(1, Fill(1920, 1000));         // 40 ms
        mixer.Enqueue(2, Fill(480, 10));            // 10 ms
        Pull(mixer, Lead);
        var frame = Pull(mixer, 1920);
        Assert.All(frame[..480], s => Assert.Equal(1010, s));
        Assert.All(frame[480..], s => Assert.Equal(1000, s));
    }

    [Fact]
    public void LoudTalkersAreClampedInsteadOfWrapping()
    {
        var mixer = new RxMixer();
        mixer.Enqueue(1, Fill(960, 30000));
        mixer.Enqueue(2, Fill(960, 30000));
        mixer.Enqueue(3, Fill(960, -30000));
        mixer.Enqueue(4, Fill(960, 30000));
        Pull(mixer, Lead);
        Assert.All(Pull(mixer, 960), s => Assert.Equal(short.MaxValue, s));

        mixer.Enqueue(1, Fill(960, -30000));
        mixer.Enqueue(2, Fill(960, -30000));
        Pull(mixer, Lead);
        Assert.All(Pull(mixer, 960), s => Assert.Equal(short.MinValue, s));
    }

    [Fact]
    public void GainIsAppliedAfterSummingThenClamped()
    {
        var mixer = new RxMixer { Gain = 2f };
        mixer.Enqueue(1, Fill(960, 10000));
        mixer.Enqueue(2, Fill(960, 8000));          // (10000 + 8000) × 2 = 36000 → 限幅
        Pull(mixer, Lead);
        Assert.All(Pull(mixer, 960), s => Assert.Equal(short.MaxValue, s));

        mixer.Gain = 0.5f;
        mixer.Enqueue(1, Fill(960, 1000));
        Pull(mixer, Lead);
        Assert.All(Pull(mixer, 960), s => Assert.Equal(500, s));
    }

    [Fact]
    public void BacklogDropsOnlyTheOldestExcess()
    {
        var mixer = new RxMixer();
        for (var i = 0; i < 20; i++)                // 400 ms，超过 240 ms 上限
            mixer.Enqueue(7, Fill(960, (short)(i + 1)));
        Assert.Equal(RxMixer.MaxQueuedSamples, mixer.Queued(7));

        var all = Pull(mixer, RxMixer.MaxQueuedSamples);
        // 留下的是最新的 12 帧（9..20），中间没有被清空的空洞
        for (var f = 0; f < 12; f++)
            Assert.All(all.AsSpan(f * 960, 960).ToArray(), s => Assert.Equal((short)(f + 9), s));
    }

    [Fact]
    public void OddReadSizesDoNotLoseSamples()
    {
        var mixer = new RxMixer();
        var packet = Enumerable.Range(1, 960).Select(i => (short)i).ToArray();
        mixer.Enqueue(1, packet);
        Pull(mixer, Lead);
        var got = Pull(mixer, 333).Concat(Pull(mixer, 441)).Concat(Pull(mixer, 186)).ToArray();
        Assert.Equal(packet, got);
    }

    [Fact]
    public void ClipsMixWithVoice()
    {
        var mixer = new RxMixer();
        mixer.AddClip(Fill(Lead + 960, 50));
        mixer.Enqueue(1, Fill(960, 100));
        Assert.All(Pull(mixer, Lead), s => Assert.Equal(50, s));
        Assert.All(Pull(mixer, 960), s => Assert.Equal(150, s));
        Assert.All(Pull(mixer, 960), s => Assert.Equal(0, s));
    }
}
