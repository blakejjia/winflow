using System.Net.Http.Headers;
using System.Text.Json;
using WinFlow.Core.Models;

namespace WinFlow.Core.Services;

public class TranscriptionService : ITranscriptionService
{
    private readonly HttpClient _httpClient;
    private readonly AppSettings _settings;

    public TranscriptionService(HttpClient httpClient, AppSettings settings)
    {
        _httpClient = httpClient;
        _settings = settings;
    }

    public async Task<string> TranscribeAsync(byte[] wavBytes, string? language = null, CancellationToken ct = default)
    {
        if (wavBytes == null || wavBytes.Length == 0)
            return string.Empty;

        var apiKey = !string.IsNullOrWhiteSpace(_settings.TranscriptionApiKey)
            ? _settings.TranscriptionApiKey
            : _settings.GroqApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
            return string.Empty;

        var baseUrl = !string.IsNullOrWhiteSpace(_settings.TranscriptionApiUrl)
            ? _settings.TranscriptionApiUrl.TrimEnd('/')
            : _settings.ApiBaseUrl.TrimEnd('/');

        var endpoint = $"{baseUrl}/audio/transcriptions";
        var model = !string.IsNullOrWhiteSpace(_settings.TranscriptionModel)
            ? _settings.TranscriptionModel
            : "whisper-large-v3";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var content = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(wavBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audioContent, "file", "audio.wav");
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent("json"), "response_format");

        var effectiveLanguage = !string.IsNullOrWhiteSpace(language) ? language : _settings.TranscriptionLanguage;
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
