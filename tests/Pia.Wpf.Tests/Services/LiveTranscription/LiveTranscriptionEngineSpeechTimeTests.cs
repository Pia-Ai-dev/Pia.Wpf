using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// An utterance's <see cref="TranscriptUtterance.Timestamp"/> is when the recogniser returned, which is
/// speech time plus however long the decode took. Cross-channel echo detection needs the audio's own
/// clock, so the engine dates every segment off its source's epoch and its offset in the stream.
/// </summary>
public class LiveTranscriptionEngineSpeechTimeTests
{
    private const int SampleRate = 16000;

    [Fact]
    public async Task Utterance_IsDatedFromTheSourceEpochAndTheSegmentOffset()
    {
        var epoch = new DateTimeOffset(2026, 9, 1, 9, 15, 0, TimeSpan.Zero);
        var source = new PushableAudioSource { StartedAt = epoch };
        var sink = Channel.CreateUnbounded<TranscriptUtterance>();

        var sut = new LiveTranscriptionEngineService(
            TranscriptSpeaker.You, source, string.Empty, new EchoingTranscriptionEngine(),
            sink.Writer, NullLogger.Instance);
        await sut.StartAsync(TestContext.Current.CancellationToken);

        var frame = SpeechThenSilence();
        source.Push(frame);
        source.Complete();
        await sut.DisposeAsync();
        sink.Writer.TryComplete();

        var utterance = await sink.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(utterance.SpeechStart);
        Assert.NotNull(utterance.SpeechEnd);
        Assert.InRange(
            utterance.SpeechStart!.Value,
            epoch,
            epoch.AddSeconds(frame.Length / (double)SampleRate));
        Assert.Equal(
            utterance.DurationSeconds!.Value,
            (utterance.SpeechEnd!.Value - utterance.SpeechStart.Value).TotalSeconds,
            precision: 6);
    }

    [Fact]
    public async Task UndatedSource_LeavesTheSpeechClockNull()
    {
        var source = new PushableAudioSource();
        var sink = Channel.CreateUnbounded<TranscriptUtterance>();

        var sut = new LiveTranscriptionEngineService(
            TranscriptSpeaker.You, source, string.Empty, new EchoingTranscriptionEngine(),
            sink.Writer, NullLogger.Instance);
        await sut.StartAsync(TestContext.Current.CancellationToken);

        source.Push(SpeechThenSilence());
        source.Complete();
        await sut.DisposeAsync();
        sink.Writer.TryComplete();

        var utterance = await sink.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Null(utterance.SpeechStart);
        Assert.Null(utterance.SpeechEnd);
    }

    /// <summary>Two seconds of tone, then enough silence for the VAD to close the segment inside one frame.</summary>
    private static float[] SpeechThenSilence()
    {
        var frame = new float[SampleRate * 4];
        for (int i = 0; i < SampleRate * 2; i++)
            frame[i] = 0.3f * MathF.Sin(2f * MathF.PI * 440f * i / SampleRate);
        return frame;
    }

    private sealed class EchoingTranscriptionEngine : ITranscriptionEngine
    {
        public Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken)
            => Task.FromResult("und noch einmal bitte");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
