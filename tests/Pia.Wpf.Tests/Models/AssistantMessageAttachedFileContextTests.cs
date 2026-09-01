using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.AI;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>Both <c>ToChatMessage</c> overloads and both attachment shapes have to carry the attached-file
/// block; a two-branch edit that only handles the image case loses every text-only attachment.</summary>
public sealed class AssistantMessageAttachedFileContextTests
{
    private const string Block =
        "The user attached the following file(s) to this message. Use them as context for the request.\n\n" +
        "<attached_file name=\"notes.txt\" type=\"text\">\nhello\n</attached_file>";

    private static ImageAttachment NewAttachment()
    {
        var thumb = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        return new ImageAttachment
        {
            JpegBytes = [1, 2, 3, 4],
            MimeType = "image/jpeg",
            Width = 1,
            Height = 1,
            Thumbnail = thumb,
        };
    }

    [Fact]
    public void ToChatMessage_NoAttachment_AppendsAttachedFileContext()
    {
        var msg = new AssistantMessage(ChatRole.User, "summarize this") { AttachedFileContext = Block };

        var chat = msg.ToChatMessage();

        Assert.Equal($"summarize this\n\n{Block}", chat.Text);
        Assert.Empty(chat.Contents.OfType<DataContent>());
    }

    [Fact]
    public void ToChatMessage_WithOverrideText_NoAttachment_AppendsAttachedFileContext()
    {
        var msg = new AssistantMessage(ChatRole.User, "displayed prompt") { AttachedFileContext = Block };

        var chat = msg.ToChatMessage("ai-visible text");

        Assert.Equal($"ai-visible text\n\n{Block}", chat.Text);
        Assert.Empty(chat.Contents.OfType<DataContent>());
    }

    [Fact]
    public void ToChatMessage_WithImage_AppendsAttachedFileContextToTheTextContent()
    {
        var msg = new AssistantMessage(ChatRole.User, "summarize this")
        {
            Attachment = NewAttachment(),
            AttachedFileContext = Block,
        };

        var chat = msg.ToChatMessage();

        Assert.Collection(chat.Contents,
            c => Assert.Equal($"summarize this\n\n{Block}", Assert.IsType<TextContent>(c).Text),
            c => Assert.IsType<DataContent>(c));
    }

    [Fact]
    public void ToChatMessage_WithOverrideText_AndImage_AppendsAttachedFileContext()
    {
        var msg = new AssistantMessage(ChatRole.User, "displayed prompt")
        {
            Attachment = NewAttachment(),
            AttachedFileContext = Block,
        };

        var chat = msg.ToChatMessage("ai-visible text");

        Assert.Collection(chat.Contents,
            c => Assert.Equal($"ai-visible text\n\n{Block}", Assert.IsType<TextContent>(c).Text),
            c => Assert.IsType<DataContent>(c));
    }

    [Fact]
    public void ToChatMessage_EmptyText_UsesTheAttachedFileContextAlone()
    {
        var msg = new AssistantMessage(ChatRole.User, string.Empty) { AttachedFileContext = Block };

        var chat = msg.ToChatMessage();

        Assert.Equal(Block, chat.Text);
        Assert.DoesNotContain("\n\n\n", chat.Text);
    }

    [Fact]
    public void ToChatMessage_NoAttachedFileContext_IsUnchanged()
    {
        var plain = new AssistantMessage(ChatRole.User, "displayed prompt");
        var withImage = new AssistantMessage(ChatRole.User, "displayed prompt") { Attachment = NewAttachment() };

        Assert.Equal("displayed prompt", plain.ToChatMessage().Text);
        Assert.Empty(plain.ToChatMessage().Contents.OfType<DataContent>());
        Assert.Equal("ai-visible text", plain.ToChatMessage("ai-visible text").Text);

        Assert.Collection(withImage.ToChatMessage().Contents,
            c => Assert.Equal("displayed prompt", Assert.IsType<TextContent>(c).Text),
            c => Assert.IsType<DataContent>(c));
    }
}
