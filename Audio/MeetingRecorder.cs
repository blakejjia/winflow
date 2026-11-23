using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WinFlow.Audio;

/// <summary>
/// Records a meeting on two tracks: the microphone (you) and the WASAPI loopback
/// (everyone else / system audio). Both are converted to 16kHz mono WAV in memory.
/// Recording only runs between explicit Start and Stop — never in the background.
/// </summary>
public interface IMeetingRecorder : IDisposable
{
    bool IsRecording { get; }
    TimeSpan Elapsed { get; }
    /// <summary>Fires true when recording starts, false when it stops (Stop or Dispose). Raised off-lock.</summary>
    event Action<bool>? RecordingChanged;
    void Start(string? microphoneId = null);
    Task<MeetingAudio> StopAsync();
}

public record MeetingAudio(byte[] MicrophoneWav, byte[] SystemWav, TimeSpan Duration);

public class MeetingRecorder : IMeetingRecorder
{
    private readonly object _gate = new();
    private WasapiCapture? _mic;
    private WasapiLoopbackCapture? _loopback;
    private MemoryStream? _micPcm;
    private MemoryStream? _sysPcm;
    private bool _recording;
    private DateTime _startedAt;

    public bool IsRecording { get { lock (_gate) return _recording; } }

    public event Action<bool>? RecordingChanged;

    public TimeSpan Elapsed => IsRecording ? DateTime.UtcNow - _startedAt : TimeSpan.Zero;

    public void Start(string? microphoneId = null)
    {
        lock (_gate)
        {
            if (_recording) return;

            _micPcm = new MemoryStream();
            _sysPcm = new MemoryStream();

            NAudio.CoreAudioApi.MMDevice? device = null;
            if (!string.IsNullOrWhiteSpace(microphoneId))
            {
                try
                {
                    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    device = enumerator.GetDevice(microphoneId);
                }
                catch { device = null; }
            }

            try
            {
                _mic = device != null ? new WasapiCapture(device) : new WasapiCapture();
            }
            finally
            {
                device?.Dispose();
            }
            var micFormat = _mic.WaveFormat;
            var micStream = _micPcm;
            _mic.DataAvailable += (_, a) =>
            {
                if (a.BytesRecorded == 0) return;
                var pcm = WasapiAudioRecorder.ConvertTo16kHzMono16(a.Buffer, a.BytesRecorded, micFormat);
                lock (_gate) { if (_recording) micStream.Write(pcm, 0, pcm.Length); }
            };

            _loopback = new WasapiLoopbackCapture();
            var sysFormat = _loopback.WaveFormat;
            var sysStream = _sysPcm;
            _loopback.DataAvailable += (_, a) =>
            {
                if (a.BytesRecorded == 0) return;
                var pcm = WasapiAudioRecorder.ConvertTo16kHzMono16(a.Buffer, a.BytesRecorded, sysFormat);
                lock (_gate) { if (_recording) sysStream.Write(pcm, 0, pcm.Length); }
            };

            _mic.StartRecording();
            try
            {
                _loopback.StartRecording();
            }
            catch
            {
                // No render endpoint (rare): meeting still records the microphone.
                _loopback.Dispose();
                _loopback = null;
            }

            _startedAt = DateTime.UtcNow;
            _recording = true;
        }
        RecordingChanged?.Invoke(true);
    }

    public Task<MeetingAudio> StopAsync()
    {
        WasapiCapture? mic;
        WasapiLoopbackCapture? loopback;
        byte[] micPcm, sysPcm;
        TimeSpan duration;

        lock (_gate)
        {
            if (!_recording)
            {
                return Task.FromResult(new MeetingAudio(Array.Empty<byte>(), Array.Empty<byte>(), TimeSpan.Zero));
            }
            _recording = false;
            mic = _mic; _mic = null;
            loopback = _loopback; _loopback = null;
            micPcm = _micPcm?.ToArray() ?? Array.Empty<byte>();
            sysPcm = _sysPcm?.ToArray() ?? Array.Empty<byte>();
            _micPcm?.Dispose(); _micPcm = null;
            _sysPcm?.Dispose(); _sysPcm = null;
            duration = DateTime.UtcNow - _startedAt;
        }
        RecordingChanged?.Invoke(false);

        // StopRecording waits for capture threads; keep it off the caller's thread.
        return Task.Run(() =>
        {
            try { mic?.StopRecording(); } catch { } finally { mic?.Dispose(); }
            try { loopback?.StopRecording(); } catch { } finally { loopback?.Dispose(); }

            return new MeetingAudio(
                WavEncoder.EncodePcm16(micPcm, 16000, 1),
                WavEncoder.EncodePcm16(sysPcm, 16000, 1),
                duration);
        });
    }

    public void Dispose()
    {
        WasapiCapture? mic;
        WasapiLoopbackCapture? loopback;
        bool wasRecording;
        lock (_gate)
        {
            wasRecording = _recording;
            _recording = false;
            mic = _mic; _mic = null;
            loopback = _loopback; _loopback = null;
            _micPcm?.Dispose(); _micPcm = null;
            _sysPcm?.Dispose(); _sysPcm = null;
        }
        if (wasRecording) RecordingChanged?.Invoke(false);
        mic?.Dispose();
        loopback?.Dispose();
    }
}
