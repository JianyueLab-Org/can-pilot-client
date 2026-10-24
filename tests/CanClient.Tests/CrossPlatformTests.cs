using CanClient.Core.Ptt;
using CanClient.Core.Util;
using CanClient.Core.Voice;
using Concentus;
using Concentus.Enums;

namespace CanClient.Tests;

/// <summary>macOS / Linux 版用到的那几块：键码换算、托管 Opus 和平台能力判断。</summary>
public class CrossPlatformTests
{
    [Fact]
    public void CommonPttKeysHaveEvdevAndMacCodes()
    {
        // 字母、数字、F1–F12、常用修饰键和标点：两个平台都要能录、能按
        var keys = Enumerable.Range('A', 26).Concat(Enumerable.Range('0', 10)).Concat(Enumerable.Range(0x70, 12))
            .Concat([0x20, 0x0D, 0x09, 0x1B, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xC0, 0xBD, 0xBB, 0xDB, 0xDD, 0xDC, 0xBA, 0xDE, 0xBC, 0xBE, 0xBF]);
        foreach (var vk in keys)
        {
            Assert.NotEmpty(KeyCodeMap.Evdev(vk));
            Assert.NotEmpty(KeyCodeMap.Mac(vk));
        }
    }

    [Fact]
    public void DifferentKeysNeverShareAPlatformCode()
    {
        var vks = Enumerable.Range(1, 254).ToArray();
        var evdev = vks.SelectMany(vk => KeyCodeMap.Evdev(vk).Select(code => (vk, code))).ToList();
        var mac = vks.SelectMany(vk => KeyCodeMap.Mac(vk).Select(code => (vk, code))).ToList();
        Assert.Equal(evdev.Count, evdev.Select(e => e.code).Distinct().Count());
        Assert.Equal(mac.Count, mac.Select(e => e.code).Distinct().Count());
    }

    [Fact]
    public void ManagedOpusInteroperatesWithTheClientCodec()
    {
        // Windows 上 OpusEncoder / OpusDecoder 是 opus.dll，其他平台是 Concentus：两边的码流必须互通
        var pcm = new short[AudioEngine.FrameSamples];
        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / AudioEngine.SampleRate));

        using var clientEncoder = new OpusEncoder { Bitrate = 40000 };
        var packet = clientEncoder.Encode(pcm);
        var managedDecoder = OpusCodecFactory.CreateDecoder(AudioEngine.SampleRate, 1, TextWriter.Null);
        var decoded = new short[OpusDecoder.MaxFrameSamples];
        Assert.Equal(AudioEngine.FrameSamples, managedDecoder.Decode(packet, decoded, decoded.Length, false));

        var managedEncoder = OpusCodecFactory.CreateEncoder(AudioEngine.SampleRate, 1, OpusApplication.OPUS_APPLICATION_AUDIO, TextWriter.Null);
        var buffer = new byte[1275];
        var length = managedEncoder.Encode(pcm, pcm.Length, buffer, buffer.Length);
        Assert.True(length > 0);
        using var clientDecoder = new OpusDecoder();
        Assert.Equal(AudioEngine.FrameSamples, clientDecoder.Decode(buffer.AsSpan(0, length), decoded));
    }

    [Fact]
    public void OnlyWindowsCanChooseMsfs()
    {
        foreach (var configured in new[] { "Auto", "MSFS", "XPlane" })
        {
            var effective = PlatformSupport.EffectiveSimulatorConnection(configured);
            Assert.Equal(OperatingSystem.IsWindows() ? configured : "XPlane", effective);
        }
        Assert.EndsWith(".xpl", PlatformSupport.XPlanePluginBinary);
    }
}
