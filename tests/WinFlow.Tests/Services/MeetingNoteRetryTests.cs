using System.IO;
using System.Net.Http;
using WinFlow.Audio;
using WinFlow.Core.Models;
using WinFlow.Core.Services;
using Xunit;

namespace WinFlow.Tests.Services;

public class MeetingNoteRetryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "WinFlowTests", Guid.NewGuid().ToString("N"));
    private readonly string _notesFile;
    private readonly PendingAudioStore _pending;

    public MeetingNoteRetryTests()
    {
        _notesFile = Path.Combine(_dir, "notes.json");
        _pending = new PendingAudioStore(Path.Combine(_dir, "pending"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private class FlakyTranscription : ITranscriptionService
    {
        public bool Fails { get; set; }
        public int CallCount;

        public Task<string> TranscribeAsync(byte[] wavBytes, string? language = null, CancellationToken ct = default)
        {
            CallCount++;
            if (Fails) throw new HttpRequestException("Transcription failed with status InternalServerError");
            return Task.FromResult("hello from the meeting");
        }
    }

    private static byte[] MakeWav(int seconds)
        => WavEncoder.EncodePcm16(new byte[seconds * 16000 * 2], 16000, 1);

    private MeetingNotesService CreateService(FlakyTranscription transcription)
    {
        // No API key -> summary falls back locally, no network in tests.
        return new MeetingNotesService(transcription, new HttpClient(),
            new FixedSettingsProvider(new AppSettings()), _notesFile, _pending);
    }

    [Fact]
    public async Task CreateNote_ServerFails_KeepsRecordingAndRetryableNote()
    {
        var transcription = new FlakyTranscription { Fails = true };
        var service = CreateService(transcription);

        var audio = new MeetingAudio(MakeWav(2), MakeWav(2), TimeSpan.FromSeconds(2));
        var note = await service.CreateNoteAsync(audio);

        // The failure surfaces as a kept note, not a lost meeting.
        Assert.True(note.HasError);
        Assert.True(note.CanRetry);
        Assert.Single(service.Notes);
        Assert.True(File.Exists(note.MicAudioPath));
        Assert.True(File.Exists(note.SystemAudioPath));

        // Server recovers; retry rebuilds the note from disk and cleans up.
        transcription.Fails = false;
        var micPath = note.MicAudioPath!;
        var sysPath = note.SystemAudioPath!;
        var recovered = await service.RetryNoteAsync(note);

        Assert.False(recovered.HasError);
        Assert.Contains("hello from the meeting", recovered.Transcript);
        Assert.Contains("[You]", recovered.Transcript);
        Assert.Contains("[Others]", recovered.Transcript);
        Assert.False(File.Exists(micPath));
        Assert.False(File.Exists(sysPath));
        Assert.Single(service.Notes);
    }

    [Fact]
    public async Task CreateNote_Success_DeletesPendingWavs()
    {
        var service = CreateService(new FlakyTranscription());
        var note = await service.CreateNoteAsync(new MeetingAudio(MakeWav(1), MakeWav(1), TimeSpan.FromSeconds(1)));

        Assert.False(note.HasError);
        Assert.Null(note.MicAudioPath);
        Assert.False(Directory.Exists(_pending.DirectoryPath)
            && Directory.GetFiles(_pending.DirectoryPath, "*.wav").Length > 0);
    }

    [Fact]
    public async Task DeleteNote_RemovesSavedRecordingToo()
    {
        var transcription = new FlakyTranscription { Fails = true };
        var service = CreateService(transcription);
        var note = await service.CreateNoteAsync(new MeetingAudio(MakeWav(1), MakeWav(1), TimeSpan.FromSeconds(1)));
        var micPath = note.MicAudioPath!;

        await service.DeleteNoteAsync(note.Id);

        Assert.Empty(service.Notes);
        Assert.False(File.Exists(micPath));
    }

    [Fact]
    public async Task RetryNote_RecordingGone_ThrowsClearly()
    {
        var transcription = new FlakyTranscription { Fails = true };
        var service = CreateService(transcription);
        var note = await service.CreateNoteAsync(new MeetingAudio(MakeWav(1), MakeWav(1), TimeSpan.FromSeconds(1)));

        File.Delete(note.MicAudioPath!);
        File.Delete(note.SystemAudioPath!);

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.RetryNoteAsync(note));
    }
}
