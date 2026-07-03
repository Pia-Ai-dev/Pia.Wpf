using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.AI;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

public class AssistantMessageFileRefsTests
{
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
    public void AddFileRef_FlipsHasFileRefs_AndRaisesPropertyChanged()
    {
        var msg = new AssistantMessage(ChatRole.Assistant);
        var raised = new List<string>();
        msg.PropertyChanged += (_, e) => { if (e.PropertyName is { } n) raised.Add(n); };

        Assert.False(msg.HasFileRefs);
        msg.AddOrUpgradeFileRef(new FileRef(@"C:\work\a.txt", FileRefKind.Read));

        Assert.True(msg.HasFileRefs);
        Assert.Single(msg.FileRefs);
        Assert.Contains(nameof(AssistantMessage.HasFileRefs), raised);
    }

    [Fact]
    public void AddOrUpgradeFileRef_DedupesByPath_CaseInsensitive()
    {
        var msg = new AssistantMessage(ChatRole.Assistant);
        msg.AddOrUpgradeFileRef(new FileRef(@"C:\work\a.txt", FileRefKind.Read));
        msg.AddOrUpgradeFileRef(new FileRef(@"C:\WORK\A.TXT", FileRefKind.Read));

        Assert.Single(msg.FileRefs);
    }

    [Fact]
    public void AddOrUpgradeFileRef_KeepsHigherPrecedenceKind()
    {
        var msg = new AssistantMessage(ChatRole.Assistant);
        msg.AddOrUpgradeFileRef(new FileRef(@"C:\work\a.txt", FileRefKind.Created));
        // A later write of the same file must NOT downgrade "Created" to "Updated".
        msg.AddOrUpgradeFileRef(new FileRef(@"C:\work\a.txt", FileRefKind.Updated));

        Assert.Single(msg.FileRefs);
        Assert.Equal(FileRefKind.Created, msg.FileRefs[0].Kind);
    }

    [Fact]
    public void AddOrUpgradeFileRef_UpgradesToHigherKind()
    {
        var msg = new AssistantMessage(ChatRole.Assistant);
        msg.AddOrUpgradeFileRef(new FileRef(@"C:\work\a.txt", FileRefKind.Read));
        msg.AddOrUpgradeFileRef(new FileRef(@"C:\work\a.txt", FileRefKind.Exported));

        Assert.Single(msg.FileRefs);
        Assert.Equal(FileRefKind.Exported, msg.FileRefs[0].Kind);
    }

    [Fact]
    public void FileRef_FileName_IsDerivedFromPath()
    {
        var fileRef = new FileRef(@"C:\work\notes\report.html", FileRefKind.Exported);
        Assert.Equal("report.html", fileRef.FileName);
    }

    [Fact]
    public void ToChatMessage_WithOverrideText_NoAttachment_ReturnsPlainText()
    {
        var msg = new AssistantMessage(ChatRole.User, "displayed prompt");

        var chat = msg.ToChatMessage("ai-visible text");

        Assert.Equal(ChatRole.User, chat.Role);
        Assert.Equal("ai-visible text", chat.Text);
        Assert.DoesNotContain(chat.Contents, c => c is DataContent);
    }

    [Fact]
    public void ToChatMessage_WithOverrideText_PreservesImageAttachment()
    {
        var msg = new AssistantMessage(ChatRole.User, "displayed prompt") { Attachment = NewAttachment() };

        var chat = msg.ToChatMessage("ai-visible text");

        // The fix: the override-text path must keep the image (the prior text-only path dropped it).
        Assert.Contains(chat.Contents, c => c is DataContent);
        Assert.Contains(chat.Contents, c => c is TextContent t && t.Text == "ai-visible text");
    }
}
