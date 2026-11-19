using System.Diagnostics;
using WinFlow.Core.Audio;
using WinFlow.Core.Context;
using WinFlow.Core.Keyboard;
using WinFlow.Core.Models;
using WinFlow.Core.Services;
using WinFlow.Core.TextInsertion;

namespace WinFlow.Core.Pipeline;

public class AppState : IDisposable
{
    private readonly IAudioRecorder _audioRecorder;
    private readonly IKeyboardHook _keyboardHook;
    private readonly IContextService _contextService;
    private readonly ITranscriptionService _transcriptionService;
    private readonly IContextVisionService _contextVisionService;
    private readonly IPostProcessingService _postProcessingService;
    private readonly ITextInjector _textInjector;
    private readonly ISettingsService _settingsService;
    private readonly SettingsProvider _settingsProvider;
    private readonly IHistoryStore? _historyStore;

    private readonly List<PipelineHistoryItem> _history = new();
    private readonly object _historyLock = new();

    /// <summary>
    /// Serializes all state transitions. Keyboard actions arrive on thread-pool threads
    /// and the pipeline continuation runs concurrently; every read/write of the fields
    /// below happens under this gate so phases can never interleave.
    /// </summary>
    private readonly object _stateLock = new();

    private PipelinePhase _phase = PipelinePhase.Idle;
    private long _recordingStartTimestamp;

    /// <summary>
    /// Holds shorter than this are treated as accidental taps: the audio is
    /// discarded without transcription. Whisper hallucinates phrases like
    /// "Thank you" on near-silence, so a stray tap must never reach the model.
    /// Internal setter exists for tests.
    /// </summary>
    internal TimeSpan MinimumDictationDuration { get; set; } = TimeSpan.FromSeconds(1);
    private float _audioLevel;
    private int _runGeneration;
    private Task<AppContextSnapshot>? _contextSnapshotTask;
    private Task<string>? _contextVisionTask;
    private CancellationTokenSource? _currentCts;
    private TaskCompletionSource? _idleTcs;

    /// <summary>
    /// The live settings instance. Services share this through <see cref="SettingsProvider"/>
    /// so saved changes take effect on the next pipeline run without a restart.
    /// </summary>
    public AppSettings Settings => _settingsProvider.Current;

    public ISettingsProvider SettingsProvider => _settingsProvider;

    public PipelinePhase Phase
    {
        get { lock (_stateLock) return _phase; }
        private set
        {
            bool changed = false;
            lock (_stateLock)
            {
                if (_phase != value)
                {
                    _phase = value;
                    changed = true;
                    if (_phase == PipelinePhase.Idle)
                    {
                        _idleTcs?.TrySetResult();
                    }
                }
            }
            // Events fire outside the lock; subscribers marshal to their own threads.
            if (changed)
            {
                PhaseChanged?.Invoke(value);
            }
        }
    }

    public float AudioLevel
    {
        get => _audioLevel;
        private set
        {
            _audioLevel = value;
            AudioLevelChanged?.Invoke(_audioLevel);
        }
    }

    public IReadOnlyList<PipelineHistoryItem> History
    {
        get
        {
            lock (_historyLock) return _history.ToList();
        }
    }

    public event Action<PipelinePhase>? PhaseChanged;
    public event Action<float>? AudioLevelChanged;
    public event Action<PipelineHistoryItem>? RunCompleted;
    public event Action<string>? ErrorOccurred;
    public event Action<PipelineNotice>? NoticeRaised;

    public AppState(
        IAudioRecorder audioRecorder,
        IKeyboardHook keyboardHook,
        IContextService contextService,
        ITranscriptionService transcriptionService,
        IContextVisionService contextVisionService,
        IPostProcessingService postProcessingService,
        ITextInjector textInjector,
        ISettingsService settingsService,
        SettingsProvider? settingsProvider = null,
        IHistoryStore? historyStore = null)
    {
        _audioRecorder = audioRecorder;
        _keyboardHook = keyboardHook;
        _contextService = contextService;
        _transcriptionService = transcriptionService;
        _contextVisionService = contextVisionService;
        _postProcessingService = postProcessingService;
        _textInjector = textInjector;
        _settingsService = settingsService;
        _settingsProvider = settingsProvider ?? new SettingsProvider();
        _historyStore = historyStore;
    }

    /// <summary>Persistent local history + lifetime stats (null in unit tests).</summary>
    public IHistoryStore? HistoryStore => _historyStore;

    public async Task InitializeAsync()
    {
        _settingsProvider.Current = await _settingsService.LoadSettingsAsync();
        if (_historyStore != null)
        {
            await _historyStore.LoadAsync();
            lock (_historyLock)
            {
                _history.Clear();
                _history.AddRange(_historyStore.Recent.Take(20));
            }
        }

        _audioRecorder.AudioLevelUpdated += level => AudioLevel = level;
        _keyboardHook.ActionTriggered += OnKeyboardAction;
        ApplyShortcutSettings();
        _keyboardHook.Start();
    }

