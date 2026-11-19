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
}
