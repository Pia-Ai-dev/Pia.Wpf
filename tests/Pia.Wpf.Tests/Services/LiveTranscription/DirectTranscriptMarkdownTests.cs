using System.Globalization;
using Pia.Localization;
using Pia.Models;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Measures the actual rendered Markdown/YAML text produced by <see cref="DirectTranscriptMarkdown"/>.
/// All timestamps/durations here are synthesised; nothing is measured against a real recording.
/// </summary>
public class DirectTranscriptMarkdownTests
{
    private static readonly DateTimeOffset SessionStart = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SessionEnd = new(2026, 8, 3, 9, 30, 0, TimeSpan.Zero);

    private static TranscriptBubble MakeBubble(
        TranscriptSpeaker speaker, string text, string? speakerLabel, DateTimeOffset start, DateTimeOffset end)
    {
        var bubble = new TranscriptBubble(speaker, start, text, speakerLabel)
        {
            EndTimestamp = end,
        };
        return bubble;
    }

    [Fact]
    public void Render_FrontMatter_StartsAtFirstCharacter()
    {
        var bubbles = new[] { MakeBubble(TranscriptSpeaker.You, "hello", null, SessionStart, SessionStart) };
        var stats = Array.Empty<SpeakerVoiceStats>();

        var md = DirectTranscriptMarkdown.Render("Title", SessionStart, SessionEnd, bubbles, stats, null);

        Assert.StartsWith("---", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ContainsExactSchemaConstant()
    {
        var bubbles = Array.Empty<TranscriptBubble>();
        var stats = Array.Empty<SpeakerVoiceStats>();

        var md = DirectTranscriptMarkdown.Render("Title", SessionStart, SessionEnd, bubbles, stats, null);

        Assert.Contains($"schema: {DirectTranscriptMarkdown.Schema}", md, StringComparison.Ordinal);
        Assert.Equal("pia-direct-transcript/v1", DirectTranscriptMarkdown.Schema);
    }

    [Fact]
    public void Render_SpeakersList_IsDeduplicatedInFirstAppearanceOrder()
    {
        var bubbles = new[]
        {
            MakeBubble(TranscriptSpeaker.Them, "hi", "Speaker 2", SessionStart, SessionStart),
            MakeBubble(TranscriptSpeaker.You, "hey", null, SessionStart, SessionStart),
            MakeBubble(TranscriptSpeaker.Them, "again", "Speaker 2", SessionStart, SessionStart),
            MakeBubble(TranscriptSpeaker.Them, "third", "Speaker 3", SessionStart, SessionStart),
        };
        var stats = Array.Empty<SpeakerVoiceStats>();

        var md = DirectTranscriptMarkdown.Render("Title", SessionStart, SessionEnd, bubbles, stats, null);

        var speakersBlock = ExtractYamlList(md, "speakers:");
        // The mic side resolves to the localized Speaker_Me resource, not an English literal, so it is
        // asserted against the resource to stay true under any culture.
        Assert.Equal(
            new[] { "Speaker 2", LocalizationSource.Instance["Speaker_Me"], "Speaker 3" },
            speakersBlock);
    }

    [Fact]
    public void Render_LabelContainingColon_IsQuoted_AndYamlLineScanStillParses()
    {
        var bubbles = new[]
        {
            MakeBubble(TranscriptSpeaker.Them, "hi", null, SessionStart, SessionStart),
        };
        var stats = Array.Empty<SpeakerVoiceStats>();

        var md = DirectTranscriptMarkdown.Render("Title", SessionStart, SessionEnd, bubbles, stats, "Bob: Team A");

        var speakersBlock = ExtractYamlList(md, "speakers:");
        Assert.Single(speakersBlock);
        // Single-quoted, with the raw colon preserved inside the quotes.
        Assert.Equal("'Bob: Team A'", speakersBlock[0]);

        // A naive line-scan must still recover the whole label rather than truncating at the embedded
        // colon, which is what the quoting buys.
        var rawLine = md.Split('\n').Single(l => l.TrimStart().StartsWith("- 'Bob", StringComparison.Ordinal));
        var dashIndex = rawLine.IndexOf('-');
        var recovered = rawLine[(dashIndex + 1)..].Trim();
        Assert.Equal("'Bob: Team A'", recovered);
    }

    [Fact]
    public void Render_VoiceStats_UseTheSameDisplayLabelAsTheSpeakersList()
    {
        // Stats are keyed by the diarizer's label, bubbles by the renumbered one; one document must
        // not name the same person twice over.
        var bubbles = new[]
        {
            new TranscriptBubble(TranscriptSpeaker.Them, SessionStart, "hi", "Speaker 17", "Speaker 1"),
        };
        var stats = new[] { new SpeakerVoiceStats(TranscriptSpeaker.Them, "Speaker 17", 1, 4.0, 4.0, 1.0) };

        var md = DirectTranscriptMarkdown.Render("Title", SessionStart, SessionEnd, bubbles, stats, null);

        Assert.Contains("- speaker: Speaker 1\n", md, StringComparison.Ordinal);
        Assert.DoesNotContain("Speaker 17", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_VoiceStats_DoNotResurrectALabelTheBubbleSuppressed()
    {
        // A suppressed bubble carries a null display label. Falling back to the stats key here would
        // put "Speaker 17" back into the front matter, which is the part that gets ingested.
        var bubbles = new[]
        {
            new TranscriptBubble(TranscriptSpeaker.Them, SessionStart, "hi", "Speaker 17", null),
        };
        var stats = new[] { new SpeakerVoiceStats(TranscriptSpeaker.Them, "Speaker 17", 1, 4.0, 4.0, 1.0) };

        var md = DirectTranscriptMarkdown.Render("Title", SessionStart, SessionEnd, bubbles, stats, "Acme call");

        Assert.DoesNotContain("Speaker 17", md, StringComparison.Ordinal);
        Assert.Contains("- speaker: Acme call\n", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_VoiceStatsBlock_HasOneEntryPerStat_WithInvariantCultureDecimals()
    {
        var bubbles = Array.Empty<TranscriptBubble>();
        var stats = new[]
        {
            new SpeakerVoiceStats(TranscriptSpeaker.You, null, 3, 12.34, 4.113333, 0.6),
            new SpeakerVoiceStats(TranscriptSpeaker.Them, "Speaker 1", 2, 8.0, 4.0, 0.4),
        };

        var md = DirectTranscriptMarkdown.Render("Title", SessionStart, SessionEnd, bubbles, stats, null);

        var speakerEntries = md.Split('\n').Count(l => l.TrimStart().StartsWith("- speaker:", StringComparison.Ordinal));
        Assert.Equal(stats.Length, speakerEntries);

        Assert.Contains("totalSeconds: 12.3", md, StringComparison.Ordinal);
        Assert.Contains("meanSeconds: 4.1", md, StringComparison.Ordinal);
        Assert.Contains("sharePercent: 60.0", md, StringComparison.Ordinal);
        Assert.Contains("totalSeconds: 8.0", md, StringComparison.Ordinal);
        Assert.Contains("sharePercent: 40.0", md, StringComparison.Ordinal);

        // Non-vacuity: prove this isn't accidentally matching zero rendered numeric lines.
        var numericLineCount = md.Split('\n').Count(l =>
            l.Contains("totalSeconds:", StringComparison.Ordinal) ||
            l.Contains("meanSeconds:", StringComparison.Ordinal) ||
            l.Contains("sharePercent:", StringComparison.Ordinal));
        Assert.True(numericLineCount >= 6, "non-vacuity: expected 3 numeric lines per stat entry");
    }

    [Fact]
    public void RenderBody_ContainsOneHeadingPerBubble_AndTheBubbleText()
    {
        var bubbles = new[]
        {
            MakeBubble(TranscriptSpeaker.You, "first message", null, SessionStart, SessionStart),
            MakeBubble(TranscriptSpeaker.Them, "second message", "Speaker 1", SessionStart.AddMinutes(1), SessionStart.AddMinutes(1)),
        };

        var body = DirectTranscriptMarkdown.RenderBody("My Title", bubbles, "Alex");

        Assert.Contains("first message", body, StringComparison.Ordinal);
        Assert.Contains("second message", body, StringComparison.Ordinal);

        var boldHeadingCount = body.Split('\n').Count(l => l.StartsWith("**", StringComparison.Ordinal));
        Assert.Equal(bubbles.Length, boldHeadingCount);
    }

    [Fact]
    public void RenderBody_ContainsNoFrontMatter()
    {
        var bubbles = new[] { MakeBubble(TranscriptSpeaker.You, "hi", null, SessionStart, SessionStart) };

        var body = DirectTranscriptMarkdown.RenderBody("Title", bubbles, null);

        Assert.DoesNotContain("---", body, StringComparison.Ordinal);
        Assert.DoesNotContain("schema:", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderBody_Timestamps_AreRenderedInDocumentedHmsFormat()
    {
        var start = new DateTimeOffset(2026, 8, 3, 9, 15, 42, TimeSpan.Zero);
        var end = start.AddSeconds(37);
        var bubbles = new[] { MakeBubble(TranscriptSpeaker.You, "hi", null, start, end) };

        var body = DirectTranscriptMarkdown.RenderBody("Title", bubbles, null);

        var expectedStart = start.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var expectedEnd = end.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Assert.Contains($"_{expectedStart}–{expectedEnd}_", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_SessionEndEarlierThanStart_IsEmittedAsIs()
    {
        var bubbles = Array.Empty<TranscriptBubble>();
        var stats = Array.Empty<SpeakerVoiceStats>();
        var earlierEnd = SessionStart.AddMinutes(-5);

        var md = DirectTranscriptMarkdown.Render("Title", SessionStart, earlierEnd, bubbles, stats, null);

        Assert.Contains($"end: {earlierEnd.ToString("O", CultureInfo.InvariantCulture)}", md, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extracts a simple YAML block-sequence's item values for a top-level key by scanning lines —
    /// mirrors how a hand-rolled front-matter parser (like the old MeetingTranscriptWriter) would read it.
    /// </summary>
    private static List<string> ExtractYamlList(string markdown, string key)
    {
        var lines = markdown.Split('\n');
        var items = new List<string>();
        var inBlock = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (!inBlock)
            {
                if (line.TrimStart() == key) inBlock = true;
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                items.Add(trimmed[2..].Trim());
            }
            else
            {
                break;
            }
        }
        return items;
    }
}
