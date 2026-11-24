using System.IO;
using WinFlow.Core.Services;
using Xunit;

namespace WinFlow.Tests.Services;

public class PendingAudioStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "WinFlowTests", Guid.NewGuid().ToString("N"));
    private readonly PendingAudioStore _store;

    public PendingAudioStoreTests()
    {
        _store = new PendingAudioStore(_dir);
    }

    [Fact]
    public void SaveLoadDelete_RoundTrips()
    {
        var wav = new byte[] { 1, 2, 3, 4 };
        var path = _store.Save(wav);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(wav, _store.Load(path));

        _store.Delete(path);
        Assert.False(File.Exists(path));
        Assert.Null(_store.Load(path));
    }

    [Fact]
    public void Load_MissingOrNullPath_ReturnsNull()
    {
        Assert.Null(_store.Load(null));
        Assert.Null(_store.Load(Path.Combine(_dir, "nope.wav")));
    }

    [Fact]
    public void CleanupOlderThan_RemovesOnlyOldFiles()
    {
        var oldPath = _store.Save(new byte[] { 1 })!;
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow - TimeSpan.FromDays(8));
        var freshPath = _store.Save(new byte[] { 2 })!;

        _store.CleanupOlderThan(TimeSpan.FromDays(7));

        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(freshPath));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
