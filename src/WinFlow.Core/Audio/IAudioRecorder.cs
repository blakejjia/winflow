namespace WinFlow.Core.Audio;

public record AudioDeviceInfo(string Id, string Name);

public interface IAudioRecorder : IDisposable
{
    bool IsRecording { get; }
    event Action<float>? AudioLevelUpdated;
    void StartRecording(string? deviceId = null);
    Task<byte[]> StopRecordingAsync();
    IEnumerable<AudioDeviceInfo> GetAvailableDevices();
}
