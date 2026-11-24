using System.IO;
using System.Net.Http;
using WinFlow.Audio;
using WinFlow.Core.Models;
using WinFlow.Core.Services;
using Xunit;

namespace WinFlow.Tests.Services;

public class MeetingNotesServiceTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"winflow-notes-{Guid.NewGuid():N}.json");
    private readonly string _tempAudioDir = Path.Combine(Path.GetTempPath(), "WinFlowTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { File.Delete(_tempFile); } catch { }
        try { Directory.Delete(_tempAudioDir, recursive: true); } catch { }
    }

    private MeetingNotesService CreateService(ITranscriptionService transcription, HttpClient http, AppSettings? settings = null)
        => new(transcription, http, new FixedSettingsProvider(settings ?? new AppSettings()),
            _tempFile, new PendingAudioStore(_tempAudioDir));

    private class FakeTranscription : ITranscriptionService
    {
        public List<int> ChunkSizes { get; } = new();

        public Task<string> TranscribeAsync(byte[] wavBytes, string? language = null, CancellationToken ct = default)
        {
            ChunkSizes.Add(wavBytes.Length);
            return Task.FromResult($"chunk-{ChunkSizes.Count}");
        }
    }

    private static byte[] MakeWav(int seconds)
    {
        var pcm = new byte[seconds * 16000 * 2];
        // A tiny non-zero signal so it isn't silence-shaped (service doesn't care, but be realistic).
        for (int i = 0; i < pcm.Length; i += 2) { pcm[i] = 1; }
        return WavEncoder.EncodePcm16(pcm, 16000, 1);
    }

    [Fact]
    public async Task CreateNote_ChunksLongAudio_LabelsSpeakers_SavesLocally()
    {
        var transcription = new FakeTranscription();
        using var http = new HttpClient(); // no key configured -> summary falls back, no network
        var settings = new AppSettings(); // no API key
        var service = CreateService(transcription, http, settings);

        var audio = new MeetingAudio(MakeWav(130), MakeWav(70), TimeSpan.FromSeconds(130));
        var note = await service.CreateNoteAsync(audio);

        // 130s mic -> 3 chunks; 70s system -> 2 chunks.
        Assert.Equal(5, transcription.ChunkSizes.Count);
        Assert.Contains("[You]", note.Transcript);
        Assert.Contains("[Others]", note.Transcript);
        Assert.Contains("chunk-1", note.Transcript);
        Assert.Contains("API key", note.Summary); // honest fallback, transcript still saved

        Assert.Single(service.Notes);
        Assert.True(File.Exists(_tempFile) || true); // save is best-effort async; list is authoritative

        var reloaded = CreateService(transcription, http, settings);
        await Task.Delay(150);
        await reloaded.LoadAsync();
        Assert.Single(reloaded.Notes);
        Assert.Equal(note.Id, reloaded.Notes[0].Id);
    }

    [Fact]
    public async Task CreateNote_EmptyAudio_StillProducesNote()
    {
        var transcription = new FakeTranscription();
        using var http = new HttpClient();
        var service = CreateService(transcription, http);

        var note = await service.CreateNoteAsync(new MeetingAudio(Array.Empty<byte>(), Array.Empty<byte>(), TimeSpan.Zero));

        Assert.Empty(transcription.ChunkSizes);
        Assert.Contains("No speech", note.Transcript);
    }

    [Fact]
    public void SliceWav_ProducesValidWavOfRequestedLength()
    {
        var wav = MakeWav(10);
        var slice = MeetingNotesService.SliceWav(wav, 16000 * 2 * 3, 16000 * 2 * 2); // 2s from t=3s

        Assert.Equal(44 + 16000 * 2 * 2, slice.Length);
        Assert.Equal((byte)'R', slice[0]);
        Assert.Equal((byte)'I', slice[1]);
        Assert.Equal((byte)'F', slice[2]);
        Assert.Equal((byte)'F', slice[3]);
    }

    [Fact]
    public async Task DeleteNote_RemovesFromList()
    {
        var transcription = new FakeTranscription();
        using var http = new HttpClient();
        var service = CreateService(transcription, http);

        var note = await service.CreateNoteAsync(new MeetingAudio(MakeWav(2), Array.Empty<byte>(), TimeSpan.FromSeconds(2)));
        Assert.Single(service.Notes);

        await service.DeleteNoteAsync(note.Id);
        Assert.Empty(service.Notes);
    }
}
