using System.Net;
using System.Net.Http;
using System.Text;
using WinFlow.Core.Models;
using WinFlow.Core.Services;
using Xunit;

namespace WinFlow.Tests.Services;

public class PostProcessingServiceTests
{
    private class MockChatHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastRequestBody { get; private set; } = string.Empty;
        public string ResponseContent { get; set; } = "Hello, world! This is cleaned.";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            var json = $"{{\"choices\":[{{\"message\":{{\"content\":\"{ResponseContent}\"}}}}]}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task CleanupTranscriptAsync_DictationMode_CallsModelWithStandardPrompt()
    {
        var mockHandler = new MockChatHandler { ResponseContent = "Cleaned transcript text." };
        using var httpClient = new HttpClient(mockHandler);
        var settings = new AppSettings
        {
            GroqApiKey = "gsk_test_key",
            PostProcessingModel = "openai/gpt-oss-20b"
        };

        var service = new PostProcessingService(httpClient, settings);
        var result = await service.CleanupTranscriptAsync("um hello world", "User is editing code", "WinFlow", null, CancellationToken.None);

        Assert.Equal("Cleaned transcript text.", result);
        Assert.Contains("RAW_TRANSCRIPTION", mockHandler.LastRequestBody);
        Assert.Contains("um hello world", mockHandler.LastRequestBody);
        Assert.Contains("User is editing code", mockHandler.LastRequestBody);
        Assert.Contains("WinFlow", mockHandler.LastRequestBody);
    }

    [Fact]
    public async Task CleanupTranscriptAsync_EditMode_UsesCommandModePrompt()
    {
        var mockHandler = new MockChatHandler { ResponseContent = "Shortened text" };
        using var httpClient = new HttpClient(mockHandler);
        var settings = new AppSettings
        {
            GroqApiKey = "gsk_test_key",
            PostProcessingModel = "openai/gpt-oss-20b"
        };

        var service = new PostProcessingService(httpClient, settings);
        var result = await service.CleanupTranscriptAsync("make this shorter", "User in editor", "", "A very long sentence here.", CancellationToken.None);

        Assert.Equal("Shortened text", result);
        Assert.Contains("SELECTED_TEXT", mockHandler.LastRequestBody);
        Assert.Contains("A very long sentence here.", mockHandler.LastRequestBody);
        Assert.Contains("VOICE_COMMAND", mockHandler.LastRequestBody);
        Assert.Contains("make this shorter", mockHandler.LastRequestBody);
    }

    [Fact]
    public async Task CleanupTranscriptAsync_EmptyOutputModel_ReturnsEmpty()
    {
        var mockHandler = new MockChatHandler { ResponseContent = "EMPTY" };
        using var httpClient = new HttpClient(mockHandler);
        var settings = new AppSettings { GroqApiKey = "gsk_test_key" };

        var service = new PostProcessingService(httpClient, settings);
        var result = await service.CleanupTranscriptAsync("um uh yeah", "", "", null, CancellationToken.None);

        Assert.Equal(string.Empty, result);
    }
}
