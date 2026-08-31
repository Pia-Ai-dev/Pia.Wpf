using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Controls.Flow;
using Pia.Models;
using Pia.Models.Flow;
using Pia.Navigation;
using Pia.Services.Flow;
using Pia.Services.Interfaces;
using Pia.ViewModels.Flow;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The rail card's body carries the whole approval question, so it must read past one line without letting
/// one long body grow the row — nothing virtualizes this ItemsControl, so every card pays for it.
/// </summary>
[Collection("WpfApplicationStatic")]
public class FlowCardBodyBoundsTests
{
    private const int MaxBodyLines = 3;

    private FlowView _view = null!;
    private FlowViewModel _vm = null!;
    private IFlowService _flow = null!;
    private List<FlowItem> _snapshot = null!;

    [Fact]
    public void AnApprovalBody_RendersUpToThreeWrappedLines_WithAWrappingTooltip()
    {
        var body = ApprovalBody();
        WpfStaHost.Run(() => { BuildRail(body); return 0; });
        WpfStaHost.Pump();
        WpfStaHost.Run(Layout);
        WpfStaHost.Pump();

        var probe = WpfStaHost.Run(Probe);

        Assert.Equal(1, probe.Bodies);
        Assert.Equal(TextWrapping.Wrap, probe.Wrapping);
        Assert.Equal(TextTrimming.CharacterEllipsis, probe.Trimming);
        Assert.Equal(30000, probe.ShowDuration);

        // WPF's TextBlock has no MaxLines, so the three-line bound is a declared line height times three;
        // that only holds if the declared height still matches what the font would give on its own.
        Assert.Equal(LineStackingStrategy.BlockLineHeight, probe.Stacking);
        Assert.Equal(MaxBodyLines * probe.DeclaredLineHeight, probe.MaxHeight);
        Assert.True(
            Math.Abs(probe.DeclaredLineHeight - probe.NaturalLineHeight) <= 2,
            $"declared line height {probe.DeclaredLineHeight} px is not the font's {probe.NaturalLineHeight} px");

        // Both legs matter: without the upper bound a long body grows the row, and without the lower one a
        // body that laid out at a single line (or at nothing) satisfies the upper bound vacuously.
        Assert.True(
            probe.Height > probe.NaturalLineHeight * 1.5,
            $"the body did not wrap: {probe.Height} px at a {probe.NaturalLineHeight} px line height");
        Assert.True(
            probe.Height <= MaxBodyLines * probe.NaturalLineHeight + 2,
            $"the body exceeded {MaxBodyLines} lines: {probe.Height} px at a {probe.NaturalLineHeight} px line height");

        // A string tooltip renders one unwrapped line off the screen edge, so the explicit element is the point.
        Assert.Equal(nameof(ToolTip), probe.TooltipType);
        Assert.Equal(nameof(TextBlock), probe.TooltipChildType);
        Assert.Equal(TextWrapping.Wrap, probe.TooltipWrapping);
        Assert.Equal(body, probe.TooltipText);
    }

    [Fact]
    public void AnOversizedBody_StillCannotGrowTheCard()
    {
        WpfStaHost.Run(() => { BuildRail(OversizedBody()); return 0; });
        WpfStaHost.Pump();
        WpfStaHost.Run(Layout);
        WpfStaHost.Pump();

        var probe = WpfStaHost.Run(Probe);

        Assert.Equal(1, probe.Bodies);
        Assert.True(
            probe.Height > 0 && probe.Height <= MaxBodyLines * probe.NaturalLineHeight + 2,
            $"a 200k-char body arranged to {probe.Height} px at a {probe.NaturalLineHeight} px line height");
    }

    private static string ApprovalBody()
    {
        var args = string.Join(", ", Enumerable.Range(1, 18).Select(n => $"path: reports/quarter-{n}.md"));
        return "write_file — " + args[..Math.Min(args.Length, 440)];
    }

    /// <summary>Breakable text, not one pathological token: an unbreakable 200k word would prove nothing.</summary>
    private static string OversizedBody()
    {
        var text = new StringBuilder(200_000);
        for (var word = 1; text.Length < 200_000; word++)
            text.Append("recoverable").Append(word % 7 == 0 ? '\n' : ' ');
        return text.ToString();
    }

