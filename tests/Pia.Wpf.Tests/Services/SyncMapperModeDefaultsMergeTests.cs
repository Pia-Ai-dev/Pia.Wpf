using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

// Covers the per-mode merge semantics of ApplySyncSettings that replaced the
// previous "wholesale overwrite" of AppSettings.ModeProviderDefaults during
// sync pull. The old behavior wiped local defaults whenever an incoming
// SyncSettings carried an empty dict, which was the root of the
// "mode dropdown is empty after every restart" bug.
public class SyncMapperModeDefaultsMergeTests
{
    private static SyncMapper Make()
    {
        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        return new SyncMapper(dpapi);
    }

    private static SyncSettings BaseSync(Dictionary<int, Guid>? modes = null)
        => new()
        {
            UseSameProviderForAllModes = true,
            ModeProviderDefaults = modes ?? new Dictionary<int, Guid>(),
        };

    [Fact]
    public void Empty_incoming_preserves_local_defaults()
    {
        var mapper = Make();
        var localOpt = Guid.NewGuid();
        var target = new AppSettings();
        target.ModeProviderDefaults[WindowMode.Optimize] = localOpt;

        mapper.ApplySyncSettings(BaseSync(), target);

        Assert.Single(target.ModeProviderDefaults);
        Assert.Equal(localOpt, target.ModeProviderDefaults[WindowMode.Optimize]);
    }

    [Fact]
    public void Non_empty_incoming_for_one_mode_only_sets_that_mode()
    {
        var mapper = Make();
        var localOpt = Guid.NewGuid();
        var localAsst = Guid.NewGuid();
        var target = new AppSettings();
        target.ModeProviderDefaults[WindowMode.Optimize] = localOpt;
        target.ModeProviderDefaults[WindowMode.Assistant] = localAsst;

        var newOpt = Guid.NewGuid();
        var sync = BaseSync(new Dictionary<int, Guid> { [(int)WindowMode.Optimize] = newOpt });

        mapper.ApplySyncSettings(sync, target);

        Assert.Equal(newOpt, target.ModeProviderDefaults[WindowMode.Optimize]);
        Assert.Equal(localAsst, target.ModeProviderDefaults[WindowMode.Assistant]);
    }

    [Fact]
    public void Tombstone_value_removes_local_mode()
    {
        var mapper = Make();
        var localOpt = Guid.NewGuid();
        var target = new AppSettings();
        target.ModeProviderDefaults[WindowMode.Optimize] = localOpt;

        var sync = BaseSync(new Dictionary<int, Guid> { [(int)WindowMode.Optimize] = Guid.Empty });

        mapper.ApplySyncSettings(sync, target);

        Assert.False(target.ModeProviderDefaults.ContainsKey(WindowMode.Optimize));
    }

    [Fact]
    public void Tombstone_on_missing_mode_is_no_op()
    {
        var mapper = Make();
        var target = new AppSettings();

        var sync = BaseSync(new Dictionary<int, Guid> { [(int)WindowMode.Optimize] = Guid.Empty });

        mapper.ApplySyncSettings(sync, target);

        Assert.Empty(target.ModeProviderDefaults);
    }

    [Fact]
    public void Incoming_adds_new_mode_when_local_does_not_have_one()
    {
        var mapper = Make();
        var target = new AppSettings();

        var newAsst = Guid.NewGuid();
        var sync = BaseSync(new Dictionary<int, Guid> { [(int)WindowMode.Assistant] = newAsst });

        mapper.ApplySyncSettings(sync, target);

        Assert.Equal(newAsst, target.ModeProviderDefaults[WindowMode.Assistant]);
    }

    [Fact]
    public void Updating_an_existing_mode_replaces_the_value()
    {
        var mapper = Make();
        var oldOpt = Guid.NewGuid();
        var newOpt = Guid.NewGuid();
        var target = new AppSettings();
        target.ModeProviderDefaults[WindowMode.Optimize] = oldOpt;

        var sync = BaseSync(new Dictionary<int, Guid> { [(int)WindowMode.Optimize] = newOpt });

        mapper.ApplySyncSettings(sync, target);

        Assert.Equal(newOpt, target.ModeProviderDefaults[WindowMode.Optimize]);
    }
}
