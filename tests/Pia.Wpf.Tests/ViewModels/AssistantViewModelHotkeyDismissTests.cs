using Pia.Tests.Views;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The window hotkey may hide the Assistant only when there is nothing to lose by hiding it, the way
/// it already may for Optimize.
/// </summary>
public sealed class AssistantViewModelHotkeyDismissTests
{
    [Fact]
    public void AnIdleEmptyComposerCanBeDismissed()
    {
        var can = WpfStaHost.Run(() => AssistantViewModelBuilder.Create().CanDismissWithHotkey);
        Assert.True(can);
    }

    [Fact]
    public void AStreamingTurnCannotBeDismissed()
    {
        var can = WpfStaHost.Run(() =>
        {
            var vm = AssistantViewModelBuilder.Create();
            vm.IsStreaming = true;
            return vm.CanDismissWithHotkey;
        });

        Assert.False(can);
    }

    [Fact]
    public void AnAttachedFileCannotBeDismissed()
    {
        var can = WpfStaHost.Run(() =>
        {
            var vm = AssistantViewModelBuilder.Create();
            vm.PendingFiles.Add(new Pia.Models.PendingFileAttachment
            {
                FullPath = @"C:\work\notes.txt",
                FileName = "notes.txt",
                Kind = Pia.Models.PendingFileKind.Text,
                Text = "body",
                Truncated = false,
                OriginalCharCount = 4,
            });
            return vm.CanDismissWithHotkey;
        });

        Assert.False(can);
    }
}
