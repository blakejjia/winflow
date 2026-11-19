using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WinFlow.Core.Services;

public enum ModelKind
{
    Unknown,
    Transcription,
    Chat,
    Vision
}

public record ModelInfo(string Id, ModelKind Kind);

public interface IModelCatalogService
{
    /// <summary>
    /// Fetches GET {baseUrl}/models from the configured OpenAI-compatible endpoint.
    /// Returns an empty list on any failure; callers fall back to free-text entry.
    /// </summary>
    Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct = default);
}

/// <summary>
/// Lists models from the live endpoint so users pick instead of type.
/// The OpenAI /models payload has no capability field, so kinds are inferred
/// from well-known id patterns; unknown models stay selectable everywhere.
/// </summary>
public class ModelCatalogService : IModelCatalogService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsProvider _settingsProvider;

    public ModelCatalogService(HttpClient httpClient, ISettingsProvider settingsProvider)
    {
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
    }

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct = default)
    {
        var settings = _settingsProvider.Current;
        var apiKey = !string.IsNullOrWhiteSpace(settings.TranscriptionApiKey)
            ? settings.TranscriptionApiKey
            : settings.GroqApiKey;
        var baseUrl = settings.ApiBaseUrl.TrimEnd('/');

        if (string.IsNullOrWhiteSpace(baseUrl))
            return Array.Empty<ModelInfo>();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return Array.Empty<ModelInfo>();

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseModels(json);
        }
        catch
        {
            return Array.Empty<ModelInfo>();
        }
    }

    internal static IReadOnlyList<ModelInfo> ParseModels(string json)
    {
        var models = new List<ModelInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return models;

            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idProp))
                    continue;
                var id = idProp.GetString();
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                models.Add(new ModelInfo(id, Classify(id)));
            }
        }
        catch
        {
            // Malformed payload: treat as no models.
        }

        return models
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static ModelKind Classify(string id)
    {
        var lower = id.ToLowerInvariant();

        if (lower.Contains("whisper") || lower.Contains("transcribe") || lower.Contains("distil-whisper"))
            return ModelKind.Transcription;

        bool vision = lower.Contains("vision") || lower.Contains("-vl") || lower.EndsWith("vl")
            || lower.Contains("llava") || lower.Contains("scout") || lower.Contains("maverick")
            || lower.Contains("gpt-4o") || lower.Contains("gpt-4.1") || lower.Contains("gpt-5")
            || lower.Contains("gemini") || lower.Contains("claude") || lower.Contains("pixtral")
            || lower.Contains("qvq") || lower.Contains("qwen3-vl") || lower.Contains("qwen2-vl")
            || lower.Contains("internvl") || lower.Contains("minicpm-v");

        if (vision)
            return ModelKind.Vision;

        // Everything else that looks like a chat model is usable for cleanup.
        return ModelKind.Chat;
    }

    /// <summary>
    /// Models suitable for a slot. Vision models also do text chat, so they appear
    /// in chat slots too; unknown-kind models are always included.
    /// </summary>
    public static IEnumerable<ModelInfo> ForSlot(IEnumerable<ModelInfo> models, ModelKind slot)
    {
        return models.Where(m =>
            m.Kind == ModelKind.Unknown
            || m.Kind == slot
            || (slot == ModelKind.Chat && m.Kind == ModelKind.Vision));
    }
}
