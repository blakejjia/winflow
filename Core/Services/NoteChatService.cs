using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace WinFlow.Core.Services;

public interface INoteChatService
{
    /// <summary>
    /// Streams an answer about one meeting note. The transcript (trimmed to fit)
    /// plus the minutes ground the model; My thoughts are never included.
    /// Yields response text deltas as they arrive (SSE).
    /// </summary>
    IAsyncEnumerable<string> AskAsync(
        MeetingNote note,
        IReadOnlyList<(string Question, string Answer)> history,
        string question,
        CancellationToken ct = default);
}

/// <summary>
/// Single-note Q&amp;A over the configured OpenAI-compatible chat endpoint.
/// Long transcripts are trimmed by relevance: paragraphs are scored against
/// the question's words and the best ones are kept in chronological order.
/// </summary>
public class NoteChatService : INoteChatService
{
    internal const int MaxTranscriptChars = 24000;
    private const int MaxHistoryTurns = 4;

    private readonly HttpClient _httpClient;
    private readonly ISettingsProvider _settingsProvider;

    public NoteChatService(HttpClient httpClient, ISettingsProvider settingsProvider)
    {
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
    }

    public async IAsyncEnumerable<string> AskAsync(
        MeetingNote note,
        IReadOnlyList<(string Question, string Answer)> history,
        string question,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var settings = _settingsProvider.Current;
        if (string.IsNullOrWhiteSpace(settings.GroqApiKey))
        {
            yield return "Add an API key in Settings → AI & models to ask questions about this meeting.";
            yield break;
        }

        var transcript = SelectRelevantTranscript(note.Transcript, question, MaxTranscriptChars);
        var systemPrompt = $"""
You answer questions about one recorded meeting. Ground every answer in the
transcript and minutes below; if the answer is not there, say so plainly.
Answer in the language of the question. Be concise; use markdown when it helps.
The transcript labels [You] for the local speaker and [Others] for everyone else.

MINUTES:
{note.Summary}

TRANSCRIPT:
{transcript}
""";

        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        foreach (var (q, a) in history.TakeLast(MaxHistoryTurns))
        {
            messages.Add(new { role = "user", content = q });
            messages.Add(new { role = "assistant", content = a });
        }
        messages.Add(new { role = "user", content = question });

        var payload = new
        {
            model = string.IsNullOrWhiteSpace(settings.PostProcessingModel) ? "openai/gpt-oss-20b" : settings.PostProcessingModel,
            messages,
            temperature = 0.3,
            max_tokens = 1200,
            stream = true
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{settings.ApiBaseUrl.TrimEnd('/')}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.GroqApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        HttpResponseMessage? response = null;
        string? sendError = null;
        bool cancelled = false;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex) { sendError = ex.Message; }

        if (cancelled) yield break;
        if (sendError != null)
        {
            yield return $"Couldn't reach the model: {sendError}";
            yield break;
        }

        if (response == null) yield break;

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                yield return $"The model request failed ({(int)response.StatusCode}). Check the API key and endpoint in Settings.";
                yield break;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new System.IO.StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:")) continue;
                var data = line["data:".Length..].Trim();
                if (data == "[DONE]") yield break;

                string? delta = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() > 0 &&
                        choices[0].TryGetProperty("delta", out var d) &&
                        d.TryGetProperty("content", out var c))
                    {
                        delta = c.GetString();
                    }
                }
                catch (JsonException) { /* keep-alive or partial frame; skip */ }

                if (!string.IsNullOrEmpty(delta)) yield return delta;
            }
        }
    }

    /// <summary>
    /// Fits the transcript into <paramref name="maxChars"/>. Short transcripts pass
    /// through whole; long ones keep the paragraphs most relevant to the question
    /// (word-overlap score), restored to chronological order. Speaker labels are
    /// carried along with their paragraphs.
    /// </summary>
    internal static string SelectRelevantTranscript(string transcript, string question, int maxChars)
    {
        if (transcript.Length <= maxChars) return transcript;

        var questionWords = Tokenize(question);
        var paragraphs = SplitParagraphs(transcript);
        if (paragraphs.Count == 0) return transcript[..maxChars];

        var scored = paragraphs
            .Select((text, index) => (text, index, score: Score(text, questionWords)))
            .OrderByDescending(p => p.score)
            .ThenBy(p => p.index);

        var chosen = new List<(string text, int index)>();
        int used = 0;
        foreach (var p in scored)
        {
            if (used + p.text.Length + 2 > maxChars) continue;
            chosen.Add((p.text, p.index));
            used += p.text.Length + 2;
        }

        if (chosen.Count == 0) return transcript[..maxChars];
        return string.Join("\n\n", chosen.OrderBy(p => p.index).Select(p => p.text));
    }

    private static List<string> SplitParagraphs(string transcript)
    {
        var result = new List<string>();
        string? speaker = null;
        foreach (var raw in transcript.Split("\n\n"))
        {
            var block = raw.Trim();
            if (block.Length == 0) continue;

            // Keep "[You]" / "[Others]" attached to the paragraph that follows.
            var lines = block.Split('\n');
            if (lines[0].StartsWith('[') && lines[0].EndsWith(']'))
            {
                speaker = lines[0];
                block = string.Join('\n', lines.Skip(1)).Trim();
                if (block.Length == 0) continue;
            }
            result.Add(speaker != null ? speaker + "\n" + block : block);
        }
        return result;
    }

    private static HashSet<string> Tokenize(string text) =>
        text.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', '?', '!', ':', ';', '"', '\'', '(', ')', '[', ']', '。', '，', '？', '！'))
            .Where(w => w.Length > 2)
            .ToHashSet();

    private static int Score(string paragraph, HashSet<string> questionWords)
    {
        if (questionWords.Count == 0) return 0;
        var words = Tokenize(paragraph);
        return words.Count(questionWords.Contains);
    }
}
