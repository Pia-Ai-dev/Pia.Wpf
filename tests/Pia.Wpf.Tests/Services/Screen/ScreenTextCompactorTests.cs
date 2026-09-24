using Pia.Services.Screen;
using Xunit;

namespace Pia.Tests.Services.Screen;

public class ScreenTextCompactorTests
{
    private static UiaTextNode Node(string controlType, string? name, string? value = null, bool offscreen = false) =>
        new(1, controlType, name, value, offscreen);

    [Fact]
    public void Offscreen_IsSkipped()
    {
        var (text, nodes, _) = ScreenTextCompactor.Compact(
            [Node("Text", "visible"), Node("Text", "hidden", offscreen: true)], 8000);

        Assert.Equal("visible", text);
        Assert.Equal(1, nodes);
    }

    [Theory]
    [InlineData("Pane")]
    [InlineData("Group")]
    [InlineData("Custom")]
    [InlineData("Image")]
    [InlineData("ScrollBar")]
    [InlineData("Separator")]
    [InlineData("Thumb")]
    public void StructuralTypes_ContributeNothing(string controlType)
    {
        var (text, nodes, _) = ScreenTextCompactor.Compact([Node(controlType, "structure")], 8000);

        Assert.Equal(string.Empty, text);
        Assert.Equal(0, nodes);
    }

    [Fact]
    public void EditPrefersValue_TextPrefersName()
    {
        var (text, _, _) = ScreenTextCompactor.Compact(
            [Node("Edit", "Search box", "typed query"), Node("Text", "a label", "ignored")], 8000);

        Assert.Equal("typed query\na label", text);
    }

    [Fact]
    public void EditWithNoValue_FallsBackToItsName()
    {
        var (text, _, _) = ScreenTextCompactor.Compact([Node("Edit", "Search box")], 8000);

        Assert.Equal("Search box", text);
    }

    [Fact]
    public void Whitespace_IsCollapsed_EmptyDropped()
    {
        var (text, nodes, _) = ScreenTextCompactor.Compact(
            [Node("Text", "  two   words \n here "), Node("Text", "   "), Node("Text", null)], 8000);

        Assert.Equal("two words here", text);
        Assert.Equal(1, nodes);
    }

    [Fact]
    public void ConsecutiveDuplicates_Collapse_NonConsecutiveStay()
    {
        var (text, nodes, _) = ScreenTextCompactor.Compact(
            [Node("ListItem", "Inbox"), Node("Text", "Inbox"), Node("Text", "Drafts"), Node("Text", "Inbox")], 8000);

        Assert.Equal("Inbox\nDrafts\nInbox", text);
        Assert.Equal(3, nodes);
    }

    [Fact]
    public void Cap_TruncatesWithMarker_AndFlags()
    {
        var (text, _, truncated) = ScreenTextCompactor.Compact(
            [Node("Text", new string('a', 20)), Node("Text", new string('b', 20))], 30);

        Assert.True(truncated);
        Assert.EndsWith("\n[truncated]", text);
        Assert.DoesNotContain("bbb", text);
    }

    [Fact]
    public void TextNodes_CountsKeptLines()
    {
        var (_, nodes, truncated) = ScreenTextCompactor.Compact(
            [Node("Text", "one"), Node("Button", "two"), Node("Pane", "skipped"), Node("Hyperlink", "three")], 8000);

        Assert.Equal(3, nodes);
        Assert.False(truncated);
    }

    [Fact]
    public void Hash_IsStable_16Hex_EmptyForEmpty()
    {
        Assert.Equal(string.Empty, ScreenTextCompactor.Hash(string.Empty));

        var first = ScreenTextCompactor.Hash("alpha bravo");
        Assert.Equal(16, first.Length);
        Assert.Equal(first, ScreenTextCompactor.Hash("alpha bravo"));
        Assert.NotEqual(first, ScreenTextCompactor.Hash("alpha charlie"));
        Assert.Matches("^[0-9a-f]{16}$", first);
    }
}
