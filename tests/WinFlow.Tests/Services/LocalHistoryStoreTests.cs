using System.IO;
using WinFlow.Core.Models;
using WinFlow.Core.Services;
using Xunit;

namespace WinFlow.Tests.Services;

public class LocalHistoryStoreTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"winflow-test-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_tempFile); } catch { }
    }

    [Fact]
    public async Task Append_PersistsAndReloads()
    {
        var store = new LocalHistoryStore(_tempFile);
        store.Append(new PipelineHistoryItem { CleanedText = "Hello world from dictation" });
        await Task.Delay(150); // fire-and-forget save

        var reloaded = new LocalHistoryStore(_tempFile);
        await reloaded.LoadAsync();

        Assert.Single(reloaded.Recent);
        Assert.Equal("Hello world from dictation", reloaded.Recent[0].CleanedText);
        Assert.Equal(4, reloaded.Stats.TotalWords);
        Assert.Equal(1, reloaded.Stats.TotalRuns);
    }

    [Fact]
    public void CountWords_MixedCjkAndLatin()
    {
        Assert.Equal(0, LocalHistoryStore.CountWords(""));
        Assert.Equal(3, LocalHistoryStore.CountWords("hello brave world"));
        Assert.Equal(4, LocalHistoryStore.CountWords("你好世界")); // each CJK char is a word
        Assert.Equal(5, LocalHistoryStore.CountWords("你好 hello brave world"));
    }

    [Fact]
    public void Streak_CountsConsecutiveDaysEndingToday()
    {
        var stats = new UsageStats();
        var today = DateTime.Today;
        stats.ActiveDays.Add(today.ToString("yyyy-MM-dd"));
        stats.ActiveDays.Add(today.AddDays(-1).ToString("yyyy-MM-dd"));
        stats.ActiveDays.Add(today.AddDays(-2).ToString("yyyy-MM-dd"));
        Assert.Equal(3, stats.CurrentStreak);

        // A gap breaks the streak.
        stats.ActiveDays.Add(today.AddDays(-4).ToString("yyyy-MM-dd"));
        Assert.Equal(3, stats.CurrentStreak);
    }

    [Fact]
    public void Streak_YesterdayOnly_IsStillAlive()
    {
        var stats = new UsageStats();
        stats.ActiveDays.Add(DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd"));
        Assert.Equal(1, stats.CurrentStreak); // today hasn't been used yet
    }

    [Fact]
    public void Wpm_RequiresEnoughSpeech()
    {
        var stats = new UsageStats { TotalWords = 100, TotalSpeechSeconds = 10 };
        Assert.Equal(0, stats.WordsPerMinute); // too little data to be meaningful

        stats.TotalSpeechSeconds = 120;
        Assert.Equal(50, stats.WordsPerMinute);
    }
}
