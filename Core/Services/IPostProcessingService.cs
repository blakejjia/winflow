namespace WinFlow.Core.Services;

public interface IPostProcessingService
{
    Task<string> CleanupTranscriptAsync(
        string rawTranscript,
        string contextSummary,
        string customVocabulary,
        string? selectedText = null,
        CancellationToken ct = default);
}
