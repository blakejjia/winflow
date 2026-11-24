namespace WinFlow.Core.Services;

/// <summary>
/// Retries a transcription request up to three attempts with short backoffs
/// (0.5s after the first failure, 2s after the second). Cancellation is never
/// retried — Escape or a superseding run must stop immediately. After the third
/// failure the exception propagates so the caller can keep the audio for a
/// manual retry instead of losing the recording.
/// </summary>
public static class TranscriptionRetry
{
    public const int MaxAttempts = 3;

    /// <summary>Delays between attempts; internal setter exists for tests.</summary>
    internal static TimeSpan[] RetryDelays { get; set; } =
    {
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(2)
    };

    public static async Task<string> RunAsync(
        Func<CancellationToken, Task<string>> attempt,
        CancellationToken ct = default)
    {
        for (int i = 0; ; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await attempt(ct);
            }
            catch (Exception) when (i < RetryDelays.Length)
            {
                await Task.Delay(RetryDelays[i], ct);
            }
        }
    }
}
