using Pia.Services.Consent;
using Pia.Services.Consent.Cloud;
using Xunit;

namespace Pia.Wpf.Tests.Consent.Privacy;

public sealed class StaticCloudProviderRegistryTests
{
    [Fact]
    public void Find_ByKnownId_ReturnsDescriptor()
    {
        var sut = new StaticCloudProviderRegistry();
        Assert.NotNull(sut.Find("mistral"));
        Assert.NotNull(sut.Find("MISTRAL"));
        Assert.Null(sut.Find("nope"));
    }

    [Fact]
    public void ForProfile_Strict_ReturnsNothing()
    {
        var sut = new StaticCloudProviderRegistry();
        Assert.Empty(sut.ForProfile(SecurityProfile.Strict));
    }

    [Fact]
    public void ForProfile_Standard_ReturnsOnlyEu()
    {
        var sut = new StaticCloudProviderRegistry();
        var allowed = sut.ForProfile(SecurityProfile.Standard).ToList();
        Assert.NotEmpty(allowed);
        Assert.All(allowed, p => Assert.Equal(CloudJurisdiction.EuOnly, p.Jurisdiction));
    }

    [Fact]
    public void ForProfile_Permissive_IncludesUs()
    {
        var sut = new StaticCloudProviderRegistry();
        var allowed = sut.ForProfile(SecurityProfile.Permissive).ToList();
        Assert.Contains(allowed, p => p.Jurisdiction == CloudJurisdiction.UsAdequacyFramework);
    }
}
