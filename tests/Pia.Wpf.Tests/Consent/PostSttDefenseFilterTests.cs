using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class PostSttDefenseFilterTests
{
    [Fact]
    public void NonGranted_RegularChannel_DropsAndAudits()
    {
        var (sut, mgr, audit) = Build();
        mgr.GetOrCreate("Speaker 1");
        mgr.MarkPrompted("Speaker 1");

        var decision = sut.Evaluate(new TranscriptUtterance(
            TranscriptSpeaker.Them, "leaked", DateTimeOffset.UtcNow, "Speaker 1", TranscriptChannel.Regular));

        Assert.Equal(PostSttFilterDecision.DropAndAudit, decision);
        Assert.Equal(1, sut.DropCount);
        audit.Received(1).Append(Arg.Is<AuditEvent>(e =>
            e.EventType == "DROPPED_TRANSCRIPT_NO_CONSENT" && e.SpeakerLabel == "Speaker 1"));
    }

    [Fact]
    public void Granted_RegularChannel_Allows()
    {
        var (sut, mgr, audit) = Build();
        mgr.GetOrCreate("Speaker 1");
        mgr.MarkPrompted("Speaker 1");
        mgr.RecordClassification("Speaker 1",
            new ConsentClassification(ConsentDecision.Grant, 0.95f),
            "ja", "h", "p", "stt");

        var decision = sut.Evaluate(new TranscriptUtterance(
            TranscriptSpeaker.Them, "ok now", DateTimeOffset.UtcNow, "Speaker 1", TranscriptChannel.Regular));

        Assert.Equal(PostSttFilterDecision.Allow, decision);
        Assert.Equal(0, sut.DropCount);
        audit.DidNotReceiveWithAnyArgs().Append(default!);
    }

    [Fact]
    public void NoSpeakerLabel_Allows()
    {
        var (sut, _, _) = Build();
        var decision = sut.Evaluate(new TranscriptUtterance(
            TranscriptSpeaker.You, "hello", DateTimeOffset.UtcNow, SpeakerLabel: null, TranscriptChannel.Regular));
        Assert.Equal(PostSttFilterDecision.Allow, decision);
    }

    [Fact]
    public void ConsentClassificationChannel_Allows()
    {
        var (sut, mgr, _) = Build();
        mgr.GetOrCreate("Speaker 1");
        mgr.MarkPrompted("Speaker 1");

        var decision = sut.Evaluate(new TranscriptUtterance(
            TranscriptSpeaker.Them, "ja", DateTimeOffset.UtcNow, "Speaker 1", TranscriptChannel.ConsentClassification));

        Assert.Equal(PostSttFilterDecision.Allow, decision);
    }

    [Fact]
    public void DropCount_IncrementsAcrossMultipleDrops()
    {
        var (sut, mgr, _) = Build();
        mgr.GetOrCreate("S1");
        mgr.MarkPrompted("S1");

        for (int i = 0; i < 4; i++)
        {
            sut.Evaluate(new TranscriptUtterance(
                TranscriptSpeaker.Them, "leak", DateTimeOffset.UtcNow, "S1", TranscriptChannel.Regular));
        }
        Assert.Equal(4, sut.DropCount);
    }

    private static (PostSttDefenseFilter sut, ConsentStateManager mgr, IConsentAuditLog audit) Build()
    {
        var mgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        var audit = Substitute.For<IConsentAuditLog>();
        var sut = new PostSttDefenseFilter(mgr, audit, NullLogger<PostSttDefenseFilter>.Instance);
        return (sut, mgr, audit);
    }
}
