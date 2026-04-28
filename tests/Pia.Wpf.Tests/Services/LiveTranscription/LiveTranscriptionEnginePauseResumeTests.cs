using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Integration tests for the Strategy-A pause/resume primitives. Requires the Silero VAD
/// model in the local cache; otherwise the test is skipped.
/// </summary>
public sealed class LiveTranscriptionEnginePauseResumeTests
{
    private static bool ModelMissing => !LiveTranscriptionModels.IsSileroVadAvailable();

    private sealed class StubAudioSource : IAudioCaptureSource
    {
        private readonly Channel<float[]> _ch = Channel.CreateUnbounded<float[]>();
        public int SampleRate => 16000;
        public bool IsRunning { get; private set; }
        public ChannelReader<float[]> Reader => _ch.Reader;
        public Task StartAsync(CancellationToken cancellationToken = default) { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { IsRunning = false; _ch.Writer.TryComplete(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { _ch.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    private sealed class StubEngine : ITranscriptionEngine
    {
        public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken = default)
            => Task.FromResult("stub");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Pause_IsIdempotent_AndResumeIsIdempotent()
    {
        if (ModelMissing) return;
        var sink = Channel.CreateUnbounded<TranscriptUtterance>();
        await using var engine = new LiveTranscriptionEngineService(
            TranscriptSpeaker.You,
            new StubAudioSource(),
            new StubEngine(),
            LiveTranscriptionModels.SileroVadModelPath,
            sink.Writer,
            NullLogger.Instance);

        Assert.False(engine.IsPaused);
        await engine.PauseAsync();
        await engine.PauseAsync();
        Assert.True(engine.IsPaused);
        await engine.ResumeAsync();
        await engine.ResumeAsync();
        Assert.False(engine.IsPaused);
    }
}
