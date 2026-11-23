using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
using WinFlow.Audio;
using WinFlow.Core.Models;
using WinFlow.Core.Pipeline;
using WinFlow.Core.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WinFlow.Views;

/// <summary>
/// One settings pane (General / System / AI / Privacy), hosted inside
/// the main window under a breadcrumb. Pure view over the app-lifetime AppState.
/// </summary>
public partial class SettingsPanel : System.Windows.Controls.UserControl
{
    private readonly AppState _appState;
    private readonly HttpClient _httpClient;
    private string _currentApiKey = string.Empty;
    private bool _loadingSettings;
    private bool _autostartLoadedState;
    private IReadOnlyList<ModelInfo> _models = Array.Empty<ModelInfo>();
    private bool _modelsLoading;

    public SettingsPanel(AppState appState, HttpClient httpClient)
    {
        InitializeComponent();
        _appState = appState;
        _httpClient = httpClient;
        Loaded += OnPanelLoaded;
    }

    /// <summary>Shows one pane by tag: General / System / Ai / Privacy.</summary>
    public void ShowPane(string tag)
    {
        foreach (var pane in new[] { PaneGeneral, PaneSystem, PaneAi, PanePrivacy })
        {
            pane.Visibility = pane.Name == $"Pane{tag}" ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        LoadSettingsIntoUi();
        if (_models.Count == 0 && !_modelsLoading)
        {
            _ = RefreshModelsAsync(quiet: true);
        }
    }

    // ---------- load / save ----------

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox == null || _loadingSettings) return;

        var theme = ThemeComboBox.SelectedIndex switch
        {
            1 => "Dark",
            2 => "Light",
            _ => "System"
        };

