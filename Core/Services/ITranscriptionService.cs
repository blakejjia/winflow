namespace WinFlow.Core.Services;

public interface ITranscriptionService
{
    Task<string> TranscribeAsync(byte[] wavBytes, string? language = null, CancellationToken ct = default);
}
