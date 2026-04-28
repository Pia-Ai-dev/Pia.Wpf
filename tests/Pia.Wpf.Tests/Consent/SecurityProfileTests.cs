using Pia.Models;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class SecurityProfileTests
{
    [Fact]
    public void Strict_MatchesSpec()
    {
        var p = SecurityProfile.Strict;
        Assert.Equal(SecurityMode.Strict, p.Mode);
        Assert.Equal(NewSpeakerStrategy.PauseAndReConsent, p.Strategy);
        Assert.False(p.AllowEuCloud);
        Assert.False(p.AllowNonEuCloud);
        Assert.Equal(7, p.TranscriptRetentionDays);
        Assert.True(p.PersistConsentAudioSnippet);
    }

    [Fact]
    public void Standard_MatchesSpec()
    {
        var p = SecurityProfile.Standard;
        Assert.Equal(SecurityMode.Standard, p.Mode);
        Assert.Equal(NewSpeakerStrategy.SelectiveRecording, p.Strategy);
        Assert.True(p.AllowEuCloud);
        Assert.False(p.AllowNonEuCloud);
        Assert.Equal(30, p.TranscriptRetentionDays);
    }

    [Fact]
    public void Permissive_MatchesSpec()
    {
        var p = SecurityProfile.Permissive;
        Assert.Equal(SecurityMode.Permissive, p.Mode);
        Assert.Equal(NewSpeakerStrategy.SelectiveRecording, p.Strategy);
        Assert.True(p.AllowEuCloud);
        Assert.True(p.AllowNonEuCloud);
        Assert.Equal(90, p.TranscriptRetentionDays);
    }

    [Fact]
    public void ForMode_ReturnsMatchingPreset()
    {
        Assert.Same(SecurityProfile.Strict, SecurityProfile.ForMode(SecurityMode.Strict));
        Assert.Same(SecurityProfile.Standard, SecurityProfile.ForMode(SecurityMode.Standard));
        Assert.Same(SecurityProfile.Permissive, SecurityProfile.ForMode(SecurityMode.Permissive));
    }
}