    /// <summary>Pushes the current shortcut settings into the keyboard hook.</summary>
    public void ApplyShortcutSettings()
    {
        _keyboardHook.UpdateSettings(Settings.DoubleTapLockEnabled, Settings.PauseInFullscreenGames);
    }

    private void OnKeyboardAction(ShortcutAction action)
    {
        switch (action)
        {
            case ShortcutAction.HoldStarted:
            case ShortcutAction.ToggleLocked:
                StartDictation();
                break;

            case ShortcutAction.HoldReleased:
            case ShortcutAction.ToggleStopped:
                StopDictation();
                break;

            case ShortcutAction.EscapePressed:
                CancelDictation();
                break;
        }
    }

    private void StartDictation()
    {
        CancellationToken ct;
        lock (_stateLock)
        {
            if (_phase != PipelinePhase.Idle) return;

            _runGeneration++;
            _idleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _currentCts?.Cancel();
            _currentCts?.Dispose();
            _currentCts = new CancellationTokenSource();
            ct = _currentCts.Token;
            _phase = PipelinePhase.Recording;
            _recordingStartTimestamp = Stopwatch.GetTimestamp();
        }
        PhaseChanged?.Invoke(PipelinePhase.Recording);

        try
        {
            _audioRecorder.StartRecording(Settings.SelectedMicrophoneId);

            // Concurrent Stage 1: snapshot first (fast, local), then vision synthesis (slow,
            // network). The snapshot task is kept separate so Stop can await just the
            // snapshot without waiting on the vision LLM.
            var snapshotTask = _contextService.CaptureContextSnapshotAsync(
                Settings.ContextCaptureEnabled,
                Settings.ContextScreenshotMaxDimension);

            lock (_stateLock)
            {
                _contextSnapshotTask = snapshotTask;
                _contextVisionTask = ContinueWithVision(snapshotTask, ct);
            }
        }
        catch (Exception ex)
        {
            HandleError($"Failed to start recording: {ex.Message}");
        }
    }

    private async Task<string> ContinueWithVision(Task<AppContextSnapshot> snapshotTask, CancellationToken ct)
    {
        try
        {
            var snapshot = await snapshotTask;
            if (ct.IsCancellationRequested) return string.Empty;

            return await _contextVisionService.SynthesizeContextAsync(
                snapshot.ScreenshotBase64Jpeg,
                snapshot.ProcessName,
                snapshot.WindowTitle,
                ct);
        }
        catch
        {
            return string.Empty;
        }
    }

