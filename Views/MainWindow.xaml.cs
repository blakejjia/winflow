using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinFlow.Audio;
using WinFlow.Core.Models;
using WinFlow.Core.Pipeline;
using WinFlow.Core.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WinFlow.Views;

/// <summary>
/// The app's primary window: Home (today + stats), History, Snippets, Words, Notes.
/// Settings live in a separate dialog. Closing hides to tray; the tray's Quit ends
/// the process through App.ShutdownAsync.
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly AppState _appState;
    private readonly HttpClient _httpClient;
    private readonly IMeetingRecorder _meetingRecorder;
    private readonly IMeetingNotesService _notesService;

    private readonly ObservableCollection<VoiceMacro> _macroRows = new();
    private MeetingNote? _selectedNote;
    private SettingsPanel? _settingsPanel;
    private string? _settingsCategory;
    private bool _allowClose;
    private bool _notesBusy;
    private readonly DispatcherTimer _recordClock = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow(AppState appState, HttpClient httpClient,
        IMeetingRecorder meetingRecorder, IMeetingNotesService notesService)
    {
        InitializeComponent();
        _appState = appState;
        _httpClient = httpClient;
        _meetingRecorder = meetingRecorder;
        _notesService = notesService;

        SystemThemeWatcher.Watch(this);
        Loaded += OnWindowLoaded;

        _appState.RunCompleted += OnRunCompleted;
        _notesService.NotesChanged += () => Dispatcher.BeginInvoke(RefreshNotesList);
        _recordClock.Tick += (_, _) =>
        {
            var e = _meetingRecorder.Elapsed;
            RecordStatusText.Text = $"Recording {(int)e.TotalMinutes}:{e.Seconds:D2}";
        };
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        RefreshHome();
        RefreshHistory();
        LoadEditableContent();
        await _notesService.LoadAsync();
        RefreshNotesList();
    }

    // ---------- navigation ----------

    private FrameworkElement[] Pages => new FrameworkElement[]
    {
        PageHome, PageHistory, PageSnippets, PageWords, PageNotes, PageSettings
    };

    private NavigationViewItem[] NavItems => new[] { NavHome, NavHistory, NavSnippets, NavWords, NavNotes, NavSettings };

    private void NavItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is NavigationViewItem item && item.Tag is string tag)
        {
            ShowPage(tag);
        }
    }

    public void ShowPage(string tag)
    {
        foreach (var page in Pages)
        {
            page.Visibility = page.Name == $"Page{tag}" ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var item in NavItems)
        {
            item.IsActive = Equals(item.Tag, tag);
        }

        bool editable = tag is "Snippets" or "Words";
        SaveButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        SaveHintText.Text = editable ? "Changes apply after saving." : "";

        switch (tag)
        {
            case "Home": RefreshHome(); break;
            case "History": RefreshHistory(); break;
            case "Notes": RefreshNotesList(); break;
            case "Settings": ShowSettingsHome(); break;
        }
    }

    public void NavigateToHistory() => ShowPage("History");

    // ---------- home & history ----------

    private void OnRunCompleted(PipelineHistoryItem item)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RefreshHome();
            RefreshHistory();
        });
    }

    private void RefreshHome()
    {
        var hour = DateTime.Now.Hour;
        GreetingText.Text = hour switch
        {
            < 5 => "Still up.",
            < 12 => "Good morning.",
            < 18 => "Good afternoon.",
            _ => "Good evening."
        };

        var store = _appState.HistoryStore;
        var recent = store?.Recent ?? _appState.History;
        var today = recent.Where(h => h.Timestamp.ToLocalTime().Date == DateTime.Today).ToList();
        TodayList.ItemsSource = today;
        TodayEmpty.Visibility = today.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var stats = store?.Stats;
        StatWords.Text = (stats?.TotalWords ?? 0).ToString("N0");
        StatWpm.Text = stats is { WordsPerMinute: > 0 } ? stats.WordsPerMinute.ToString() : "—";
        StatStreak.Text = (stats?.CurrentStreak ?? 0).ToString();
    }

    private void RefreshHistory()
    {
        var items = _appState.HistoryStore?.Recent ?? _appState.History;
        HistoryList.ItemsSource = items;
        HistoryEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PipelineHistoryItem item)
        {
            TrySetClipboard(item.CleanedText);
            ShowSnackbar("Copied", "The text is on your clipboard.", ControlAppearance.Success, SymbolRegular.Copy24);
        }
    }

    private static void TrySetClipboard(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch { /* clipboard busy; not worth failing the UI over */ }
    }

    // ---------- snippets & words ----------

    private void LoadEditableContent()
    {
        var s = _appState.Settings;
        _macroRows.Clear();
        foreach (var m in s.VoiceMacros)
        {
            _macroRows.Add(new VoiceMacro { Id = m.Id, Command = m.Command, Payload = m.Payload });
        }
        MacrosList.ItemsSource = _macroRows;
        VocabularyBox.Text = s.CustomVocabulary;
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

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var s = _appState.Settings;
        s.CustomVocabulary = VocabularyBox.Text.Trim();
        s.VoiceMacros = _macroRows
            .Where(m => !string.IsNullOrWhiteSpace(m.Command))
            .Select(m => new VoiceMacro { Id = m.Id, Command = m.Command.Trim(), Payload = m.Payload })
            .ToList();

        await _appState.SaveSettingsAsync(s);
        _appState.ApplyShortcutSettings();
        ShowSnackbar("Saved", "Your snippets and words apply to the next dictation.", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
    }

    // ---------- notes ----------

    private async void RecordToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_notesBusy) return;

        if (!_meetingRecorder.IsRecording)
        {
            try
            {
                _meetingRecorder.Start(_appState.Settings.SelectedMicrophoneId);
                RecordButton.Content = "Stop";
                RecordButton.Appearance = ControlAppearance.Danger;
                RecordHintText.Text = "Capturing both sides of the call";
                _recordClock.Start();
            }
            catch (Exception ex)
            {
                ShowSnackbar("Couldn't start recording", ex.Message, ControlAppearance.Danger, SymbolRegular.DismissCircle24);
            }
            return;
        }

        // Stop and process.
        _notesBusy = true;
        _recordClock.Stop();
        RecordButton.IsEnabled = false;
        RecordButton.Content = "Working…";
        RecordHintText.Text = "Transcribing and writing minutes";

        try
        {
            var audio = await _meetingRecorder.StopAsync();
            RecordStatusText.Text = "Writing your note…";
            var progress = new Progress<string>(msg => Dispatcher.BeginInvoke(() => RecordStatusText.Text = msg));
            var note = await _notesService.CreateNoteAsync(audio, progress);
            RefreshNotesList();
            SelectNote(note);
            ShowSnackbar("Note ready", "Minutes and transcript saved on this PC.", ControlAppearance.Success, SymbolRegular.Notepad24);
        }
        catch (Exception ex)
        {
            ShowSnackbar("Note failed", ex.Message, ControlAppearance.Danger, SymbolRegular.DismissCircle24);
        }
        finally
        {
            _notesBusy = false;
            RecordButton.IsEnabled = true;
            RecordButton.Content = "Start";
            RecordButton.Appearance = ControlAppearance.Primary;
            RecordStatusText.Text = "Ready to record";
            RecordHintText.Text = "Microphone + system audio";
        }
    }

    private void RefreshNotesList()
    {
        var notes = _notesService.Notes;
        NotesList.ItemsSource = notes;
        NotesEmpty.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NoteItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MeetingNote note)
        {
            SelectNote(note);
        }
    }

    private void SelectNote(MeetingNote note)
    {
        _selectedNote = note;
        NotePlaceholder.Visibility = Visibility.Collapsed;
        NoteTitleText.Text = note.Title;
        NoteSummaryCard.Visibility = Visibility.Visible;
        NoteSummaryText.Text = note.Summary;
        NoteTranscriptLabel.Visibility = Visibility.Visible;
        NoteTranscriptText.Text = note.Transcript;
        CopyNoteButton.Visibility = Visibility.Visible;
        DeleteNoteButton.Visibility = Visibility.Visible;
    }

    private void CopyNote_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNote == null) return;
        TrySetClipboard($"{_selectedNote.Title}\n\n{_selectedNote.Summary}\n\n---\n\n{_selectedNote.Transcript}");
        ShowSnackbar("Copied", "Minutes and transcript are on your clipboard.", ControlAppearance.Success, SymbolRegular.Copy24);
    }

    private async void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNote == null) return;
        await _notesService.DeleteNoteAsync(_selectedNote.Id);
        _selectedNote = null;
        NoteTitleText.Text = "";
        NoteSummaryCard.Visibility = Visibility.Collapsed;
        NoteTranscriptLabel.Visibility = Visibility.Collapsed;
        NoteTranscriptText.Text = "";
        CopyNoteButton.Visibility = Visibility.Collapsed;
        DeleteNoteButton.Visibility = Visibility.Collapsed;
        NotePlaceholder.Visibility = Visibility.Visible;
        RefreshNotesList();
    }

    // ---------- settings (in-navigation, breadcrumb drill-in) ----------

    private static readonly (string Tag, string Label)[] SettingsCategories =
    {
        ("General", "General"),
        ("System", "System"),
        ("Ai", "AI & models"),
        ("Privacy", "Privacy"),
        ("Prompts", "Prompts")
    };

    private void ShowSettingsHome()
    {
        _settingsCategory = null;
        SettingsBreadcrumb.ItemsSource = new[] { "Settings" };
        SettingsHome.Visibility = Visibility.Visible;
        SettingsDetailHost.Visibility = Visibility.Collapsed;
    }

    private void SettingsCategory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag)
        {
            OpenSettingsCategory(tag);
        }
    }

    private void OpenSettingsCategory(string tag)
    {
        _settingsCategory = tag;
        var label = SettingsCategories.FirstOrDefault(c => c.Tag == tag).Label ?? tag;
        SettingsBreadcrumb.ItemsSource = new[] { "Settings", label };

        if (_settingsPanel == null)
        {
            _settingsPanel = new SettingsPanel(_appState, _httpClient);
            SettingsDetailHost.Children.Add(_settingsPanel);
        }
        _settingsPanel.ShowPane(tag);

        SettingsHome.Visibility = Visibility.Collapsed;
        SettingsDetailHost.Visibility = Visibility.Visible;
    }

    private void SettingsBreadcrumb_ItemClicked(object sender, RoutedEventArgs e)
    {
        if (e is not Wpf.Ui.Controls.BreadcrumbBarItemClickedEventArgs args) return;
        if (args.Index == 0)
        {
            ShowSettingsHome();
        }
    }

    internal void ShowSnackbar(string title, string message, ControlAppearance appearance, SymbolRegular icon)
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
