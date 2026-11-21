using System.IO;
using System.Net.Http;
using WinFlow.Core.Models;
using WinFlow.Core.Services;
using Xunit;

namespace WinFlow.Tests.Services;

public class MeetingNoteUpdateTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"winflow-notes-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_tempFile); } catch { }
    }

    private MeetingNotesService NewService() =>
        new(new NoopTranscription(), new HttpClient(), new FixedSettingsProvider(new AppSettings()), _tempFile);

    private class NoopTranscription : ITranscriptionService
    {
        public Task<string> TranscribeAsync(byte[] wavBytes, string? language = null, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
    }

    [Fact]
    public async Task UpdateNote_PersistsTitleAndThoughts_AcrossReload()
    {
        var service = NewService();
        var audio = new WinFlow.Audio.MeetingAudio(
            WinFlow.Audio.WavEncoder.EncodePcm16(new byte[32000], 16000, 1),
            Array.Empty<byte>(), TimeSpan.FromSeconds(1));
        var created = await service.CreateNoteAsync(audio);

        created.Title = "Renamed meeting";
        created.MyThoughts = "follow up with the team";
        await service.UpdateNoteAsync(created);

        var reloaded = NewService();
        await reloaded.LoadAsync();
        var loaded = Assert.Single(reloaded.Notes);
        Assert.Equal("Renamed meeting", loaded.Title);
        Assert.Equal("follow up with the team", loaded.MyThoughts);
    }

    [Fact]
    public async Task UpdateNote_UnknownId_DoesNotAddOrCreate()
    {
        var service = NewService();
        await service.UpdateNoteAsync(new MeetingNote { Title = "ghost" });
        Assert.Empty(service.Notes);
    }

    [Fact]
    public async Task OldNotesJson_WithoutMyThoughts_LoadsWithEmptyDefault()
    {
        File.WriteAllText(_tempFile,
            """[{"Id":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","Title":"Legacy","Transcript":"t","Summary":"s"}]""");
        var service = NewService();
        await service.LoadAsync();
        var note = Assert.Single(service.Notes);
        Assert.Equal(string.Empty, note.MyThoughts);
    }
}
