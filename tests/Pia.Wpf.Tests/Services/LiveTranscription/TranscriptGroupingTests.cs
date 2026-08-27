using Pia.Models;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// These two decide what a saved transcript looks like, and the live overlay and the unattended recorder both
/// go through them — so a change here silently changes both, which is exactly why they are pinned.
/// </summary>
public class TranscriptGroupingTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    private static TranscriptBubble Bubble(string? label, int atSecond = 0) =>
        new(TranscriptSpeaker.Them, Start.AddSeconds(atSecond), speakerLabel: label, displayLabel: label);

    [Fact]
    public void ShouldReuse_IsFalse_ForTheFirstUtterance() =>
        Assert.False(TranscriptGrouping.ShouldReuse(null, TranscriptSpeaker.Them, Start, "Speaker 1"));

    [Fact]
    public void ShouldReuse_MergesTheSameSpeakerAndLabelInsideTheWindow() =>
        Assert.True(TranscriptGrouping.ShouldReuse(
            Bubble("Speaker 1"), TranscriptSpeaker.Them, Start.AddSeconds(5), "Speaker 1"));

    [Fact]
    public void ShouldReuse_SplitsOnceTheWindowHasElapsed() =>
        Assert.False(TranscriptGrouping.ShouldReuse(
            Bubble("Speaker 1"), TranscriptSpeaker.Them,
            Start.AddSeconds(TranscriptGrouping.BubbleWindowSeconds), "Speaker 1"));

    [Fact]
    public void ShouldReuse_SplitsOnADifferentSpeaker() =>
        Assert.False(TranscriptGrouping.ShouldReuse(
            Bubble("Speaker 1"), TranscriptSpeaker.You, Start.AddSeconds(1), "Speaker 1"));

    [Fact]
    public void ShouldReuse_SplitsOnADifferentLabel() =>
        Assert.False(TranscriptGrouping.ShouldReuse(
            Bubble("Speaker 1"), TranscriptSpeaker.Them, Start.AddSeconds(1), "Speaker 2"));

    // "ja", "genau", laughter: too short to diarize, and splitting the run on them would litter the
    // transcript with unlabeled one-word bubbles.
    [Fact]
    public void ShouldReuse_LetsAnUnlabeledSegmentInheritTheRun() =>
        Assert.True(TranscriptGrouping.ShouldReuse(
            Bubble("Speaker 1"), TranscriptSpeaker.Them, Start.AddSeconds(1), null));

    [Fact]
    public void ShouldReuse_DoesNotAttachALabeledSegmentToAnUnlabeledRun() =>
        Assert.False(TranscriptGrouping.ShouldReuse(
            Bubble(null), TranscriptSpeaker.Them, Start.AddSeconds(1), "Speaker 1"));

    [Fact]
    public void Numbering_RenumbersMintCounterLabelsByFirstAppearance()
    {
        var numbering = new SpeakerDisplayNumbering();

        // Speaker 17 for the second voice is the identification service's mint counter leaking out.
        Assert.Equal("Speaker 1", numbering.Resolve("Speaker 4", suppressLabels: false));
        Assert.Equal("Speaker 2", numbering.Resolve("Speaker 17", suppressLabels: false));
        Assert.Equal("Speaker 1", numbering.Resolve("Speaker 4", suppressLabels: false));
    }

    [Fact]
    public void Numbering_LeavesARealNameAlone() =>
        Assert.Equal("Marco", new SpeakerDisplayNumbering().Resolve("Marco", suppressLabels: false));

    [Fact]
    public void Numbering_DropsEveryLabel_WhenSuppressed() =>
        Assert.Null(new SpeakerDisplayNumbering().Resolve("Speaker 4", suppressLabels: true));

    [Fact]
    public void Numbering_PassesBlankThrough() =>
        Assert.Null(new SpeakerDisplayNumbering().Resolve(null, suppressLabels: false));

    [Fact]
    public void Numbering_Reset_ClosesTheGapAStaleLabelLeaves()
    {
        var numbering = new SpeakerDisplayNumbering();
        numbering.Resolve("Speaker 4", suppressLabels: false);
        numbering.Resolve("Speaker 17", suppressLabels: false);

        // A rebuild re-derives numbers from the surviving labels; without the reset, Speaker 17 would keep
        // number 2 even after Speaker 4 was clustered away.
        numbering.Reset();
        Assert.Equal("Speaker 1", numbering.Resolve("Speaker 17", suppressLabels: false));
    }
}
