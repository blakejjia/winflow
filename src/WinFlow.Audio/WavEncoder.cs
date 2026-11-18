using System.Text;

namespace WinFlow.Audio;

public static class WavEncoder
{
    public static byte[] EncodePcm16(byte[] rawPcm, int sampleRate = 16000, short channels = 1)
    {
        var dataSize = rawPcm.Length;
        var header = new byte[44];

        // "RIFF"
        Encoding.ASCII.GetBytes("RIFF", 0, 4, header, 0);

        // ChunkSize: 36 + SubChunk2Size
        var chunkSize = 36 + dataSize;
        BitConverter.GetBytes(chunkSize).CopyTo(header, 4);

        // "WAVE"
        Encoding.ASCII.GetBytes("WAVE", 0, 4, header, 8);

        // "fmt "
        Encoding.ASCII.GetBytes("fmt ", 0, 4, header, 12);

        // Subchunk1Size: 16 for PCM
        BitConverter.GetBytes(16).CopyTo(header, 16);

        // AudioFormat: 1 for PCM
        BitConverter.GetBytes((short)1).CopyTo(header, 20);

        // NumChannels
        BitConverter.GetBytes(channels).CopyTo(header, 22);

        // SampleRate
        BitConverter.GetBytes(sampleRate).CopyTo(header, 24);

        // ByteRate = SampleRate * NumChannels * BitsPerSample/8
        short bitsPerSample = 16;
        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        BitConverter.GetBytes(byteRate).CopyTo(header, 28);

        // BlockAlign = NumChannels * BitsPerSample/8
        var blockAlign = (short)(channels * (bitsPerSample / 8));
        BitConverter.GetBytes(blockAlign).CopyTo(header, 32);

        // BitsPerSample
        BitConverter.GetBytes(bitsPerSample).CopyTo(header, 34);

        // "data"
        Encoding.ASCII.GetBytes("data", 0, 4, header, 36);

        // Subchunk2Size: dataSize
        BitConverter.GetBytes(dataSize).CopyTo(header, 40);

        var result = new byte[44 + dataSize];
        Buffer.BlockCopy(header, 0, result, 0, 44);
        Buffer.BlockCopy(rawPcm, 0, result, 44, dataSize);

        return result;
    }
}
