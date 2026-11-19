using System.IO;
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

        DispatcherUnhandledException += (s, args) =>
        {
            LogException("DispatcherUnhandledException", args.Exception);
            System.Windows.MessageBox.Show(
                $"WinFlow encountered an unexpected error:\n\n{args.Exception.Message}\n\nStack Trace:\n{args.Exception.StackTrace}",
                "WinFlow Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                LogException("AppDomainUnhandledException", ex);
            }
        };

        try
        {
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

            bool isMinimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(a, "-m", StringComparison.OrdinalIgnoreCase));

            if (!isMinimized)
            {
                ShowSettings();
            }
            else
            {
                _trayManager.ShowNotification(
                    "WinFlow is Running",
                    "WinFlow started in background. Hold CapsLock to speak, or double-click this tray icon to open Settings.");
            }
        }
        catch (Exception ex)
        {
            LogException("StartupException", ex);
            System.Windows.MessageBox.Show(
                $"Failed to start WinFlow:\n\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}",
                "WinFlow Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
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
        _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Topmost = true;
        _settingsWindow.Topmost = false;
        _settingsWindow.Activate();
        _settingsWindow.Focus();
    }

    private static void LogException(string source, Exception ex)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var logDir = Path.Combine(appData, "WinFlow");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, "winflow-error.log");
            File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}\n\n");
        }
        catch
        {
            // Ignore logging failures
        }
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
