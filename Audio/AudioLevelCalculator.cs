namespace WinFlow.Audio;

public static class AudioLevelCalculator
{
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
}
