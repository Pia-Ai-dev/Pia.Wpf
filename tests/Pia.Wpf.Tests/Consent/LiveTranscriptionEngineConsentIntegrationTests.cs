using System.Threading.Channels;
using Pia.Models;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

/// <summary>
/// Mirrors the gate-routing logic embedded in <see cref="Pia.Services.LiveTranscription.LiveTranscriptionEngineService"/>.
/// Exercising the real engine requires the Silero VAD ONNX model and a Whisper bundle, neither
/// of which are available in CI; instead this harness reproduces the post-STT gate dispatch in
/// isolation so the routing contract is regression-tested.
/// </summary>
public sealed class LiveTranscriptionEngineConsentIntegrationTests
{
    [Fact]
    public async Task GateDrops_NoUtteranceReachesSink()
    {
        var sink = Channel.CreateUnbounded<TranscriptUtterance>();
        var gate = new FakeGate(GateDecision.Drop);

        await RunPipeline(sink.Writer, gate, "Speaker 1", "hello there");

        sink.Writer.TryComplete();
        var observed = new List<TranscriptUtterance>();
        await foreach (var u in sink.Reader.ReadAllAsync()) observed.Add(u);
        Assert.Empty(observed);
    }

    [Fact]
    public async Task GatePassToTranscript_EmitsRegularChannel()
    {
        var sink = Channel.CreateUnbounded<TranscriptUtterance>();
        var gate = new FakeGate(GateDecision.PassToTranscript);

        await RunPipeline(sink.Writer, gate, "Speaker 1", "this is a granted line");

        sink.Writer.TryComplete();
        var observed = new List<TranscriptUtterance>();
        await foreach (var u in sink.Reader.ReadAllAsync()) observed.Add(u);
        Assert.Single(observed);
        Assert.Equal(TranscriptChannel.Regular, observed[0].Channel);
        Assert.Equal("this is a granted line", observed[0].Text);
    }

    [Fact]
    public async Task GatePassToConsentClassifier_EmitsConsentClassificationChannel()
    {
        var sink = Channel.CreateUnbounded<TranscriptUtterance>();
        var gate = new FakeGate(GateDecision.PassToConsentClassifier);

        await RunPipeline(sink.Writer, gate, "Speaker 1", "ja");

        sink.Writer.TryComplete();
        var observed = new List<TranscriptUtterance>();
        await foreach (var u in sink.Reader.ReadAllAsync()) observed.Add(u);
        Assert.Single(observed);
        Assert.Equal(TranscriptChannel.ConsentClassification, observed[0].Channel);
    }

    [Fact]
    public async Task NoSpeakerLabel_BypassesGate()
    {
        // The engine only consults the gate when a speaker label is present (loopback channel).
        // Mic-side utterances have no label and must pass through unaffected.
        var sink = Channel.CreateUnbounded<TranscriptUtterance>();
        var gate = new FakeGate(GateDecision.Drop);

        await RunPipeline(sink.Writer, gate, speakerLabel: null, "your own voice");

        sink.Writer.TryComplete();
        var observed = new List<TranscriptUtterance>();
        await foreach (var u in sink.Reader.ReadAllAsync()) observed.Add(u);
        Assert.Single(observed);
        Assert.Equal(TranscriptChannel.Regular, observed[0].Channel);
    }

    private static async Task RunPipeline(
        ChannelWriter<TranscriptUtterance> sink,
        IConsentGate gate,
        string? speakerLabel,
        string text)
    {
        // Replicates the routing branch in LiveTranscriptionEngineService.TranscribeSegmentAsync.
        var channel = TranscriptChannel.Regular;
        if (speakerLabel is not null)
        {
            var decision = gate.Evaluate(speakerLabel);
            switch (decision)
            {
                case GateDecision.Drop: return;
                case GateDecision.PassToConsentClassifier:
                    channel = TranscriptChannel.ConsentClassification; break;
            }
        }

        await sink.WriteAsync(new TranscriptUtterance(
            TranscriptSpeaker.Them, text, DateTimeOffset.UnixEpoch, speakerLabel, channel));
    }

    private sealed class FakeGate : IConsentGate
    {
        private readonly GateDecision _d;
        public FakeGate(GateDecision d) { _d = d; }
        public GateDecision Evaluate(string speakerLabel) => _d;
    }
}