    private void BuildRail(string body)
    {
        _snapshot = [Item(body)];
        _flow = Substitute.For<IFlowService>();
        _flow.Snapshot.Returns(_ => (IReadOnlyList<FlowItem>)_snapshot);
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        _vm = new FlowViewModel(
            _flow,
            Substitute.For<IWindowManagerService>(),
            Substitute.For<IReminderService>(),
            settings,
            Substitute.For<INavigationService>(),
            Substitute.For<ILocalizationService>(),
            Substitute.For<IAgentRunResumeService>(),
            NullLogger<FlowViewModel>.Instance,
            NullLogger<FlowItemViewModel>.Instance)
        {
            IsExpanded = true,
        };

        _view = new FlowView { DataContext = _vm };
    }

    private static FlowItem Item(string body) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow,
        Severity = FlowSeverity.ActionRequired,
        Source = FlowSource.AgentRun,
        Title = "Approval needed",
        Body = body,
        DedupKey = Guid.NewGuid().ToString(),
        Lifetime = FlowLifetime.Persistent,
        Action = new ToolApprovalRunAction(Guid.NewGuid(), "write_file"),
    };

    private int Layout()
    {
        _view.Measure(new Size(1000, 900));
        _view.Arrange(new Rect(0, 0, 1000, 900));
        _view.UpdateLayout();

        // ToolTipService assigns PlacementTarget when the popup opens, and that is the only route the item
        // has into the tooltip; without it here the tooltip reports an empty Text whatever it is bound to.
        foreach (var candidate in Bodies())
            if (candidate.ToolTip is ToolTip tooltip)
                tooltip.PlacementTarget = candidate;

        return 0;
    }

    private BodyProbe Probe()
    {
        var bodies = Bodies();
        if (bodies.Count != 1)
            return new BodyProbe { Bodies = bodies.Count };

        var body = bodies[0];
        var tooltip = body.ToolTip as ToolTip;
        var tooltipChild = tooltip?.Content as TextBlock;

        return new BodyProbe
        {
            Bodies = 1,
            Wrapping = body.TextWrapping,
            Trimming = body.TextTrimming,
            Stacking = body.LineStackingStrategy,
            DeclaredLineHeight = body.LineHeight,
            MaxHeight = body.MaxHeight,
            ShowDuration = ToolTipService.GetShowDuration(body),
            Height = body.ActualHeight,
            NaturalLineHeight = OneLine(body),
            TooltipType = body.ToolTip?.GetType().Name ?? "<none>",
            TooltipChildType = tooltip?.Content?.GetType().Name ?? "<none>",
            TooltipWrapping = tooltipChild?.TextWrapping ?? TextWrapping.NoWrap,
            TooltipText = tooltipChild?.Text ?? string.Empty,
        };
    }

    private List<TextBlock> Bodies() =>
        [.. Descendants<TextBlock>(_view)
            .Where(t => AutomationProperties.GetAutomationId(t).StartsWith("Flow_Body_", StringComparison.Ordinal))];

    private static double OneLine(TextBlock body)
    {
        var probe = new TextBlock
        {
            FontFamily = body.FontFamily,
            FontSize = body.FontSize,
            FontStyle = body.FontStyle,
            FontWeight = body.FontWeight,
            FontStretch = body.FontStretch,
            Text = "Ag",
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Height;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var found in Descendants<T>(child))
                yield return found;
        }
    }

    /// <summary>WPF objects are thread-affine, so only these primitives cross back off the host thread.</summary>
    private sealed record BodyProbe
    {
        public int Bodies { get; init; }
        public TextWrapping Wrapping { get; init; }
        public TextTrimming Trimming { get; init; }
        public LineStackingStrategy Stacking { get; init; }
        public double DeclaredLineHeight { get; init; }
        public double MaxHeight { get; init; }
        public int ShowDuration { get; init; }
        public double Height { get; init; }
        public double NaturalLineHeight { get; init; }
        public string TooltipType { get; init; } = "<none>";
        public string TooltipChildType { get; init; } = "<none>";
        public TextWrapping TooltipWrapping { get; init; }
        public string TooltipText { get; init; } = string.Empty;
    }
}
