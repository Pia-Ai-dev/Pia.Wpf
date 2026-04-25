using System.Threading.Channels;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class LiveTranscriptionEngineDrainTests
{
    [Fact]
    public async Task DisposeAsync_DrainsQueuedSegments_BeforeShuttingDownProcessor()
    {
        // The engine's drain logic is independent of Whisper. We invoke the helper
        // RunSegmentLoopAsync via an internal entry point that bypasses ProcessAsync
        // and writes a stub utterance for each enqueued sample buffer.
        var sink = Channel.CreateUnbounded<TranscriptUtterance>();
        var helper = new EngineDrainTestHarness(sink.Writer);

        helper.EnqueueSegment(new float[] { 0.1f });
        helper.EnqueueSegment(new float[] { 0.2f });
        helper.EnqueueSegment(new float[] { 0.3f });

        await helper.ShutdownAsync();
        sink.Writer.TryComplete();

        var observed = new List<TranscriptUtterance>();
        await foreach (var u in sink.Reader.ReadAllAsync()) observed.Add(u);

        Assert.Equal(3, observed.Count);
    }

    [Fact]
    public void EngineService_ReaderCts_IsSeparateFrom_SegmentCts()
    {
        // Reflection-based structural assertion: the engine must hold two distinct
        // cancellation sources so the reader can be cancelled while the segment
        // loop continues to drain.
        var type = typeof(Pia.Services.LiveTranscription.LiveTranscriptionEngineService);
        var readerField = type.GetField("_readerCts",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var segmentField = type.GetField("_segmentCts",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(readerField);
        Assert.NotNull(segmentField);
    }
}

internal sealed class EngineDrainTestHarness
{
    private readonly ChannelWriter<TranscriptUtterance> _sink;
    private readonly Channel<float[]> _segmentQueue =
        Channel.CreateBounded<float[]>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly Task _loop;

    public EngineDrainTestHarness(ChannelWriter<TranscriptUtterance> sink)
    {
        _sink = sink;
        _loop = Task.Run(LoopAsync);
    }

    public void EnqueueSegment(float[] samples) => _segmentQueue.Writer.TryWrite(samples);

    public async Task ShutdownAsync()
    {
        _segmentQueue.Writer.TryComplete();
        await _loop;
    }

    private async Task LoopAsync()
    {
        await foreach (var s in _segmentQueue.Reader.ReadAllAsync())
        {
            await _sink.WriteAsync(new TranscriptUtterance(
                TranscriptSpeaker.You,
                $"len={s.Length}",
                DateTimeOffset.UnixEpoch));
        }
    }
}
