using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WinFlow.Core.Models;
using WinFlow.Core.Prompts;

namespace WinFlow.Core.Services;

public class PostProcessingService : IPostProcessingService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsProvider _settingsProvider;

    public PostProcessingService(HttpClient httpClient, AppSettings settings)
        : this(httpClient, new FixedSettingsProvider(settings))
    {
    }

    public PostProcessingService(HttpClient httpClient, ISettingsProvider settingsProvider)
    {
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
    }

    public async Task<string> CleanupTranscriptAsync(
        string rawTranscript,
        string contextSummary,
        string customVocabulary,
        string? selectedText = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawTranscript))
            return string.Empty;

        var settings = _settingsProvider.Current;
        var apiKey = settings.GroqApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return rawTranscript;

        var baseUrl = settings.ApiBaseUrl.TrimEnd('/');
        var endpoint = $"{baseUrl}/chat/completions";

        bool isEditMode = !string.IsNullOrWhiteSpace(selectedText);
        string systemPrompt;
        string userMessage;

        if (isEditMode)
        {
            systemPrompt = PromptTemplates.CommandModePrompt;
            userMessage = $"""
SELECTED_TEXT: "{selectedText}"
VOICE_COMMAND: "{rawTranscript}"
CONTEXT: "{contextSummary}"
CUSTOM_VOCABULARY: "{customVocabulary}"
""";
        }
        else
        {
            systemPrompt = PromptTemplates.DefaultSystemPrompt;

            userMessage = $"""
RAW_TRANSCRIPTION: "{rawTranscript}"
CONTEXT: "{contextSummary}"
CUSTOM_VOCABULARY: "{customVocabulary}"
""";
        }

        var primaryModel = !string.IsNullOrWhiteSpace(settings.PostProcessingModel)
            ? settings.PostProcessingModel
            : "openai/gpt-oss-20b";

        var fallbackModel = !string.IsNullOrWhiteSpace(settings.PostProcessingFallbackModel)
            ? settings.PostProcessingFallbackModel
            : "qwen/qwen3.8-27b";

        try
        {
            return await CallModelAsync(endpoint, apiKey, primaryModel, systemPrompt, userMessage, ct);
        }
        catch
        {
            if (!string.Equals(primaryModel, fallbackModel, StringComparison.OrdinalIgnoreCase))
            {
                return await CallModelAsync(endpoint, apiKey, fallbackModel, systemPrompt, userMessage, ct);
            }
            throw;
        }
    }

    private async Task<string> CallModelAsync(
        string endpoint,
        string apiKey,
        string model,
        string systemPrompt,
        string userMessage,
        CancellationToken ct)
    {
        var payload = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            temperature = 0.1,
            max_tokens = 2048
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Post-processing failed with status {response.StatusCode}: {err}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()?.Trim();

        if (string.Equals(content, "EMPTY", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return content ?? string.Empty;
    }
}
