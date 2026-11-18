using System.Text;
using WinFlow.Audio;
using Xunit;

namespace WinFlow.Tests.Audio;

public class WavEncoderTests
{
    [Fact]
    public void EncodePcm16_ProducesValidWavHeader()
    {
        // 1 second of 16kHz mono 16-bit audio = 16000 samples * 2 bytes = 32000 bytes
        var pcmData = new byte[32000];
        var wav = WavEncoder.EncodePcm16(pcmData, 16000, 1);

        Assert.NotNull(wav);
        Assert.Equal(32000 + 44, wav.Length);

        // Check RIFF header
        var riff = Encoding.ASCII.GetString(wav, 0, 4);
        Assert.Equal("RIFF", riff);

        var wave = Encoding.ASCII.GetString(wav, 8, 4);
        Assert.Equal("WAVE", wave);

        var fmt = Encoding.ASCII.GetString(wav, 12, 4);
        Assert.Equal("fmt ", fmt);

        var audioFormat = BitConverter.ToInt16(wav, 20);
        Assert.Equal(1, audioFormat); // PCM

        var numChannels = BitConverter.ToInt16(wav, 22);
        Assert.Equal(1, numChannels);

        var sampleRate = BitConverter.ToInt32(wav, 24);
        Assert.Equal(16000, sampleRate);

        var bitsPerSample = BitConverter.ToInt16(wav, 34);
        Assert.Equal(16, bitsPerSample);

        var dataChunk = Encoding.ASCII.GetString(wav, 36, 4);
        Assert.Equal("data", dataChunk);

        var dataSize = BitConverter.ToInt32(wav, 40);
        Assert.Equal(32000, dataSize);
    }
}
