using WinFlow.WindowsInterops.TextInsertion;
using Xunit;

namespace WinFlow.Tests.Interops;

public class ClipboardSnapshotTests
{
    [Fact]
    public void ClipboardSnapshot_EmptyEntries_RestoresGracefully()
    {
        var snapshot = new ClipboardSnapshot();
        Assert.NotNull(snapshot.Entries);
        Assert.Empty(snapshot.Entries);

        // Restoring empty snapshot should not throw
        snapshot.Restore();
    }

    [Fact]
    public void ClipboardSnapshot_TextEntry_PreservesContent()
    {
        var snapshot = new ClipboardSnapshot();
        snapshot.Entries.Add(new ClipboardEntry(13 /* CF_UNICODETEXT */, "Previous copied text"));

        Assert.Single(snapshot.Entries);
        Assert.Equal("Previous copied text", snapshot.Entries[0].Data as string);
    }
}
