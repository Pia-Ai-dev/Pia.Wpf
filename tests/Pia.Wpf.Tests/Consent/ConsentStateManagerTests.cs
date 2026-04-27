using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class ConsentStateManagerTests
{
    [Fact]
    public void NewSpeaker_StartsInUnknown()
    {
        var sut = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        var entry = sut.GetOrCreate("Speaker 1");
        Assert.Equal(ConsentState.Unknown, entry.State);
    }

    [Fact]
    public void TransitionToPrompted_RaisesStateChanged()
    {
        var sut = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        sut.GetOrCreate("Speaker 1");
        var raised = false;
        sut.StateChanged += (_, e) => { if (e.SpeakerLabel == "Speaker 1" && e.NewState == ConsentState.Prompted) raised = true; };

        sut.MarkPrompted("Speaker 1");

        Assert.True(raised);
        Assert.Equal(ConsentState.Prompted, sut.GetOrCreate("Speaker 1").State);
    }

    [Fact]
    public void RecordDecision_Grant_SetsGrantedAndStoresEvidence()
    {
        var sut = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        sut.GetOrCreate("Speaker 1");
        sut.MarkPrompted("Speaker 1");

        sut.RecordClassification("Speaker 1",
            new ConsentClassification(ConsentDecision.Grant, 0.95f),
            transcriptText: "ja",
            promptHash: "v1",
            promptText: "Sind Sie einverstanden?",
            sttModelId: "whisper-base");

        var entry = sut.GetOrCreate("Speaker 1");
        Assert.Equal(ConsentState.Granted, entry.State);
        Assert.NotNull(entry.Evidence);
        Assert.Equal("ja", entry.Evidence!.TranscriptText);
    }

    [Fact]
    public void RecordDecision_Ambiguous_BelowThreshold_SetsAmbiguous()
    {
        var sut = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        sut.GetOrCreate("Speaker 1");
        sut.MarkPrompted("Speaker 1");

        sut.RecordClassification("Speaker 1",
            new ConsentClassification(ConsentDecision.Grant, 0.5f),
            "vielleicht", "v1", "...", "whisper-base");

        Assert.Equal(ConsentState.Ambiguous, sut.GetOrCreate("Speaker 1").State);
    }

    [Fact]
    public void RecordDecision_Deny_SetsDenied()
    {
        var sut = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        sut.GetOrCreate("Speaker 1");
        sut.MarkPrompted("Speaker 1");

        sut.RecordClassification("Speaker 1",
            new ConsentClassification(ConsentDecision.Deny, 0.95f),
            "nein", "v1", "...", "whisper-base");

        Assert.Equal(ConsentState.Denied, sut.GetOrCreate("Speaker 1").State);
    }

    [Fact]
    public void Timeout_AfterPromptWindow_TransitionsToTimeout()
    {
        var clock = new FakeTimeProvider();
        var sut = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, clock)
        {
            PromptTimeout = TimeSpan.FromSeconds(15)
        };
        sut.GetOrCreate("Speaker 1");
        sut.MarkPrompted("Speaker 1");
        clock.Advance(TimeSpan.FromSeconds(16));

        sut.SweepTimeouts();

        Assert.Equal(ConsentState.Timeout, sut.GetOrCreate("Speaker 1").State);
    }

    [Fact]
    public void Revoke_TransitionsToRevoked()
    {
        var sut = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        sut.GetOrCreate("Speaker 1");
        sut.Revoke("Speaker 1");
        Assert.Equal(ConsentState.Revoked, sut.GetOrCreate("Speaker 1").State);
    }

    [Fact]
    public void Rename_PreservesState()
    {
        var sut = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        sut.GetOrCreate("Speaker 1");
        sut.MarkPrompted("Speaker 1");
        sut.RecordClassification("Speaker 1",
            new ConsentClassification(ConsentDecision.Grant, 0.95f),
            "ja", "v1", "...", "whisper-base");

        sut.Rename("Speaker 1", "Alice");

        Assert.Equal(ConsentState.Granted, sut.CurrentState("Alice"));
        Assert.Equal(ConsentState.Unknown, sut.CurrentState("Speaker 1"));
    }
}
