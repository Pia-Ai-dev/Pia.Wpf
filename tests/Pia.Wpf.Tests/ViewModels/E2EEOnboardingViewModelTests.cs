namespace Pia.Tests.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Pia.ViewModels;
using Xunit;

public class E2EEOnboardingViewModelTests
{
    // A refused hand-over is a verdict, not a network blip: retrying it only ends in "Unable to reach the server".
    [Fact]
    public async Task AnApprovalThatCannotBeVerified_StopsWaitingInsteadOfRetrying()
    {
        var deviceMgmt = Substitute.For<IDeviceManagementService>();
        deviceMgmt.RegisterPendingDeviceAsync().Returns(
            new DeviceRegistrationResponse { OnboardingSessionId = "session-1", ServerChallenge = "challenge" });
        deviceMgmt.GetDeviceStatusAsync(Arg.Any<string>()).Returns(
            new DeviceStatusResponse { DeviceId = "dev-self", Status = DeviceStatus.Active });
        deviceMgmt.FetchAndUnwrapUmkAsync().ThrowsAsync(new UnverifiedApprovalException("unsigned"));
        var keys = Substitute.For<IDeviceKeyService>();
        keys.GetDeviceId().Returns("dev-self");
        keys.GetFingerprint().Returns("FP");
        var sync = Substitute.For<ISyncClientService>();

        var sut = new E2EEOnboardingViewModel(
            deviceMgmt, keys, Substitute.For<IE2EEService>(), sync, Substitute.For<ISettingsService>(),
            NullLogger<E2EEOnboardingViewModel>.Instance, pollIntervalOverride: TimeSpan.FromMilliseconds(10));

        await sut.StartDeviceApprovalCommand.ExecuteAsync(null);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && sut.State != OnboardingState.Error)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(OnboardingState.Error, sut.State);
        Assert.False(string.IsNullOrEmpty(sut.ErrorMessage));
        await deviceMgmt.Received(1).FetchAndUnwrapUmkAsync();
        sync.DidNotReceive().NotifyE2EEOnboardingCompleted();
    }
}
