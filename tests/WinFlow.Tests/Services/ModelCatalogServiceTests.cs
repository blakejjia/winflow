using System.Net;
using System.Net.Http;
using System.Text;
using WinFlow.Core.Models;
using WinFlow.Core.Services;
using Xunit;

namespace WinFlow.Tests.Services;

public class ModelCatalogServiceTests
{
    private const string GroqLikePayload = """
    {
      "object": "list",
      "data": [
        { "id": "whisper-large-v3", "object": "model" },
        { "id": "whisper-large-v3-turbo", "object": "model" },
        { "id": "openai/gpt-oss-20b", "object": "model" },
        { "id": "meta-llama/llama-4-scout-17b-16e-instruct", "object": "model" },
        { "id": "qwen/qwen3-32b", "object": "model" },
        { "id": "playai-tts", "object": "model" }
      ]
    }
    """;

    private class CannedHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = GroqLikePayload;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task GetModelsAsync_ParsesIdsAndSendsAuth()
    {
        var handler = new CannedHandler();
        using var http = new HttpClient(handler);
        var settings = new AppSettings { GroqApiKey = "gsk_synthetic", ApiBaseUrl = "https://api.groq.com/openai/v1/" };
        var service = new ModelCatalogService(http, new FixedSettingsProvider(settings));

        var models = await service.GetModelsAsync();

        Assert.Equal(6, models.Count);
        Assert.Equal("https://api.groq.com/openai/v1/models", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("gsk_synthetic", handler.LastRequest.Headers.Authorization?.Parameter);
        Assert.Contains(models, m => m.Id == "whisper-large-v3" && m.Kind == ModelKind.Transcription);
    }

    [Fact]
    public async Task GetModelsAsync_HttpError_ReturnsEmptyInsteadOfThrowing()
    {
        var handler = new CannedHandler { Status = HttpStatusCode.Unauthorized };
        using var http = new HttpClient(handler);
        var service = new ModelCatalogService(http, new FixedSettingsProvider(new AppSettings()));

        Assert.Empty(await service.GetModelsAsync());
    }

    [Fact]
    public void ParseModels_MalformedJson_ReturnsEmpty()
    {
        Assert.Empty(ModelCatalogService.ParseModels("not json"));
        Assert.Empty(ModelCatalogService.ParseModels("{\"data\": \"oops\"}"));
    }

    [Theory]
    [InlineData("whisper-large-v3", ModelKind.Transcription)]
    [InlineData("distil-whisper-large-v3-en", ModelKind.Transcription)]
    [InlineData("meta-llama/llama-4-scout-17b-16e-instruct", ModelKind.Vision)]
    [InlineData("qwen/qwen2-vl-7b", ModelKind.Vision)]
    [InlineData("gpt-4o", ModelKind.Vision)]
    [InlineData("openai/gpt-oss-20b", ModelKind.Chat)]
    [InlineData("qwen/qwen3-32b", ModelKind.Chat)]
    public void Classify_KnownFamilies(string id, ModelKind expected)
    {
        Assert.Equal(expected, ModelCatalogService.Classify(id));
    }

    [Fact]
    public void ForSlot_TranscriptionGetsWhisper_ChatGetsChatAndVision()
    {
        var models = ModelCatalogService.ParseModels(GroqLikePayload);

        var transcription = ModelCatalogService.ForSlot(models, ModelKind.Transcription).Select(m => m.Id).ToList();
        Assert.Contains("whisper-large-v3", transcription);
        Assert.DoesNotContain("openai/gpt-oss-20b", transcription);

        var chat = ModelCatalogService.ForSlot(models, ModelKind.Chat).Select(m => m.Id).ToList();
        Assert.Contains("openai/gpt-oss-20b", chat);
        Assert.Contains("meta-llama/llama-4-scout-17b-16e-instruct", chat); // vision models chat too
        Assert.Contains("playai-tts", chat); // unknown kind stays selectable everywhere
        Assert.DoesNotContain("whisper-large-v3", chat);

        var vision = ModelCatalogService.ForSlot(models, ModelKind.Vision).Select(m => m.Id).ToList();
        Assert.Contains("meta-llama/llama-4-scout-17b-16e-instruct", vision);
        Assert.DoesNotContain("qwen/qwen3-32b", vision);
    }
}
