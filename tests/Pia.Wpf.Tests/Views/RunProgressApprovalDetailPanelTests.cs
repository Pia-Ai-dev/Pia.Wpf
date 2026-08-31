using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Controls.Assistant;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The band's approval line is capped at 400 chars, so the disclosure below it is the only place the person
/// deciding can read what the call will act on.
/// </summary>
[Collection("WpfApplicationStatic")]
public class RunProgressApprovalDetailPanelTests
{
    private const double MaxDetailHeight = 220;

    [Fact]
    public void TheFullCallFoldRow_AppearsOnlyWhenThereIsADetail_AndRevealsIt()
    {
        const string detail = "path=reports/q3.md\ncontent=the first line\nand the second";

        RunProgressViewModel? vm = null;
        RunProgressPanel? panel = null;
        Probe empty, withDetail, expanded;
        try
        {
            WpfStaHost.Run(() =>
            {
                vm = CreateViewModel();
                panel = new RunProgressPanel { DataContext = vm };
                return 0;
            });
            WpfStaHost.Pump();

            // Visibility defaults to Visible, so these Collapsed readings are the ones that bite.
            empty = WpfStaHost.Run(() => Take(panel!));

            WpfStaHost.Run(() => { vm!.ApprovalDetailText = detail; return 0; });
            WpfStaHost.Pump();
            withDetail = WpfStaHost.Run(() => Take(panel!));

            WpfStaHost.Run(() =>
            {
                var toggle = FoldRow(panel!);
                Assert.True(ReferenceEquals(toggle.Command, vm!.ToggleApprovalDetailCommand));
                toggle.Command!.Execute(null);
                return 0;
            });
            WpfStaHost.Pump();
            expanded = WpfStaHost.Run(() => Take(panel!));
        }
        finally
        {
            WpfStaHost.Run(() => { vm?.Dispose(); return 0; });
        }

        Assert.Equal(Visibility.Collapsed, empty.FoldRow);
        Assert.Equal(Visibility.Collapsed, empty.Box);

        Assert.Equal(Visibility.Visible, withDetail.FoldRow);
        Assert.Equal(Visibility.Collapsed, withDetail.Box);

        Assert.Equal(Visibility.Visible, expanded.Box);
        Assert.Equal(detail, expanded.Text);
        Assert.Contains('\n', expanded.Text);
    }

    [Fact]
    public void TheExpandedDetail_ScrollsInsteadOfGrowingTheRunCard()
    {
        RunProgressViewModel? vm = null;
        RunProgressPanel? panel = null;
        Probe probe;
        try
        {
            WpfStaHost.Run(() =>
            {
                vm = CreateViewModel();
                vm.IsCardExpanded = true;
                vm.ApprovalDetailText = LongArguments();
                vm.IsApprovalDetailExpanded = true;
                panel = new RunProgressPanel { DataContext = vm };
                return 0;
            });
            WpfStaHost.Pump();

            WpfStaHost.Run(() =>
            {
                panel!.Measure(new Size(640, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, 640, panel.DesiredSize.Height));
                panel.UpdateLayout();
                return 0;
            });
            WpfStaHost.Pump();

            probe = WpfStaHost.Run(() => Take(panel!));
        }
        finally
        {
            WpfStaHost.Run(() => { vm?.Dispose(); return 0; });
        }

        Assert.Equal(Visibility.Visible, probe.Box);
        Assert.True(probe.ViewportHeight > 0 && probe.ViewportHeight <= MaxDetailHeight,
            $"the detail viewport arranged to {probe.ViewportHeight} px against a {MaxDetailHeight} px bound");
        Assert.True(probe.TextHeight > MaxDetailHeight * 2,
            $"the 8000-char detail only wanted {probe.TextHeight} px, so the bound was never exercised");
        Assert.True(probe.ScrollableHeight > 0,
            "the detail is clipped rather than scrolled, so the tail of the call cannot be read");
    }