    private void StopDictation()
    {
        // Accidental tap: discard silently, exactly like Escape.
        if (Stopwatch.GetElapsedTime(_recordingStartTimestamp) < MinimumDictationDuration)
        {
            CancelDictation();
            return;
        }

        CancellationToken ct;
        Task<AppContextSnapshot>? snapshotTask;
        Task<string>? visionTask;
        int generation;

        lock (_stateLock)
        {
            if (_phase != PipelinePhase.Recording) return;
            _phase = PipelinePhase.Transcribing;
            ct = _currentCts?.Token ?? CancellationToken.None;
            snapshotTask = _contextSnapshotTask;
            visionTask = _contextVisionTask;
            generation = _runGeneration;
        }
        PhaseChanged?.Invoke(PipelinePhase.Transcribing);

        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            string rawTranscript = string.Empty;
            string cleanedText = string.Empty;
            string? contextSummary = null;
            AppContextSnapshot? snapshot = null;

            try
            {
                var wav = await _audioRecorder.StopRecordingAsync();
                if (wav == null || wav.Length <= 44 || ct.IsCancellationRequested)
                {
                    return;
                }

                // Audio Transcription
                rawTranscript = await _transcriptionService.TranscribeAsync(wav, Settings.TranscriptionLanguage, ct);
                if (string.IsNullOrWhiteSpace(rawTranscript) || ct.IsCancellationRequested)
                {
                    return;
                }

                // Await the local context snapshot (bounded) so Edit Mode selection and
                // elevation info are deterministic, without waiting on the vision LLM.
                if (snapshotTask != null)
                {
                    var done = await Task.WhenAny(snapshotTask, Task.Delay(500, CancellationToken.None));
                    if (done == snapshotTask)
                    {
                        snapshot = await snapshotTask;
                    }
                }
                var selectedText = snapshot?.SelectedText;

                if (snapshot?.IsElevated == true)
                {
                    NoticeRaised?.Invoke(PipelineNotice.TargetElevated);
                }

                bool pressEnterAfter = Settings.PressEnterAfterDictation;

                // Check Voice Macros (local bypass)
                var macroPayload = VoiceMacroMatcher.Match(rawTranscript, Settings.VoiceMacros);
                if (macroPayload != null)
                {
                    cleanedText = macroPayload;
                }
                else
                {
                    // LLM cleanup needs a key; macros above intentionally work offline.
                    if (string.IsNullOrWhiteSpace(Settings.GroqApiKey))
                    {
                        HandleError("No API key yet — add one in Settings to start dictating.", generation);
                        return;
                    }

                    // Await concurrent vision synthesis with 2000ms timeout
                    if (visionTask != null)
                    {
                        var completed = await Task.WhenAny(visionTask, Task.Delay(2000, CancellationToken.None));
                        if (completed == visionTask)
                        {
                            contextSummary = await visionTask;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(contextSummary))
                    {
                        contextSummary = $"User in {snapshot?.ProcessName ?? "active app"}.";
                    }

                    // Check for "press enter" voice command
                    if (rawTranscript.EndsWith("press enter", StringComparison.OrdinalIgnoreCase) ||
                        rawTranscript.EndsWith("回车", StringComparison.OrdinalIgnoreCase))
                    {
                        pressEnterAfter = true;
                    }

                    // LLM Cleanup
                    cleanedText = await _postProcessingService.CleanupTranscriptAsync(
                        rawTranscript,
                        contextSummary,
                        Settings.CustomVocabulary,
                        selectedText,
                        ct);
                }

                if (string.IsNullOrWhiteSpace(cleanedText) || ct.IsCancellationRequested)
                {
                    return;
                }

                // Text Injection
                Phase = PipelinePhase.Inserting;
                bool injected = await _textInjector.InjectTextAsync(cleanedText, pressEnterAfter);
                if (!injected)
                {
                    HandleError("Couldn't paste — your words are safe in the run log.", generation);
                    return;
                }

                sw.Stop();

                var historyItem = new PipelineHistoryItem
                {
                    RawTranscript = rawTranscript,
                    CleanedText = cleanedText,
                    SelectedText = selectedText,
                    ContextSummary = contextSummary,
                    DurationSeconds = sw.Elapsed.TotalSeconds,
                    LatencyMs = sw.ElapsedMilliseconds,
                    IsCommandMode = !string.IsNullOrWhiteSpace(selectedText)
                };

                AddHistory(historyItem);
                RunCompleted?.Invoke(historyItem);

                Phase = PipelinePhase.Feedback;
                await Task.Delay(300);
            }
            catch (OperationCanceledException)
            {
                // User pressed Escape or a new run superseded this one; not an error.
            }
            catch (Exception ex)
            {
                HandleError($"Pipeline error: {ex.Message}", generation);
            }
            finally
            {
                lock (_stateLock)
                {
                    // Only the newest run may return the pipeline to Idle; a superseded
                    // run must not yank a fresh recording back to Idle.
                    if (_runGeneration == generation && _phase != PipelinePhase.Error)
                    {
                        _phase = PipelinePhase.Idle;
                        _idleTcs?.TrySetResult();
                    }
                }
                if (Phase == PipelinePhase.Idle)
                {
                    PhaseChanged?.Invoke(PipelinePhase.Idle);
                }
            }
        });
    }

    private void CancelDictation()
    {
        lock (_stateLock)
        {
            _runGeneration++; // invalidate any in-flight run's finally block
            _currentCts?.Cancel();
            _contextSnapshotTask = null;
            _contextVisionTask = null;
            _phase = PipelinePhase.Idle;
            _idleTcs?.TrySetResult();
        }
        _ = Task.Run(async () =>
        {
            try { await _audioRecorder.StopRecordingAsync(); }
            catch { /* device may already be gone */ }
        });
        PhaseChanged?.Invoke(PipelinePhase.Idle);
    }

    private void HandleError(string message, int? generation = null)
    {
        int gen;
        lock (_stateLock)
        {
            gen = generation ?? _runGeneration;
            _phase = PipelinePhase.Error;
        }
        ErrorOccurred?.Invoke(message);
        PhaseChanged?.Invoke(PipelinePhase.Error);

        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            bool becameIdle = false;
            lock (_stateLock)
            {
                // Reset only if no newer run started and we are still showing this error.
                if (_runGeneration == gen && _phase == PipelinePhase.Error)
                {
                    _phase = PipelinePhase.Idle;
                    _idleTcs?.TrySetResult();
                    becameIdle = true;
                }
            }
            if (becameIdle)
            {
                PhaseChanged?.Invoke(PipelinePhase.Idle);
            }
        });
    }

    private void AddHistory(PipelineHistoryItem item)
    {
        _historyStore?.Append(item);
        lock (_historyLock)
        {
            _history.Insert(0, item);
            if (_history.Count > 20)
            {
                _history.RemoveAt(_history.Count - 1);
            }
        }
    }

    /// <summary>
    /// Persists settings and swaps the live instance atomically so all services
    /// observe the new values on their next request.
    /// </summary>
    public async Task SaveSettingsAsync(AppSettings settings)
    {
        await _settingsService.SaveSettingsAsync(settings);
        _settingsProvider.Current = settings;
    }

    public async Task WaitForIdleAsync(int timeoutMs = 5000)
    {
        Task? idleTask;
        lock (_stateLock)
        {
            if (_phase == PipelinePhase.Idle) return;
            idleTask = _idleTcs?.Task;
        }
        if (idleTask != null)
        {
            await Task.WhenAny(idleTask, Task.Delay(timeoutMs));
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            _runGeneration++;
            _currentCts?.Cancel();
            _currentCts?.Dispose();
            _currentCts = null;
        }
        _keyboardHook.Dispose();
        _audioRecorder.Dispose();
    }
}
