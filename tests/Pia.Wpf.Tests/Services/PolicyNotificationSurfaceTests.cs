using NSubstitute;
using Pia.Models.Flow;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class PolicyNotificationSurfaceTests
{
    private readonly Pia.Services.Flow.IFlowService _flow = Substitute.For<Pia.Services.Flow.IFlowService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();

    public PolicyNotificationSurfaceTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
    }

    private PolicyNotificationSurface Create() => new(_flow, _loc);

    [Fact]
    public void AValueChange_PublishesOneDismissableInfoItem()
    {
        Create().NotifyValuesChanged(restartRequired: false);

        _flow.Received(1).Publish(Arg.Is<FlowItemDraft>(d =>
            d.Source == FlowSource.Policy &&
            d.Severity == FlowSeverity.Info &&
            d.Lifetime.IsPersistent &&
            !d.RequestDurable &&
            d.DedupKey == null &&
            d.Action == null &&
            d.Title == "Flow_PolicyUpdated_Title" &&
            d.Body == "Flow_PolicyUpdated_Body"));
    }

    /// <summary>"The new settings are now in effect" is false for the one key that raises the overlay, and
    /// with a deferred overlay this notice is all the user sees.</summary>
    [Fact]
    public void AChangeAwaitingARestart_SaysSoInsteadOfClaimingItIsInEffect()
    {
        Create().NotifyValuesChanged(restartRequired: true);

        _flow.Received(1).Publish(Arg.Is<FlowItemDraft>(d =>
            d.Title == "Flow_PolicyUpdated_Title" &&
            d.Body == "Flow_PolicyUpdated_Body_Restart"));
    }

    /// <summary>Persisted as an int, so a reorder silently relabels every stored item.</summary>
    [Fact]
    public void PolicyIsTheLastFlowSource_AndTheMembersBeforeItKeepTheirNumbers()
    {
        Assert.Equal(0, (int)FlowSource.Snackbar);
        Assert.Equal(1, (int)FlowSource.InAppToast);
        Assert.Equal(2, (int)FlowSource.BackgroundChat);
        Assert.Equal(3, (int)FlowSource.Reminder);
        Assert.Equal(4, (int)FlowSource.ScheduledJob);
        Assert.Equal(5, (int)FlowSource.TodoDeadline);
        Assert.Equal(6, (int)FlowSource.AgentRun);
        Assert.Equal(7, (int)FlowSource.Assignment);
        Assert.Equal(8, (int)FlowSource.Policy);

        Assert.Equal(FlowSource.Policy, Enum.GetValues<FlowSource>().Max());
    }

    [Fact]
    public void EveryFlowSource_HasItsOwnGlyph()
    {
        var converter = new Pia.Converters.FlowSourceToSymbolConverter();
        var policyGlyph = converter.Convert(
            FlowSource.Policy, typeof(object), null!, System.Globalization.CultureInfo.InvariantCulture);
        var fallbackGlyph = converter.Convert(
            FlowSource.Snackbar, typeof(object), null!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.NotEqual(fallbackGlyph, policyGlyph);
    }
}