    [Fact]
    public void TheShortenedNote_ShowsOnlyWhenTheDisplayCapBit()
    {
        RunProgressViewModel? vm = null;
        RunProgressPanel? panel = null;
        Probe whole, shortened;
        try
        {
            WpfStaHost.Run(() =>
            {
                vm = CreateViewModel();
                vm.ApprovalDetailText = "path=notes.md";
                vm.IsApprovalDetailExpanded = true;
                panel = new RunProgressPanel { DataContext = vm };
                return 0;
            });
            WpfStaHost.Pump();
            whole = WpfStaHost.Run(() => Take(panel!));

            WpfStaHost.Run(() => { vm!.IsApprovalDetailShortened = true; return 0; });
            WpfStaHost.Pump();
            shortened = WpfStaHost.Run(() => Take(panel!));
        }
        finally
        {
            WpfStaHost.Run(() => { vm?.Dispose(); return 0; });
        }

        Assert.Equal(Visibility.Collapsed, whole.Note);
        Assert.Equal(Visibility.Visible, shortened.Note);

        // By path, not by rendered text: loc:Str resolves against the real LocalizationSource.
        Assert.Equal("[Run_ToolApproval_DetailShortened]", shortened.NotePath);
    }

    /// <summary>Breakable text with newlines, so it wraps the way real arguments do.</summary>
    private static string LongArguments()
    {
        var text = new StringBuilder(8000);
        for (var word = 1; text.Length < 8000; word++)
            text.Append("reports/").Append(word).Append(word % 6 == 0 ? '\n' : ' ');
        return text.ToString(0, 8000);
    }

    private static ButtonBase FoldRow(RunProgressPanel panel) =>
        BindingPathWalker.FindLogical<ButtonBase>(panel)
            .Single(b => BindingPathWalker.PathOf(b, ButtonBase.CommandProperty) == "ToggleApprovalDetailCommand");

    private static Border DetailBox(RunProgressPanel panel) =>
        BindingPathWalker.FindLogical<Border>(panel)
            .Single(b => BindingPathWalker.PathOf(b, UIElement.VisibilityProperty) == "ShowApprovalDetail");

    private static Probe Take(RunProgressPanel panel)
    {
        var box = DetailBox(panel);
        var scroller = BindingPathWalker.FindLogical<ScrollViewer>(box).Single();
        var body = BindingPathWalker.FindLogical<TextBlock>(box)
            .Single(t => BindingPathWalker.PathOf(t, TextBlock.TextProperty) == "ApprovalDetailText");
        var note = BindingPathWalker.FindLogical<TextBlock>(box)
            .Single(t => BindingPathWalker.PathOf(t, UIElement.VisibilityProperty) == "IsApprovalDetailShortened");

        return new Probe
        {
            FoldRow = FoldRow(panel).Visibility,
            Box = box.Visibility,
            Text = body.Text,
            ViewportHeight = scroller.ViewportHeight,
            ScrollableHeight = scroller.ScrollableHeight,
            TextHeight = body.DesiredSize.Height,
            Note = note.Visibility,
            NotePath = BindingPathWalker.PathOf(note, TextBlock.TextProperty) ?? string.Empty,
        };
    }

    private static RunProgressViewModel CreateViewModel()
    {
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{(string)ci[0]}|{string.Join(",", (object[])ci[1])}");

        return new RunProgressViewModel(
            Substitute.For<IAgentRunService>(), Guid.NewGuid(), loc,
            Substitute.For<IAgentRunResumeService>(), NullLogger.Instance);
    }

    /// <summary>WPF objects are thread-affine, so only these primitives cross back off the host thread.</summary>
    private sealed record Probe
    {
        public Visibility FoldRow { get; init; }
        public Visibility Box { get; init; }
        public string Text { get; init; } = string.Empty;
        public double ViewportHeight { get; init; }
        public double ScrollableHeight { get; init; }
        public double TextHeight { get; init; }
        public Visibility Note { get; init; }
        public string NotePath { get; init; } = string.Empty;
    }
}
