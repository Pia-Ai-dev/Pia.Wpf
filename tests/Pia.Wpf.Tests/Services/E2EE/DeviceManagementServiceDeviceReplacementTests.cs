namespace Pia.Tests.Services.E2EE;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

// The server keeps a session tied to the device id it first saw, so a computer that lost its keys continues under a new id only after a fresh sign-in.
public class DeviceManagementServiceDeviceReplacementTests
{
    private static readonly Guid OldId = Guid.NewGuid();
    private static readonly Guid NewId = Guid.NewGuid();

    private readonly IDeviceKeyService _keys = Substitute.For<IDeviceKeyService>();
    private readonly IScheduledJobService _jobs = Substitute.For<IScheduledJobService>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();

    public DeviceManagementServiceDeviceReplacementTests()
    {
        _keys.StartNewDevice().Returns(OldId.ToString());
        _keys.GetDeviceId().Returns(NewId.ToString());
    }

    [Fact]
    public async Task ReplacingTheDevice_MovesItsJobsToTheNewIdThenSignsOut()
    {
        await CreateSut().ReplaceLostDeviceAsync();

        Received.InOrder(() =>
        {
            _keys.StartNewDevice();
            _jobs.ReassignOwnerAsync(OldId, NewId);
            _auth.SignOutUnexpectedlyAsync(AuthSignOutReason.DeviceKeysLost);
        });
    }

    [Fact]
    public async Task JobsThatCannotBeMoved_DoNotKeepTheOldSessionAlive()
    {
        _jobs.ReassignOwnerAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).ThrowsAsync(new InvalidOperationException("db locked"));

        await CreateSut().ReplaceLostDeviceAsync();

        await _auth.Received(1).SignOutUnexpectedlyAsync(AuthSignOutReason.DeviceKeysLost);
    }

    private DeviceManagementService CreateSut() => new(
        Substitute.For<IE2EEService>(), _keys, Substitute.For<IRecoveryCodeService>(), Substitute.For<ISettingsService>(),
        _auth, Substitute.For<System.Net.Http.IHttpClientFactory>(), Substitute.For<IConfirmedApproverStore>(), _jobs,
        new UnsignedRequests(), NullLogger<DeviceManagementService>.Instance);
}
