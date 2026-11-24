namespace WinFlow.Core.Models;

public class PipelineHistoryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string RawTranscript { get; set; } = string.Empty;
    public string CleanedText { get; set; } = string.Empty;
    public string? SelectedText { get; set; }
    public string? ContextSummary { get; set; }
    public double DurationSeconds { get; set; }
    public long LatencyMs { get; set; }
    public bool IsCommandMode { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Full path of the saved WAV for a failed run, kept for manual retry.</summary>
    public string? AudioFilePath { get; set; }

    /// <summary>True when the run failed; drives the error/retry UI in History.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>True when a failed run can be retried (saved audio or transcript exists).</summary>
    public bool CanRetry => HasError &&
        (!string.IsNullOrWhiteSpace(CleanedText) ||
         !string.IsNullOrWhiteSpace(RawTranscript) ||
         !string.IsNullOrEmpty(AudioFilePath));

    /// <summary>The text shown in History: the cleaned result, or the raw transcript for failed runs.</summary>
    public string DisplayText => !string.IsNullOrWhiteSpace(CleanedText) ? CleanedText : RawTranscript;
}
