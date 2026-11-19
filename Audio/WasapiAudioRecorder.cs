using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WinFlow.Core.Audio;

namespace WinFlow.Audio;

public class WasapiAudioRecorder : IAudioRecorder
{
    private WasapiCapture? _capture;
    private MemoryStream? _pcmStream;
    private readonly object _lock = new();
    private bool _isRecording;

    public bool IsRecording
    {
        get
        {
            lock (_lock) return _isRecording;
        }
    }

    public event Action<float>? AudioLevelUpdated;

    public IEnumerable<AudioDeviceInfo> GetAvailableDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var endpoint in endpoints)
            {
                devices.Add(new AudioDeviceInfo(endpoint.ID, endpoint.FriendlyName));
            }
        }
        catch
        {
            // Fallback if audio endpoint enumeration fails
        }
        return devices;
    }

    public void StartRecording(string? deviceId = null)
    {
        lock (_lock)
        {
            if (_isRecording)
                return;

            _pcmStream?.Dispose();
            _pcmStream = new MemoryStream();

            MMDevice? selectedDevice = null;
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    selectedDevice = enumerator.GetDevice(deviceId);
                }
                catch
                {
                    selectedDevice = null;
                }
            }

            _capture = selectedDevice != null
                ? new WasapiCapture(selectedDevice)
                : new WasapiCapture();

            var waveFormat = _capture.WaveFormat;

            _capture.DataAvailable += (s, a) =>
            {
                if (a.BytesRecorded == 0) return;

                // Convert incoming buffer to 16kHz 16-bit mono PCM
                var converted = ConvertTo16kHzMono16(a.Buffer, a.BytesRecorded, waveFormat);

                lock (_lock)
                {
                    if (_isRecording && _pcmStream != null)
                    {
                        _pcmStream.Write(converted, 0, converted.Length);
                    }
                }

                var rms = AudioLevelCalculator.CalculateRmsLevel(converted, converted.Length);
                AudioLevelUpdated?.Invoke(rms);
            };

            _capture.StartRecording();
            _isRecording = true;
        }
    }

    public Task<byte[]> StopRecordingAsync()
    {
        lock (_lock)
        {
            if (!_isRecording || _capture == null)
            {
                return Task.FromResult(Array.Empty<byte>());
            }

            _isRecording = false;
            try
            {
                _capture.StopRecording();
            }
            catch
            {
                // ignore
            }

            _capture.Dispose();
            _capture = null;

            var rawPcm = _pcmStream?.ToArray() ?? Array.Empty<byte>();
            _pcmStream?.Dispose();
            _pcmStream = null;

            // Encode raw PCM into valid 16kHz 16-bit mono WAV file
            var wav = WavEncoder.EncodePcm16(rawPcm, 16000, 1);
            return Task.FromResult(wav);
        }
    }

    private static byte[] ConvertTo16kHzMono16(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        // Extract samples as floats
        var channels = format.Channels;
        var sampleRate = format.SampleRate;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        var bitsPerSample = format.BitsPerSample;

        int bytesPerSample = bitsPerSample / 8;
        int totalFrames = bytesRecorded / (channels * bytesPerSample);
        if (totalFrames <= 0) return Array.Empty<byte>();

        var monoSamples = new float[totalFrames];

        for (int frame = 0; frame < totalFrames; frame++)
        {
            float sum = 0f;
            for (int ch = 0; ch < channels; ch++)
            {
                int offset = (frame * channels + ch) * bytesPerSample;
                float sampleValue = 0f;

                if (isFloat && bitsPerSample == 32)
                {
                    sampleValue = BitConverter.ToSingle(buffer, offset);
                }
                else if (bitsPerSample == 16)
                {
                    short val = BitConverter.ToInt16(buffer, offset);
                    sampleValue = val / 32768.0f;
                }
                else if (bitsPerSample == 24)
                {
                    int val = (buffer[offset + 2] << 16) | (buffer[offset + 1] << 8) | buffer[offset];
                    if ((val & 0x800000) != 0) val |= unchecked((int)0xFF000000);
                    sampleValue = val / 8388608.0f;
                }
                sum += sampleValue;
            }
            monoSamples[frame] = sum / channels;
        }

        // Resample from format.SampleRate to 16000Hz
        int targetRate = 16000;
        int targetFrames = (int)Math.Round((double)totalFrames * targetRate / sampleRate);
        if (targetFrames <= 0) return Array.Empty<byte>();

        var pcm16 = new byte[targetFrames * 2];
        for (int i = 0; i < targetFrames; i++)
        {
            double srcIdx = (double)i * sampleRate / targetRate;
            int idx0 = (int)Math.Floor(srcIdx);
            int idx1 = Math.Min(idx0 + 1, totalFrames - 1);
            double frac = srcIdx - idx0;

            float sample = (float)((1.0 - frac) * monoSamples[idx0] + frac * monoSamples[idx1]);
            short s16 = (short)Math.Clamp(Math.Round(sample * 32767.0), short.MinValue, short.MaxValue);

            pcm16[i * 2] = (byte)(s16 & 0xFF);
            pcm16[i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
        }

        return pcm16;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _isRecording = false;
            _capture?.Dispose();
            _capture = null;
            _pcmStream?.Dispose();
            _pcmStream = null;
        }
    }
}
