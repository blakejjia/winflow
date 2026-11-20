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
    private readonly ObservableCollection<ChatTurn> _chatTurns = new();
    private INoteChatService? _noteChat;
    private CancellationTokenSource? _askCts;
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
        AnimatePageIn(Pages.First(p => p.Name == $"Page{tag}"));

        bool editable = tag is "Snippets" or "Words"
            || (tag == "Settings" && _settingsCategory != null);
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

    /// <summary>Quick fade + rise so page switches feel continuous, not swapped.</summary>
    private static void AnimatePageIn(FrameworkElement page)
    {
        var transform = new System.Windows.Media.TranslateTransform(0, 8);
        page.RenderTransform = transform;
        page.Opacity = 0;

        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);
        page.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        transform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(8, 0, duration) { EasingFunction = ease });
    }

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
        SnippetsEmpty.Visibility = _macroRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        VocabularyBox.Text = s.CustomVocabulary;
    }

    private void AddMacro_Click(object sender, RoutedEventArgs e)
    {
        _macroRows.Add(new VoiceMacro());
        SnippetsEmpty.Visibility = Visibility.Collapsed;
    }

    private void DeleteMacro_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is VoiceMacro macro)
        {
            _macroRows.Remove(macro);
            SnippetsEmpty.Visibility = _macroRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // A drilled-in settings pane owns saving while it is on screen.
        if (_settingsCategory != null && _settingsPanel != null
            && PageSettings.Visibility == Visibility.Visible)
        {
            await _settingsPanel.SaveAsync();
            return;
        }

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
        var all = _notesService.Notes;
        var query = NotesSearchBox?.Text?.Trim() ?? "";
        var filtered = query.Length == 0
            ? all
            : all.Where(n =>
                n.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                n.Transcript.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                n.Summary.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        var view = new System.Windows.Data.CollectionViewSource { Source = filtered };
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(MeetingNote.DateGroup)));
        // Notes arrive newest-first; keep that order inside and across groups.
        NotesList.ItemsSource = view.View;
        NotesEmpty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NotesNoMatches.Visibility = all.Count > 0 && filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NotesSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshNotesList();

    private void NoteItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MeetingNote note)
        {
            SelectNote(note);
        }
    }

    private void SelectNote(MeetingNote note)
    {
        FlushThoughtsSave();
        _selectedNote = note;
        NotePlaceholder.Visibility = Visibility.Collapsed;
        NoteTitleText.Text = note.Title;
        NoteTitleText.Visibility = Visibility.Visible;
        NoteTitleBox.Visibility = Visibility.Collapsed;
        NoteDateChip.Visibility = Visibility.Visible;
        NoteDateText.Text = note.CreatedAt.ToString("MMM d, yyyy · h:mm tt", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        NoteTabs.Visibility = Visibility.Visible;
        NoteTabsDivider.Visibility = Visibility.Visible;
        NoteSummaryMarkdown.Markdown = note.Summary;
        NoteReadingTimeText.Text = Controls.MarkdownView.ReadingTime(note.Summary);
        NoteTranscriptBubbles.ItemsSource = SplitTranscript(note.Transcript);
        NoteTranscriptMeta.Text =
            $"{(int)note.Duration.TotalHours}:{note.Duration.Minutes:D2}:{note.Duration.Seconds:D2}" +
            $" · {CountWords(note.Transcript):N0} WORDS";
        _suppressThoughtsChange = true;
        NoteThoughtsBox.Text = note.MyThoughts;
        _suppressThoughtsChange = false;
        CopyNoteButton.Visibility = Visibility.Visible;
        ExportNoteButton.Visibility = Visibility.Visible;
        DeleteNoteButton.Visibility = Visibility.Visible;
        _askCts?.Cancel();
        _chatTurns.Clear();
        NoteChatList.ItemsSource = _chatTurns;
        NoteAskBox.Text = "";
        NoteAskArea.Visibility = Visibility.Visible;
        TabSummary.IsChecked = true;
        UpdateNoteTabPanes(); // IsChecked may already be true -> Checked wouldn't fire
    }

    /// <summary>One Q/A pair in the note chat. Answer streams in via PropertyChanged.</summary>
    public sealed class ChatTurn : INotifyPropertyChanged
    {
        private string _answer = "";
        public string Question { get; init; } = "";
        public string Answer
        {
            get => _answer;
            set { _answer = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Answer))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private void NoteAskBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && !System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
        {
            e.Handled = true;
            _ = AskNoteAsync();
        }
    }

    private void NoteAsk_Click(object sender, RoutedEventArgs e) => _ = AskNoteAsync();

    private async Task AskNoteAsync()
    {
        if (_selectedNote == null) return;
        var question = NoteAskBox.Text.Trim();
        if (question.Length == 0) return;
        if (_askCts is { IsCancellationRequested: false }) return; // one question at a time

        _noteChat ??= new NoteChatService(_httpClient, new FixedSettingsProvider(_appState.Settings));
        var history = _chatTurns.Select(t => (t.Question, t.Answer)).ToList();
        var turn = new ChatTurn { Question = question, Answer = "…" };
        _chatTurns.Add(turn);
        NoteAskBox.Text = "";
        NoteAskBox.IsEnabled = false;
        NoteAskButton.IsEnabled = false;
        NoteChatScroll.ScrollToEnd();

        _askCts?.Dispose();
        _askCts = new CancellationTokenSource();
        var answer = new System.Text.StringBuilder();
        try
        {
            await foreach (var delta in _noteChat.AskAsync(_selectedNote, history, question, _askCts.Token))
            {
                answer.Append(delta);
                turn.Answer = answer.ToString();
                NoteChatScroll.ScrollToEnd();
            }
            if (answer.Length == 0) turn.Answer = "(No answer came back — try again.)";
        }
        catch (OperationCanceledException)
        {
            if (answer.Length > 0) turn.Answer = answer.ToString();
            else _chatTurns.Remove(turn);
        }
        catch (Exception ex)
        {
            turn.Answer = $"Something went wrong: {ex.Message}";
        }
        finally
        {
            NoteAskBox.IsEnabled = true;
            NoteAskButton.IsEnabled = true;
            NoteAskBox.Focus();
        }
    }

    private void ClearNoteDetail()
    {
        FlushThoughtsSave();
        _askCts?.Cancel();
        _chatTurns.Clear();
        NoteAskArea.Visibility = Visibility.Collapsed;
        _selectedNote = null;
        NoteTitleText.Text = "";
        NoteDateChip.Visibility = Visibility.Collapsed;
        NoteTabs.Visibility = Visibility.Collapsed;
        NoteTabsDivider.Visibility = Visibility.Collapsed;
        NoteSummaryPane.Visibility = Visibility.Collapsed;
        NoteTranscriptPane.Visibility = Visibility.Collapsed;
        NoteThoughtsPane.Visibility = Visibility.Collapsed;
        NoteSummaryMarkdown.Markdown = "";
        NoteTranscriptBubbles.ItemsSource = null;
        _suppressThoughtsChange = true;
        NoteThoughtsBox.Text = "";
        _suppressThoughtsChange = false;
        CopyNoteButton.Visibility = Visibility.Collapsed;
        ExportNoteButton.Visibility = Visibility.Collapsed;
        DeleteNoteButton.Visibility = Visibility.Collapsed;
        NotePlaceholder.Visibility = Visibility.Visible;
    }

    private void NoteTab_Checked(object sender, RoutedEventArgs e)
    {
        if (NoteSummaryPane == null) return; // early Checked during InitializeComponent
        UpdateNoteTabPanes();
        if (TabThoughts.IsChecked != true) FlushThoughtsSave();
    }

    private void UpdateNoteTabPanes()
    {
        NoteSummaryPane.Visibility = TabSummary.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        NoteTranscriptPane.Visibility = TabTranscript.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        NoteThoughtsPane.Visibility = TabThoughts.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ThoughtsToSummary_Click(object sender, RoutedEventArgs e) => TabSummary.IsChecked = true;

    /// <summary>
    /// Splits the merged transcript into readable bubbles: speaker labels start
    /// a new bubble, and long runs are broken on paragraph boundaries or every
    /// few sentences. Purely visual — the stored transcript is untouched.
    /// </summary>
    internal static List<string> SplitTranscript(string transcript)
    {
        var bubbles = new List<string>();
        if (string.IsNullOrWhiteSpace(transcript)) return bubbles;

        foreach (var rawLine in transcript.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('[')) // "[You]" / "[Others]" speaker label
            {
                bubbles.Add(line);
                continue;
            }

            // Break long runs into ~2-3 sentence bubbles.
            var sentences = System.Text.RegularExpressions.Regex.Split(line, @"(?<=[.!?。！？])\s+");
            var current = new System.Text.StringBuilder();
            int count = 0;
            foreach (var s in sentences)
            {
                if (current.Length > 0) current.Append(' ');
                current.Append(s);
                if (++count >= 3 || current.Length >= 400)
                {
                    bubbles.Add(current.ToString());
                    current.Clear();
                    count = 0;
                }
            }
            if (current.Length > 0) bubbles.Add(current.ToString());
        }
        return bubbles;
    }

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ----- title rename -----

    private void NoteTitle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_selectedNote == null) return;
        NoteTitleBox.Text = _selectedNote.Title;
        NoteTitleText.Visibility = Visibility.Collapsed;
        NoteTitleBox.Visibility = Visibility.Visible;
        NoteTitleBox.Focus();
        NoteTitleBox.SelectAll();
    }

    private void NoteTitleBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { CommitTitleRename(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Escape) { EndTitleRename(); e.Handled = true; }
    }

    private void NoteTitleBox_LostFocus(object sender, RoutedEventArgs e) => CommitTitleRename();

    private async void CommitTitleRename()
    {
        if (_selectedNote == null || NoteTitleBox.Visibility != Visibility.Visible) return;
        var title = NoteTitleBox.Text.Trim();
        EndTitleRename();
        if (title.Length == 0 || title == _selectedNote.Title) return;
        _selectedNote.Title = title;
        NoteTitleText.Text = title;
        await _notesService.UpdateNoteAsync(_selectedNote);
    }

    private void EndTitleRename()
    {
        NoteTitleBox.Visibility = Visibility.Collapsed;
        NoteTitleText.Visibility = Visibility.Visible;
    }

    // ----- my thoughts (debounced autosave) -----

    private bool _suppressThoughtsChange;
    private readonly DispatcherTimer _thoughtsSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool _thoughtsHooked;

    private void NoteThoughts_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressThoughtsChange || _selectedNote == null) return;
        if (!_thoughtsHooked)
        {
            _thoughtsHooked = true;
            _thoughtsSaveTimer.Tick += async (_, _) => await SaveThoughtsAsync();
        }
        _thoughtsSaveTimer.Stop();
        _thoughtsSaveTimer.Start();
    }

    private void FlushThoughtsSave()
    {
        if (_thoughtsSaveTimer.IsEnabled) _ = SaveThoughtsAsync();
    }

    private async Task SaveThoughtsAsync()
    {
        _thoughtsSaveTimer.Stop();
        if (_selectedNote == null) return;
        var text = NoteThoughtsBox.Text;
        if (text == _selectedNote.MyThoughts) return;
        _selectedNote.MyThoughts = text;
        await _notesService.UpdateNoteAsync(_selectedNote);
    }

    private void CopyNote_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNote == null) return;
        TrySetClipboard($"{_selectedNote.Title}\n\n{_selectedNote.Summary}\n\n---\n\n{_selectedNote.Transcript}");
        ShowSnackbar("Copied", "Minutes and transcript are on your clipboard.", ControlAppearance.Success, SymbolRegular.Copy24);
    }

    private void ExportNote_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNote == null) return;
        var note = _selectedNote;
        var safeName = string.Join("_", note.Title.Split(System.IO.Path.GetInvalidFileNameChars())).Trim();
        if (safeName.Length == 0) safeName = "meeting-note";

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export note",
            Filter = "Markdown (*.md)|*.md|Text file (*.txt)|*.txt",
            FileName = safeName + ".md",
            DefaultExt = ".md"
        };
        if (dialog.ShowDialog(this) != true) return;

        // My thoughts are intentionally excluded from exports.
        var content = $"# {note.Title}\n\n{note.CreatedAt:MMM d, yyyy · h:mm tt} · {(int)note.Duration.TotalMinutes} min\n\n{note.Summary}\n\n---\n\n## Transcript\n\n{note.Transcript}\n";
        try
        {
            System.IO.File.WriteAllText(dialog.FileName, content);
            ShowSnackbar("Exported", "The note was saved as Markdown.", ControlAppearance.Success, SymbolRegular.ArrowDownload24);
        }
        catch (Exception ex)
        {
            ShowSnackbar("Export failed", ex.Message, ControlAppearance.Danger, SymbolRegular.DismissCircle24);
        }
    }

    private async void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNote == null) return;
        var id = _selectedNote.Id;
        ClearNoteDetail();
        await _notesService.DeleteNoteAsync(id);
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
        SettingsTitleHome.Visibility = Visibility.Visible;
        SettingsTitleParentLink.Visibility = Visibility.Collapsed;
        SettingsTitleChevron.Visibility = Visibility.Collapsed;
        SettingsTitleCurrent.Visibility = Visibility.Collapsed;
        SettingsHome.Visibility = Visibility.Visible;
        SettingsDetailHost.Visibility = Visibility.Collapsed;
        SaveButton.Visibility = Visibility.Collapsed;
        SaveHintText.Text = "";
        AnimatePageIn(SettingsHome);
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
        SettingsTitleHome.Visibility = Visibility.Collapsed;
        SettingsTitleParentLink.Visibility = Visibility.Visible;
        SettingsTitleChevron.Visibility = Visibility.Visible;
        SettingsTitleCurrent.Text = label;
        SettingsTitleCurrent.Visibility = Visibility.Visible;

        if (_settingsPanel == null)
        {
            _settingsPanel = new SettingsPanel(_appState, _httpClient);
            SettingsDetailHost.Children.Add(_settingsPanel);
        }
        _settingsPanel.ShowPane(tag);

        SettingsHome.Visibility = Visibility.Collapsed;
        SettingsDetailHost.Visibility = Visibility.Visible;
        SaveButton.Visibility = Visibility.Visible;
        SaveHintText.Text = "Changes apply after saving.";
        AnimatePageIn(SettingsDetailHost);
    }

    private void SettingsTitleParent_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsHome();
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
