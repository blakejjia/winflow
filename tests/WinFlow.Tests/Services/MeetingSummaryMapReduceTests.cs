using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using WinFlow.Audio;
using WinFlow.Core.Models;
using WinFlow.Core.Services;
using Xunit;

namespace WinFlow.Tests.Services;

public class MeetingSummaryMapReduceTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"winflow-notes-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_tempFile); } catch { }
    }

    /// <summary>Transcription that returns a long paragraph per chunk, to build a >24k transcript.</summary>
    private class LongTranscription : ITranscriptionService
    {
        public Task<string> TranscribeAsync(byte[] wavBytes, string? language = null, CancellationToken ct = default)
            => Task.FromResult(string.Join(" ", Enumerable.Repeat("The team discussed the roadmap and budget decisions in detail today.", 200)));
    }

    private class RecordingChatHandler : HttpMessageHandler
    {
        public List<string> SystemPrompts { get; } = new();
        public List<string> UserContents { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var messages = doc.RootElement.GetProperty("messages");
            SystemPrompts.Add(messages[0].GetProperty("content").GetString()!);
            UserContents.Add(messages[1].GetProperty("content").GetString()!);

            var json = """{"choices":[{"message":{"content":"- condensed point"}}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private static byte[] MakeWav(int seconds) =>
        WavEncoder.EncodePcm16(new byte[seconds * 16000 * 2], 16000, 1);

    [Fact]
    public async Task LongMeeting_SummarizesEverySection_ThenMerges()
    {
        var handler = new RecordingChatHandler();
        using var http = new HttpClient(handler);
        var settings = new AppSettings { GroqApiKey = "key" };
        var service = new MeetingNotesService(new LongTranscription(), http, new FixedSettingsProvider(settings), _tempFile);

        // 5 mic chunks x ~13k chars each => ~65k transcript, well past the old 24k cut.
        var audio = new MeetingAudio(MakeWav(300), Array.Empty<byte>(), TimeSpan.FromSeconds(300));
        var note = await service.CreateNoteAsync(audio);

        Assert.True(note.Transcript.Length > MeetingNotesService.SummaryChunkChars);
        // Several condense calls + exactly one final merge call.
        Assert.True(handler.SystemPrompts.Count >= 3);
        Assert.Contains("condense one part", handler.SystemPrompts[0]);
        Assert.Contains("final meeting minutes", handler.SystemPrompts[^1]);
        // Every condense input stayed within the cap; nothing was silently dropped.
        foreach (var input in handler.UserContents.Take(handler.UserContents.Count - 1))
        {
            Assert.True(input.Length <= MeetingNotesService.SummaryChunkChars);
        }
        // The merge saw all partials.
        var mergeInput = handler.UserContents[^1];
        Assert.Equal(handler.SystemPrompts.Count - 1,
            System.Text.RegularExpressions.Regex.Matches(mergeInput, "condensed point").Count);
        Assert.DoesNotContain("truncated", note.Summary);
    }

    [Fact]
    public async Task ShortMeeting_StillSingleSummaryCall()
    {
        var handler = new RecordingChatHandler();
        using var http = new HttpClient(handler);
        var service = new MeetingNotesService(new LongTranscription(), http,
            new FixedSettingsProvider(new AppSettings { GroqApiKey = "key" }), _tempFile);

        var audio = new MeetingAudio(MakeWav(30), Array.Empty<byte>(), TimeSpan.FromSeconds(30));
        await service.CreateNoteAsync(audio);

        Assert.Single(handler.SystemPrompts);
        Assert.Contains("meeting minutes from a raw transcript", handler.SystemPrompts[0]);
    }

    [Fact]
    public void SplitIntoSections_CoversWholeText_OnParagraphBoundaries()
    {
        var paragraph = string.Join(" ", Enumerable.Repeat("word", 500)); // ~2500 chars
        var text = string.Join("\n\n", Enumerable.Repeat(paragraph, 40)); // ~100k

        var sections = MeetingNotesService.SplitIntoSections(text, 24000);

        Assert.True(sections.Count >= 4);
        Assert.All(sections, s => Assert.True(s.Length <= 24000));
        // Rejoining with the paragraph separator reproduces the original.
        Assert.Equal(text, string.Join("\n\n", sections));
    }

    [Fact]
    public void SplitIntoSections_GiantSingleParagraph_HardCuts()
    {
        var text = new string('x', 50000);
        var sections = MeetingNotesService.SplitIntoSections(text, 24000);
        Assert.Equal(text, string.Concat(sections));
        Assert.All(sections, s => Assert.True(s.Length <= 24000));
    }

    [Fact]
    public void DurationLabel_ShowsHours_WhenOverAnHour()
    {
        var note = new MeetingNote { Duration = TimeSpan.FromSeconds(3779) }; // 1:02:59
        Assert.Equal("1:02:59", note.DurationLabel);
        note.Duration = TimeSpan.FromSeconds(125);
        Assert.Equal("2:05", note.DurationLabel);
    }

    [Fact]
    public void DateGroup_LabelsTodayYesterdayAndDates()
    {
        Assert.Equal("TODAY", new MeetingNote { CreatedAt = DateTime.Now }.DateGroup);
        Assert.Equal("YESTERDAY", new MeetingNote { CreatedAt = DateTime.Now.AddDays(-1) }.DateGroup);
        var older = new MeetingNote { CreatedAt = new DateTime(2026, 11, 17, 14, 44, 0) };
        Assert.Equal("TUE, NOV 17", older.DateGroup);
    }
}
