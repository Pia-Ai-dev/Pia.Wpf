using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;
using Pia.Controls.Chat;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// A vault/chat chip has to reach the ViewModel through a RelativeSource hop out of the overflow panel's
/// DataTemplate — the one binding here that fails silently. Driven through the real controls and a real
/// layout pass, because the template is only instantiated inside the visual tree.
/// </summary>
[Collection("WpfApplicationStatic")]
public class SourceChipOpenCommandTests
{
    [Fact]
    public void VaultChip_Click_InvokesOpenSourceCommandWithItsSourceRef()
    {
        var vault = new SourceRef(0, "coffee", "Espresso", Kind: SourceRefKind.VaultPage, Target: "topics/coffee");
        var opened = Clicked(vault);

        Assert.NotNull(opened);
        Assert.Equal("topics/coffee", opened!.Target);
        Assert.Equal(SourceRefKind.VaultPage, opened.Kind);
    }

    [Fact]
    public void ChatChip_Click_InvokesOpenSourceCommandWithItsSourceRef()
    {
        var id = Guid.NewGuid();
        var chat = new SourceRef(0, "Hetzner", "2026-08-19", Kind: SourceRefKind.Chat, Target: id.ToString());

        Assert.Equal(id.ToString(), Clicked(chat)?.Target);
    }

    [Fact]
    public void WebChip_Click_DoesNotReachTheCommand()
    {
        var web = new SourceRef(1, "example.com", "anchor", "https://example.com/a");

        // Opening the browser is the chip's own job; the VM must not see it.
        Assert.Null(Clicked(web, clickOpensBrowser: true));
    }

    [Fact]
    public void ChipsInOneMessage_ReportDistinctAutomationIds()
    {
        var message = new AssistantMessage(ChatRole.Assistant, "answer");
        message.AddSource(new SourceRef(0, "coffee", "", Kind: SourceRefKind.VaultPage, Target: "topics/coffee"));
        message.AddSource(new SourceRef(0, "tea", "", Kind: SourceRefKind.VaultPage, Target: "topics/tea"));
        message.AddSource(new SourceRef(1, "example.com", "anchor", "https://example.com/a"));

        var ids = WpfStaHost.Run(() =>
        {
            var view = Rendered(message);
            return Descendants(view).OfType<PiaSourceChip>()
                .SelectMany(c => Descendants(c).OfType<Button>().Take(1))
                .Select(AutomationProperties.GetAutomationId)
                .ToArray();
        });
        WpfStaHost.Pump();

        Assert.Equal(
            ["SourceChip_Open_1", "SourceChip_Open_2", "SourceChip_Open_3"],
            ids);
    }

    [Fact]
    public void NonWebChip_ShowsItsGlyphInsteadOfTheMeaninglessZero()
    {
        var message = new AssistantMessage(ChatRole.Assistant, "answer");
        message.AddSource(new SourceRef(0, "coffee", "", Kind: SourceRefKind.VaultPage, Target: "topics/coffee"));
        message.AddSource(new SourceRef(1, "example.com", "anchor", "https://example.com/a"));

        var observed = WpfStaHost.Run(() =>
        {
            var chips = Descendants(Rendered(message)).OfType<PiaSourceChip>().ToArray();
            return chips.Select(c => (
                Number: Descendants(c).OfType<TextBlock>().First().Visibility,
                Glyph: Descendants(c).OfType<Wpf.Ui.Controls.SymbolIcon>().First().Visibility))
                .ToArray();
        });
        WpfStaHost.Pump();

        Assert.Equal((Visibility.Collapsed, Visibility.Visible), observed[0]);
        Assert.Equal((Visibility.Visible, Visibility.Collapsed), observed[1]);
    }

    private static PiaAssistantMessage Rendered(AssistantMessage message)
    {
        var view = new PiaAssistantMessage { DataContext = message };
        view.Measure(new Size(1000, 1000));
        view.Arrange(new Rect(0, 0, 1000, 1000));
        view.UpdateLayout();
        return view;
    }

    private static SourceRef? Clicked(SourceRef source, bool clickOpensBrowser = false)
    {
        var message = new AssistantMessage(ChatRole.Assistant, "answer");
        message.AddSource(source);

        SourceRef? opened = null;
        var command = new RelayCommand<SourceRef>(s => opened = s);

        WpfStaHost.Run(() =>
        {
            var view = new PiaAssistantMessage { DataContext = message, OpenSourceCommand = command };
            view.Measure(new Size(1000, 1000));
            view.Arrange(new Rect(0, 0, 1000, 1000));
            view.UpdateLayout();

            var chip = Descendants(view).OfType<PiaSourceChip>().FirstOrDefault();
            Assert.NotNull(chip);
            Assert.Equal(source.Kind, chip!.Kind);
            Assert.Same(message.Sources[0], chip.Reference);

            // A web chip's click would ShellExecute; only the in-app kinds are pressed here.
            if (!clickOpensBrowser)
            {
                Descendants(chip).OfType<Button>().First().RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
            else
            {
                Assert.NotNull(chip.OpenCommand);
            }

            return 0;
        });
        WpfStaHost.Pump();

        return opened;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }
}
