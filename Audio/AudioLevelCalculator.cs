namespace WinFlow.Audio;

public static class AudioLevelCalculator
{
    /// <summary>
    /// Raw RMS of 16-bit PCM, normalized to [0, 1]. A full-scale sine measures ~0.707.
    /// </summary>
    public static float CalculateRmsLevel(byte[] buffer, int bytesRecorded)
    {
        if (buffer == null || bytesRecorded < 2)
            return 0.0f;

        var sampleCount = bytesRecorded / 2;
        double sumSquares = 0.0;

        for (int i = 0; i < bytesRecorded - 1; i += 2)
        {
            short sample = BitConverter.ToInt16(buffer, i);
            double normalized = sample / 32768.0;
            sumSquares += normalized * normalized;
        }

        double rms = Math.Sqrt(sumSquares / sampleCount);
        return (float)Math.Clamp(rms, 0.0, 1.0);
    }

    /// <summary>
    /// Perceptual level for the HUD waveform. Typical speech sits at RMS 0.02–0.15,
    /// which is nearly invisible on a linear meter; map it through a dB-style curve
    /// so quiet speech still moves the bars and loud speech approaches full height.
    /// </summary>
    public static float CalculateDisplayLevel(byte[] buffer, int bytesRecorded)
    {
        var rms = CalculateRmsLevel(buffer, bytesRecorded);
        return ShapeForDisplay(rms);
    }

    /// <summary>Maps raw RMS [0,1] to a perceptual display level [0,1].</summary>
    public static float ShapeForDisplay(float rms)
    {
        if (rms <= 0.0005f) return 0.0f; // noise floor: keep silence truly still

        // ~ -66 dB .. 0 dB mapped to 0 .. 1, then a gentle gamma to favor speech.
        const double minDb = -66.0;
        double db = 20.0 * Math.Log10(rms);
        double linear = (db - minDb) / -minDb;
        double shaped = Math.Pow(Math.Clamp(linear, 0.0, 1.0), 1.4);
        return (float)shaped;
    }
}
