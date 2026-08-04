using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Tests.Consent;

/// <summary>
/// Measures the real state-machine behaviour of <see cref="ConsentStateManager"/>: the Unknown-only
/// gate default, Grant/Revoke transitions and their preserved evidence, Rename's no-event guarantee,
/// session reset, and that a throwing subscriber cannot break the machine for anyone else.
/// </summary>
public sealed class ConsentStateManagerTests
{
    private readonly ConsentStateManager _sut = new(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);

    [Fact]
    public void NewSpeaker_StartsInUnknown()
    {
        var entry = _sut.GetOrCreate("Speaker 1");
        Assert.Equal(ConsentState.Unknown, entry.State);
    }

    [Fact]
    public void CurrentState_ForUnknownLabel_ReturnsUnknown_AndDoesNotCreateAnEntry()
    {
        var state = _sut.CurrentState("Never Seen");

        Assert.Equal(ConsentState.Unknown, state);
        Assert.Empty(_sut.Snapshot());
    }

    [Fact]
    public void Grant_SetsGrantedAndStoresEvidence()
    {
        _sut.GetOrCreate("Speaker 1");
        var evidence = MakeEvidence("Speaker 1", "Alice");

        _sut.Grant("Speaker 1", "Alice", evidence);

        var entry = _sut.GetOrCreate("Speaker 1");
        Assert.Equal(ConsentState.Granted, entry.State);
        Assert.Equal("Alice", entry.ExtractedName);
        Assert.NotNull(entry.Evidence);
        Assert.Equal(evidence, entry.Evidence);
    }

    [Fact]
    public void Grant_IsIdempotent_AndRaisesOnce()
    {
        var raiseCount = 0;
        _sut.StateChanged += (_, _) => raiseCount++;

        var firstEvidence = MakeEvidence("Speaker 1", "Alice");
        _sut.Grant("Speaker 1", "Alice", firstEvidence);

        var secondEvidence = MakeEvidence("Speaker 1", "Someone Else");
        _sut.Grant("Speaker 1", "Someone Else", secondEvidence);

        Assert.Equal(1, raiseCount);
        var entry = _sut.GetOrCreate("Speaker 1");
        Assert.Equal(ConsentState.Granted, entry.State);
        Assert.Equal("Alice", entry.ExtractedName);
        Assert.Equal(firstEvidence, entry.Evidence);
    }

    [Fact]
    public void Revoke_TransitionsToRevoked()
    {
        _sut.Grant("Speaker 1", "Alice", MakeEvidence("Speaker 1", "Alice"));

        _sut.Revoke("Speaker 1");

        Assert.Equal(ConsentState.Revoked, _sut.GetOrCreate("Speaker 1").State);
    }

    [Fact]
    public void Revoke_PreservesEvidence()
    {
        var evidence = MakeEvidence("Speaker 1", "Alice");
        _sut.Grant("Speaker 1", "Alice", evidence);

        _sut.Revoke("Speaker 1");

        var entry = _sut.GetOrCreate("Speaker 1");
        Assert.Equal(ConsentState.Revoked, entry.State);
        Assert.Equal("Alice", entry.ExtractedName);
        Assert.Equal(evidence, entry.Evidence);
    }

    [Fact]
    public void Revoke_ThenGrant_RaisesRevokedToGranted()
    {
        _sut.Grant("Speaker 1", "Alice", MakeEvidence("Speaker 1", "Alice"));
        _sut.Revoke("Speaker 1");

        ConsentStateChangedEventArgs? observed = null;
        _sut.StateChanged += (_, e) => observed = e;

        var newEvidence = MakeEvidence("Speaker 1", "Alice");
        _sut.Grant("Speaker 1", "Alice", newEvidence);

        Assert.NotNull(observed);
        Assert.Equal(ConsentState.Revoked, observed!.OldState);
        Assert.Equal(ConsentState.Granted, observed.NewState);
        Assert.Equal(ConsentState.Granted, _sut.CurrentState("Speaker 1"));
    }

    [Fact]
    public void Rename_PreservesState()
    {
        _sut.Grant("Speaker 1", "Alice", MakeEvidence("Speaker 1", "Alice"));

        var renamed = _sut.Rename("Speaker 1", "Alice");

        Assert.True(renamed);
        Assert.Equal(ConsentState.Granted, _sut.CurrentState("Alice"));
        Assert.Equal(ConsentState.Unknown, _sut.CurrentState("Speaker 1"));
    }

    [Fact]
    public void Rename_RaisesNoStateChangedEvent()
    {
        _sut.Grant("Speaker 1", "Alice", MakeEvidence("Speaker 1", "Alice"));

        var raised = false;
        _sut.StateChanged += (_, _) => raised = true;

        _sut.Rename("Speaker 1", "Alice (renamed)");

        Assert.False(raised);
    }

    [Fact]
    public void Rename_ToExistingLabel_ReturnsFalse_AndChangesNothing()
    {
        _sut.Grant("Speaker 1", "Alice", MakeEvidence("Speaker 1", "Alice"));
        _sut.GetOrCreate("Speaker 2");

        var renamed = _sut.Rename("Speaker 1", "Speaker 2");

        Assert.False(renamed);
        Assert.Equal(ConsentState.Granted, _sut.CurrentState("Speaker 1"));
        Assert.Equal(ConsentState.Unknown, _sut.CurrentState("Speaker 2"));
    }

    [Fact]
    public void ResetSession_ClearsEveryEntry_AndSubsequentCurrentStateIsUnknown()
    {
        _sut.Grant("Speaker 1", "Alice", MakeEvidence("Speaker 1", "Alice"));
        _sut.GetOrCreate("Speaker 2");

        _sut.ResetSession();

        Assert.Empty(_sut.Snapshot());
        Assert.Equal(ConsentState.Unknown, _sut.CurrentState("Speaker 1"));
        Assert.Equal(ConsentState.Unknown, _sut.CurrentState("Speaker 2"));
    }

    [Fact]
    public void ThrowingSubscriber_DoesNotBreakTheStateMachine()
    {
        var secondSubscriberFired = false;
        _sut.StateChanged += (_, _) => throw new InvalidOperationException("boom");
        _sut.StateChanged += (_, _) => secondSubscriberFired = true;

        _sut.Grant("Speaker 1", "Alice", MakeEvidence("Speaker 1", "Alice"));

        Assert.Equal(ConsentState.Granted, _sut.CurrentState("Speaker 1"));
        Assert.True(secondSubscriberFired, "non-vacuity: the second, non-throwing subscriber must still have fired");
    }

    [Fact]
    public void Snapshot_ReturnsSnapshots_NotLiveState()
    {
        _sut.Grant("Speaker 1", "Alice", MakeEvidence("Speaker 1", "Alice"));

        var snapshot = _sut.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(ConsentState.Granted, snapshot[0].State);

        _sut.Revoke("Speaker 1");

        Assert.Equal(ConsentState.Granted, snapshot[0].State);
        Assert.Equal(ConsentState.Revoked, _sut.CurrentState("Speaker 1"));
    }

    private static ConsentEvidence MakeEvidence(string speakerLabel, string name) => new(
        SpeakerLabel: speakerLabel,
        ExtractedName: name,
        ConsentSentence: $"Ich bin {name}, ja, das ist ok, dass Pia mitschreibt.",
        Language: "de",
        Confidence: 0.95f,
        GrantedAt: DateTimeOffset.UtcNow,
        SttModelId: "whisper-base");
}
