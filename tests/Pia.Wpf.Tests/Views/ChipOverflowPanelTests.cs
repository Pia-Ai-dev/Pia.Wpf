using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.AI;
using Pia.Controls.Chat;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>The chip rows fold at three: a fourth chip becomes a "+N" dropdown holding the rest. Driven
/// through the real <see cref="PiaAssistantMessage"/> so the bindings are the ones the chat renders.</summary>
[Collection("WpfApplicationStatic")]
public class ChipOverflowPanelTests
{
    [Fact]
    public void FileChips_FoldAtThree_WithTheRestInTheDropdown()
    {
        var message = new AssistantMessage(ChatRole.Assistant, "answer");
        foreach (var name in new[] { "one.txt", "two.txt", "three.txt", "four.txt", "five.txt" })
            message.FileRefs.Add(new FileRef($@"C:\work\{name}", FileRefKind.Read));

        PiaAssistantMessage? view = null;
        PiaChipOverflowPanel? panel = null;

        WpfStaHost.Run(() =>
        {
            view = new PiaAssistantMessage { DataContext = message };
            return 0;
        });
        WpfStaHost.Pump();

        PiaFileChip? chip = null;

        // A ContentPresenter with the right Content and a broken ContentTemplate renders NOTHING, so the
        // template is instantiated the way WPF would and read back through the chip's own DP.
        WpfStaHost.Run(() =>
        {
            panel = PanelFor(view!, nameof(AssistantMessage.FileRefs));
            chip = (PiaFileChip)panel.ItemTemplate!.LoadContent();
            chip.DataContext = panel.Slot1;
            return 0;
        });
        WpfStaHost.Pump();

        (bool slotsMatch, bool slotsVisible, bool templatesMatch, string? label, string[] overflow,
            Visibility dropdownVisibility, string? popupItemsPath, string? popupTemplatePath,
            string? realizedFileName) observed = WpfStaHost.Run(() =>
        {
            var slots = SlotPresenters(panel!);
            var matches = slots.Select(s => s.Content).SequenceEqual(message.FileRefs.Take(3));
            var visible = slots.All(s => s.Visibility == Visibility.Visible);
            var sameTemplate = slots.All(s => ReferenceEquals(s.ContentTemplate, panel!.ItemTemplate));

            var popupItems = BindingPathWalker.FindLogical<ItemsControl>(panel!).Single();

            return (matches, visible, sameTemplate,
                panel!.OverflowLabel,
                panel.OverflowItems!.Cast<FileRef>().Select(f => f.FileName).ToArray(),
                DropdownHost(panel).Visibility,
                BindingPathWalker.PathOf(popupItems, ItemsControl.ItemsSourceProperty),
                BindingPathWalker.PathOf(popupItems, ItemsControl.ItemTemplateProperty),
                chip!.FileName);
        });

        Assert.True(observed.slotsMatch, "the three inline slots do not hold the first three FileRefs");
        Assert.True(observed.slotsVisible, "an inline slot holding an item is not Visible");
        Assert.True(observed.templatesMatch, "an inline slot does not carry the panel's ItemTemplate");

        // The template genuinely binds against a FileRef — an unbound DP would read as null here.
        Assert.Equal("one.txt", observed.realizedFileName);

        Assert.Equal("+2", observed.label);
        Assert.Equal(["four.txt", "five.txt"], observed.overflow);
        Assert.Equal(Visibility.Visible, observed.dropdownVisibility);
        Assert.Equal(nameof(PiaChipOverflowPanel.OverflowItems), observed.popupItemsPath);
        Assert.Equal(nameof(PiaChipOverflowPanel.ItemTemplate), observed.popupTemplatePath);
    }

