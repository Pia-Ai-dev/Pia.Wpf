using System.Windows;
using System.Windows.Automation.Peers;
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
/// The id sweep in <see cref="ViewAutomationIdTests"/> only walks ButtonBase and friends, so it cannot see
/// that the rail card's title and body were addressable only by their shared x:Name - four cards, one id,
/// and a script silently reaching the first. This walks the real automation peer tree instead, which is what
/// a UIA client reads, and pins one distinct id per card per field.
/// </summary>
[Collection("WpfApplicationStatic")]
public class FlowRailCardAutomationTests
{
    private FlowView _view = null!;
    private FlowViewModel _vm = null!;
    private IFlowService _flow = null!;
    private List<FlowItem> _snapshot = null!;

    [Fact]
    public void EveryRailCard_ExposesItsOwnCardTitleBodyActionAndDismissIds()
    {
        var expected = WpfStaHost.Run(() => Expected(BuildRail(3)));
        WpfStaHost.Pump();

        // Twice with nothing in between: a peer subtree that only resolves on the first read is the failure
        // this test exists for.
        Assert.Equal(expected, WpfStaHost.Run(Survey));
        Assert.Equal(expected, WpfStaHost.Run(Survey));
    }

    [Fact]
    public void DismissingACard_LeavesEveryRemainingCardFullyAddressable()
    {
        WpfStaHost.Run(() => BuildRail(3));
        WpfStaHost.Pump();

        var expected = WpfStaHost.Run(() =>
        {
            _snapshot.RemoveAt(0);
            _flow.Changed += Raise.Event<EventHandler>(_flow, EventArgs.Empty);
            return Expected(_snapshot);
        });
        WpfStaHost.Pump();

        Assert.Equal(expected, WpfStaHost.Run(Survey));
    }

    private List<FlowItem> BuildRail(int cards)
    {
        _snapshot = [.. Enumerable.Range(1, cards).Select(n => Item("card " + n))];
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
        return _snapshot;
    }

    private static FlowItem Item(string title) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow,
        Severity = FlowSeverity.ActionRequired,
        Source = FlowSource.AgentRun,
        Title = title,
        Body = "body of " + title,
        DedupKey = Guid.NewGuid().ToString(),
        Lifetime = FlowLifetime.Persistent,
        Action = new ContinueRunAction(Guid.NewGuid(), "Continue run"),
    };

    /// <summary>The rail mirrors the store's snapshot order, so this does too.</summary>
    private static string[] Expected(IEnumerable<FlowItem> items) =>
        [.. items.Select(i =>
            $"Flow_Card_{i.Id}|Flow_Title_{i.Id}|Flow_Body_{i.Id}|Flow_ActionLink_{i.Id}"
            + $"|Flow_Decisions_{i.Id}|Flow_Dismiss_{i.Id}")];

    private string[] Survey()
    {
        _view.Measure(new Size(1000, 900));
        _view.Arrange(new Rect(0, 0, 1000, 900));
        _view.UpdateLayout();

        var root = UIElementAutomationPeer.CreatePeerForElement(_view)
            ?? throw new InvalidOperationException("FlowView no longer creates an automation peer");

        // A live UIA client gets this invalidation from WPF when the item collection changes; in-process
        // nobody is listening, so the walk would otherwise read a tree from before the reconcile.
        ResetSubtree(root);

        var cards = new List<string>();
        Collect(root, cards);
        return [.. cards];
    }

    private static void ResetSubtree(AutomationPeer peer)
    {
        peer.ResetChildrenCache();
        foreach (var child in peer.GetChildren() ?? [])
            ResetSubtree(child);
    }

    private static void Collect(AutomationPeer peer, List<string> cards)
    {
        if (peer.GetAutomationControlType() == AutomationControlType.DataItem)
        {
            var ids = new List<string> { peer.GetAutomationId() };
            Descendants(peer, ids);
            cards.Add(string.Join('|', ids));
            return;
        }

        foreach (var child in peer.GetChildren() ?? [])
            Collect(child, cards);
    }

    private static void Descendants(AutomationPeer peer, List<string> ids)
    {
        foreach (var child in peer.GetChildren() ?? [])
        {
            if (child.GetAutomationId() is { Length: > 0 } id)
                ids.Add(id);
            Descendants(child, ids);
        }
    }
}