        _appState.Settings.Theme = theme;
        ApplyTheme(theme);
    }

    internal static void ApplyTheme(string theme)
    {
        switch (theme)
        {
            case "Dark":
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, updateAccent: true);
                break;
            case "Light":
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, updateAccent: true);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme();
                break;
        }
        WinFlowTheme.Apply();
    }

    private void LoadSettingsIntoUi()
    {
        _loadingSettings = true;
        try
        {
            var s = _appState.Settings;

            _currentApiKey = s.GroqApiKey;
            ApiKeyBox.Password = s.GroqApiKey;
            BaseUrlBox.Text = s.ApiBaseUrl;
            LanguageBox.Text = s.TranscriptionLanguage;
            PressEnterCheckBox.IsChecked = s.PressEnterAfterDictation;
            PreserveClipboardCheckBox.IsChecked = s.PreserveClipboard;
            ContextCaptureCheckBox.IsChecked = s.ContextCaptureEnabled;
            PauseGamesCheckBox.IsChecked = s.PauseInFullscreenGames;

            // The registry Run value is the source of truth (installer task or in-app toggle).
            _autostartLoadedState = AutostartService.IsEnabled();
            AutostartCheckBox.IsChecked = _autostartLoadedState;

            ThemeComboBox.SelectedIndex = s.Theme switch { "Dark" => 1, "Light" => 2, _ => 0 };

            TranscriptionModelBox.Text = s.TranscriptionModel;
            PostProcessingModelBox.Text = s.PostProcessingModel;
            FallbackModelBox.Text = s.PostProcessingFallbackModel;
            ContextModelBox.Text = s.ContextModel;

            PopulateMicrophones(s.SelectedMicrophoneId);
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void PopulateMicrophones(string selectedId)
    {
        MicComboBox.Items.Clear();
        MicComboBox.Items.Add(new ComboBoxItem { Content = "Default microphone", Tag = "" });

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

    /// <summary>Invoked by MainWindow's footer Save button while a settings pane is shown.</summary>
    internal async Task SaveAsync()
    {
        var s = _appState.Settings;

        s.GroqApiKey = _currentApiKey.Trim();
        s.ApiBaseUrl = BaseUrlBox.Text.Trim();
        s.TranscriptionLanguage = LanguageBox.Text.Trim();
        s.PressEnterAfterDictation = PressEnterCheckBox.IsChecked ?? false;
        s.PreserveClipboard = PreserveClipboardCheckBox.IsChecked ?? true;
        s.ContextCaptureEnabled = ContextCaptureCheckBox.IsChecked ?? true;
        s.PauseInFullscreenGames = PauseGamesCheckBox.IsChecked ?? true;

        s.TranscriptionModel = TranscriptionModelBox.Text.Trim();
        s.PostProcessingModel = PostProcessingModelBox.Text.Trim();
        s.PostProcessingFallbackModel = FallbackModelBox.Text.Trim();
        s.ContextModel = ContextModelBox.Text.Trim();

        var selectedMic = (MicComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        s.SelectedMicrophoneId = selectedMic ?? string.Empty;

        await _appState.SaveSettingsAsync(s);
        _appState.ApplyShortcutSettings();

        // Only touch the registry when the toggle actually changed, so a Save from
        // another pane never rewrites the installer's Run value.
        var autostartWanted = AutostartCheckBox.IsChecked ?? false;
        if (autostartWanted != _autostartLoadedState)
        {
            try
            {
                AutostartService.SetEnabled(autostartWanted);
                _autostartLoadedState = autostartWanted;
            }
            catch (Exception ex)
            {
                AutostartCheckBox.IsChecked = _autostartLoadedState; // revert UI to actual state
                ShowSnackbar("Couldn't update startup setting", ex.Message, ControlAppearance.Danger, SymbolRegular.DismissCircle24);
                return; // settings are already saved; keep the failure visible
            }
        }

        ShowSnackbar("Saved", "Changes apply to your next dictation — no restart needed.", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
    }

    // ---------- connection test ----------

    private async void TestApiKey_Click(object sender, RoutedEventArgs e)
    {
        var key = _currentApiKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            ShowSnackbar("Add your API key first", "Paste a Groq key above, then test the connection.", ControlAppearance.Caution, SymbolRegular.Key24);
            return;
        }

        var baseUrl = BaseUrlBox.Text.Trim().TrimEnd('/');
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var response = await _httpClient.SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                ShowSnackbar("Connected", "The key and endpoint work. You're ready to dictate.", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
            }
            else
            {
                ShowSnackbar($"Connection failed ({(int)response.StatusCode})",
                    response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        ? "The endpoint rejected this key. Check for typos or an expired key."
                        : "The endpoint answered with an error. Check the base URL.",
                    ControlAppearance.Danger, SymbolRegular.DismissCircle24);
            }
        }
        catch (Exception ex)
        {
            ShowSnackbar("Couldn't reach the endpoint", ex.Message, ControlAppearance.Danger, SymbolRegular.DismissCircle24);
        }
    }

    // ---------- model catalog ----------

    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        await RefreshModelsAsync(quiet: false);
    }

    private async Task RefreshModelsAsync(bool quiet)
    {
        if (_modelsLoading) return;
        _modelsLoading = true;
        RefreshModelsButton.IsEnabled = false;
        ModelsStatusText.Text = "Fetching models from your endpoint…";

        try
        {
            var preview = new AppSettings
            {
                ApiBaseUrl = BaseUrlBox.Text.Trim(),
                GroqApiKey = _currentApiKey.Trim(),
                TranscriptionApiKey = _appState.Settings.TranscriptionApiKey
            };
            var catalog = new ModelCatalogService(_httpClient, new FixedSettingsProvider(preview));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            _models = await catalog.GetModelsAsync(cts.Token);

            if (_models.Count > 0)
            {
                ModelsStatusText.Text = $"{_models.Count} models available — start typing or open the list to pick one.";
                PopulateModelSuggestions();
                if (!quiet)
                {
                    ShowSnackbar("Model list updated", $"{_models.Count} models loaded from your endpoint.", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                }
            }
            else
            {
                ModelsStatusText.Text = "Couldn't load the model list — check the API key and base URL. You can still type any model id.";
                if (!quiet)
                {
                    ShowSnackbar("No models loaded", "The endpoint didn't return a list. Check the key and URL, or type model ids manually.", ControlAppearance.Caution, SymbolRegular.Warning24);
                }
            }
        }
        finally
        {
            _modelsLoading = false;
            RefreshModelsButton.IsEnabled = true;
        }
    }

    private IEnumerable<string> SuggestionsFor(AutoSuggestBox box)
    {
        var slot = box == TranscriptionModelBox ? ModelKind.Transcription
            : box == ContextModelBox ? ModelKind.Vision
            : ModelKind.Chat;
        return ModelCatalogService.ForSlot(_models, slot).Select(m => m.Id);
    }

    private void PopulateModelSuggestions()
    {
        foreach (var box in new[] { TranscriptionModelBox, PostProcessingModelBox, FallbackModelBox, ContextModelBox })
        {
            box.OriginalItemsSource = SuggestionsFor(box).ToList();
            box.ItemsSource = box.OriginalItemsSource;
        }
    }

    private void ModelBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        var query = sender.Text?.Trim() ?? string.Empty;
        var candidates = SuggestionsFor(sender);
        sender.ItemsSource = query.Length == 0
            ? candidates.ToList()
            : candidates.Where(id => id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        sender.IsSuggestionListOpen = sender.ItemsSource is ICollection<string> { Count: > 0 };
    }

    private void ModelBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string id)
        {
            sender.Text = id;
        }
    }

    private System.Windows.Threading.DispatcherTimer? _infoBarTimer;

    internal void ShowSnackbar(string title, string message, ControlAppearance appearance, SymbolRegular icon)
    {
        StatusInfoBar.Severity = appearance switch
        {
            ControlAppearance.Success => InfoBarSeverity.Success,
            ControlAppearance.Danger => InfoBarSeverity.Error,
            ControlAppearance.Caution => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational
        };
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;

        _infoBarTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _infoBarTimer.Stop();
        _infoBarTimer.Tick -= CloseInfoBar;
        _infoBarTimer.Tick += CloseInfoBar;
        _infoBarTimer.Start();
    }

    private void CloseInfoBar(object? sender, EventArgs e)
    {
        _infoBarTimer?.Stop();
        StatusInfoBar.IsOpen = false;
    }
}
