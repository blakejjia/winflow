namespace WinFlow.Core.Models;

public class AppSettings
{
    public string GroqApiKey { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "https://api.groq.com/openai/v1";
    public string TranscriptionApiUrl { get; set; } = string.Empty;
    public string TranscriptionApiKey { get; set; } = string.Empty;

    public string TranscriptionModel { get; set; } = "whisper-large-v3";
    public string PostProcessingModel { get; set; } = "openai/gpt-oss-20b";
    public string PostProcessingFallbackModel { get; set; } = "qwen/qwen3.8-27b";
    public string ContextModel { get; set; } = "qwen/qwen3.8-27b";

    public string TriggerKey { get; set; } = "CapsLock";

    /// <summary>"System", "Dark", or "Light".</summary>
    public string Theme { get; set; } = "System";
    public bool DoubleTapLockEnabled { get; set; } = true;

    /// <summary>When true, CapsLock passes through untouched while a fullscreen game/app is active.</summary>
    public bool PauseInFullscreenGames { get; set; } = true;
    public bool PreserveClipboard { get; set; } = true;
    public bool PressEnterAfterDictation { get; set; } = false;
    public bool ContextCaptureEnabled { get; set; } = true;
    public int ContextScreenshotMaxDimension { get; set; } = 1024;

    public string CustomVocabulary { get; set; } = string.Empty;
    public string TranscriptionLanguage { get; set; } = string.Empty; // Empty = Auto-detect
    public string OutputLanguage { get; set; } = string.Empty;
    public string SelectedMicrophoneId { get; set; } = string.Empty;

    public List<VoiceMacro> VoiceMacros { get; set; } = new();
}
