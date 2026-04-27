using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class ConsentGateTests
{
    private static (ConsentStateManager mgr, ConsentGate gate) Build()
    {
        var mgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        var gate = new ConsentGate(mgr, NullLogger<ConsentGate>.Instance);
        return (mgr, gate);
    }

    [Fact]
    public void UnknownSpeaker_ReturnsDrop()
    {
        var (_, gate) = Build();
        Assert.Equal(GateDecision.Drop, gate.Evaluate("Speaker 1"));
    }

    [Fact]
    public void GrantedSpeaker_ReturnsPassToTranscript()
    {
        var (mgr, gate) = Build();
        mgr.GetOrCreate("Speaker 1");
        mgr.MarkPrompted("Speaker 1");
        mgr.RecordClassification("Speaker 1", new ConsentClassification(ConsentDecision.Grant, 0.95f),
            "ja", "v1", "...", "whisper-base");
        Assert.Equal(GateDecision.PassToTranscript, gate.Evaluate("Speaker 1"));
    }

    [Fact]
    public void PromptedSpeaker_ReturnsPassToConsentClassifier()
    {
        var (mgr, gate) = Build();
        mgr.GetOrCreate("Speaker 1");
        mgr.MarkPrompted("Speaker 1");
        Assert.Equal(GateDecision.PassToConsentClassifier, gate.Evaluate("Speaker 1"));
    }

    [Theory]
    [InlineData(ConsentState.Denied)]
    [InlineData(ConsentState.Revoked)]
    [InlineData(ConsentState.Timeout)]
    [InlineData(ConsentState.Ambiguous)]
    public void NonGrantedTerminalStates_ReturnDrop(ConsentState state)
    {
        var (mgr, gate) = Build();
        var entry = mgr.GetOrCreate("Speaker 1");
        entry.State = state;
        Assert.Equal(GateDecision.Drop, gate.Evaluate("Speaker 1"));
    }
}
