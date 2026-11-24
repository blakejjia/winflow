using System.IO;

namespace WinFlow.Core.Services;

/// <summary>
/// Keeps recorded WAVs on disk in %TEMP%\WinFlow\pending while a run is in flight,
/// so a server failure can never eat a recording. Successful runs delete their file;
/// failed runs keep it and the history item points at it for a manual retry.
/// Files older than a week are swept at startup so the folder cannot grow forever.
/// </summary>
public class PendingAudioStore
{
    private readonly string _dir;

    public PendingAudioStore(string? directory = null)
    {
        _dir = directory ?? Path.Combine(Path.GetTempPath(), "WinFlow", "pending");
    }

    public string DirectoryPath => _dir;

    /// <summary>Saves the WAV and returns its full path, or null if the disk write failed.</summary>
    public string? Save(byte[] wav, string prefix = "dictation")
    {
        try
        {
            System.IO.Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(path, wav);
            return path;
        }
        catch
        {
            // Disk full or folder locked: the in-memory run still proceeds.
            return null;
        }
    }

    public byte[]? Load(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            return File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }
    }

    public void Delete(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort; the startup sweep will pick it up later.
        }
    }

    /// <summary>Removes pending files older than the cutoff. Called once at startup.</summary>
    public void CleanupOlderThan(TimeSpan age)
    {
        try
        {
            if (!System.IO.Directory.Exists(_dir)) return;
            var cutoff = DateTime.UtcNow - age;
            foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "*.wav"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch { /* file in use; next sweep will get it */ }
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
