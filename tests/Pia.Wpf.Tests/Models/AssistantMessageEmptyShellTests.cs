using System.ComponentModel;
using Microsoft.Extensions.AI;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>
/// Every section of an assistant bubble hides itself on its own condition, so a message with nothing in it
/// renders as a lone avatar. Rows like that are already on disk from before headless runs stopped writing
/// contentless replies, and a cancelled turn still leaves one behind.
/// </summary>
public class AssistantMessageEmptyShellTests
{
    private static AssistantMessage Settled() => new(ChatRole.Assistant) { IsStreaming = false };

    [Fact]
    public void AMessageWithNothingInIt_IsAnEmptyShell()
    {
        Assert.True(Settled().IsEmptyShell);
    }

    [Fact]
    public void AStreamingMessage_IsNotAShell_ItIsStillArriving()
    {
        var msg = new AssistantMessage(ChatRole.Assistant) { IsStreaming = true };

        Assert.False(msg.IsEmptyShell);
    }

    [Fact]
    public void ContentAlone_IsEnoughToRender()
    {
        var msg = Settled();
        msg.Content = "the answer";

        Assert.False(msg.IsEmptyShell);
    }

    /// <summary>A contentless message that still carries a reasoning trace renders the "thought for" toggle.</summary>
    [Fact]
    public void AReasoningTrace_IsEnoughToRender()
    {
        var msg = Settled();
        msg.ThinkingContent = "weighing options";

        Assert.False(msg.IsEmptyShell);
    }

    [Fact]
    public void AFileChip_IsEnoughToRender()
    {
        var msg = Settled();
        msg.FileRefs.Add(new FileRef(@"C:\work\report.html", FileRefKind.Created));

        Assert.False(msg.IsEmptyShell);
    }

    /// <summary>The end of streaming is where an empty message settles, so that is where the view must be told.</summary>
    [Fact]
    public void TheEndOfStreaming_RaisesTheChange()
    {
        var msg = new AssistantMessage(ChatRole.Assistant) { IsStreaming = true };
        var raised = new List<string?>();
        ((INotifyPropertyChanged)msg).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        msg.IsStreaming = false;

        Assert.Contains(nameof(AssistantMessage.IsEmptyShell), raised);
        Assert.True(msg.IsEmptyShell);
    }

    [Fact]
    public void ArrivingContent_RaisesTheChange()
    {
        var msg = Settled();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)msg).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        msg.Content = "the answer";

        Assert.Contains(nameof(AssistantMessage.IsEmptyShell), raised);
        Assert.False(msg.IsEmptyShell);
    }
}
