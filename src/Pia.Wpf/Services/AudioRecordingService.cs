using System.IO;
using NAudio.Wave;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class AudioRecordingService : IAudioRecordingService
{
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _tempFilePath;

    public bool IsRecording => _waveIn is not null;

    public async Task StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (IsRecording)
            throw new InvalidOperationException("Already recording");

        _tempFilePath = Path.Combine(Path.GetTempPath(), $"pia_recording_{Guid.NewGuid()}.wav");

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1)
        };

        _writer = new WaveFileWriter(_tempFilePath, _waveIn.WaveFormat);

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        try
        {
            _waveIn.StartRecording();
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    public async Task<string> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRecording)
            throw new InvalidOperationException("Not recording");

        _waveIn?.StopRecording();

        while (_writer is not null)
        {
            await Task.Delay(50, cancellationToken);
        }

        var filePath = _tempFilePath ?? string.Empty;
        _tempFilePath = null;
        Cleanup();

        return filePath;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _writer?.Write(e.Buffer, 0, e.BytesRecorded);

        var level = CalculateRmsLevel(e.Buffer, e.BytesRecorded);
        AudioLevelChanged?.Invoke(this, level);
    }

    private static float CalculateRmsLevel(byte[] buffer, int bytesRecorded)
    {
        var sampleCount = bytesRecorded / 2;
        if (sampleCount == 0) return 0f;

        double sumSquares = 0;
        for (var i = 0; i < bytesRecorded; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            var normalized = sample / 32768.0;
            sumSquares += normalized * normalized;
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);
        return (float)Math.Min(1.0, rms * 5);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _writer?.Close();
        _writer?.Dispose();
        _writer = null;

        if (_tempFilePath is not null && File.Exists(_tempFilePath))
        {
            RecordingCompleted?.Invoke(this, _tempFilePath);
        }
    }

    private void Cleanup()
    {
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }
    }

    public bool HasAudioContent(string audioFilePath, float silenceThreshold = 0.01f)
    {
        if (!File.Exists(audioFilePath))
            return false;

        try
        {
            using var reader = new WaveFileReader(audioFilePath);

            if (reader.TotalTime.TotalMilliseconds < 500)
                return false;

            var buffer = new byte[reader.WaveFormat.AverageBytesPerSecond];
            int bytesRead;
            var totalSamples = 0;
            var samplesAboveThreshold = 0;

            while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < bytesRead - 1; i += 2)
                {
                    short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                    var normalized = Math.Abs(sample / 32768.0f);
                    totalSamples++;

                    if (normalized > silenceThreshold)
                        samplesAboveThreshold++;
                }
            }

            if (totalSamples == 0)
                return false;

            var contentRatio = (float)samplesAboveThreshold / totalSamples;
            return contentRatio > 0.01f;
        }
        catch
        {
            return false;
        }
    }

    public event EventHandler<string>? RecordingCompleted;
    public event EventHandler<float>? AudioLevelChanged;
}
