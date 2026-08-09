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

    // The snapshot is a reassignable closure so it can change between assertions, and the sync context is
    // nulled so the VM's Post() runs Reconcile inline rather than queuing it.
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
            Substitute.For<IAgentRunResumeService>(),
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

        wrapperA.IsBusy = true;

        flow.Changed += Raise.Event<EventHandler>(flow, EventArgs.Empty);

        Assert.Same(wrapperA, vm.Items[0]);
        Assert.True(vm.Items[0].IsBusy);
    }
}
