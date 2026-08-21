#if DEBUG
using System.IO;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// The tee's contract is that the WAV and the pipeline see the same stream: a dropped or reordered
/// hop would make the dump a different recording from the one Pia transcribed, which is the only
/// thing the dump is for.
/// </summary>
public class DebugWavTeeAudioCaptureServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "pia-wav-tee-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private sealed class FakeSource : IAudioCaptureSource
    {
        private readonly Channel<float[]> _channel = Channel.CreateUnbounded<float[]>();

        public int SampleRate => 16000;
        public bool IsRunning { get; private set; }
        public ChannelReader<float[]> Reader => _channel.Reader;
        public bool Disposed { get; private set; }
        public int DisposeOrder { get; private set; }
        public static int Sequence;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public void Emit(params float[][] hops)
        {
            foreach (var hop in hops) _channel.Writer.TryWrite(hop);
        }

        public void Complete() => _channel.Writer.TryComplete();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            DisposeOrder = ++Sequence;
            return ValueTask.CompletedTask;
        }
    }

    private static float[] Hop(float start)
    {
        var hop = new float[800];
        for (var i = 0; i < hop.Length; i++) hop[i] = start + i / 100000f;
        return hop;
    }

    private DebugWavTeeAudioCaptureService Create(FakeSource inner, out string path)
    {
        Directory.CreateDirectory(_dir);
        path = Path.Combine(_dir, "dump.wav");
        return new DebugWavTeeAudioCaptureService(
            inner, path, NullLogger<DebugWavTeeAudioCaptureService>.Instance);
    }

    [Fact]
    public async Task ForwardsEveryHopInOrder()
    {
        var inner = new FakeSource();
        await using var tee = Create(inner, out _);
        await tee.StartAsync(TestContext.Current.CancellationToken);

        inner.Emit(Hop(0.1f), Hop(0.2f), Hop(0.3f));
        inner.Complete();

        var seen = new List<float>();
        await foreach (var hop in tee.Reader.ReadAllAsync(TestContext.Current.CancellationToken)) seen.Add(hop[0]);

        Assert.Equal([0.1f, 0.2f, 0.3f], seen);
        Assert.Equal(16000, tee.SampleRate);
    }

    [Fact]
    public async Task WrittenWavRoundTripsToTheSameSamples()
    {
        var inner = new FakeSource();
        var tee = Create(inner, out var path);
        await tee.StartAsync(TestContext.Current.CancellationToken);

        var sent = new[] { Hop(0.25f), Hop(-0.5f) };
        inner.Emit(sent);
        inner.Complete();
        await foreach (var _ in tee.Reader.ReadAllAsync(TestContext.Current.CancellationToken)) { }
        await tee.DisposeAsync();

        using var reader = new AudioFileReader(path);
        var read = new float[sent.Length * 800];
        var got = reader.Read(read, 0, read.Length);

        Assert.Equal(read.Length, got);
        // 16-bit PCM: WaveFileWriter scales by 32767 and truncates while the reader divides by 32768, so
        // the round trip can lose a little over one quantization step. Two is a tight honest bound.
        const double tolerance = 2.0 / 32768;
        for (var i = 0; i < sent.Length; i++)
            for (var j = 0; j < 800; j++)
                Assert.Equal(sent[i][j], read[i * 800 + j], tolerance);
    }

    [Fact]
    public async Task DoubleStopIsSafe()
    {
        var inner = new FakeSource();
        await using var tee = Create(inner, out _);
        await tee.StartAsync(TestContext.Current.CancellationToken);

        await tee.StopAsync(TestContext.Current.CancellationToken);
        await tee.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(inner.IsRunning);
    }

    [Fact]
    public async Task FlushesTheWriterBeforeDisposingTheInnerSource()
    {
        var inner = new FakeSource();
        var tee = Create(inner, out var path);
        await tee.StartAsync(TestContext.Current.CancellationToken);
        inner.Emit(Hop(0.4f));
        inner.Complete();
        await foreach (var _ in tee.Reader.ReadAllAsync(TestContext.Current.CancellationToken)) { }

        await tee.DisposeAsync();

        // A complete WAV means the writer closed before the inner source went away: the header's
        // length fields are only patched up on close.
        using var reader = new WaveFileReader(path);
        Assert.Equal(800, reader.SampleCount);
        Assert.True(inner.Disposed);

        // Idempotent — a second dispose must not throw or reopen anything.
        await tee.DisposeAsync();
    }
}
#endif
