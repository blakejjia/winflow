namespace WinFlow.Core.Services;

public interface IContextVisionService
{
    Task<string> SynthesizeContextAsync(string? screenshotBase64, string appName, string windowTitle, CancellationToken ct = default);
}
