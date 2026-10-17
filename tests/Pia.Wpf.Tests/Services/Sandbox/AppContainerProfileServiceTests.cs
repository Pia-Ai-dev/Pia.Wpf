using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Plugins.Sandbox;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class AppContainerProfileServiceTests
{
    private const int Ok = 0;
    private const int NotFound = unchecked((int)0x80070490);
    private const int AccessDenied = unchecked((int)0x80070005);

    private static AppContainerProfileService Service(List<string> deleted, int hresult = Ok) =>
        new(NullLogger<AppContainerProfileService>.Instance, name => { deleted.Add(name); return hresult; });

    [Fact]
    public void RemovingAServer_DeletesItsContainerAndItsProbeContainer()
    {
        var deleted = new List<string>();

        Service(deleted).DeleteServerProfiles("fs");

        Assert.Equal(["pia-mcp-fs", "pia-mcp-fs-probe"], deleted);
    }

    [Fact]
    public void Success_IsReported()
    {
        Assert.True(Service([]).DeleteServerProfiles("fs"));
    }

    [Fact]
    public void AProfileThatWasNeverCreated_CountsAsGone()
    {
        Assert.True(Service([], NotFound).DeleteServerProfiles("fs"));
    }

    [Fact]
    public void AFailure_IsReported_NotThrown()
    {
        Assert.False(Service([], AccessDenied).DeleteServerProfiles("fs"));
    }

    [Fact]
    public void ANativeCallThatThrows_IsReported_NotThrown()
    {
        var service = new AppContainerProfileService(
            NullLogger<AppContainerProfileService>.Instance, _ => throw new DllNotFoundException("userenv"));

        Assert.False(service.DeleteServerProfiles("fs"));
    }

    [Fact]
    public void TheRealApi_ForANameThatNeverExisted_IsHarmless()
    {
        var service = new AppContainerProfileService(NullLogger<AppContainerProfileService>.Instance);

        Assert.True(service.DeleteServerProfiles("tests-never-created-" + Guid.NewGuid().ToString("N")[..8]));
    }
}
