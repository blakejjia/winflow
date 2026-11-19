using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using WinFlow.Audio;
using WinFlow.Core.Pipeline;
using WinFlow.Core.Services;
using WinFlow.Tray;
using WinFlow.Views;
using WinFlow.WindowsInterops.Context;
using WinFlow.WindowsInterops.Keyboard;
using WinFlow.WindowsInterops.TextInsertion;

namespace WinFlow;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\WinFlow_SingleInstance_Mutex";

    private Mutex? _mutex;
    private bool _ownsMutex;

    private AppState? _appState;
    private OverlayWindow? _overlayWindow;
    private SystemTrayManager? _trayManager;
    private MainWindow? _mainWindow;
    private MeetingRecorder? _meetingRecorder;
    private MeetingNotesService? _notesService;
    private HttpClient? _httpClient;
    private bool _isShuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            // Another instance owns the pipeline. Never run a second keyboard hook.
            Debug.WriteLine("[WinFlow] Another instance is already running; exiting.");
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Debug.WriteLine($"[WinFlow Fatal] {args.ExceptionObject}");
        };

        DispatcherUnhandledException += (_, args) =>
        {
            Debug.WriteLine($"[WinFlow UI Error] {args.Exception}");
            _trayManager?.ShowNotification("WinFlow error", args.Exception.Message);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Debug.WriteLine($"[WinFlow Task Error] {args.Exception}");
            args.SetObserved();
        };

        // Composition root: one pipeline for the whole app lifetime, independent of any window.
        // The settings provider is shared by AppState and every API service, so saving
        // settings takes effect immediately with no stale second instance.
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var settingsService = new LocalSettingsService();
        var settingsProvider = new SettingsProvider();
        var historyStore = new LocalHistoryStore();
        var transcription = new TranscriptionService(_httpClient, settingsProvider);

        _appState = new AppState(
            new WasapiAudioRecorder(),
            new KeyboardHookManager(),
            new WindowsContextService(),
            transcription,
            new ContextVisionService(_httpClient, settingsProvider),
            new PostProcessingService(_httpClient, settingsProvider),
            new WindowsTextInjector(settingsProvider),
            settingsService,
            settingsProvider,
            historyStore);

        _meetingRecorder = new MeetingRecorder();
        _notesService = new MeetingNotesService(transcription, _httpClient, settingsProvider);

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _appState!.InitializeAsync();

            try
            {
                Views.SettingsPanel.ApplyTheme(_appState.Settings.Theme);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WinFlow] Theme initialization failed: {ex.Message}");
            }

            _overlayWindow = new OverlayWindow(_appState);
            _trayManager = new SystemTrayManager(_appState);

            _trayManager.OpenSettingsRequested += () => ShowMain();
            _trayManager.OpenRunLogRequested += () => ShowMain(navigateToHistory: true);
            _trayManager.ExitRequested += () => _ = ShutdownAsync();

            ShowMain();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WinFlow] Initialization failed: {ex}");
            System.Windows.MessageBox.Show(
                $"WinFlow failed to initialize:\n{ex.Message}",
                "WinFlow",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await ShutdownAsync();
        }
    }

    private void ShowMain(bool navigateToHistory = false)
    {
        if (_appState == null || _isShuttingDown) return;

        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow(_appState, _httpClient!, _meetingRecorder!, _notesService!);
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }

        if (navigateToHistory)
        {
            _mainWindow.NavigateToHistory();
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
        _mainWindow.Activate();
    }

    /// <summary>
    /// The single exit path: cancel the pipeline, unhook the keyboard, dispose everything,
    /// then shut the dispatcher down. Tray Exit and fatal init errors both land here.
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        try
        {
            if (_appState != null)
            {
                await _appState.WaitForIdleAsync(1500);
            }

            _mainWindow?.CloseForShutdown();
            _overlayWindow?.CloseForShutdown();
            _trayManager?.Dispose();
            _meetingRecorder?.Dispose();
            _appState?.Dispose();
            _httpClient?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WinFlow] Shutdown cleanup error: {ex.Message}");
        }
        finally
        {
            if (_ownsMutex)
            {
                try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
                _ownsMutex = false;
            }
            _mutex?.Dispose();
            _mutex = null;
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
            _ownsMutex = false;
        }
        _mutex?.Dispose();
        _mutex = null;
        base.OnExit(e);
    }
}
