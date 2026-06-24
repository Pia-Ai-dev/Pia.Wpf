using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

public class TranscriptBubbleTests
{
    [Fact]
    public void Append_AddsSpaceSeparatedText_AndUpdatesEndTimestamp()
    {
        var start = new DateTimeOffset(2026, 4, 26, 14, 0, 0, TimeSpan.Zero);
        var bubble = new TranscriptBubble(TranscriptSpeaker.You, start);

        bubble.Append("hello", start.AddSeconds(1));
        bubble.Append("world", start.AddSeconds(2));

        Assert.Equal("hello world", bubble.Text);
        Assert.Equal(start.AddSeconds(2), bubble.EndTimestamp);
        Assert.Equal(start, bubble.StartTimestamp);
    }

    [Fact]
    public void Append_SkipsWhitespace_DoesNotMutate()
    {
        var start = DateTimeOffset.UtcNow;
        var bubble = new TranscriptBubble(TranscriptSpeaker.Them, start, "existing");

        bubble.Append("   ", start.AddSeconds(5));

        Assert.Equal("existing", bubble.Text);
        Assert.Equal(start, bubble.EndTimestamp);
    }

    [Fact]
    public void Append_FromEmpty_DoesNotPrependSpace()
    {
        var start = DateTimeOffset.UtcNow;
        var bubble = new TranscriptBubble(TranscriptSpeaker.You, start);

        bubble.Append("first", start.AddSeconds(1));

        Assert.Equal("first", bubble.Text);
    }

    [Fact]
    public void Append_OlderTimestamp_DoesNotRewindEnd()
    {
        var start = DateTimeOffset.UtcNow;
        var bubble = new TranscriptBubble(TranscriptSpeaker.You, start);

        bubble.Append("a", start.AddSeconds(10));
        bubble.Append("b", start.AddSeconds(5));

        Assert.Equal(start.AddSeconds(10), bubble.EndTimestamp);
    }

    [Fact]
    public void Ctor_FourArg_SetsSpeakerLabel()
    {
        var start = DateTimeOffset.UtcNow;
        var bubble = new TranscriptBubble(TranscriptSpeaker.Them, start, "hi", "Speaker 1");

        Assert.Equal("Speaker 1", bubble.SpeakerLabel);
        Assert.Equal(0, bubble.ColorIndex);
    }

    [Fact]
    public void Ctor_ThreeArg_LeavesSpeakerLabelNull()
    {
        var start = DateTimeOffset.UtcNow;
        var bubble = new TranscriptBubble(TranscriptSpeaker.Them, start, "hi");

        Assert.Null(bubble.SpeakerLabel);
    }

    [Fact]
    public void SpeakerLabel_IsObservable_AndMutable()
    {
        var bubble = new TranscriptBubble(TranscriptSpeaker.Them, DateTimeOffset.UtcNow);
        var raised = new List<string?>();
        bubble.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TranscriptBubble.SpeakerLabel)) raised.Add(bubble.SpeakerLabel);
        };

        bubble.SpeakerLabel = "Marco";

        Assert.Equal("Marco", bubble.SpeakerLabel);
        Assert.Equal(new string?[] { "Marco" }, raised);
    }

    [Fact]
    public void ColorIndex_IsObservable_AndMutable()
    {
        var bubble = new TranscriptBubble(TranscriptSpeaker.Them, DateTimeOffset.UtcNow);
        var raised = new List<int>();
        bubble.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TranscriptBubble.ColorIndex)) raised.Add(bubble.ColorIndex);
        };

        bubble.ColorIndex = 3;

        Assert.Equal(3, bubble.ColorIndex);
        Assert.Equal(new[] { 3 }, raised);
    }
}
