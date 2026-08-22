using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.LiveTranscription;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>Speaker identification runs downstream of the segment queue, so a segment the queue drops
/// is lost to the diarizer as well — and under DropOldest nothing fails, so only the eviction
/// callback can see it.</summary>
public class LiveTranscriptionEngineBackpressureTests
{
    private const int WindowSize = 512;
    private const int QueueCapacity = 8;

    [Fact]
    public async Task EvictedSegments_AreCountedAndCarryTheirStreamPosition()
    {
        var logger = new CapturingLogger<LiveTranscriptionEngineService>();
        var source = new PushableAudioSource();
        using var block = new BlockingTranscriptionEngine();
        var sink = Channel.CreateUnbounded<TranscriptUtterance>();

        await using var sut = new LiveTranscriptionEngineService(
            TranscriptSpeaker.Them, source, string.Empty, block, sink.Writer, logger);
        await sut.StartAsync(TestContext.Current.CancellationToken);

        // One segment per frame. Only QueueCapacity + the one in flight can be retained.
        const int Frames = 12;
        for (int i = 0; i < Frames; i++) source.Push(OneSegmentFrame());
        source.Complete();

        var expected = Frames - (QueueCapacity + 1);
        await Eventually.TrueAsync(
            () => Drops(logger).Count >= expected,
            $"{expected} evicted segments to be reported",
            TestContext.Current.CancellationToken);

        var drops = Drops(logger);
        Assert.Equal(expected, drops.Count);
        Assert.Contains($"dropped={expected}", drops[^1]);
        // The eviction has to say WHERE it happened, or a gap in the journal is unattributable.
        Assert.Contains("start=", drops[0]);

        block.Release();
    }

    [Fact]
    public async Task NoSummaryIsLogged_WhenNothingWasDropped()
    {
        var logger = new CapturingLogger<LiveTranscriptionEngineService>();
        var source = new PushableAudioSource();
        var sink = Channel.CreateUnbounded<TranscriptUtterance>();

        var sut = new LiveTranscriptionEngineService(
            TranscriptSpeaker.Them, source, string.Empty,
            new PassThroughTranscriptionEngine(), sink.Writer, logger);
        await sut.StartAsync(TestContext.Current.CancellationToken);

        source.Push(OneSegmentFrame());
        source.Complete();
        await sut.DisposeAsync();

        Assert.Empty(Drops(logger));
        Assert.DoesNotContain(
            logger.Entries,
            e => e.Message.Contains("segments to transcription backpressure"));
    }

    private static List<string> Drops(CapturingLogger<LiveTranscriptionEngineService> logger) =>
        [.. logger.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("transcription is falling behind"))
            .Select(e => e.Message)];

    /// <summary>Loud enough and long enough to clear the 1.5 s diarization gate, then silent long
    /// enough to close the segment inside this one frame.</summary>
    private static float[] OneSegmentFrame()
    {
        var frame = new float[WindowSize * 64];
        for (int i = 0; i < WindowSize * 32; i++)
            frame[i] = 0.3f * MathF.Sin(2f * MathF.PI * 440f * i / 16000f);
        return frame;
    }
}

internal sealed class PushableAudioSource : IAudioCaptureSource
{
    private readonly Channel<float[]> _channel = Channel.CreateUnbounded<float[]>();

    public int SampleRate => 16000;
    public bool IsRunning => true;
    public ChannelReader<float[]> Reader => _channel.Reader;

    public void Push(float[] frame) => _channel.Writer.TryWrite(frame);
    public void Complete() => _channel.Writer.TryComplete();

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Holds the segment loop on its first segment, which is what makes the queue overflow.</summary>
internal sealed class BlockingTranscriptionEngine : ITranscriptionEngine, IDisposable
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _gate.TrySetResult();

    public async Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken)
    {
        await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return string.Empty;
    }

    public void Dispose() => Release();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class PassThroughTranscriptionEngine : ITranscriptionEngine
{
    public Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken)
        => Task.FromResult(string.Empty);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
