using System.Net;
using System.Net.Http;
using System.Text;
using WinFlow.Core.Models;
using WinFlow.Core.Services;
using Xunit;

namespace WinFlow.Tests.Services;

public class TranscriptionServiceTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string ResponseJson { get; set; } = "{\"text\": \"Hello world testing dictation.\"}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseJson, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task TranscribeAsync_SendsMultipartRequestWithAuthAndModel()
    {
        var mockHandler = new MockHttpMessageHandler();
        using var httpClient = new HttpClient(mockHandler);
        var settings = new AppSettings
        {
            GroqApiKey = "gsk_test_mock_key",
            ApiBaseUrl = "https://api.groq.com/openai/v1",
            TranscriptionModel = "whisper-large-v3"
        };

        var service = new TranscriptionService(httpClient, settings);
        var wavBytes = new byte[100];

        var result = await service.TranscribeAsync(wavBytes, "en", CancellationToken.None);

        Assert.Equal("Hello world testing dictation.", result);
        Assert.NotNull(mockHandler.LastRequest);
        Assert.Equal("Bearer", mockHandler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("gsk_test_mock_key", mockHandler.LastRequest.Headers.Authorization?.Parameter);
        Assert.Equal("https://api.groq.com/openai/v1/audio/transcriptions", mockHandler.LastRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task TranscribeAsync_EmptyAudio_ReturnsEmpty()
    {
        using var httpClient = new HttpClient(new MockHttpMessageHandler());
        var settings = new AppSettings { GroqApiKey = "gsk_test" };
        var service = new TranscriptionService(httpClient, settings);

        var result = await service.TranscribeAsync(Array.Empty<byte>(), "", CancellationToken.None);
        Assert.Equal(string.Empty, result);
    }
}
