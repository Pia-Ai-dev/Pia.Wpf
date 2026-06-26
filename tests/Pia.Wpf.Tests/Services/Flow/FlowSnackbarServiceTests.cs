using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models.Flow;
using Pia.Services.Flow;
using Wpf.Ui.Controls;
using Xunit;

namespace Pia.Tests.Services.Flow;

/// <summary>
/// The snackbar funnel: both <c>ISnackbarService.Show</c> and the action path produce FlowItems
/// (design §11 "snackbar-funnel capture"). Items are session-only with a null dedup key.
/// </summary>
public class FlowSnackbarServiceTests
{
    private static (FlowSnackbarService snackbar, FlowService flow) Create()
    {
        var flow = new FlowService(new FakeFlowPersistenceStore(), NullLogger<FlowService>.Instance);
        return (new FlowSnackbarService(flow), flow);
    }

    [Fact]
    public void Show_Success_PublishesTransientSuccessItem()
    {
        var (snackbar, flow) = Create();

        snackbar.Show("Saved", "Your changes were saved", ControlAppearance.Success, null, TimeSpan.FromSeconds(2));

        var item = Assert.Single(flow.Snapshot);
        Assert.Equal(FlowSeverity.Success, item.Severity);
        Assert.Equal(FlowSource.Snackbar, item.Source);
        Assert.Equal("Saved", item.Title);
        Assert.Equal("Your changes were saved", item.Body);
        Assert.Null(item.DedupKey);
        Assert.False(item.Lifetime.IsPersistent);
        Assert.False(item.Durable);
    }

    [Fact]
    public void Show_Danger_PublishesPersistentErrorItem()
    {
        var (snackbar, flow) = Create();

        snackbar.Show("Failed", "Web search failed", ControlAppearance.Danger, null, TimeSpan.FromSeconds(5));

        var item = Assert.Single(flow.Snapshot);
        Assert.Equal(FlowSeverity.Error, item.Severity);
        Assert.True(item.Lifetime.IsPersistent);
    }

    [Fact]
    public void PublishAction_PublishesActionRequiredInvokeItem()
    {
        var (snackbar, flow) = Create();
        var invoked = false;

        snackbar.PublishAction("Heads up", "Undo this?", "Undo", () => invoked = true, ControlAppearance.Secondary, TimeSpan.FromSeconds(8));

        var item = Assert.Single(flow.Snapshot);
        Assert.Equal(FlowSeverity.ActionRequired, item.Severity);
        Assert.True(item.Lifetime.IsPersistent);
        var action = Assert.IsType<InvokeAction>(item.Action);
        Assert.Equal("Undo", action.Label);

        action.Callback();
        Assert.True(invoked);
    }
}
