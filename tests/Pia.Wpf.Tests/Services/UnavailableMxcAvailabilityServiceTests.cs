using Pia.Services.Interfaces;
using Pia.Services.Plugins.Sandbox;
using Xunit;

namespace Pia.Tests.Services;

public class UnavailableMxcAvailabilityServiceTests
{
    [Fact]
    public void Status_IsUnsupported_WithAReason()
    {
        var availability = new UnavailableMxcAvailabilityService();

        Assert.Equal(MxcRuntimeState.Unsupported, availability.Status.State);
        Assert.False(string.IsNullOrWhiteSpace(availability.Status.Reason));
        Assert.False(availability.Status.IsReady);
    }

    [Fact]
    public async Task RefreshAndInstall_ReturnTheSameStatus_WithoutRaisingChanged()
    {
        var availability = new UnavailableMxcAvailabilityService();
        var raised = 0;
        availability.StatusChanged += (_, _) => raised++;

        var ct = TestContext.Current.CancellationToken;
        var refreshed = await availability.RefreshAsync(ct);
        var installed = await availability.InstallRuntimeAsync(cancellationToken: ct);

        Assert.Equal(availability.Status, refreshed);
        Assert.Equal(availability.Status, installed);
        Assert.Equal(0, raised);
    }
}
