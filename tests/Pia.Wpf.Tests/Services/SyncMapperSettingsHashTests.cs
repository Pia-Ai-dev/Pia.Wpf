using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Tests for <see cref="SyncMapper.ComputeSettingsHash"/> (plan Sec 5.4): the hash must be
/// deterministic for identical content regardless of dictionary enumeration order (the payload
/// sorts the mode-default dictionaries) and must change when any hashed field changes.
/// </summary>
public class SyncMapperSettingsHashTests
{
    [Fact]
    public void ComputeSettingsHash_IsStableForIdenticalContent()
    {
        var a = new AppSettings { Theme = AppTheme.Dark, AutoTypeDelayMs = 42 };
        var b = new AppSettings { Theme = AppTheme.Dark, AutoTypeDelayMs = 42 };

        Assert.Equal(SyncMapper.ComputeSettingsHash(a), SyncMapper.ComputeSettingsHash(b));
    }

    [Fact]
    public void ComputeSettingsHash_IgnoresModeDefaultDictionaryInsertionOrder()
    {
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();

        var a = new AppSettings();
        a.ModeProviderDefaults[WindowMode.Optimize] = g1;
        a.ModeProviderDefaults[WindowMode.Assistant] = g2;
        a.ModePersonaDefaults[WindowMode.Assistant] = g2;
        a.ModePersonaDefaults[WindowMode.Optimize] = g1;

        // Same key/value pairs, inserted in the opposite order.
        var b = new AppSettings();
        b.ModeProviderDefaults[WindowMode.Assistant] = g2;
        b.ModeProviderDefaults[WindowMode.Optimize] = g1;
        b.ModePersonaDefaults[WindowMode.Optimize] = g1;
        b.ModePersonaDefaults[WindowMode.Assistant] = g2;

        Assert.Equal(SyncMapper.ComputeSettingsHash(a), SyncMapper.ComputeSettingsHash(b));
    }

    [Fact]
    public void ComputeSettingsHash_ChangesWhenAFieldChanges()
    {
        var baseline = new AppSettings { AutoTypeDelayMs = 10 };
        var changed = new AppSettings { AutoTypeDelayMs = 11 };

        Assert.NotEqual(SyncMapper.ComputeSettingsHash(baseline), SyncMapper.ComputeSettingsHash(changed));
    }

    [Fact]
    public void ComputeSettingsHash_ChangesWhenAModeDefaultChanges()
    {
        var g1 = Guid.NewGuid();
        var a = new AppSettings();
        a.ModeProviderDefaults[WindowMode.Optimize] = g1;

        var b = new AppSettings();
        b.ModeProviderDefaults[WindowMode.Optimize] = Guid.NewGuid();

        Assert.NotEqual(SyncMapper.ComputeSettingsHash(a), SyncMapper.ComputeSettingsHash(b));
    }
}
