using WinFlow.Core.Audio;
using WinFlow.Core.Context;
using WinFlow.Core.Keyboard;
using WinFlow.Core.Models;
using WinFlow.Core.Pipeline;
using WinFlow.Core.Services;
using WinFlow.Core.TextInsertion;
using Xunit;

namespace WinFlow.Tests.Pipeline;

public class AppStatePipelineTests
{
    private class MockAudioRecorder : IAudioRecorder
    {
        public bool IsRecording { get; private set; }
        public event Action<float>? AudioLevelUpdated;
        public byte[] ResultWav { get; set; } = new byte[100];

        public void StartRecording(string? deviceId = null)
        {
            IsRecording = true;
            AudioLevelUpdated?.Invoke(0.5f);
        }

        public Task<byte[]> StopRecordingAsync()
        {
            IsRecording = false;
            return Task.FromResult(ResultWav);
        }

        public IEnumerable<AudioDeviceInfo> GetAvailableDevices() => Array.Empty<AudioDeviceInfo>();
        public void Dispose() { }
    }

    private class MockKeyboardHook : IKeyboardHook
    {
        public event Action<ShortcutAction>? ActionTriggered;
        public void Trigger(ShortcutAction action) => ActionTriggered?.Invoke(action);
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private class MockContextService : IContextService
    {
        public AppContextSnapshot Snapshot { get; set; } = new()
        {
            ProcessName = "notepad",
            WindowTitle = "Untitled - Notepad",
            SelectedText = null
        };

        public Task<AppContextSnapshot> CaptureContextSnapshotAsync(bool captureScreenshot = true, int maxDimension = 1024)
            => Task.FromResult(Snapshot);
    }

    private class MockTranscriptionService : ITranscriptionService
    {
        public string Transcript { get; set; } = "hello world from mock";
        public Task<string> TranscribeAsync(byte[] wavBytes, string? language = null, CancellationToken ct = default)
            => Task.FromResult(Transcript);
    }

    private class MockContextVisionService : IContextVisionService
    {
        public string Summary { get; set; } = "User is typing notes in notepad.";
        public Task<string> SynthesizeContextAsync(string? screenshotBase64, string appName, string windowTitle, CancellationToken ct = default)
            => Task.FromResult(Summary);
    }

    private class MockPostProcessingService : IPostProcessingService
    {
        public string CleanedText { get; set; } = "Hello, world from mock.";
        public Task<string> CleanupTranscriptAsync(string rawTranscript, string contextSummary, string customVocabulary, string? selectedText = null, CancellationToken ct = default)
            => Task.FromResult(CleanedText);
    }

    private class MockTextInjector : ITextInjector
    {
        public string? InjectedText { get; private set; }
        public bool PressEnterPassed { get; private set; }

        public Task<bool> InjectTextAsync(string text, bool pressEnterAfter = false, int restoreDelayMs = 1000)
        {
            InjectedText = text;
            PressEnterPassed = pressEnterAfter;
            return Task.FromResult(true);
        }
    }

    private class MockSettingsService : ISettingsService
    {
        public AppSettings Settings { get; set; } = new();
        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);
        public Task SaveSettingsAsync(AppSettings settings)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task FullPipeline_HoldToTalk_RecordsTranscribesAndInjects()
    {
        var audio = new MockAudioRecorder();
        var hook = new MockKeyboardHook();
        var context = new MockContextService();
        var transcription = new MockTranscriptionService();
        var vision = new MockContextVisionService();
        var post = new MockPostProcessingService();
        var injector = new MockTextInjector();
        var settings = new MockSettingsService();

        var appState = new AppState(audio, hook, context, transcription, vision, post, injector, settings);
        await appState.InitializeAsync();

        Assert.Equal(PipelinePhase.Idle, appState.Phase);

        // 1. User holds CapsLock down
        hook.Trigger(ShortcutAction.HoldStarted);
        Assert.Equal(PipelinePhase.Recording, appState.Phase);
        Assert.True(audio.IsRecording);

        // 2. User releases CapsLock
        hook.Trigger(ShortcutAction.HoldReleased);

        // Wait for pipeline processing task to complete
        await appState.WaitForIdleAsync(5000);

        Assert.Equal(PipelinePhase.Idle, appState.Phase);
        Assert.Equal("Hello, world from mock.", injector.InjectedText);
        Assert.Single(appState.History);
        Assert.Equal("hello world from mock", appState.History[0].RawTranscript);
        Assert.Equal("Hello, world from mock.", appState.History[0].CleanedText);
    }

    [Fact]
    public async Task Pipeline_VoiceMacro_BypassesLlmCleanup()
    {
        var audio = new MockAudioRecorder();
        var hook = new MockKeyboardHook();
        var context = new MockContextService();
        var transcription = new MockTranscriptionService { Transcript = "my email" };
        var vision = new MockContextVisionService();
        var post = new MockPostProcessingService { CleanedText = "SHOULD NOT BE CALLED" };
        var injector = new MockTextInjector();
        var settings = new MockSettingsService
        {
            Settings = new AppSettings
            {
                VoiceMacros = new()
                {
                    new() { Command = "my email", Payload = "test@winflow.dev" }
                }
            }
        };

        var appState = new AppState(audio, hook, context, transcription, vision, post, injector, settings);
        await appState.InitializeAsync();

        hook.Trigger(ShortcutAction.HoldStarted);
        hook.Trigger(ShortcutAction.HoldReleased);

        await appState.WaitForIdleAsync(5000);

        Assert.Equal("test@winflow.dev", injector.InjectedText);
    }
}
