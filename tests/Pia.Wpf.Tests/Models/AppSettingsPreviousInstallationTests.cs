namespace Pia.Tests.Models;

using Pia.Models;
using Xunit;

// The replaced id must outlive the sign-out that follows a key loss, yet never be offered to another account.
public class AppSettingsPreviousInstallationTests
{
    [Fact]
    public void TheReplacedId_SurvivesTheSignOutAndIsOfferedToTheSameAccountAgain()
    {
        var settings = new AppSettings
        {
            SyncUserId = "ACCOUNT-1", ReplacedDeviceId = "old-id", ReplacedDeviceAccountId = "account-1",
        };

        settings.ClearAccountState();
        Assert.Null(settings.PreviousInstallationToRemove());

        settings.SyncUserId = "account-1";
        Assert.Equal("old-id", settings.PreviousInstallationToRemove());
    }

    [Fact]
    public void AnotherAccount_IsNotOfferedTheReplacedId()
    {
        var settings = new AppSettings
        {
            SyncUserId = "account-2", ReplacedDeviceId = "old-id", ReplacedDeviceAccountId = "account-1",
        };

        Assert.Null(settings.PreviousInstallationToRemove());
    }

    [Fact]
    public void ForgettingThePreviousInstallation_ClearsIt()
    {
        var settings = new AppSettings
        {
            SyncUserId = "account-1", ReplacedDeviceId = "old-id", ReplacedDeviceAccountId = "account-1",
        };

        settings.ForgetPreviousInstallation();

        Assert.Null(settings.PreviousInstallationToRemove());
        Assert.Null(settings.ReplacedDeviceId);
    }
}
