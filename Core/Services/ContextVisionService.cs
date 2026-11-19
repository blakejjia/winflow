using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WinFlow.Core.Models;
using WinFlow.Core.Prompts;

namespace WinFlow.Core.Services;

public class ContextVisionService : IContextVisionService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsProvider _settingsProvider;

    public ContextVisionService(HttpClient httpClient, AppSettings settings)
        : this(httpClient, new FixedSettingsProvider(settings))
    {
    }

    public ContextVisionService(HttpClient httpClient, ISettingsProvider settingsProvider)
    {
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
    }

    public async Task<string> SynthesizeContextAsync(
        string? screenshotBase64,
        string appName,
        string windowTitle,
        CancellationToken ct = default)
    {
        var settings = _settingsProvider.Current;
        var fallbackSummary = $"User is working in {appName} - {windowTitle}.";
        if (string.IsNullOrWhiteSpace(settings.GroqApiKey))
            return fallbackSummary;

        var baseUrl = settings.ApiBaseUrl.TrimEnd('/');
        var endpoint = $"{baseUrl}/chat/completions";
        var model = !string.IsNullOrWhiteSpace(settings.ContextModel) ? settings.ContextModel : "qwen/qwen3.8-27b";

        var systemPrompt = !string.IsNullOrWhiteSpace(settings.CustomContextPrompt)
            ? settings.CustomContextPrompt
            : PromptTemplates.ContextSynthesisPrompt;

        var textContent = $"Foreground App: {appName}\nWindow Title: {windowTitle}";

        var userContentList = new List<object>
        {
            new { type = "text", text = textContent }
        };

        if (!string.IsNullOrWhiteSpace(screenshotBase64))
        {
            userContentList.Add(new
            {
                type = "image_url",
                image_url = new { url = screenshotBase64 }
            });
        }

        var payload = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContentList }
            },
            max_tokens = 250,
            temperature = 0.2
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.GroqApiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return fallbackSummary;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return !string.IsNullOrWhiteSpace(content) ? content.Trim() : fallbackSummary;
        }
        catch
        {
            return fallbackSummary;
        }
    }
}
