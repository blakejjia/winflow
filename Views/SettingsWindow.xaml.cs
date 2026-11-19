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
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WinFlow.Views;

/// <summary>
/// A pure view over the app-lifetime <see cref="AppState"/>. Closing this window hides it
/// (the tray keeps the pipeline alive); only the tray's Exit command ends the process.
/// </summary>
public partial class SettingsWindow : FluentWindow
{
    private readonly AppState _appState;
    private readonly HttpClient _httpClient;
    private string _currentApiKey = string.Empty;
    private bool _allowClose;
    private bool _loadingSettings;
    private IReadOnlyList<ModelInfo> _models = Array.Empty<ModelInfo>();
    private bool _modelsLoading;
    private System.Collections.ObjectModel.ObservableCollection<VoiceMacro> _macroRows = new();

    private FrameworkElement[] Pages => new FrameworkElement[]
    {
        PageGeneral, PageModels, PageShortcuts, PageVocabulary, PageMacros, PagePrompts, PageRunLog
    };

    public SettingsWindow(AppState appState, HttpClient httpClient)
    {
        InitializeComponent();
        _appState = appState;
        _httpClient = httpClient;
        Loaded += OnWindowLoaded;
        SystemThemeWatcher.Watch(this);
    }

    /// <summary>Called by the app shutdown path so the window can actually close.</summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // Hide to tray instead of killing the dictation pipeline.
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        LoadSettingsIntoUi();
        if (_models.Count == 0 && !_modelsLoading)
        {
            _ = RefreshModelsAsync(quiet: true);
        }
    }

    private void RootNavigation_ItemInvoked(NavigationView sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is not NavigationViewItem item || item.Tag is not string tag)
            return;

        ShowPage(tag);
    }

    /// <summary>
    /// NavigationViewItem.OnClick only notifies the parent NavigationView when
    /// TargetPageType is set; these items host in-place panels instead of pages,
    /// so ItemInvoked never fires for them. Handle Click directly.
    /// </summary>
    private void NavItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is NavigationViewItem item && item.Tag is string tag)
        {
            ShowPage(tag);
        }
    }

    private NavigationViewItem[] NavItems => new[]
    {
        NavGeneral, NavModels, NavShortcuts, NavVocabulary, NavMacros, NavPrompts, NavRunLog
    };

    private void ShowPage(string tag)
    {
        foreach (var page in Pages)
        {
            page.Visibility = page.Name == $"Page{tag}" ? Visibility.Visible : Visibility.Collapsed;
        }

        // Without TargetPageType the NavigationView never updates selection itself.
        foreach (var item in NavItems)
        {
            item.IsActive = Equals(item.Tag, tag);
        }

        if (tag == "RunLog")
        {
            RefreshHistoryGrid();
        }
    }

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

            ThemeComboBox.SelectedIndex = s.Theme switch { "Dark" => 1, "Light" => 2, _ => 0 };

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

            _macroRows = new System.Collections.ObjectModel.ObservableCollection<VoiceMacro>(
                s.VoiceMacros.Select(m => new VoiceMacro { Id = m.Id, Command = m.Command, Payload = m.Payload }));
            if (_macroRows.Count == 0)
            {
                _macroRows.Add(new VoiceMacro()); // one empty row so the fields are visible
            }
            MacrosList.ItemsSource = _macroRows;

            PopulateMicrophones(s.SelectedMicrophoneId);
            RefreshHistoryGrid();
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

    // ---------- Model catalog ----------

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
            // Use the URL/key currently in the editor, not the last saved ones, so
            // "Test connection" and "Refresh list" agree with what the user sees.
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
                ModelsStatusText.Text = "Couldn't load the model list — check the API key and base URL on General. You can still type any model id.";
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

    private void AddMacro_Click(object sender, RoutedEventArgs e)
    {
        _macroRows.Add(new VoiceMacro());
    }

    private void DeleteMacro_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is VoiceMacro macro)
        {
            _macroRows.Remove(macro);
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
        ShowPage("RunLog");
    }

    private void RefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        RefreshHistoryGrid();
    }

    private void RefreshHistoryGrid()
    {
        var history = _appState.History;
        HistoryDataGrid.ItemsSource = null;
        HistoryDataGrid.ItemsSource = history;
        RunLogEmpty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
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

        s.VoiceMacros = _macroRows
            .Where(m => !string.IsNullOrWhiteSpace(m.Command))
            .Select(m => new VoiceMacro { Id = m.Id, Command = m.Command.Trim(), Payload = m.Payload })
            .ToList();

        // Persists AND swaps the live instance shared by every API service.
        await _appState.SaveSettingsAsync(s);
        _appState.ApplyShortcutSettings();

        ShowSnackbar("Saved", "Changes apply to your next dictation — no restart needed.", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
    }

    private void ShowSnackbar(string title, string message, ControlAppearance appearance, SymbolRegular icon)
    {
        var snackbar = new Snackbar(SnackbarPresenter)
        {
            Title = title,
            Content = message,
            Appearance = appearance,
            Icon = new SymbolIcon(icon),
            Timeout = TimeSpan.FromSeconds(4)
        };
        snackbar.Show();
    }
}
