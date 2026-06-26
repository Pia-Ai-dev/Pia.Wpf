using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Models.Flow;
using Pia.Navigation;
using Pia.Services.Flow;
using Pia.Services.Interfaces;
using Pia.ViewModels.Flow;
using Xunit;

namespace Pia.Tests.ViewModels.Flow;

/// <summary>
/// Covers the Id-keyed store→VM reconcile (design §5, §9): wrapper identity is preserved for an
/// unchanged Id across a Changed event, newest-first order matches the snapshot, removed Ids drop
/// their wrapper, new Ids insert at their snapshot position, and a Changed event mid-decision keeps
/// the in-flight wrapper (and its IsBusy) instead of tearing it down.
/// </summary>
public class FlowViewModelReconcileTests
{
    private static FlowItem Item(Guid id, FlowSource source = FlowSource.TodoDeadline)
        => new()
        {
            Id = id,
            CreatedAt = DateTimeOffset.UtcNow,
            Severity = FlowSeverity.Info,
            Source = source,
            Title = "t",
            Body = "",
            DedupKey = Guid.NewGuid().ToString(),
            Lifetime = FlowLifetime.Persistent,
        };

    // Builds a VM whose store snapshot is backed by a reassignable closure, so we can change the
    // snapshot and raise Changed between assertions. The sync context is nulled before construction
    // so the VM's Post() runs Reconcile inline (deterministic) rather than queuing it.
    private static FlowViewModel CreateWith(out IFlowService flow, Func<IReadOnlyList<FlowItem>> snapshot)
    {
        flow = Substitute.For<IFlowService>();
        flow.Snapshot.Returns(_ => snapshot());

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        SynchronizationContext.SetSynchronizationContext(null);
        return new FlowViewModel(
            flow,
            Substitute.For<IWindowManagerService>(),
            Substitute.For<IReminderService>(),
            settings,
            Substitute.For<INavigationService>(),
            Substitute.For<ILocalizationService>(),
            NullLogger<FlowViewModel>.Instance,
            NullLogger<FlowItemViewModel>.Instance);
    }

    [Fact]
    public void Reconcile_UnchangedId_ReusesSameWrapperInstance()
    {
        var a = Item(Guid.NewGuid());
        var b = Item(Guid.NewGuid());
        IReadOnlyList<FlowItem> snapshot = new[] { a, b };
        var vm = CreateWith(out var flow, () => snapshot);

        Assert.Equal(2, vm.Items.Count);
        var wrapperA = vm.Items[0];
        var wrapperB = vm.Items[1];

        // Same snapshot contents, new Changed event.
        flow.Changed += Raise.Event<EventHandler>(flow, EventArgs.Empty);

        Assert.Same(wrapperA, vm.Items[0]);
        Assert.Same(wrapperB, vm.Items[1]);
    }

    [Fact]
    public void Reconcile_RemovedId_DropsItsWrapper()
    {
        var a = Item(Guid.NewGuid());
        var b = Item(Guid.NewGuid());
        IReadOnlyList<FlowItem> snapshot = new[] { a, b };
        var vm = CreateWith(out var flow, () => snapshot);
        var wrapperA = vm.Items[0];

        snapshot = new[] { a };
        flow.Changed += Raise.Event<EventHandler>(flow, EventArgs.Empty);

        Assert.Single(vm.Items);
        Assert.Same(wrapperA, vm.Items[0]);
    }

    [Fact]
    public void Reconcile_NewId_InsertsAtSnapshotOrderedPosition()
    {
        var a = Item(Guid.NewGuid());
        var b = Item(Guid.NewGuid());
        IReadOnlyList<FlowItem> snapshot = new[] { a, b };
        var vm = CreateWith(out var flow, () => snapshot);
        var wrapperA = vm.Items[0];
        var wrapperB = vm.Items[1];

        // A newer item arrives at the front (newest-first).
        var c = Item(Guid.NewGuid());
        snapshot = new[] { c, a, b };
        flow.Changed += Raise.Event<EventHandler>(flow, EventArgs.Empty);

        Assert.Equal(3, vm.Items.Count);
        Assert.Equal(c.Id, vm.Items[0].Item.Id);
        Assert.Same(wrapperA, vm.Items[1]);
        Assert.Same(wrapperB, vm.Items[2]);
    }

    [Fact]
    public void Reconcile_ReorderedSnapshot_MatchesNewestFirstOrder()
    {
        var a = Item(Guid.NewGuid());
        var b = Item(Guid.NewGuid());
        var c = Item(Guid.NewGuid());
        IReadOnlyList<FlowItem> snapshot = new[] { a, b, c };
        var vm = CreateWith(out var flow, () => snapshot);
        var wrapperA = vm.Items[0];
        var wrapperC = vm.Items[2];

        // c republished → bumped to newest (front); reconcile must move the SAME wrapper.
        snapshot = new[] { c, a, b };
        flow.Changed += Raise.Event<EventHandler>(flow, EventArgs.Empty);

        Assert.Same(wrapperC, vm.Items[0]);
        Assert.Same(wrapperA, vm.Items[1]);
    }

    [Fact]
    public void Reconcile_ChangedWhileWrapperBusy_KeepsWrapperAndIsBusy()
    {
        var a = Item(Guid.NewGuid());
        var b = Item(Guid.NewGuid());
        IReadOnlyList<FlowItem> snapshot = new[] { a, b };
        var vm = CreateWith(out var flow, () => snapshot);
        var wrapperA = vm.Items[0];

        // Simulate an in-flight decision.
        wrapperA.IsBusy = true;

        // A poller Changed event arrives with the same items.
        flow.Changed += Raise.Event<EventHandler>(flow, EventArgs.Empty);

        Assert.Same(wrapperA, vm.Items[0]);
        Assert.True(vm.Items[0].IsBusy);
    }
}
