using System.Net.Http;
using System.Windows;
using WinFlow.App.Tray;
using WinFlow.App.Views;
using WinFlow.Audio;
using WinFlow.Core.Pipeline;
using WinFlow.Core.Services;
using WinFlow.WindowsInterops.Context;
using WinFlow.WindowsInterops.Keyboard;
using WinFlow.WindowsInterops.TextInsertion;

namespace WinFlow.App;

public partial class App : System.Windows.Application
{
    private AppState? _appState;
    private OverlayWindow? _overlayWindow;
    private SystemTrayManager? _trayManager;
    private SettingsWindow? _settingsWindow;
    private HttpClient? _httpClient;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var settingsService = new LocalSettingsService();
        var settings = await settingsService.LoadSettingsAsync();

        var audio = new WasapiAudioRecorder();
        var hook = new KeyboardHookManager();
        var context = new WindowsContextService();
        var transcription = new TranscriptionService(_httpClient, settings);
        var vision = new ContextVisionService(_httpClient, settings);
        var postProcessing = new PostProcessingService(_httpClient, settings);
        var injector = new WindowsTextInjector();

        _appState = new AppState(
            audio,
            hook,
            context,
            transcription,
            vision,
            postProcessing,
            injector,
            settingsService);

        await _appState.InitializeAsync();

        _overlayWindow = new OverlayWindow(_appState);

        _trayManager = new SystemTrayManager(_appState);
        _trayManager.OpenSettingsRequested += () => ShowSettings();
        _trayManager.OpenRunLogRequested += () => ShowSettings(openRunLog: true);
        _trayManager.ExitRequested += ExitApp;

        // If no API key configured, open Settings automatically on first launch
        if (string.IsNullOrWhiteSpace(_appState.Settings.GroqApiKey))
        {
            ShowSettings();
        }
    }

    private void ShowSettings(bool openRunLog = false)
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(_appState!);
            _settingsWindow.Closed += (s, e) => _settingsWindow = null;
        }

        if (openRunLog)
        {
            _settingsWindow.NavigateToRunLog();
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ExitApp()
    {
        _trayManager?.Dispose();
        _overlayWindow?.Close();
        _settingsWindow?.Close();
        _appState?.Dispose();
        _httpClient?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayManager?.Dispose();
        _overlayWindow?.Close();
        _appState?.Dispose();
        _httpClient?.Dispose();
        base.OnExit(e);
    }
}
