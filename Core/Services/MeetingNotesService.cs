using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WinFlow.Audio;

namespace WinFlow.Core.Services;

public class MeetingNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Title { get; set; } = "Untitled meeting";
    public TimeSpan Duration { get; set; }
    public string Transcript { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

public interface IMeetingNotesService
{
    IReadOnlyList<MeetingNote> Notes { get; }
    event Action? NotesChanged;
    Task LoadAsync();

    /// <summary>
    /// Transcribes both tracks in ~60s chunks, merges them into a labeled transcript,
    /// then asks the cleanup model for meeting minutes. Saves locally; never uploads
    /// anything beyond the configured API endpoint.
    /// </summary>
    Task<MeetingNote> CreateNoteAsync(MeetingAudio audio, IProgress<string>? progress = null, CancellationToken ct = default);
    Task DeleteNoteAsync(Guid id);
}

public class MeetingNotesService : IMeetingNotesService
{
    private const int ChunkSeconds = 60;
    private const int BytesPerSecond = 16000 * 2; // 16kHz 16-bit mono
    private const int WavHeaderBytes = 44;

    private readonly ITranscriptionService _transcription;
    private readonly HttpClient _httpClient;
    private readonly ISettingsProvider _settingsProvider;
    private readonly string _filePath;
    private readonly object _gate = new();
    private List<MeetingNote> _notes = new();

    public MeetingNotesService(
        ITranscriptionService transcription,
        HttpClient httpClient,
        ISettingsProvider settingsProvider,
        string? filePath = null)
    {
        _transcription = transcription;
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinFlow", "notes.json");
    }

    public IReadOnlyList<MeetingNote> Notes
    {
        get { lock (_gate) return _notes.ToList(); }
    }

    public event Action? NotesChanged;

    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = await File.ReadAllTextAsync(_filePath);
            var notes = JsonSerializer.Deserialize<List<MeetingNote>>(json);
            if (notes != null)
            {
                lock (_gate) _notes = notes;
                NotesChanged?.Invoke();
            }
        }
        catch { /* corrupt store: start fresh */ }
    }

    public async Task<MeetingNote> CreateNoteAsync(MeetingAudio audio, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var micText = await TranscribeTrackAsync(audio.MicrophoneWav, "You", progress, ct);
        var sysText = await TranscribeTrackAsync(audio.SystemWav, "Others", progress, ct);

        var transcript = string.Join("\n\n", new[] { micText, sysText }.Where(t => !string.IsNullOrWhiteSpace(t)));
        if (string.IsNullOrWhiteSpace(transcript))
        {
            transcript = "(No speech was detected in this recording.)";
        }

        progress?.Report("Writing the summary…");
        var summary = await SummarizeAsync(transcript, ct);

        var note = new MeetingNote
        {
            Title = MakeTitle(summary, transcript),
            Duration = audio.Duration,
            Transcript = transcript,
            Summary = summary
        };

        lock (_gate) _notes.Insert(0, note);
        await SaveAsync();
        NotesChanged?.Invoke();
        return note;
    }

    public async Task DeleteNoteAsync(Guid id)
    {
        lock (_gate) _notes.RemoveAll(n => n.Id == id);
        await SaveAsync();
        NotesChanged?.Invoke();
    }

    private async Task<string> TranscribeTrackAsync(byte[] wav, string speaker, IProgress<string>? progress, CancellationToken ct)
    {
        if (wav.Length <= WavHeaderBytes) return string.Empty;

        var pcmBytes = wav.Length - WavHeaderBytes;
        var chunkBytes = ChunkSeconds * BytesPerSecond;
        var parts = new List<string>();
        int offset = 0;
        int chunkIndex = 0;
        int totalChunks = (int)Math.Ceiling((double)pcmBytes / chunkBytes);

        while (offset < pcmBytes)
        {
            ct.ThrowIfCancellationRequested();
            int take = Math.Min(chunkBytes, pcmBytes - offset);
            progress?.Report($"Transcribing {speaker.ToLowerInvariant()} — part {chunkIndex + 1} of {totalChunks}…");

            var chunkWav = SliceWav(wav, offset, take);
            var text = await _transcription.TranscribeAsync(chunkWav, _settingsProvider.Current.TranscriptionLanguage, ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text.Trim());
            }
            offset += take;
            chunkIndex++;
        }

        return parts.Count == 0 ? string.Empty : $"[{speaker}]\n{string.Join(" ", parts)}";
    }

    /// <summary>Copies a PCM range into a fresh valid WAV (header + slice).</summary>
    internal static byte[] SliceWav(byte[] wav, int pcmOffset, int pcmLength)
    {
        var slice = new byte[pcmLength];
        Buffer.BlockCopy(wav, WavHeaderBytes + pcmOffset, slice, 0, pcmLength);
        return WavEncoder.EncodePcm16(slice, 16000, 1);
    }

    private async Task<string> SummarizeAsync(string transcript, CancellationToken ct)
    {
        var settings = _settingsProvider.Current;
        if (string.IsNullOrWhiteSpace(settings.GroqApiKey))
        {
            return "Add an API key in Settings to generate summaries. The full transcript is saved.";
        }

        // Cap the transcript so a 2-hour meeting doesn't blow the context window.
        const int maxChars = 24000;
        var body = transcript.Length > maxChars
            ? transcript[..maxChars] + "\n…(transcript truncated)"
            : transcript;

        var payload = new
        {
            model = string.IsNullOrWhiteSpace(settings.PostProcessingModel) ? "openai/gpt-oss-20b" : settings.PostProcessingModel,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = """
You write meeting minutes from a raw transcript. The transcript labels [You] for the
local speaker and [Others] for everyone else on the call. Return only the minutes:
a one-line topic, then short sections for Key points, Decisions, and Action items
(with owners when stated). Use the transcript's language. Never invent facts that
are not in the transcript; if a section has nothing, omit it. No preamble.
"""
                },
                new { role = "user", content = body }
            },
            temperature = 0.2,
            max_tokens = 1500
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.ApiBaseUrl.TrimEnd('/')}/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.GroqApiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return "Summary unavailable (the model request failed). The full transcript is saved.";
            }
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim()
                ?? string.Empty;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return "Summary unavailable. The full transcript is saved.";
        }
    }

    private static string MakeTitle(string summary, string transcript)
    {
        var firstLine = summary.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? "Untitled meeting";
        firstLine = firstLine.Trim().TrimStart('#', '*', '-', ' ').Trim();
        return firstLine.Length > 60 ? firstLine[..60] + "…" : firstLine;
    }

    private async Task SaveAsync()
    {
        string json;
        lock (_gate) json = JsonSerializer.Serialize(_notes);
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _filePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch { /* best effort */ }
    }
}
