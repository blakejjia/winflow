using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
using WinFlow.Audio;
using WinFlow.Core.Models;
using WinFlow.Core.Pipeline;
using WinFlow.Core.Prompts;
using WinFlow.Core.Services;
using WinFlow.Tray;
using WinFlow.WindowsInterops.Context;
using WinFlow.WindowsInterops.Keyboard;
using WinFlow.WindowsInterops.TextInsertion;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WinFlow.Views;

public partial class SettingsWindow : FluentWindow
{
    private AppState? _appState;
    private OverlayWindow? _overlayWindow;
    private SystemTrayManager? _trayManager;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly LocalSettingsService _settingsService = new();
    private string _currentApiKey = string.Empty;

    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += OnWindowLoaded;
    }

    public SettingsWindow(AppState appState) : this()
    {
        _appState = appState;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_appState == null)
        {
            var settings = await _settingsService.LoadSettingsAsync();
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
                _settingsService);

            await _appState.InitializeAsync();

            _overlayWindow = new OverlayWindow(_appState);
            _trayManager = new SystemTrayManager(_appState);

            _trayManager.OpenSettingsRequested += () =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            };

            _trayManager.OpenRunLogRequested += () =>
            {
                Show();
                WindowState = WindowState.Normal;
                NavigateToRunLog();
                Activate();
            };

            _trayManager.ExitRequested += () =>
            {
                _trayManager?.Dispose();
                _overlayWindow?.Close();
                _appState?.Dispose();
                _httpClient?.Dispose();
                System.Windows.Application.Current.Shutdown();
            };
        }

        LoadSettingsIntoUi();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Minimize to system tray on window close
        e.Cancel = true;
        Hide();
        _trayManager?.ShowNotification(
            "WinFlow Minimized",
            "WinFlow is running in your system tray. Hold CapsLock anytime to speak, or click the tray icon to reopen.");
        base.OnClosing(e);
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox == null) return;
        switch (ThemeComboBox.SelectedIndex)
        {
            case 0:
                ApplicationThemeManager.ApplySystemTheme();
                break;
            case 1:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                break;
            case 2:
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                break;
        }
    }

    private void LoadSettingsIntoUi()
    {
        if (_appState == null) return;
        var s = _appState.Settings;

        _currentApiKey = s.GroqApiKey;
        ApiKeyBox.Password = s.GroqApiKey;
        BaseUrlBox.Text = s.ApiBaseUrl;
        LanguageBox.Text = s.TranscriptionLanguage;
        PressEnterCheckBox.IsChecked = s.PressEnterAfterDictation;
        PreserveClipboardCheckBox.IsChecked = s.PreserveClipboard;
        ContextCaptureCheckBox.IsChecked = s.ContextCaptureEnabled;

        TranscriptionModelBox.Text = s.TranscriptionModel;
        PostProcessingModelBox.Text = s.PostProcessingModel;
        FallbackModelBox.Text = s.PostProcessingFallbackModel;
        ContextModelBox.Text = s.ContextModel;

        DoubleTapCheckBox.IsChecked = s.DoubleTapLockEnabled;
        VocabularyBox.Text = s.CustomVocabulary;

        SystemPromptBox.Text = !string.IsNullOrWhiteSpace(s.CustomSystemPrompt)
            ? s.CustomSystemPrompt
            : PromptTemplates.DefaultSystemPrompt;

        ContextPromptBox.Text = !string.IsNullOrWhiteSpace(s.CustomContextPrompt)
            ? s.CustomContextPrompt
            : PromptTemplates.ContextSynthesisPrompt;

        PopulateMicrophones(s.SelectedMicrophoneId);
        RefreshHistoryGrid();
    }

    private void PopulateMicrophones(string selectedId)
    {
        MicComboBox.Items.Clear();
        MicComboBox.Items.Add(new ComboBoxItem { Content = "Default System Microphone", Tag = "" });

        try
        {
            using var recorder = new WasapiAudioRecorder();
            var devices = recorder.GetAvailableDevices();
            int selectedIndex = 0;
            int idx = 1;

            foreach (var dev in devices)
            {
                var item = new ComboBoxItem { Content = dev.Name, Tag = dev.Id };
                MicComboBox.Items.Add(item);
                if (!string.IsNullOrEmpty(selectedId) && dev.Id == selectedId)
                {
                    selectedIndex = idx;
                }
                idx++;
            }

            MicComboBox.SelectedIndex = selectedIndex;
        }
        catch
        {
            MicComboBox.SelectedIndex = 0;
        }
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _currentApiKey = ApiKeyBox.Password;
    }

    private async void TestApiKey_Click(object sender, RoutedEventArgs e)
    {
        var key = _currentApiKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            System.Windows.MessageBox.Show("Please enter an API key first.", "Test API Key", System.Windows.MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var baseUrl = BaseUrlBox.Text.Trim().TrimEnd('/');
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                System.Windows.MessageBox.Show("API key and endpoint successfully verified! Connection succeeded.", "Success", System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync();
                System.Windows.MessageBox.Show($"Verification failed with HTTP {response.StatusCode}:\n{body}", "Verification Failed", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Connection failed: {ex.Message}", "Connection Error", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetSystemPrompt_Click(object sender, RoutedEventArgs e)
    {
        SystemPromptBox.Text = PromptTemplates.DefaultSystemPrompt;
    }

    private void ResetContextPrompt_Click(object sender, RoutedEventArgs e)
    {
        ContextPromptBox.Text = PromptTemplates.ContextSynthesisPrompt;
    }

    public void NavigateToRunLog()
    {
        SettingsTabs.SelectedItem = RunLogTab;
        RefreshHistoryGrid();
    }

    private void RefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        RefreshHistoryGrid();
    }

    private void RefreshHistoryGrid()
    {
        if (_appState == null) return;
        HistoryDataGrid.ItemsSource = null;
        HistoryDataGrid.ItemsSource = _appState.History;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_appState == null) return;
        var s = _appState.Settings;

        s.GroqApiKey = _currentApiKey.Trim();
        s.ApiBaseUrl = BaseUrlBox.Text.Trim();
        s.TranscriptionLanguage = LanguageBox.Text.Trim();
        s.PressEnterAfterDictation = PressEnterCheckBox.IsChecked ?? false;
        s.PreserveClipboard = PreserveClipboardCheckBox.IsChecked ?? true;
        s.ContextCaptureEnabled = ContextCaptureCheckBox.IsChecked ?? true;

        s.TranscriptionModel = TranscriptionModelBox.Text.Trim();
        s.PostProcessingModel = PostProcessingModelBox.Text.Trim();
        s.PostProcessingFallbackModel = FallbackModelBox.Text.Trim();
        s.ContextModel = ContextModelBox.Text.Trim();

        s.DoubleTapLockEnabled = DoubleTapCheckBox.IsChecked ?? true;
        s.CustomVocabulary = VocabularyBox.Text.Trim();

        var selectedMic = (MicComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        s.SelectedMicrophoneId = selectedMic ?? string.Empty;

        var customSys = SystemPromptBox.Text.Trim();
        s.CustomSystemPrompt = (customSys == PromptTemplates.DefaultSystemPrompt) ? string.Empty : customSys;

        var customCtx = ContextPromptBox.Text.Trim();
        s.CustomContextPrompt = (customCtx == PromptTemplates.ContextSynthesisPrompt) ? string.Empty : customCtx;

        await _settingsService.SaveSettingsAsync(s);

        StatusMessage.Text = "Settings saved successfully!";
        await Task.Delay(2000);
        StatusMessage.Text = string.Empty;
    }
}
