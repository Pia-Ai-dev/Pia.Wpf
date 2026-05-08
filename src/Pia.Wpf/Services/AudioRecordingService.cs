using System.IO;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class AudioRecordingService : IAudioRecordingService
{
    private static readonly TimeSpan StopFlushTimeout = TimeSpan.FromSeconds(3);

    private readonly ILogger<AudioRecordingService> _logger;
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _tempFilePath;

    public AudioRecordingService(ILogger<AudioRecordingService> logger)
    {
        _logger = logger;
    }

    public bool IsRecording => _waveIn is not null;

    public async Task StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (IsRecording)
        {
            // Stale state from a leaked previous session - self-heal so the user is not
            // locked out for the rest of the process lifetime. Logged so we can see
            // which leak paths still fire in the wild.
            _logger.LogWarning("Audio recording state was still active at StartRecording; resetting leaked state before starting a new recording.");
            ForceCleanup();
        }

        _tempFilePath = Path.Combine(Path.GetTempPath(), $"pia_recording_{Guid.NewGuid()}.wav");

        try
        {
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1)
            };

            _writer = new WaveFileWriter(_tempFilePath, _waveIn.WaveFormat);

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;

            _waveIn.StartRecording();
        }
        catch
        {
            ForceCleanup();
            throw;
        }
    }

    public async Task<string> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRecording)
            throw new InvalidOperationException("Not recording");

        try
        {
            _waveIn?.StopRecording();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stopping wave-in raised an exception; will force cleanup.");
        }

        // Bounded wait for OnRecordingStopped to flush the writer. If it never fires
        // (NAudio thread/event quirk, or Close/Dispose threw before _writer was nulled)
        // we fall through and force cleanup, so the singleton never stays stuck in
        // IsRecording=true forever.
        var deadline = DateTime.UtcNow + StopFlushTimeout;
        while (_writer is not null && DateTime.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(50, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (_writer is not null)
        {
            _logger.LogWarning("OnRecordingStopped did not flush the writer within {Timeout}; forcing cleanup.", StopFlushTimeout);
        }

        var filePath = _tempFilePath ?? string.Empty;
        _tempFilePath = null;
        ForceCleanup();

        return filePath;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        }
        catch (ObjectDisposedException)
        {
            // Writer was disposed concurrently during stop; ignore the late buffer.
            return;
        }

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
        try
        {
            _writer?.Close();
            _writer?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close writer in recording-stopped handler.");
        }
        finally
        {
            // Must run regardless of whether Close/Dispose threw, otherwise the
            // StopRecordingAsync wait loop hangs forever on a non-null _writer.
            _writer = null;
        }

        if (_tempFilePath is not null && File.Exists(_tempFilePath))
        {
            RecordingCompleted?.Invoke(this, _tempFilePath);
        }
    }

    private void ForceCleanup()
    {
        if (_writer is not null)
        {
            try
            {
                _writer.Close();
                _writer.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose writer during force cleanup.");
            }
            _writer = null;
        }

        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            try
            {
                _waveIn.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose wave-in during force cleanup.");
            }
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
