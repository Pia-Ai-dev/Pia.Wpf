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
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    public DeviceManagementServiceDeviceReplacementTests()
    {
        _settings.GetSettingsAsync().Returns(new Pia.Models.AppSettings { SyncUserId = "account-1" });
        _keys.StartNewDeviceAsync(Arg.Any<string?>()).Returns(OldId.ToString());
        _keys.GetDeviceId().Returns(NewId.ToString());
    }

    [Fact]
    public async Task ReplacingTheDevice_SignsOutBeforeSwitchingTheIdAndMovingItsJobs()
    {
        await CreateSut().ReplaceLostDeviceAsync();

        Received.InOrder(() =>
        {
            _auth.SignOutUnexpectedlyAsync(AuthSignOutReason.DeviceKeysLost);
            _keys.StartNewDeviceAsync("account-1");
            _jobs.ReassignOwnerAsync(OldId, NewId);
        });
    }

    [Fact]
    public async Task JobsThatCannotBeMoved_StillEndTheOldSession()
    {
        _jobs.ReassignOwnerAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).ThrowsAsync(new InvalidOperationException("db locked"));

        await CreateSut().ReplaceLostDeviceAsync();

        await _auth.Received(1).SignOutUnexpectedlyAsync(AuthSignOutReason.DeviceKeysLost);
    }

    [Fact]
    public void ADeviceWhoseKeysAreGoneLocally_HoldsOtherKeysThanTheServerLists()
    {
        _keys.HasDeviceKeys().Returns(false);
        var registered = new Pia.Shared.E2EE.DeviceInfo
        {
            DeviceId = NewId.ToString(), DeviceName = "PC", AgreementPublicKey = "a", SigningPublicKey = "s",
        };

        Assert.True(CreateSut().HoldsOtherKeysThan(registered));
        _keys.DidNotReceive().GetAgreementPublicKey();
    }

    [Fact]
    public void MatchingKeys_AreNotALoss()
    {
        _keys.HasDeviceKeys().Returns(true);
        _keys.GetAgreementPublicKey().Returns("a");
        _keys.GetSigningPublicKey().Returns("s");
        var registered = new Pia.Shared.E2EE.DeviceInfo
        {
            DeviceId = NewId.ToString(), DeviceName = "PC", AgreementPublicKey = "a", SigningPublicKey = "s",
        };

        Assert.False(CreateSut().HoldsOtherKeysThan(registered));
    }

    private DeviceManagementService CreateSut() => new(
        Substitute.For<IE2EEService>(), _keys, Substitute.For<IRecoveryCodeService>(), _settings,
        _auth, Substitute.For<System.Net.Http.IHttpClientFactory>(), Substitute.For<IConfirmedApproverStore>(), _jobs,
        new UnsignedRequests(), NullLogger<DeviceManagementService>.Instance);
}
