using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Services.Plugins;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>An MCP server the admin distributes runs a process on this device, so the user's switch decides
/// whether it starts — on launch, on a server update, and when it is switched back on.</summary>
public sealed class PluginServiceServerMcpActivationTests : IDisposable
{
    private const string Name = "admin server";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "pia-server-mcp-" + Guid.NewGuid().ToString("N") + ".db");

    private readonly List<SqliteContext> _contexts = [];

    public void Dispose()
    {
        foreach (var context in _contexts)
            context.Dispose();
        TempPath.RemoveFile(_dbPath);
    }

    [Fact]
    public async Task ANewlyDistributedExtension_WaitsForTheUser()
    {
        var pushed = Pushed(defaultEnabled: null);
        var log = new CapturingLogger<PluginService>();
        var service = CreateService(log);

        await service.ApplyServerPluginsAsync([pushed], []);
        Assert.Equal(0, StartAttempts(log));

        await service.SetPluginEnabledAsync(pushed.Id, true);
        Assert.Equal(1, StartAttempts(log));
    }

    [Fact]
    public async Task AnExtensionTheAdminSwitchesOnByDefault_StartsWithoutAsking()
    {
        var log = new CapturingLogger<PluginService>();

        await CreateService(log).ApplyServerPluginsAsync([Pushed(defaultEnabled: true)], []);

        Assert.Equal(1, StartAttempts(log));
    }

    // Ran before this rule existed without the user ever choosing: it keeps running rather than vanish.
    [Fact]
    public async Task AnExtensionFromBeforeTheRule_KeepsStarting()
    {
        var seedLog = new CapturingLogger<PluginService>();
        var seed = CreateService(seedLog);
        await seed.ApplyServerPluginsAsync([Pushed(defaultEnabled: null)], []);
        using (var rewind = _contexts[^1].GetConnection().CreateCommand())
        {
            rewind.CommandText = "PRAGMA user_version = 0";
            rewind.ExecuteNonQuery();
        }

        var log = new CapturingLogger<PluginService>();
        await CreateService(log).InitializePersistedPluginsAsync();

        Assert.Equal(1, StartAttempts(log));
    }

    [Fact]
    public async Task ASwitchedOffExtension_DoesNotStartOnTheNextLaunch()
    {
        var pushed = Pushed();
        var seed = CreateService(new CapturingLogger<PluginService>());
        await seed.ApplyServerPluginsAsync([pushed], []);
        await seed.SetPluginEnabledAsync(pushed.Id, false);

        var log = new CapturingLogger<PluginService>();
        await CreateService(log).InitializePersistedPluginsAsync();

        Assert.Equal(0, StartAttempts(log));
    }

    [Fact]
    public async Task AServerUpdate_DoesNotStartASwitchedOffExtension()
    {
        var pushed = Pushed();
        var log = new CapturingLogger<PluginService>();
        var service = CreateService(log);
        await service.ApplyServerPluginsAsync([pushed], []);
        await service.SetPluginEnabledAsync(pushed.Id, false);
        var before = StartAttempts(log);

        await service.ApplyServerPluginsAsync([Pushed(pushed.Id)], []);

        Assert.Equal(before, StartAttempts(log));
    }

    [Fact]
    public async Task AnExtensionTheAdminSwitchedOff_DoesNotStart()
    {
        var log = new CapturingLogger<PluginService>();

        await CreateService(log).ApplyServerPluginsAsync([Pushed(isActive: false)], []);

        Assert.Equal(0, StartAttempts(log));
    }

    [Fact]
    public async Task SwitchingAnExtensionBackOn_StartsIt_AndStillTellsTheServer()
    {
        var pushed = Pushed();
        var seed = CreateService(new CapturingLogger<PluginService>());
        await seed.ApplyServerPluginsAsync([pushed], []);
        await seed.SetPluginEnabledAsync(pushed.Id, false);

        var log = new CapturingLogger<PluginService>();
        var restarted = CreateService(log);
        await restarted.InitializePersistedPluginsAsync();
        Assert.Equal(0, StartAttempts(log));

        await restarted.SetPluginEnabledAsync(pushed.Id, true);

        Assert.Equal(1, StartAttempts(log));
        Assert.Equal(pushed.Id, Assert.Single(restarted.GetPendingPreferenceChanges()).PluginId);
    }

    // The command is not on PATH, so an attempt stops at the preflight and says so.
    private static int StartAttempts(CapturingLogger<PluginService> log) =>
        log.Entries.Count(e => e.Message.Contains(Name, StringComparison.Ordinal)
            && e.Message.Contains("not found on PATH", StringComparison.Ordinal));

    private static SyncPlugin Pushed(Guid? id = null, bool isActive = true, bool? defaultEnabled = true) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Kind = "mcp_server",
        Name = Name,
        ConfigJson = defaultEnabled is bool on
            ? $$"""{"transport":"stdio","command":"pia-tests-no-such-command","defaultEnabled":{{(on ? "true" : "false")}}}"""
            : """{"transport":"stdio","command":"pia-tests-no-such-command"}""",
        IsActive = isActive,
    };

    /// <summary>A restart is a second service over the same database file.</summary>
    private PluginService CreateService(CapturingLogger<PluginService> log)
    {
        var sqlite = new SqliteContext(_dbPath);
        _contexts.Add(sqlite);

        return new PluginService(
            Substitute.For<IMemoryToolHandler>(),
            Substitute.For<ITodoToolHandler>(),
            Substitute.For<IReminderToolHandler>(),
            Substitute.For<IScheduledJobToolHandler>(),
            Substitute.For<IFilesToolHandler>(),
            Substitute.For<IIngestToolHandler>(),
            Substitute.For<IGitToolHandler>(),
            Substitute.For<IChatHistoryToolHandler>(),
            Substitute.For<IAssignmentToolHandler>(),
            Substitute.For<IScreenCaptureToolHandler>(),
            Substitute.For<IHelpToolHandler>(),
            Substitute.For<IAssignmentSurfaceCache>(),
            Substitute.For<ISettingsService>(),
            log,
            sqlite,
            cabManager: null,
            dpapiHelper: new DpapiHelper(NullLogger<DpapiHelper>.Instance));
    }
}
