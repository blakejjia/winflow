using System.IO;
using System.Text.Json;
using WinFlow.Core.Models;

namespace WinFlow.Core.Services;

/// <summary>
/// Persists dictation history and lifetime usage stats locally (never uploaded).
/// History keeps the last 200 runs for the reading view; stats accumulate forever
/// so Home can show total words / wpm / streak like a personal writing journal.
/// </summary>
public interface IHistoryStore
{
    IReadOnlyList<PipelineHistoryItem> Recent { get; }
    UsageStats Stats { get; }
    void Append(PipelineHistoryItem item);
    /// <summary>Persists changes to an existing item (e.g. a manual retry recovered its text).</summary>
    void Update(PipelineHistoryItem item);
    Task LoadAsync();
}

public class UsageStats
{
    public long TotalWords { get; set; }
    public long TotalRuns { get; set; }
    public double TotalSpeechSeconds { get; set; }
    public List<string> ActiveDays { get; set; } = new(); // yyyy-MM-dd, local time

    public int WordsPerMinute =>
        TotalSpeechSeconds > 30 ? (int)Math.Round(TotalWords / (TotalSpeechSeconds / 60.0)) : 0;

    /// <summary>Consecutive days with at least one dictation, ending today or yesterday.</summary>
    public int CurrentStreak
    {
        get
        {
            var days = ActiveDays.ToHashSet();
            var day = DateTime.Today;
            if (!days.Contains(day.ToString("yyyy-MM-dd")))
            {
                day = day.AddDays(-1); // today not used yet: streak may still be alive from yesterday
            }
            int streak = 0;
            while (days.Contains(day.ToString("yyyy-MM-dd")))
            {
                streak++;
                day = day.AddDays(-1);
            }
            return streak;
        }
    }
}

public class LocalHistoryStore : IHistoryStore
{
    private const int MaxItems = 200;
    private readonly string _filePath;
    private readonly object _gate = new();
    private List<PipelineHistoryItem> _items = new();
    private UsageStats _stats = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public LocalHistoryStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinFlow", "history.json");
    }

    public IReadOnlyList<PipelineHistoryItem> Recent
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public UsageStats Stats
    {
        get { lock (_gate) return _stats; }
    }

    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = await File.ReadAllTextAsync(_filePath);
            var doc = JsonSerializer.Deserialize<StoredHistory>(json);
            if (doc == null) return;
            lock (_gate)
            {
                _items = doc.Items ?? new();
                _stats = doc.Stats ?? new();
            }
        }
        catch
        {
            // Corrupt store: start fresh rather than crash the app.
        }
    }

    public void Append(PipelineHistoryItem item)
    {
        lock (_gate)
        {
            _items.Insert(0, item);
            if (_items.Count > MaxItems) _items.RemoveRange(MaxItems, _items.Count - MaxItems);

            // Failed runs keep their audio for retry but must not count as dictation stats.
            if (string.IsNullOrEmpty(item.ErrorMessage))
            {
                _stats.TotalRuns++;
                _stats.TotalWords += CountWords(item.CleanedText);
                _stats.TotalSpeechSeconds += Math.Max(0, item.DurationSeconds);
                var today = item.Timestamp.ToLocalTime().ToString("yyyy-MM-dd");
                if (!_stats.ActiveDays.Contains(today))
                {
                    _stats.ActiveDays.Add(today);
                    if (_stats.ActiveDays.Count > 400) _stats.ActiveDays.RemoveAt(0);
                }
            }
        }
        _ = SaveAsync();
    }

    public void Update(PipelineHistoryItem item)
    {
        lock (_gate)
        {
            var idx = _items.FindIndex(i => i.Id == item.Id);
            if (idx < 0) return;
            _items[idx] = item;
        }
        _ = SaveAsync();
    }

    internal static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int count = 0;
        bool inWord = false;
        foreach (var ch in text)
        {
            // CJK characters each count as a word; latin runs count per whitespace run.
            if (ch >= 0x4E00 && ch <= 0x9FFF)
            {
                count++;
                inWord = false;
            }
            else if (char.IsWhiteSpace(ch))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                count++;
                inWord = true;
            }
        }
        return count;
    }

    private async Task SaveAsync()
    {
        string json;
        lock (_gate)
        {
            json = JsonSerializer.Serialize(new StoredHistory { Items = _items, Stats = _stats }, JsonOptions);
        }
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _filePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch
        {
            // Best effort; history is a convenience, never a reason to fail a dictation.
        }
    }

    private class StoredHistory
    {
        public List<PipelineHistoryItem>? Items { get; set; }
        public UsageStats? Stats { get; set; }
    }
}
