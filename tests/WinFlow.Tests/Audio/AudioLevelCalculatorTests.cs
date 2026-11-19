using WinFlow.Audio;
using Xunit;

namespace WinFlow.Tests.Audio;

public class AudioLevelCalculatorTests
{
    [Fact]
    public void CalculateRmsLevel_SilentBuffer_ReturnsZero()
    {
        var buffer = new byte[3200]; // 1600 samples of 0
        var level = AudioLevelCalculator.CalculateRmsLevel(buffer, buffer.Length);
        Assert.Equal(0.0f, level);
    }

    [Fact]
    public void CalculateRmsLevel_FullScaleSineWave_ReturnsNearOne()
    {
        // 16-bit PCM: max amplitude is 32767
        var sampleCount = 1600;
        var buffer = new byte[sampleCount * 2];
        for (int i = 0; i < sampleCount; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * 440 * i / 16000.0) * 32767);
            var bytes = BitConverter.GetBytes(value);
            buffer[i * 2] = bytes[0];
            buffer[i * 2 + 1] = bytes[1];
        }

        var level = AudioLevelCalculator.CalculateRmsLevel(buffer, buffer.Length);
        Assert.InRange(level, 0.65f, 0.75f); // RMS of pure sine wave is ~0.707 of peak
    }

    [Fact]
    public void ShapeForDisplay_Silence_StaysZero()
    {
        Assert.Equal(0.0f, AudioLevelCalculator.ShapeForDisplay(0.0f));
        Assert.Equal(0.0f, AudioLevelCalculator.ShapeForDisplay(0.0004f)); // below noise floor
    }

    [Fact]
    public void ShapeForDisplay_TypicalSpeech_IsClearlyVisible()
    {
        // Normal speech RMS is roughly 0.02-0.15; on a linear meter that is nearly
        // invisible. The display curve must lift it well above the baseline.
        var quiet = AudioLevelCalculator.ShapeForDisplay(0.02f);
        var normal = AudioLevelCalculator.ShapeForDisplay(0.08f);
        var loud = AudioLevelCalculator.ShapeForDisplay(0.3f);

        Assert.InRange(quiet, 0.15f, 0.5f);
        Assert.InRange(normal, 0.35f, 0.75f);
        Assert.InRange(loud, 0.6f, 1.0f);
        Assert.True(quiet < normal && normal < loud); // monotonic
    }

    [Fact]
    public void ShapeForDisplay_FullScale_ClampsToOne()
    {
        Assert.Equal(1.0f, AudioLevelCalculator.ShapeForDisplay(1.0f));
    }
}
