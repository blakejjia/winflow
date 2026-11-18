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
}
