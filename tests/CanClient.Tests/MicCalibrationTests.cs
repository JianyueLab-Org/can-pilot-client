using System.Text.Json.Nodes;
using CanClient.Core.Settings;
using CanClient.Core.Voice;

namespace CanClient.Tests;

public class MicCalibrationTests
{
    private const int Rate = 48000;
    private const int Frame = 960;

    /// <summary>RMS 为 dbfs 的正弦帧。</summary>
    private static short[] Tone(double dbfs, int length = Frame)
    {
        var amplitude = MicGain.FullScale * Math.Pow(10, dbfs / 20) * Math.Sqrt(2);
        return Enumerable.Range(0, length)
            .Select(i => (short)Math.Clamp(Math.Round(amplitude * Math.Sin(2 * Math.PI * 440 * i / Rate)), -32768, 32767))
            .ToArray();
    }

    private static List<short[]> Frames(int count, double dbfs) => Enumerable.Range(0, count).Select(_ => Tone(dbfs)).ToList();

    [Fact]
    public void QuietSpeechGetsBoostedToTarget()
    {
        var m = MicCalibration.Measure(Frames(300, -35), Frames(50, -70), Rate);
        Assert.Equal(-35, m.SpeechDbfs, 0.2);
        Assert.Equal(-70, m.NoiseDbfs, 0.2);
        Assert.Equal(6.0, m.SpeechSeconds, 3);
        var result = MicCalibration.ComputeGain(m);
        Assert.Null(result.Failure);
        Assert.Equal(15.0, result.GainDb!.Value, 0.2);
    }

    [Fact]
    public void GainIsClamped()
    {
        var result = MicCalibration.ComputeGain(new Measurement(-60, -100, 5, 0));
        Assert.Equal(MicCalibration.MaxGainDb, result.GainDb);
        result = MicCalibration.ComputeGain(new Measurement(-3, -60, 5, 0));
        Assert.Equal(MicCalibration.MinGainDb, result.GainDb);
    }

    [Fact]
    public void FramesAtNoiseLevelAreNotSpeech()
    {
        var frames = Frames(100, -30).Concat(Frames(300, -68)).ToList();
        var m = MicCalibration.Measure(frames, Frames(50, -70), Rate);
        Assert.Equal(2.0, m.SpeechSeconds, 3);
    }

    [Theory]
    [InlineData(-35, -70, 1.0, 0.0, CalibrationFailure.TooShort)]
    [InlineData(-35, -45, 5.0, 0.0, CalibrationFailure.TooNoisy)]
    [InlineData(-10, -70, 5.0, 0.05, CalibrationFailure.Clipping)]
    public void RejectsBadRecordings(double speech, double noise, double seconds, double clip, CalibrationFailure expected)
    {
        var result = MicCalibration.ComputeGain(new Measurement(speech, noise, seconds, clip));
        Assert.Null(result.GainDb);
        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public void ClippingIsJudgedOnRawFrames()
    {
        var clipped = Frames(100, -20);
        foreach (var f in clipped)
            f[0] = short.MaxValue;
        var m = MicCalibration.Measure(Frames(100, -20), Frames(50, -70), Rate, clipped);
        Assert.Equal(1.0, m.ClipRatio);
    }

    [Fact]
    public void CalibratorCountsFramesThroughStages()
    {
        using var calibrator = new MicCalibrator(Rate, Frame, denoise: false);
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(CalibrationStage.Quiet, calibrator.Stage);
            calibrator.Feed(Tone(-70));
        }
        Assert.Equal(CalibrationStage.Read, calibrator.Stage);
        for (var i = 0; i < 400; i++)
            calibrator.Feed(Tone(i < 200 ? -30 : -70));
        Assert.Equal(CalibrationStage.Done, calibrator.Stage);
        Assert.Equal(1.0, calibrator.Progress);
        var m = calibrator.Measure();
        Assert.Equal(4.0, m.SpeechSeconds, 3);
        Assert.Equal(10.0, MicCalibration.ComputeGain(m).GainDb!.Value, 0.2);
    }

    [Fact]
    public void StoredCalibrationMatchesDenoiseState()
    {
        var settings = new AppSettings();
        var m = new Measurement(-30, -70, 5, 0);
        settings.MicCalibration["Mic"] = MicCalibration.Entry(m, 10, denoise: true, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        settings.MicCalibration["Old"] = new JsonObject { ["gain_db"] = 30.0 };
        Assert.Equal(10, settings.CalibratedGainFor("Mic", denoise: true));
        Assert.Null(settings.CalibratedGainFor("Mic", denoise: false));
        Assert.Equal(MicCalibration.MaxGainDb, settings.CalibratedGainFor("Old", denoise: false));
        Assert.Null(settings.CalibratedGainFor("", denoise: false));
        Assert.Equal("2026-01-02T03:04:05Z", (string?)settings.MicCalibration["Mic"]!["calibrated_at"]);
    }
}
