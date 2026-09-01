using Pia.Models;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// The rule that keeps the far end out of the local speaker's transcript. Every case is driven off an
/// explicit clock: what the detector decides depends on how much far-end speech a microphone segment
/// sits inside, and on whether the matching far-end text has been recognised yet.
/// </summary>
public sealed class EchoDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 9, 15, 0, TimeSpan.Zero);

    private const string Consent = "Mein Name ist Ilkin Kotsch und ich bin damit einverstanden, dass Pia aufzeichnet.";

    /// <summary>The same sentence as the mic heard it — a second recognition pass, so the name differs.</summary>
    private const string ConsentAsEchoed = "Mein Name ist Irkin Kotsch und ich bin damit einverstanden, dass Pia aufzeichnet";

    private static TranscriptUtterance Mic(string text, double atSecond, double lengthSeconds)
        => new(
            TranscriptSpeaker.You, text, T0.AddSeconds(atSecond + lengthSeconds),
            DurationSeconds: lengthSeconds,
            SpeechStart: T0.AddSeconds(atSecond),
            SpeechEnd: T0.AddSeconds(atSecond + lengthSeconds));

    private static TranscriptUtterance Remote(string text, double atSecond, double lengthSeconds)
        => new(
            TranscriptSpeaker.Them, text, T0.AddSeconds(atSecond + lengthSeconds),
            SpeakerLabel: "Ilkin Kotsch",
            SegmentId: 1,
            DurationSeconds: lengthSeconds,
            SpeechStart: T0.AddSeconds(atSecond),
            SpeechEnd: T0.AddSeconds(atSecond + lengthSeconds));

    /// <summary>Marks the far end as talking for a stretch, the way the loopback VAD would.</summary>
    private static void RemoteSpoke(EchoDetector suppressor, double fromSecond, double toSecond)
    {
        suppressor.NoteRemoteSpeaking(true, T0.AddSeconds(fromSecond));
        suppressor.NoteRemoteSpeaking(false, T0.AddSeconds(toSecond));
    }

    [Fact]
    public void MicSpeechWithNoFarEndActivity_IsEmittedImmediately()
    {
        var suppressor = new EchoDetector();

        var verdict = suppressor.Inspect(Mic("Und noch einmal bitte.", 0, 2), T0.AddSeconds(2));

        Assert.Equal(EchoVerdict.Emit, verdict);
    }

    [Fact]
    public void MicSpeechRepeatingTheFarEnd_IsDropped()
    {
        var suppressor = new EchoDetector();
        RemoteSpoke(suppressor, 5, 11);
        suppressor.NoteRemoteUtterance(Remote(Consent, 5, 6));

        var verdict = suppressor.Inspect(Mic(ConsentAsEchoed, 5, 6), T0.AddSeconds(11.5));

        Assert.Equal(EchoVerdict.Drop, verdict);
    }

    [Fact]
    public void LocalSpeechOverTheFarEnd_SurvivesAsBargeIn()
    {
        var suppressor = new EchoDetector();
        RemoteSpoke(suppressor, 5, 11);
        suppressor.NoteRemoteUtterance(Remote(Consent, 5, 6));

        var verdict = suppressor.Inspect(Mic("Warte kurz, das habe ich nicht verstanden.", 6, 3), T0.AddSeconds(9.5));

        Assert.Equal(EchoVerdict.Emit, verdict);
    }

    [Fact]
    public void MicSpeechJustAfterTheFarEndStopped_IsNotSuspected()
    {
        var suppressor = new EchoDetector();
        RemoteSpoke(suppressor, 5, 11);
        suppressor.NoteRemoteUtterance(Remote(Consent, 5, 6));

        // The gap is the whole point of the bug report: a fast turn-taking is not an echo.
        var verdict = suppressor.Inspect(Mic("Okay, super.", 11.2, 1.5), T0.AddSeconds(13));

        Assert.Equal(EchoVerdict.Emit, verdict);
    }

    [Fact]
    public void SuspectWithoutFarEndTextYet_IsHeldThenDroppedWhenItArrives()
    {
        var suppressor = new EchoDetector();
        RemoteSpoke(suppressor, 5, 11);

        var echoed = Mic(ConsentAsEchoed, 5, 6);
        Assert.Equal(EchoVerdict.Hold, suppressor.Inspect(echoed, T0.AddSeconds(11.2)));
        suppressor.Hold(echoed, T0.AddSeconds(11.2));

        Assert.Empty(suppressor.TakeDecided(T0.AddSeconds(11.4)));

        suppressor.NoteRemoteUtterance(Remote(Consent, 5, 6));
        var decided = suppressor.TakeDecided(T0.AddSeconds(11.6));

        Assert.True(Assert.Single(decided).Dropped);
    }

    [Fact]
    public void SuspectWhoseFarEndTextNeverArrives_IsEmittedWhenTheHoldExpires()
    {
        var suppressor = new EchoDetector(holdFor: TimeSpan.FromSeconds(2));
        RemoteSpoke(suppressor, 5, 11);

        var spoken = Mic("Das kann er wohl nicht auseinanderhalten.", 5, 6);
        suppressor.Hold(spoken, T0.AddSeconds(11.2));

        Assert.Empty(suppressor.TakeDecided(T0.AddSeconds(12)));

        var decided = suppressor.TakeDecided(T0.AddSeconds(13.3));

        var released = Assert.Single(decided);
        Assert.False(released.Dropped);
        Assert.Equal(spoken, released.Utterance);
    }

    [Fact]
    public void UndatedMicSpeech_IsAlwaysEmitted()
    {
        var suppressor = new EchoDetector();
        RemoteSpoke(suppressor, 5, 11);
        suppressor.NoteRemoteUtterance(Remote(Consent, 5, 6));

        var undated = new TranscriptUtterance(TranscriptSpeaker.You, ConsentAsEchoed, T0.AddSeconds(11));

        Assert.Equal(EchoVerdict.Emit, suppressor.Inspect(undated, T0.AddSeconds(11)));
    }

    [Fact]
    public void ShortBackchannelOverTheFarEnd_SurvivesUnlessItIsWordForWord()
    {
        var suppressor = new EchoDetector();
        RemoteSpoke(suppressor, 5, 11);
        suppressor.NoteRemoteUtterance(Remote("Ja genau, das sehe ich auch so.", 5, 6));

        // "mhm" is not in what the far end said, so it is the local user agreeing over the top.
        Assert.Equal(EchoVerdict.Emit, suppressor.Inspect(Mic("Mhm.", 6, 1), T0.AddSeconds(7.5)));
    }

    [Fact]
    public void FarEndStillTalking_CountsAsCoverForAnOpenWindow()
    {
        var suppressor = new EchoDetector();
        suppressor.NoteRemoteSpeaking(true, T0.AddSeconds(5));

        // No "stopped" event yet — the window is open, so a mic segment inside it is still a suspect.
        Assert.Equal(EchoVerdict.Hold, suppressor.Inspect(Mic(ConsentAsEchoed, 5, 4), T0.AddSeconds(9)));
    }

    [Fact]
    public void DrainHeld_ReturnsEverythingStillParked()
    {
        var suppressor = new EchoDetector();
        suppressor.NoteRemoteSpeaking(true, T0);

        var first = Mic("Erster Satz.", 1, 2);
        var second = Mic("Zweiter Satz.", 4, 2);
        suppressor.Hold(first, T0.AddSeconds(3));
        suppressor.Hold(second, T0.AddSeconds(6));

        Assert.Equal([first, second], suppressor.DrainHeld());
        Assert.Empty(suppressor.DrainHeld());
    }

    [Theory]
    [InlineData("Hallo, Welt!", new[] { "hallo", "welt" })]
    [InlineData("  ", new string[0])]
    [InlineData("Pia 1.4.15", new[] { "pia", "1", "4", "15" })]
    public void Tokenize_StripsPunctuationAndCase(string text, string[] expected)
        => Assert.Equal(expected, EchoDetector.Tokenize(text));
}
