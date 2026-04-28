using Pia.Services.Consent;
using Pia.Services.Consent.Cloud;
using Xunit;

namespace Pia.Wpf.Tests.Consent.Privacy;

public sealed class ConsentScopeTests
{
    [Fact]
    public void FromProfile_Strict_DisallowsAllCloud()
    {
        var scope = ConsentScope.FromProfile(SecurityProfile.Strict);
        Assert.False(scope.AllowsCloud(CloudJurisdiction.EuOnly));
        Assert.False(scope.AllowsCloud(CloudJurisdiction.UsAdequacyFramework));
        Assert.False(scope.AllowsCloud(CloudJurisdiction.OtherThirdCountry));
    }

    [Fact]
    public void FromProfile_Standard_AllowsEuOnly()
    {
        var scope = ConsentScope.FromProfile(SecurityProfile.Standard);
        Assert.True(scope.AllowsCloud(CloudJurisdiction.EuOnly));
        Assert.False(scope.AllowsCloud(CloudJurisdiction.UsAdequacyFramework));
        Assert.False(scope.AllowsCloud(CloudJurisdiction.OtherThirdCountry));
    }

    [Fact]
    public void FromProfile_Permissive_AllowsAll()
    {
        var scope = ConsentScope.FromProfile(SecurityProfile.Permissive);
        Assert.True(scope.AllowsCloud(CloudJurisdiction.EuOnly));
        Assert.True(scope.AllowsCloud(CloudJurisdiction.UsAdequacyFramework));
        Assert.True(scope.AllowsCloud(CloudJurisdiction.OtherThirdCountry));
    }

    [Fact]
    public void LocalOnly_DisallowsAllCloud()
    {
        Assert.False(ConsentScope.LocalOnly.AllowsCloud(CloudJurisdiction.EuOnly));
        Assert.False(ConsentScope.LocalOnly.AllowsCloud(CloudJurisdiction.UsAdequacyFramework));
    }

    [Fact]
    public void SpeakerConsentEntry_HasNullScopeByDefault()
    {
        var entry = new SpeakerConsentEntry("Speaker 1", DateTimeOffset.UtcNow);
        Assert.Null(entry.Scope);
    }
}
