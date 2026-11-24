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

    /// <summary>The user's own notes typed during/after the meeting. Never sent to the model.</summary>
    public string MyThoughts { get; set; } = string.Empty;

    /// <summary>Set when transcription failed; the recording is kept on disk for a manual retry.</summary>
    public string? ErrorMessage { get; set; }
    public string? MicAudioPath { get; set; }
    public string? SystemAudioPath { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanRetry => HasError &&
        (!string.IsNullOrEmpty(MicAudioPath) || !string.IsNullOrEmpty(SystemAudioPath));

    /// <summary>Duration as h:mm:ss (or m:ss under an hour) for list display.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DurationLabel => Duration.TotalHours >= 1
        ? $"{(int)Duration.TotalHours}:{Duration.Minutes:D2}:{Duration.Seconds:D2}"
        : $"{(int)Duration.TotalMinutes}:{Duration.Seconds:D2}";

    /// <summary>Bucket label for the date-grouped notes list ("TODAY", "WED, NOV 18").</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DateGroup
    {
        get
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            var date = CreatedAt.Date;
            if (date == DateTime.Today) return "TODAY";
            if (date == DateTime.Today.AddDays(-1)) return "YESTERDAY";
            return CreatedAt.ToString("ddd, MMM d", culture).ToUpperInvariant();
        }
    }
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
    /// <summary>Re-runs a note whose transcription failed, from the WAVs kept on disk.</summary>
    Task<MeetingNote> RetryNoteAsync(MeetingNote note, IProgress<string>? progress = null, CancellationToken ct = default);
    Task DeleteNoteAsync(Guid id);

    /// <summary>Persists edits to an existing note (title, my thoughts).</summary>
    Task UpdateNoteAsync(MeetingNote note);
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
    private readonly PendingAudioStore _pendingAudio;
    private readonly object _gate = new();
    private List<MeetingNote> _notes = new();

    public MeetingNotesService(
        ITranscriptionService transcription,
        HttpClient httpClient,
        ISettingsProvider settingsProvider,
        string? filePath = null,
        PendingAudioStore? pendingAudioStore = null)
    {
        _transcription = transcription;
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinFlow", "notes.json");
        _pendingAudio = pendingAudioStore ?? new PendingAudioStore();
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
        // Persist both tracks before any network call: a server failure must never
        // eat a whole meeting. Deleted below once the note is fully written.
        var micPath = audio.MicrophoneWav.Length > WavHeaderBytes
            ? _pendingAudio.Save(audio.MicrophoneWav, "meeting-you") : null;
        var sysPath = audio.SystemWav.Length > WavHeaderBytes
            ? _pendingAudio.Save(audio.SystemWav, "meeting-others") : null;

        var note = new MeetingNote
        {
            Duration = audio.Duration,
            MicAudioPath = micPath,
            SystemAudioPath = sysPath
        };

        try
        {
            await FillNoteAsync(note, audio.MicrophoneWav, audio.SystemWav, progress, ct);
        }
        catch (OperationCanceledException)
        {
            _pendingAudio.Delete(micPath);
            _pendingAudio.Delete(sysPath);
            throw;
        }
        catch (Exception ex)
        {
            // Automatic retries are exhausted. Keep the recording and a retryable
            // note stub so the user can re-run it once the server recovers.
            note.ErrorMessage = ex.Message;
            note.Title = "Recording saved — transcription failed";
            note.Summary = "The transcription server failed after 3 tries. Your recording is kept on this PC — click Retry to try again.";
            lock (_gate) _notes.Insert(0, note);
            await SaveAsync();
            NotesChanged?.Invoke();
            return note;
        }

        _pendingAudio.Delete(micPath);
        _pendingAudio.Delete(sysPath);
        note.MicAudioPath = null;
        note.SystemAudioPath = null;
        lock (_gate) _notes.Insert(0, note);
        await SaveAsync();
        NotesChanged?.Invoke();
        return note;
    }

    public async Task<MeetingNote> RetryNoteAsync(MeetingNote note, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!note.CanRetry)
            throw new InvalidOperationException("This note has no saved recording to retry from.");

        var micWav = _pendingAudio.Load(note.MicAudioPath) ?? Array.Empty<byte>();
        var sysWav = _pendingAudio.Load(note.SystemAudioPath) ?? Array.Empty<byte>();
        if (micWav.Length <= WavHeaderBytes && sysWav.Length <= WavHeaderBytes)
            throw new FileNotFoundException("The saved recording is no longer on disk.");

        await FillNoteAsync(note, micWav, sysWav, progress, ct);

        _pendingAudio.Delete(note.MicAudioPath);
        _pendingAudio.Delete(note.SystemAudioPath);
        note.MicAudioPath = null;
        note.SystemAudioPath = null;
        note.ErrorMessage = null;
        await SaveAsync();
        NotesChanged?.Invoke();
        return note;
    }

    /// <summary>Transcribes both tracks and writes title/transcript/summary onto the note.</summary>
    private async Task FillNoteAsync(MeetingNote note, byte[] micWav, byte[] sysWav, IProgress<string>? progress, CancellationToken ct)
    {
        var micText = await TranscribeTrackAsync(micWav, "You", progress, ct);
        var sysText = await TranscribeTrackAsync(sysWav, "Others", progress, ct);

        var transcript = string.Join("\n\n", new[] { micText, sysText }.Where(t => !string.IsNullOrWhiteSpace(t)));
        if (string.IsNullOrWhiteSpace(transcript))
        {
            transcript = "(No speech was detected in this recording.)";
        }

        progress?.Report("Writing the summary…");
        var summary = await SummarizeAsync(transcript, ct);

        note.Title = MakeTitle(summary, transcript);
        note.Transcript = transcript;
        note.Summary = summary;
    }

    public async Task DeleteNoteAsync(Guid id)
    {
        List<MeetingNote> removed;
        lock (_gate)
        {
            removed = _notes.Where(n => n.Id == id).ToList();
            _notes.RemoveAll(n => n.Id == id);
        }
        foreach (var n in removed)
        {
            _pendingAudio.Delete(n.MicAudioPath);
            _pendingAudio.Delete(n.SystemAudioPath);
        }
        await SaveAsync();
        NotesChanged?.Invoke();
    }

    public async Task UpdateNoteAsync(MeetingNote note)
    {
        lock (_gate)
        {
            var existing = _notes.FirstOrDefault(n => n.Id == note.Id);
            if (existing == null) return;
            existing.Title = note.Title;
            existing.MyThoughts = note.MyThoughts;
        }
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

    internal const int SummaryChunkChars = 24000;

    private const string MinutesSystemPrompt = """
You write meeting minutes from a raw transcript. The transcript labels [You] for the
local speaker and [Others] for everyone else on the call. Return only the minutes:
a one-line topic, then short sections for Key points, Decisions, and Action items
(with owners when stated). Use the transcript's language. Never invent facts that
are not in the transcript; if a section has nothing, omit it. No preamble.
""";

    private const string PartialNotesPrompt = """
You condense one part of a long meeting transcript into dense notes for a later
summary pass. Keep every decision, number, name, date and action item; drop filler.
The transcript labels [You] for the local speaker and [Others] for everyone else.
Use the transcript's language. Return plain bullet points, no preamble.
""";

    private const string MergeSystemPrompt = """
You write final meeting minutes from section-by-section notes of one long meeting
(the sections are in chronological order). Merge them into a single set of minutes:
a one-line topic, then short sections for Key points, Decisions, and Action items
(with owners when stated). Deduplicate; keep every decision and commitment. Use the
notes' language. Never invent facts; if a section has nothing, omit it. No preamble.
""";

    private async Task<string> SummarizeAsync(string transcript, CancellationToken ct)
    {
        var settings = _settingsProvider.Current;
        if (string.IsNullOrWhiteSpace(settings.GroqApiKey))
        {
            return "Add an API key in Settings to generate summaries. The full transcript is saved.";
        }

        try
        {
            if (transcript.Length <= SummaryChunkChars)
            {
                return await ChatAsync(MinutesSystemPrompt, transcript, 1500, ct)
                    ?? "Summary unavailable (the model request failed). The full transcript is saved.";
            }

            // Map-reduce: condense each section, then merge — a 2-hour meeting
            // is fully covered instead of truncated at the first 24k chars.
            var sections = SplitIntoSections(transcript, SummaryChunkChars);
            var partials = new List<string>();
            for (int i = 0; i < sections.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var part = await ChatAsync(PartialNotesPrompt, sections[i], 900, ct);
                if (!string.IsNullOrWhiteSpace(part)) partials.Add(part);
            }
            if (partials.Count == 0)
            {
                return "Summary unavailable (the model request failed). The full transcript is saved.";
            }

            var merged = string.Join("\n\n", partials);
            // Guard against pathological meetings: keep the merge input bounded too.
            if (merged.Length > SummaryChunkChars) merged = merged[..SummaryChunkChars];

            return await ChatAsync(MergeSystemPrompt, merged, 1500, ct)
                ?? "Summary unavailable (the model request failed). The full transcript is saved.";
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return "Summary unavailable. The full transcript is saved.";
        }
    }

    /// <summary>Splits on paragraph boundaries near the size cap; never mid-word.</summary>
    internal static List<string> SplitIntoSections(string text, int maxChars)
    {
        var sections = new List<string>();
        int start = 0;
        while (start < text.Length)
        {
            int remaining = text.Length - start;
            if (remaining <= maxChars)
            {
                sections.Add(text[start..]);
                break;
            }
            int cut = text.LastIndexOf("\n\n", start + maxChars, maxChars, StringComparison.Ordinal);
            if (cut <= start) cut = text.LastIndexOf('\n', start + maxChars, maxChars);
            if (cut <= start) cut = start + maxChars; // one giant paragraph: hard cut
            sections.Add(text[start..cut]);
            start = cut;
            while (start < text.Length && text[start] == '\n') start++;
        }
        return sections;
    }

    /// <summary>One non-streaming chat call; null on HTTP failure.</summary>
    private async Task<string?> ChatAsync(string systemPrompt, string userContent, int maxTokens, CancellationToken ct)
    {
        var settings = _settingsProvider.Current;
        var payload = new
        {
            model = string.IsNullOrWhiteSpace(settings.PostProcessingModel) ? "openai/gpt-oss-20b" : settings.PostProcessingModel,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            },
            temperature = 0.2,
            max_tokens = maxTokens
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.ApiBaseUrl.TrimEnd('/')}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.GroqApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim();
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
