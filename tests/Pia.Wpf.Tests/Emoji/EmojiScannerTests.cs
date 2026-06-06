using System.Linq;
using Pia.Emoji;
using Xunit;

namespace Pia.Tests.Emoji;

public class EmojiScannerTests
{
    private static (string Text, bool IsEmoji)[] Seg(string? text) => EmojiScanner.Segment(text).ToArray();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyInput_YieldsNothing(string? text)
    {
        Assert.Empty(Seg(text));
    }

    [Fact]
    public void PlainAscii_IsASingleTextRun()
    {
        var segments = Seg("hello world");
        var only = Assert.Single(segments);
        Assert.Equal(("hello world", false), only);
    }

    [Theory]
    [InlineData("😀")]          // emoticon
    [InlineData("🌍")]          // misc symbols & pictographs
    [InlineData("🤖")]          // supplemental symbols & pictographs (U+1F916)
    [InlineData("🚀")]          // transport & map
    [InlineData("🫶")]          // symbols & pictographs extended-A
    public void DefaultEmojiScalar_IsClassifiedEmoji(string emoji)
    {
        var only = Assert.Single(Seg(emoji));
        Assert.Equal((emoji, true), only);
    }

    [Fact]
    public void ZwjFamily_StaysOneEmojiCluster()
    {
        const string family = "👨‍👩‍👧"; // man + ZWJ + woman + ZWJ + girl
        var only = Assert.Single(Seg(family));
        Assert.Equal((family, true), only);
    }

    [Fact]
    public void SkinToneModifier_StaysOneEmojiCluster()
    {
        const string thumbsUp = "👍🏽"; // thumbs up + medium skin tone
        var only = Assert.Single(Seg(thumbsUp));
        Assert.Equal((thumbsUp, true), only);
    }

    [Fact]
    public void Flag_StaysOneEmojiCluster()
    {
        const string germany = "🇩🇪"; // regional indicators D + E
        var only = Assert.Single(Seg(germany));
        Assert.Equal((germany, true), only);
    }

    [Fact]
    public void TwoFlags_AreTwoSeparateEmoji()
    {
        var segments = Seg("🇩🇪🇫🇷"); // Germany then France
        Assert.Equal(2, segments.Length);
        Assert.All(segments, s => Assert.True(s.IsEmoji));
    }

    [Fact]
    public void Vs16_PromotesTextSymbolToEmoji()
    {
        Assert.Equal(("©", false), Assert.Single(Seg("©")));        // bare copyright is text
        Assert.Equal(("©️", true), Assert.Single(Seg("©️")));       // with VS16 it is emoji
    }

    [Fact]
    public void Keycap_IsEmoji_ButBareDigitIsText()
    {
        Assert.Equal(("1", false), Assert.Single(Seg("1")));
        Assert.Equal(("1️⃣", true), Assert.Single(Seg("1️⃣")));
    }

    [Fact]
    public void SurrogatePairNonEmoji_IsText()
    {
        const string cjkExtensionB = "\U00020000"; // CJK ideograph, surrogate pair, not emoji
        var only = Assert.Single(Seg(cjkExtensionB));
        Assert.Equal((cjkExtensionB, false), only);
    }

    [Fact]
    public void ConsecutiveEmoji_AreSeparateSegments()
    {
        var segments = Seg("😀😁");
        Assert.Equal(2, segments.Length);
        Assert.Equal(("😀", true), segments[0]);
        Assert.Equal(("😁", true), segments[1]);
    }

    [Fact]
    public void MixedTextAndEmoji_SplitsAndCoalescesText()
    {
        var segments = Seg("Hi 👋 there 🌍!");
        Assert.Collection(segments,
            s => Assert.Equal(("Hi ", false), s),
            s => Assert.Equal(("👋", true), s),
            s => Assert.Equal((" there ", false), s),
            s => Assert.Equal(("🌍", true), s),
            s => Assert.Equal(("!", false), s));
    }

    [Fact]
    public void Reassembling_Segments_RoundTripsTheInput()
    {
        const string input = "Pia 🤖 says hi 👋🏽 to 🇩🇪 #1️⃣!";
        var rebuilt = string.Concat(Seg(input).Select(s => s.Text));
        Assert.Equal(input, rebuilt);
    }
}
