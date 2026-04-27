using System;
using System.Collections.Generic;
using Pia.Models;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class MeetingTranscriptWriterTests
{
    private static TranscriptBubble Bubble(TranscriptSpeaker speaker, string text, DateTimeOffset start, string? label = null)
    {
        var b = new TranscriptBubble(speaker, start, text, label);
        b.EndTimestamp = start.AddSeconds(5);
        return b;
    }

    [Fact]
    public void Render_EmitsFrontMatterWithSchemaAndTimestamps()
    {
        var start = new DateTimeOffset(2026, 4, 27, 10, 30, 0, TimeSpan.FromHours(2));
        var bubbles = new List<TranscriptBubble>
        {
            Bubble(TranscriptSpeaker.You, "Hello", start),
        };

        var md = MeetingTranscriptWriter.Render(
            bubbles,
            sessionStart: start,
            originalFilename: "transcript-20260427-103000.md",
            title: "Live Transcription");

        Assert.StartsWith("---\n", md.Replace("\r\n", "\n"));
        Assert.Contains("schema: pia-meeting-transcript/v1", md);
        Assert.Contains("start: 2026-04-27T10:30:00", md);
        Assert.Contains("originalFilename: transcript-20260427-103000.md", md);
    }

    [Fact]
    public void Render_DeduplicatesAndOrdersSpeakers_ByFirstAppearance()
    {
        var start = new DateTimeOffset(2026, 4, 27, 10, 30, 0, TimeSpan.FromHours(2));
        var bubbles = new List<TranscriptBubble>
        {
            Bubble(TranscriptSpeaker.Them, "hi",   start,                   label: "Alice"),
            Bubble(TranscriptSpeaker.You,  "hey",  start.AddSeconds(10)),
            Bubble(TranscriptSpeaker.Them, "again",start.AddSeconds(20),    label: "Alice"),
            Bubble(TranscriptSpeaker.Them, "yo",   start.AddSeconds(30),    label: "Bob"),
        };

        var md = MeetingTranscriptWriter.Render(bubbles, sessionStart: start, originalFilename: "x.md", title: "T");

        var aliceIdx = md.IndexOf("- Alice", StringComparison.Ordinal);
        var youIdx   = md.IndexOf("- you",   StringComparison.Ordinal);
        var bobIdx   = md.IndexOf("- Bob",   StringComparison.Ordinal);

        Assert.True(aliceIdx > 0 && youIdx > 0 && bobIdx > 0, "All three speakers should appear in front-matter");
        Assert.True(aliceIdx < youIdx && youIdx < bobIdx, "Speakers should be ordered by first appearance");

        Assert.Equal(1, CountOccurrences(md, "- Alice"));
    }

    [Fact]
    public void Render_FallsBackToSpeakerN_WhenLabelMissing()
    {
        var start = new DateTimeOffset(2026, 4, 27, 10, 30, 0, TimeSpan.FromHours(2));
        var bubbles = new List<TranscriptBubble>
        {
            Bubble(TranscriptSpeaker.Them, "anon", start, label: null),
        };

        var md = MeetingTranscriptWriter.Render(bubbles, sessionStart: start, originalFilename: "x.md", title: "T");

        Assert.Contains("Speaker", md);
    }

    [Fact]
    public void Render_BodyContainsSpeakerHeadersAndText()
    {
        var start = new DateTimeOffset(2026, 4, 27, 10, 30, 0, TimeSpan.FromHours(2));
        var bubbles = new List<TranscriptBubble>
        {
            Bubble(TranscriptSpeaker.Them, "first message", start, label: "Alice"),
        };

        var md = MeetingTranscriptWriter.Render(bubbles, sessionStart: start, originalFilename: "x.md", title: "Live Transcription");

        Assert.Contains("**Alice**", md);
        Assert.Contains("first message", md);
        Assert.Contains("# Live Transcription", md);
    }

    [Fact]
    public void StripFrontMatter_RemovesYamlBlock()
    {
        var input = "---\nschema: x\n---\n# Title\n\nbody";
        var stripped = MeetingTranscriptWriter.StripFrontMatter(input);
        Assert.StartsWith("# Title", stripped.Replace("\r\n", "\n"));
    }

    [Fact]
    public void StripFrontMatter_ReturnsInputWhenNoFrontMatter()
    {
        var input = "# Title\n\nbody";
        Assert.Equal(input, MeetingTranscriptWriter.StripFrontMatter(input));
    }

    [Fact]
    public void TryParseFrontMatter_ReadsSpeakersAndDate()
    {
        var input = """
            ---
            schema: pia-meeting-transcript/v1
            start: 2026-04-27T10:30:00+02:00
            speakers:
              - You
              - Alice
            originalFilename: transcript-20260427-103000.md
            ---
            body
            """;

        var ok = MeetingTranscriptWriter.TryParseFrontMatter(input, out var fm);

        Assert.True(ok);
        Assert.Equal("2026-04-27", fm!.Date);
        Assert.Equal(new[] { "You", "Alice" }, fm.Speakers);
        Assert.Equal("transcript-20260427-103000.md", fm.OriginalFilename);
    }

    [Fact]
    public void TryParseFrontMatter_ReturnsFalse_WhenAbsent()
    {
        Assert.False(MeetingTranscriptWriter.TryParseFrontMatter("# Plain\n\nno fm", out _));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