    // The Visible leg above is vacuous on its own — Visibility defaults to Visible — so the fold is also
    // observed from below, and across the mutation that crosses the boundary mid-turn.
    [Fact]
    public void ThreeChips_ShowNoDropdown_UntilAFourthArrives()
    {
        var message = new AssistantMessage(ChatRole.Assistant, "answer");
        foreach (var name in new[] { "one.txt", "two.txt", "three.txt" })
            message.FileRefs.Add(new FileRef($@"C:\work\{name}", FileRefKind.Read));

        PiaAssistantMessage? view = null;
        PiaChipOverflowPanel? panel = null;

        WpfStaHost.Run(() =>
        {
            view = new PiaAssistantMessage { DataContext = message };
            return 0;
        });
        WpfStaHost.Pump();

        var (hasOverflowBefore, visibilityBefore, thirdSlotFilled) = WpfStaHost.Run(() =>
        {
            panel = PanelFor(view!, nameof(AssistantMessage.FileRefs));
            return (panel.HasOverflow, DropdownHost(panel).Visibility, panel.Slot3 is not null);
        });

        WpfStaHost.Run(() =>
        {
            message.FileRefs.Add(new FileRef(@"C:\work\four.txt", FileRefKind.Read));
            return 0;
        });
        WpfStaHost.Pump();

        var (label, visibilityAfter, overflowCount) = WpfStaHost.Run(() =>
            (panel!.OverflowLabel, DropdownHost(panel).Visibility, panel.OverflowItems!.Count));

        Assert.True(thirdSlotFilled, "the third chip is not shown inline");
        Assert.False(hasOverflowBefore);
        Assert.Equal(Visibility.Collapsed, visibilityBefore);

        // The fourth chip is the dropdown, and it holds exactly the one item that no longer fits.
        Assert.Equal(Visibility.Visible, visibilityAfter);
        Assert.Equal("+1", label);
        Assert.Equal(1, overflowCount);
    }

    [Fact]
    public void SourceChips_UseTheSameFold()
    {
        var message = new AssistantMessage(ChatRole.Assistant, "answer");
        for (var i = 1; i <= 5; i++)
            message.Sources.Add(new SourceRef(i, $"site{i}.example", "meta", $"https://site{i}.example/a"));

        PiaAssistantMessage? view = null;

        WpfStaHost.Run(() =>
        {
            view = new PiaAssistantMessage { DataContext = message };
            return 0;
        });
        WpfStaHost.Pump();

        var (label, inlineNumbers, overflowNumbers, templateType) = WpfStaHost.Run(() =>
        {
            var panel = PanelFor(view!, nameof(AssistantMessage.Sources));
            return (panel.OverflowLabel,
                SlotPresenters(panel).Select(s => ((SourceRef)s.Content).Number).ToArray(),
                panel.OverflowItems!.Cast<SourceRef>().Select(s => s.Number).ToArray(),
                panel.ItemTemplate!.LoadContent().GetType().Name);
        });

        Assert.Equal("+2", label);
        Assert.Equal([1, 2, 3], inlineNumbers);
        Assert.Equal([4, 5], overflowNumbers);
        Assert.Equal(nameof(PiaSourceChip), templateType);
    }

    // Located by the path the markup binds, never by index: a measured walk order is not a contract.
    private static PiaChipOverflowPanel PanelFor(PiaAssistantMessage view, string itemsPath) =>
        BindingPathWalker.FindLogical<PiaChipOverflowPanel>(view)
            .Single(p => BindingPathWalker.PathOf(p, PiaChipOverflowPanel.ItemsSourceProperty) == itemsPath);

    private static ContentPresenter[] SlotPresenters(PiaChipOverflowPanel panel) =>
        [.. new[]
        {
            nameof(PiaChipOverflowPanel.Slot1),
            nameof(PiaChipOverflowPanel.Slot2),
            nameof(PiaChipOverflowPanel.Slot3),
        }.Select(slot => BindingPathWalker.FindLogical<ContentPresenter>(panel)
            .Single(cp => BindingPathWalker.PathOf(cp, ContentPresenter.ContentProperty) == slot))];

    private static FrameworkElement DropdownHost(PiaChipOverflowPanel panel) =>
        BindingPathWalker.FindLogical<Grid>(panel)
            .Single(g => BindingPathWalker.PathOf(g, UIElement.VisibilityProperty)
                == nameof(PiaChipOverflowPanel.HasOverflow));
}
