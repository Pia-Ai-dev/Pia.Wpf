using NSubstitute;
using Pia.Models.Flow;
using Pia.Services.Flow;
using Pia.Services.Interfaces;
using Pia.Services.Screen;
using Xunit;

namespace Pia.Tests.Services.Screen;

/// <summary>The unattended capability exists without the user being asked, so it must not exist without them
/// finding out — and a three-second toast is unseen by definition here.</summary>
public class ScreenCaptureIndicatorTests
{
    private static (ScreenCaptureIndicator sut, IFlowService flow, List<FlowItemDraft> drafts) Create()
    {
        var flow = Substitute.For<IFlowService>();
        var drafts = new List<FlowItemDraft>();
        flow.Publish(Arg.Do<FlowItemDraft>(drafts.Add)).Returns(_ => new FlowItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.Now,
            Severity = FlowSeverity.Info,
            Source = FlowSource.InAppToast,
            Title = "",
            Body = "",
            Lifetime = FlowLifetime.Persistent,
        });

        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{ci.ArgAt<string>(0)}({string.Join(",", ci.ArgAt<object[]>(1))})");

        return (new ScreenCaptureIndicator(flow, localization), flow, drafts);
    }

    private static ScreenCaptureAuditEvent Event(
        string surface = ScreenCaptureSurfaces.Unattended,
        string? granter = "background:run-1",
        string processName = "outlook",
        string targetKind = ScreenCaptureTargetKinds.Window,
        string? title = "Inbox - Outlook") =>
        new(surface, Guid.NewGuid(), granter, targetKind, processName, title, 1568, 880);

    [Fact]
    public void UnattendedCapture_PublishesOnePersistentInfoItem()
    {
        var (sut, flow, drafts) = Create();

        sut.NotifyCapture(Event());

        flow.Received(1).Publish(Arg.Any<FlowItemDraft>());
        var draft = Assert.Single(drafts);
        Assert.Equal(FlowSeverity.Info, draft.Severity);
        Assert.Equal(FlowSource.InAppToast, draft.Source);
        Assert.True(draft.Lifetime.IsPersistent);
        Assert.False(draft.RequestDurable);
        Assert.Equal("screen-capture:background:run-1", draft.DedupKey);
    }

    [Fact]
    public void SecondCaptureInTheSameRun_RepublishesWithTheSameKeyAndCountTwo()
    {
        var (sut, _, drafts) = Create();

        sut.NotifyCapture(Event());
        sut.NotifyCapture(Event());

        Assert.Equal(2, drafts.Count);
        Assert.Equal(drafts[0].DedupKey, drafts[1].DedupKey);
        Assert.Contains(",1)", drafts[0].Title, StringComparison.Ordinal);
        Assert.Contains(",2)", drafts[1].Title, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentRuns_GetDifferentKeys()
    {
        var (sut, _, drafts) = Create();

        sut.NotifyCapture(Event(granter: "background:run-1"));
        sut.NotifyCapture(Event(granter: "routine:job-9"));

        Assert.NotEqual(drafts[0].DedupKey, drafts[1].DedupKey);
    }

    [Theory]
    [InlineData(ScreenCaptureSurfaces.Picker)]
    [InlineData(ScreenCaptureSurfaces.Interactive)]
    [InlineData(ScreenCaptureSurfaces.Voice)]
    [InlineData(ScreenCaptureSurfaces.Unknown)]
    public void AttendedSurfaces_PublishNothing(string surface)
    {
        var (sut, flow, _) = Create();

        sut.NotifyCapture(Event(surface, granter: null));

        flow.DidNotReceive().Publish(Arg.Any<FlowItemDraft>());
    }

    [Fact]
    public void TheTitleNeverReachesTheFlowItem()
    {
        var (sut, _, drafts) = Create();

        sut.NotifyCapture(Event(title: "Q3 payroll.xlsx - Excel"));

        var draft = Assert.Single(drafts);
        Assert.DoesNotContain("payroll", draft.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payroll", draft.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMonitorEvent_UsesTheMonitorLabel()
    {
        var (sut, _, drafts) = Create();

        sut.NotifyCapture(Event(processName: "", targetKind: ScreenCaptureTargetKinds.Monitor, title: null));

        Assert.Contains("Msg_ScreenCapture_MonitorLabel", Assert.Single(drafts).Title, StringComparison.Ordinal);
    }

    /// <summary>Enumerating an elevated window can return no process name; that is a program, not a monitor.</summary>
    [Fact]
    public void AWindowWithNoProcessName_UsesTheUnnamedProgramLabel()
    {
        var (sut, _, drafts) = Create();

        sut.NotifyCapture(Event(processName: ""));

        Assert.Contains("Msg_ScreenCapture_UnnamedProgramLabel", Assert.Single(drafts).Title, StringComparison.Ordinal);
    }
}
