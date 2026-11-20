using System.Net;
using System.Net.Http;
using System.Text;
using WinFlow.Core.Models;
using WinFlow.Core.Services;
using Xunit;

namespace WinFlow.Tests.Services;

public class NoteChatServiceTests
{
    private class SseHandler : HttpMessageHandler
    {
        public string LastRequestBody { get; private set; } = "";
        public string Sse { get; set; } =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo!\"}}]}\n\n" +
            "data: [DONE]\n\n";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : "";
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Sse, Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private static MeetingNote Note(string transcript = "We agreed to ship on Friday.", string summary = "Ship Friday.") =>
        new() { Title = "Standup", Transcript = transcript, Summary = summary, MyThoughts = "PRIVATE THOUGHT" };

    [Fact]
    public async Task Ask_StreamsDeltas_AndGroundsInTranscript_NotThoughts()
    {
        var handler = new SseHandler();
        using var http = new HttpClient(handler);
        var settings = new AppSettings { GroqApiKey = "test-key" };
        var service = new NoteChatService(http, new FixedSettingsProvider(settings));

        var answer = new StringBuilder();
        await foreach (var delta in service.AskAsync(Note(), Array.Empty<(string, string)>(), "When do we ship?"))
        {
            answer.Append(delta);
        }

        Assert.Equal("Hello!", answer.ToString());
        Assert.Contains("ship on Friday", handler.LastRequestBody);
        Assert.Contains("Ship Friday.", handler.LastRequestBody);
        Assert.DoesNotContain("PRIVATE THOUGHT", handler.LastRequestBody);
        Assert.Contains("When do we ship?", handler.LastRequestBody);
        Assert.Contains("\"stream\":true", handler.LastRequestBody);
    }

    [Fact]
    public async Task Ask_IncludesPriorTurns_AsChatHistory()
    {
        var handler = new SseHandler();
        using var http = new HttpClient(handler);
        var service = new NoteChatService(http, new FixedSettingsProvider(new AppSettings { GroqApiKey = "k" }));

        var history = new List<(string, string)> { ("first question", "first answer") };
        await foreach (var _ in service.AskAsync(Note(), history, "second question")) { }

        Assert.Contains("first question", handler.LastRequestBody);
        Assert.Contains("first answer", handler.LastRequestBody);
        Assert.Contains("second question", handler.LastRequestBody);
    }

    [Fact]
    public async Task Ask_NoApiKey_ReturnsGuidance_WithoutNetwork()
    {
        var handler = new SseHandler();
        using var http = new HttpClient(handler);
        var service = new NoteChatService(http, new FixedSettingsProvider(new AppSettings()));

        var parts = new List<string>();
        await foreach (var delta in service.AskAsync(Note(), Array.Empty<(string, string)>(), "q")) parts.Add(delta);

        Assert.Contains("API key", string.Concat(parts));
        Assert.Equal("", handler.LastRequestBody); // never called
    }

    [Fact]
    public async Task Ask_HttpError_ReportsStatus()
    {
        var handler = new SseHandler { Status = HttpStatusCode.Unauthorized };
        using var http = new HttpClient(handler);
        var service = new NoteChatService(http, new FixedSettingsProvider(new AppSettings { GroqApiKey = "bad" }));

        var parts = new List<string>();
        await foreach (var delta in service.AskAsync(Note(), Array.Empty<(string, string)>(), "q")) parts.Add(delta);

        Assert.Contains("401", string.Concat(parts));
    }

    // ----- relevance selection -----

    [Fact]
    public void SelectRelevant_ShortTranscript_PassesThroughWhole()
    {
        var text = "short transcript";
        Assert.Equal(text, NoteChatService.SelectRelevantTranscript(text, "anything", 1000));
    }

    [Fact]
    public void SelectRelevant_LongTranscript_KeepsQuestionRelevantParagraphs()
    {
        var filler = string.Join("\n\n", Enumerable.Repeat("unrelated chatter about lunch plans and weather", 200));
        var target = "The potassium aptamer binding constant was measured by ITC.";
        var transcript = filler + "\n\n" + target + "\n\n" + filler;

        var selected = NoteChatService.SelectRelevantTranscript(
            transcript, "What was the potassium aptamer binding constant?", NoteChatService.MaxTranscriptChars);

        Assert.Contains("potassium aptamer", selected);
        Assert.True(selected.Length <= NoteChatService.MaxTranscriptChars);
    }

    [Fact]
    public void SelectRelevant_KeepsSpeakerLabelsAttached()
    {
        var filler = string.Join("\n\n", Enumerable.Repeat("filler words about nothing important here", 300));
        var transcript = "[You]\n" + filler + "\n\n[Others]\nThe deadline moved to Tuesday afternoon.";

        var selected = NoteChatService.SelectRelevantTranscript(transcript, "deadline Tuesday", 2000);

        Assert.Contains("[Others]\nThe deadline moved to Tuesday", selected);
    }
}
