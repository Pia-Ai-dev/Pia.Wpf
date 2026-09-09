using Microsoft.Extensions.AI;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class ToolLoopImageMessagesTests
{
    private static ToolLoopImage Image(string callId = "call-1") =>
        new(callId, [1, 2, 3], "image/jpeg", 800, 600, "Screen capture: notepad, 800x600 px.");

    [Fact]
    public void Build_TagsTheMessage_AndCarriesTextThenImage()
    {
        var message = ToolLoopImageMessages.Build(Image());

        Assert.Equal(ChatRole.User, message.Role);
        Assert.IsType<TextContent>(message.Contents[0]);
        Assert.IsType<DataContent>(message.Contents[1]);
        Assert.True(ToolLoopImageMessages.IsTagged(message));
        Assert.True(ToolLoopImageMessages.CarriesImage(message));

        var tag = Assert.IsType<ToolLoopImageTag>(message.AdditionalProperties![ToolLoopImageChannel.MessageTagKey]);
        Assert.Equal("call-1", tag.CallId);
        Assert.Equal(800, tag.Width);
        Assert.Equal(600, tag.Height);
    }

    [Fact]
    public void Consume_ReplacesOnlyTaggedImageMessages_KeepsTheTag_ReturnsCount()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "look at this"),
            ToolLoopImageMessages.Build(Image()),
            new(ChatRole.Assistant, "I see it"),
        };

        var consumed = ToolLoopImageMessages.Consume(messages);

        Assert.Equal(1, consumed);
        Assert.Equal("look at this", messages[0].Text);
        Assert.Equal(ToolLoopImageMessages.Placeholder(800, 600), messages[1].Text);
        Assert.False(ToolLoopImageMessages.CarriesImage(messages[1]));
        Assert.True(ToolLoopImageMessages.IsTagged(messages[1]));
        Assert.Equal("I see it", messages[2].Text);
    }

    [Fact]
    public void Consume_IsIdempotent()
    {
        var messages = new List<ChatMessage> { ToolLoopImageMessages.Build(Image()) };

        Assert.Equal(1, ToolLoopImageMessages.Consume(messages));
        Assert.Equal(0, ToolLoopImageMessages.Consume(messages));
    }

    [Fact]
    public void Consume_LeavesAUsersOwnPastedImageAlone()
    {
        var pasted = new ChatMessage(
            ChatRole.User, [new TextContent("what is this?"), new DataContent(new byte[] { 9 }, "image/png")]);
        var messages = new List<ChatMessage> { pasted };

        Assert.Equal(0, ToolLoopImageMessages.Consume(messages));
        Assert.Same(pasted, messages[0]);
    }

    [Fact]
    public void Placeholder_NamesTheDimensionsAndSaysItIsSpent()
    {
        Assert.Equal("[screen capture, 4x2, consumed]", ToolLoopImageMessages.Placeholder(4, 2));
    }

    [Fact]
    public void CountImageMessages_CountsEveryImageBearingMessage()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "text only"),
            ToolLoopImageMessages.Build(Image()),
            new(ChatRole.User, [new TextContent("pasted"), new DataContent(new byte[] { 9 }, "image/png")]),
        };

        Assert.Equal(2, ToolLoopImageMessages.CountImageMessages(messages));

        ToolLoopImageMessages.Consume(messages);
        Assert.Equal(1, ToolLoopImageMessages.CountImageMessages(messages));
    }

    /// <summary>The reason nothing persists the bytes: the slice a run carries forward is tool content only.</summary>
    [Fact]
    public void Capture_DropsTheImageMessage()
    {
        var carried = AgentToolCarryover.Capture([ToolLoopImageMessages.Build(Image())]);

        Assert.Empty(carried);
    }
}
