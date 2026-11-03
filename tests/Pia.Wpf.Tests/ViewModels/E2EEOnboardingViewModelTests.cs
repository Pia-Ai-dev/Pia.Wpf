namespace Pia.Tests.ViewModels;

using System.Net.Http;
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
        deviceMgmt.FetchKeyHandoverAsync().ThrowsAsync(new UnverifiedApprovalException("unsigned"));
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
        await deviceMgmt.Received(1).FetchKeyHandoverAsync();
        await deviceMgmt.DidNotReceive().AcceptKeyHandoverAsync(Arg.Any<KeyHandover>());
        sync.DidNotReceive().NotifyE2EEOnboardingCompleted();
    }

    [Theory]
    [InlineData(typeof(DeviceKeyLostException))]
    [InlineData(typeof(UnverifiedApprovalException))]
    [InlineData(typeof(HttpRequestException))]
    public async Task APollCancelledByReset_DoesNotOverwriteTheRestoredState(Type failure)
    {
        var deviceMgmt = Substitute.For<IDeviceManagementService>();
        deviceMgmt.RegisterPendingDeviceAsync().Returns(
            new DeviceRegistrationResponse { OnboardingSessionId = "session-1", ServerChallenge = "challenge" });
        deviceMgmt.GetDeviceStatusAsync(Arg.Any<string>()).Returns(
            new DeviceStatusResponse { DeviceId = "dev-self", Status = DeviceStatus.Pending },
            new DeviceStatusResponse { DeviceId = "dev-self", Status = DeviceStatus.Active });
        var fetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetch = new TaskCompletionSource<KeyHandover>(TaskCreationOptions.RunContinuationsAsynchronously);
        deviceMgmt.FetchKeyHandoverAsync().Returns(_ =>
        {
            fetchStarted.TrySetResult();
            return fetch.Task;
        });
        var keys = Substitute.For<IDeviceKeyService>();
        keys.GetDeviceId().Returns("dev-self");
        keys.GetFingerprint().Returns("FP");

        var sut = new E2EEOnboardingViewModel(
            deviceMgmt, keys, Substitute.For<IE2EEService>(), Substitute.For<ISyncClientService>(),
            Substitute.For<ISettingsService>(), NullLogger<E2EEOnboardingViewModel>.Instance,
            pollIntervalOverride: TimeSpan.FromMilliseconds(10));

        await sut.StartDeviceApprovalCommand.ExecuteAsync(null);
        await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        sut.Reset();
        fetch.SetException(failure == typeof(UnverifiedApprovalException)
            ? new UnverifiedApprovalException("unsigned")
            : (Exception)Activator.CreateInstance(failure)!);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(OnboardingState.Initial, sut.State);
        Assert.Null(sut.ErrorMessage);
        Assert.Equal("", sut.StatusMessage);
    }
}
