using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using WinFlow.Core.Models;

namespace WinFlow.Core.Services;

public class TranscriptionService : ITranscriptionService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsProvider _settingsProvider;

    public TranscriptionService(HttpClient httpClient, AppSettings settings)
        : this(httpClient, new FixedSettingsProvider(settings))
    {
    }

    public TranscriptionService(HttpClient httpClient, ISettingsProvider settingsProvider)
    {
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
    }

    public Task<string> TranscribeAsync(byte[] wavBytes, string? language = null, CancellationToken ct = default)
        => TranscriptionRetry.RunAsync(innerCt => TranscribeOnceAsync(wavBytes, language, innerCt), ct);

    private async Task<string> TranscribeOnceAsync(byte[] wavBytes, string? language, CancellationToken ct)
    {
        if (wavBytes == null || wavBytes.Length == 0)
            return string.Empty;

        var settings = _settingsProvider.Current;
        var apiKey = !string.IsNullOrWhiteSpace(settings.TranscriptionApiKey)
            ? settings.TranscriptionApiKey
            : settings.GroqApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
            return string.Empty;

        var baseUrl = !string.IsNullOrWhiteSpace(settings.TranscriptionApiUrl)
            ? settings.TranscriptionApiUrl.TrimEnd('/')
            : settings.ApiBaseUrl.TrimEnd('/');

        var endpoint = $"{baseUrl}/audio/transcriptions";
        var model = !string.IsNullOrWhiteSpace(settings.TranscriptionModel)
            ? settings.TranscriptionModel
            : "whisper-large-v3";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var content = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(wavBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audioContent, "file", "audio.wav");
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent("json"), "response_format");

        var effectiveLanguage = !string.IsNullOrWhiteSpace(language) ? language : settings.TranscriptionLanguage;
        if (!string.IsNullOrWhiteSpace(effectiveLanguage))
        {
            content.Add(new StringContent(effectiveLanguage), "language");
        }

        request.Content = content;

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Transcription failed with status {response.StatusCode}: {err}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("text", out var textProp))
        {
            return textProp.GetString()?.Trim() ?? string.Empty;
        }

        return string.Empty;
    }
}
