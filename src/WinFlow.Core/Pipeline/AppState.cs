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

    private readonly List<PipelineHistoryItem> _history = new();
    private readonly object _historyLock = new();

    private PipelinePhase _phase = PipelinePhase.Idle;
    private float _audioLevel;
    private Task<string>? _contextVisionTask;
    private AppContextSnapshot? _activeContextSnapshot;
    private CancellationTokenSource? _currentCts;
    private TaskCompletionSource? _idleTcs;

    public AppSettings Settings { get; private set; } = new();

    public PipelinePhase Phase
    {
        get => _phase;
        private set
        {
            if (_phase != value)
            {
                _phase = value;
                PhaseChanged?.Invoke(_phase);
                if (_phase == PipelinePhase.Idle)
                {
                    _idleTcs?.TrySetResult();
                }
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

    public AppState(
        IAudioRecorder audioRecorder,
        IKeyboardHook keyboardHook,
        IContextService contextService,
        ITranscriptionService transcriptionService,
        IContextVisionService contextVisionService,
        IPostProcessingService postProcessingService,
        ITextInjector textInjector,
        ISettingsService settingsService)
    {
        _audioRecorder = audioRecorder;
        _keyboardHook = keyboardHook;
        _contextService = contextService;
        _transcriptionService = transcriptionService;
        _contextVisionService = contextVisionService;
        _postProcessingService = postProcessingService;
        _textInjector = textInjector;
        _settingsService = settingsService;
    }

    public async Task InitializeAsync()
    {
        Settings = await _settingsService.LoadSettingsAsync();

        _audioRecorder.AudioLevelUpdated += level => AudioLevel = level;
        _keyboardHook.ActionTriggered += OnKeyboardAction;
        _keyboardHook.Start();
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
        if (Phase != PipelinePhase.Idle) return;

        _idleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _currentCts?.Cancel();
        _currentCts = new CancellationTokenSource();
        var ct = _currentCts.Token;

        Phase = PipelinePhase.Recording;

        try
        {
            _audioRecorder.StartRecording(Settings.SelectedMicrophoneId);

            // Concurrent Stage 1: Capture context snapshot & trigger vision synthesis
            _contextVisionTask = Task.Run(async () =>
            {
                try
                {
                    _activeContextSnapshot = await _contextService.CaptureContextSnapshotAsync(
                        Settings.ContextCaptureEnabled,
                        Settings.ContextScreenshotMaxDimension);

                    if (ct.IsCancellationRequested) return string.Empty;

                    return await _contextVisionService.SynthesizeContextAsync(
                        _activeContextSnapshot.ScreenshotBase64Jpeg,
                        _activeContextSnapshot.ProcessName,
                        _activeContextSnapshot.WindowTitle,
                        ct);
                }
                catch
                {
                    return string.Empty;
                }
            }, ct);
        }
        catch (Exception ex)
        {
            HandleError($"Failed to start recording: {ex.Message}");
        }
    }

    private void StopDictation()
    {
        if (Phase != PipelinePhase.Recording) return;

        Phase = PipelinePhase.Transcribing;
        var ct = _currentCts?.Token ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            string rawTranscript = string.Empty;
            string cleanedText = string.Empty;
            string? contextSummary = null;
            string? selectedText = null;

            try
            {
                var wav = await _audioRecorder.StopRecordingAsync();
                if (wav == null || wav.Length <= 44 || ct.IsCancellationRequested)
                {
                    Phase = PipelinePhase.Idle;
                    return;
                }

                // Audio Transcription
                rawTranscript = await _transcriptionService.TranscribeAsync(wav, Settings.TranscriptionLanguage, ct);
                if (string.IsNullOrWhiteSpace(rawTranscript) || ct.IsCancellationRequested)
                {
                    Phase = PipelinePhase.Idle;
                    return;
                }

                selectedText = _activeContextSnapshot?.SelectedText;

                bool pressEnterAfter = Settings.PressEnterAfterDictation;

                // Check Voice Macros (local bypass)
                var macroPayload = VoiceMacroMatcher.Match(rawTranscript, Settings.VoiceMacros);
                if (macroPayload != null)
                {
                    cleanedText = macroPayload;
                }
                else
                {
                    // Await concurrent vision synthesis with 2000ms timeout
                    if (_contextVisionTask != null)
                    {
                        var completed = await Task.WhenAny(_contextVisionTask, Task.Delay(2000, ct));
                        if (completed == _contextVisionTask)
                        {
                            contextSummary = await _contextVisionTask;
                        }
                    }

                    contextSummary ??= $"User in {_activeContextSnapshot?.ProcessName ?? "active app"}.";

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

                if (string.IsNullOrWhiteSpace(cleanedText))
                {
                    Phase = PipelinePhase.Idle;
                    return;
                }

                // Text Injection
                Phase = PipelinePhase.Inserting;
                await _textInjector.InjectTextAsync(cleanedText, pressEnterAfter);

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
            catch (Exception ex)
            {
                HandleError($"Pipeline error: {ex.Message}");
            }
            finally
            {
                Phase = PipelinePhase.Idle;
            }
        });
    }

    private void CancelDictation()
    {
        _currentCts?.Cancel();
        _ = _audioRecorder.StopRecordingAsync();
        Phase = PipelinePhase.Idle;
    }

    private void HandleError(string message)
    {
        ErrorOccurred?.Invoke(message);
        Phase = PipelinePhase.Error;
        _ = Task.Delay(1500).ContinueWith(_ => Phase = PipelinePhase.Idle);
    }

    private void AddHistory(PipelineHistoryItem item)
    {
        lock (_historyLock)
        {
            _history.Insert(0, item);
            if (_history.Count > 20)
            {
                _history.RemoveAt(_history.Count - 1);
            }
        }
    }

    public async Task WaitForIdleAsync(int timeoutMs = 5000)
    {
        if (Phase == PipelinePhase.Idle) return;
        var tcs = _idleTcs;
        if (tcs != null)
        {
            await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        }
    }

    public void Dispose()
    {
        _currentCts?.Cancel();
        _currentCts?.Dispose();
        _keyboardHook.Dispose();
        _audioRecorder.Dispose();
    }
}
