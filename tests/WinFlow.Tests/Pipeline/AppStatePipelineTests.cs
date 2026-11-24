using System.IO;
using System.Net.Http;
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
        public int FailuresBeforeSuccess { get; set; } // int.MaxValue = always fail
        public int CallCount;
        public Task<string> TranscribeAsync(byte[] wavBytes, string? language = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CallCount);
            if (CallCount <= FailuresBeforeSuccess)
                throw new HttpRequestException("Transcription failed with status InternalServerError");
            return Task.FromResult(Transcript);
        }
    }

    private class MockPostProcessingService : IPostProcessingService
    {
        public string CleanedText { get; set; } = "Hello, world from mock.";
        public int FailuresBeforeSuccess { get; set; }
        public int CallCount;
        public Task<string> CleanupTranscriptAsync(string rawTranscript, string contextSummary, string customVocabulary, string? selectedText = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CallCount);
            if (CallCount <= FailuresBeforeSuccess)
                throw new HttpRequestException("Post-processing failed with status InternalServerError");
            return Task.FromResult(CleanedText);
        }
    }

    private class MockContextVisionService : IContextVisionService
    {
        public string Summary { get; set; } = "User is typing notes in notepad.";
        public Task<string> SynthesizeContextAsync(string? screenshotBase64, string appName, string windowTitle, CancellationToken ct = default)
            => Task.FromResult(Summary);
    }

    private class MockTextInjector : ITextInjector
    {
        public string? InjectedText { get; private set; }
        public bool PressEnterPassed { get; private set; }
        public bool Succeeds { get; set; } = true;

        public Task<bool> InjectTextAsync(string text, bool pressEnterAfter = false, int restoreDelayMs = 1000)
        {
            InjectedText = text;
            PressEnterPassed = pressEnterAfter;
            return Task.FromResult(Succeeds);
        }
    }

    private class MockSettingsService : ISettingsService
    {
        public AppSettings Settings { get; set; } = new() { GroqApiKey = "gsk_synthetic_test_key" };
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

        using var temp = new TempPendingDir();
        var appState = new AppState(audio, hook, context, transcription, vision, post, injector, settings,
            pendingAudioStore: temp.Store);
        appState.MinimumDictationDuration = TimeSpan.Zero; // instant release is intentional here
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

    /// <summary>Isolated %TEMP% subdir per test so parallel tests never sweep each other's files.</summary>
    private sealed class TempPendingDir : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "WinFlowTests", Guid.NewGuid().ToString("N"));
        public PendingAudioStore Store { get; }
        public TempPendingDir() => Store = new PendingAudioStore(Dir);
        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static AppState CreatePipeline(
        MockAudioRecorder? audio = null,
        MockKeyboardHook? hook = null,
        MockTranscriptionService? transcription = null,
        MockPostProcessingService? post = null,
        MockTextInjector? injector = null,
        MockSettingsService? settings = null,
        IHistoryStore? historyStore = null,
        PendingAudioStore? pendingAudio = null)
    {
        return new AppState(
            audio ?? new MockAudioRecorder(),
            hook ?? new MockKeyboardHook(),
            new MockContextService(),
            transcription ?? new MockTranscriptionService(),
            new MockContextVisionService(),
            post ?? new MockPostProcessingService(),
            injector ?? new MockTextInjector(),
            settings ?? new MockSettingsService(),
            historyStore: historyStore,
            pendingAudioStore: pendingAudio);
    }

    [Fact]
    public async Task EscapeDuringRecording_CancelsWithoutInjection()
    {
        var audio = new MockAudioRecorder();
        var hook = new MockKeyboardHook();
        var injector = new MockTextInjector();
        var appState = CreatePipeline(audio: audio, hook: hook, injector: injector);
        await appState.InitializeAsync();

        hook.Trigger(ShortcutAction.HoldStarted);
        Assert.True(audio.IsRecording);

        hook.Trigger(ShortcutAction.EscapePressed);
        await appState.WaitForIdleAsync(2000);

        Assert.Equal(PipelinePhase.Idle, appState.Phase);
        // Recorder stop is fire-and-forget; allow it a moment to complete.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (audio.IsRecording && sw.ElapsedMilliseconds < 2000) await Task.Delay(20);
        Assert.False(audio.IsRecording);
        Assert.Null(injector.InjectedText);
        Assert.Empty(appState.History);
    }

    [Fact]
    public async Task MissingApiKey_ReportsErrorAndDoesNotInject()
    {
        var hook = new MockKeyboardHook();
        var injector = new MockTextInjector();
        var settings = new MockSettingsService { Settings = new AppSettings() }; // no key
        using var temp = new TempPendingDir();
        var appState = CreatePipeline(hook: hook, injector: injector, settings: settings, pendingAudio: temp.Store);
        appState.MinimumDictationDuration = TimeSpan.Zero;
        await appState.InitializeAsync();

        string? error = null;
        appState.ErrorOccurred += m => error = m;

        hook.Trigger(ShortcutAction.HoldStarted);
        hook.Trigger(ShortcutAction.HoldReleased);
        await appState.WaitForIdleAsync(5000);

        Assert.Null(injector.InjectedText);
        Assert.NotNull(error);
        Assert.Contains("API key", error);
    }

    [Fact]
    public async Task ErrorReset_DoesNotClobberNewRecording()
    {
        var hook = new MockKeyboardHook();
        var settings = new MockSettingsService { Settings = new AppSettings() }; // no key -> error path
        using var temp = new TempPendingDir();
        var appState = CreatePipeline(hook: hook, settings: settings, pendingAudio: temp.Store);
        appState.MinimumDictationDuration = TimeSpan.Zero;
        await appState.InitializeAsync();

        // Subscribe BEFORE triggering so the error phase cannot be missed.
        var sawError = new TaskCompletionSource();
        appState.PhaseChanged += p => { if (p == PipelinePhase.Error) sawError.TrySetResult(); };

        hook.Trigger(ShortcutAction.HoldStarted);
        hook.Trigger(ShortcutAction.HoldReleased);

        // Wait for the error phase to appear, then start a NEW recording before the
        // 1500ms error-reset timer fires. The timer must not reset the new run.
        await Task.WhenAny(sawError.Task, Task.Delay(3000));
        Assert.True(sawError.Task.IsCompleted, "expected error phase");

        await appState.WaitForIdleAsync(5000);
        hook.Trigger(ShortcutAction.HoldStarted);
        Assert.Equal(PipelinePhase.Recording, appState.Phase);

        await Task.Delay(1800); // outlive the stale error-reset timer
        Assert.Equal(PipelinePhase.Recording, appState.Phase);

        hook.Trigger(ShortcutAction.EscapePressed);
    }

    [Fact]
    public async Task AccidentalTap_ShorterThanMinimum_DiscardsWithoutTranscribing()
    {
        var hook = new MockKeyboardHook();
        var transcription = new MockTranscriptionService();
        var injector = new MockTextInjector();
        using var temp = new TempPendingDir();
        var appState = CreatePipeline(hook: hook, transcription: transcription, injector: injector, pendingAudio: temp.Store);
        appState.MinimumDictationDuration = TimeSpan.FromSeconds(5); // release below is instant
        await appState.InitializeAsync();

        hook.Trigger(ShortcutAction.HoldStarted);
        hook.Trigger(ShortcutAction.HoldReleased);
        await appState.WaitForIdleAsync(5000);

        Assert.Equal(PipelinePhase.Idle, appState.Phase);
        Assert.Null(injector.InjectedText);
        Assert.Empty(appState.History);
        Assert.Equal(0, transcription.CallCount);
    }

    [Fact]
    public async Task HoldLongerThanMinimum_StillTranscribes()
    {
        var hook = new MockKeyboardHook();
        var injector = new MockTextInjector();
        using var temp = new TempPendingDir();
        var appState = CreatePipeline(hook: hook, injector: injector, pendingAudio: temp.Store);
        appState.MinimumDictationDuration = TimeSpan.FromMilliseconds(150);
        await appState.InitializeAsync();

        hook.Trigger(ShortcutAction.HoldStarted);
        await Task.Delay(250);
        hook.Trigger(ShortcutAction.HoldReleased);
        await appState.WaitForIdleAsync(5000);

        Assert.NotNull(injector.InjectedText);
        Assert.Single(appState.History);
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

        using var temp = new TempPendingDir();
        var appState = new AppState(audio, hook, context, transcription, vision, post, injector, settings,
            pendingAudioStore: temp.Store);
        appState.MinimumDictationDuration = TimeSpan.Zero;
        await appState.InitializeAsync();

        hook.Trigger(ShortcutAction.HoldStarted);
        hook.Trigger(ShortcutAction.HoldReleased);

        await appState.WaitForIdleAsync(5000);

        Assert.Equal("test@winflow.dev", injector.InjectedText);
    }

    [Fact]
    public async Task ServerFails_KeepsAudioAndFailedHistoryItem_ManualRetryRecovers()
    {
        var hook = new MockKeyboardHook();
        var transcription = new MockTranscriptionService { FailuresBeforeSuccess = int.MaxValue };
        var injector = new MockTextInjector();
        using var temp = new TempPendingDir();
        var historyPath = Path.Combine(temp.Dir, "history.json");
        var store = new LocalHistoryStore(historyPath);
        var appState = CreatePipeline(hook: hook, transcription: transcription, injector: injector,
            historyStore: store, pendingAudio: temp.Store);
        appState.MinimumDictationDuration = TimeSpan.Zero;
        await appState.InitializeAsync();

        string? error = null;
        appState.ErrorOccurred += m => error = m;

        hook.Trigger(ShortcutAction.HoldStarted);
        hook.Trigger(ShortcutAction.HoldReleased);
        await appState.WaitForIdleAsync(5000);

        // No injection, but the run is kept: error message + WAV on disk.
        Assert.Null(injector.InjectedText);
        Assert.NotNull(error);
        var failed = Assert.Single(appState.History);
        Assert.True(failed.HasError);
        Assert.True(failed.CanRetry);
        Assert.NotNull(failed.AudioFilePath);
        Assert.True(File.Exists(failed.AudioFilePath));

        // The failed run must not pollute dictation stats.
        Assert.Equal(0, store.Stats.TotalRuns);

        // Server recovers; the user clicks Retry — no re-speaking, no injection.
        transcription.FailuresBeforeSuccess = 0;
        var ok = await appState.RetryHistoryItemAsync(failed);

        Assert.True(ok);
        Assert.False(failed.HasError);
        Assert.Equal("hello world from mock", failed.RawTranscript);
        Assert.Equal("Hello, world from mock.", failed.CleanedText);
        Assert.Null(injector.InjectedText); // focus moved on; text is copied from History
        Assert.Empty(Directory.GetFiles(temp.Dir, "*.wav")); // recovered run cleaned up its WAV

        // The recovered text survives a reload of the persistent store.
        await Task.Delay(150);
        var reloaded = new LocalHistoryStore(historyPath);
        await reloaded.LoadAsync();
        Assert.Equal("Hello, world from mock.", reloaded.Recent[0].CleanedText);
        Assert.False(reloaded.Recent[0].HasError);
    }

    [Fact]
    public async Task SuccessfulRun_DeletesPendingAudio()
    {
        var hook = new MockKeyboardHook();
        using var temp = new TempPendingDir();
        var appState = CreatePipeline(hook: hook, pendingAudio: temp.Store);
        appState.MinimumDictationDuration = TimeSpan.Zero;
        await appState.InitializeAsync();

        hook.Trigger(ShortcutAction.HoldStarted);
        hook.Trigger(ShortcutAction.HoldReleased);
        await appState.WaitForIdleAsync(5000);

        Assert.Single(appState.History);
        Assert.False(appState.History[0].HasError);
        Assert.Empty(Directory.Exists(temp.Dir) ? Directory.GetFiles(temp.Dir, "*.wav") : Array.Empty<string>());
    }

    [Fact]
    public async Task EmptyTranscript_ReportsError_KeepsAudioForRetry()
    {
        var hook = new MockKeyboardHook();
        var transcription = new MockTranscriptionService { Transcript = "" };
        var injector = new MockTextInjector();
        using var temp = new TempPendingDir();
        var appState = CreatePipeline(hook: hook, transcription: transcription, injector: injector, pendingAudio: temp.Store);
        appState.MinimumDictationDuration = TimeSpan.Zero;
        await appState.InitializeAsync();

        string? error = null;
        appState.ErrorOccurred += m => error = m;

        hook.Trigger(ShortcutAction.HoldStarted);
        hook.Trigger(ShortcutAction.HoldReleased);
        await appState.WaitForIdleAsync(5000);

        Assert.Null(injector.InjectedText);
        Assert.NotNull(error); // no longer a silent disappearance
        var failed = Assert.Single(appState.History);
        Assert.True(failed.CanRetry);
        Assert.True(File.Exists(failed.AudioFilePath));
    }

    [Fact]
    public async Task CleanupFails_KeepsRawTranscript_RetryRecoversWithoutNewTranscription()
    {
        var hook = new MockKeyboardHook();
        var transcription = new MockTranscriptionService();
        var post = new MockPostProcessingService { FailuresBeforeSuccess = int.MaxValue };
        var injector = new MockTextInjector();
        using var temp = new TempPendingDir();
        var appState = CreatePipeline(hook: hook, transcription: transcription, post: post,
            injector: injector, pendingAudio: temp.Store);
        appState.MinimumDictationDuration = TimeSpan.Zero;
        await appState.InitializeAsync();

        hook.Trigger(ShortcutAction.HoldStarted);
        hook.Trigger(ShortcutAction.HoldReleased);
        await appState.WaitForIdleAsync(5000);

        var failed = Assert.Single(appState.History);
        Assert.True(failed.HasError);
        Assert.Equal("hello world from mock", failed.RawTranscript);
        Assert.True(File.Exists(failed.AudioFilePath));
        var transcriptionCalls = transcription.CallCount;

        post.FailuresBeforeSuccess = 0;
        var ok = await appState.RetryHistoryItemAsync(failed);

        Assert.True(ok);
        Assert.Equal("Hello, world from mock.", failed.CleanedText);
        Assert.Equal(transcriptionCalls, transcription.CallCount); // reused the saved transcript
        Assert.Null(injector.InjectedText);
    }

    [Fact]
    public async Task PasteFails_SavesTextToHistory()
    {
        var hook = new MockKeyboardHook();
        var injector = new MockTextInjector { Succeeds = false };
        using var temp = new TempPendingDir();
        var appState = CreatePipeline(hook: hook, injector: injector, pendingAudio: temp.Store);
        appState.MinimumDictationDuration = TimeSpan.Zero;
        await appState.InitializeAsync();

        hook.Trigger(ShortcutAction.HoldStarted);
        hook.Trigger(ShortcutAction.HoldReleased);
        await appState.WaitForIdleAsync(5000);

        // The old UI promised "your words are safe in the run log" — now they actually are.
        var item = Assert.Single(appState.History);
        Assert.Equal("Hello, world from mock.", item.CleanedText);
        Assert.True(item.HasError);
    }
}
