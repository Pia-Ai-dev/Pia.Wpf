using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Services.Plugins;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// A locally added MCP server rides the same Plugins table as an admin-pushed one, so the two have to stay
/// told apart: only the pushed kind has a server-side row for a preference to be pushed back to.
/// </summary>
public sealed class LocalMcpPersistenceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "pia-local-mcp-" + Guid.NewGuid().ToString("N") + ".db");

    private readonly List<SqliteContext> _contexts = [];

    public void Dispose()
    {
        foreach (var context in _contexts)
            context.Dispose();
        TempPath.RemoveFile(_dbPath);
    }

    /// <summary>A restart is a second service over the same database file.</summary>
    private PluginService CreateService()
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
            Substitute.For<IAssignmentSurfaceCache>(),
            Substitute.For<ISettingsService>(),
            NullLogger<PluginService>.Instance,
            sqlite,
            cabManager: null,
            dpapiHelper: new DpapiHelper(NullLogger<DpapiHelper>.Instance));
    }

    /// <summary>Not on PATH, so activation stops before spawning anything.</summary>
    private static LocalMcpDefinition Definition(string name = "GitHub", string? token = null) =>
        new(name,
            "pia-tests-no-such-command",
            ["--stdio"],
            token is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["TOKEN"] = token },
            WorkingDirectory: null,
            LocalMcpConfig.DeriveToolPrefix(name),
            AllowedTools: ["create_issue"]);

    [Fact]
    public async Task ASavedServer_SurvivesARestart()
    {
        var id = await CreateService().SaveLocalMcpAsync(null, Definition(), TestContext.Current.CancellationToken);

        var restarted = CreateService();
        var plugin = Assert.Single(restarted.GetLocalMcpPlugins());
        Assert.Equal(id, plugin.Id);

        var definition = restarted.GetLocalMcpDefinition(id)!;
        Assert.Equal("pia-tests-no-such-command", definition.Command);
        Assert.Equal(["--stdio"], definition.Args);
        Assert.Equal(["create_issue"], definition.AllowedTools);
        Assert.Equal("github", definition.ToolPrefix);
    }

    [Fact]
    public async Task AnEnvironmentValue_IsNotStoredInPlaintext()
    {
        var service = CreateService();
        var id = await service.SaveLocalMcpAsync(null, Definition(token: "ghp_supersecret"), TestContext.Current.CancellationToken);

        var stored = service.GetAllPluginConfigs().Single(p => p.Id == id);
        Assert.DoesNotContain("ghp_supersecret", stored.ConfigJson, StringComparison.Ordinal);
        Assert.Contains("TOKEN", stored.ConfigJson, StringComparison.Ordinal);

        // Still readable back on the same account, or the server would start without its credential.
        Assert.Equal("ghp_supersecret", CreateService().GetLocalMcpDefinition(id)!.Env["TOKEN"]);
    }

    [Fact]
    public async Task TogglingALocalServer_PushesNoPreferenceToTheServer()
    {
        var service = CreateService();
        var id = await service.SaveLocalMcpAsync(null, Definition(), TestContext.Current.CancellationToken);

        await service.SetPluginEnabledAsync(id, false);

        Assert.Empty(service.GetPendingPreferenceChanges());
        Assert.False(service.GetAllPluginConfigs().Single(p => p.Id == id).UserEnabled);
    }

    [Fact]
    public async Task TogglingABuiltIn_StillPushesAPreference()
    {
        var service = CreateService();

        await service.SetPluginEnabledAsync(BuiltInPluginDefaults.TodoPluginId, false);

        Assert.Equal(BuiltInPluginDefaults.TodoPluginId, Assert.Single(service.GetPendingPreferenceChanges()).PluginId);
    }

    [Fact]
    public async Task ARemovedServer_DoesNotComeBack()
    {
        var service = CreateService();
        var id = await service.SaveLocalMcpAsync(null, Definition(), TestContext.Current.CancellationToken);

        await service.RemoveLocalMcpAsync(id);

        Assert.Empty(service.GetLocalMcpPlugins());
        Assert.Empty(CreateService().GetLocalMcpPlugins());
    }

    [Fact]
    public async Task TwoServersWithTheSameName_GetDistinctToolPrefixes()
    {
        var service = CreateService();
        var first = await service.SaveLocalMcpAsync(null, Definition(), TestContext.Current.CancellationToken);
        var second = await service.SaveLocalMcpAsync(null, Definition(), TestContext.Current.CancellationToken);

        Assert.Equal("github", service.GetLocalMcpDefinition(first)!.ToolPrefix);
        Assert.Equal("github_2", service.GetLocalMcpDefinition(second)!.ToolPrefix);
    }

    [Fact]
    public async Task EditingAServer_KeepsItsOwnPrefix()
    {
        var service = CreateService();
        var id = await service.SaveLocalMcpAsync(null, Definition(), TestContext.Current.CancellationToken);

        await service.SaveLocalMcpAsync(id, Definition() with { AllowedTools = null }, TestContext.Current.CancellationToken);

        Assert.Equal("github", service.GetLocalMcpDefinition(id)!.ToolPrefix);
        Assert.Null(service.GetLocalMcpDefinition(id)!.AllowedTools);
    }

    [Fact]
    public async Task SavingOverAServerPushedPlugin_IsRefused()
    {
        var service = CreateService();
        var pushed = new Pia.Shared.Models.SyncPlugin
        {
            Id = Guid.NewGuid(),
            Kind = "mcp_server",
            Name = "admin server",
            ConfigJson = """{"transport":"stdio","command":"pia-tests-no-such-command"}""",
            IsActive = true
        };
        await service.ApplyServerPluginsAsync([pushed], []);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveLocalMcpAsync(pushed.Id, Definition(), TestContext.Current.CancellationToken));
        Assert.Empty(service.GetLocalMcpPlugins());
    }

    /// <summary>A local server skips the PATH preflight, so a command that cannot start reaches the handler
    /// and comes back as a reason rather than as a silent "running, 0 tools".</summary>
    [Fact]
    public async Task AServerThatCannotStart_ReportsWhyInsteadOfLookingHealthy()
    {
        var service = CreateService();
        var id = await service.SaveLocalMcpAsync(null, Definition(), TestContext.Current.CancellationToken);

        var status = service.GetLocalMcpStatus(id);

        Assert.False(status.IsRunning);
        Assert.False(string.IsNullOrWhiteSpace(status.Error));
        Assert.Empty(status.ActiveTools);
    }

    [Fact]
    public async Task RemovingAServerPushedPlugin_IsIgnored()
    {
        var service = CreateService();
        var pushed = new Pia.Shared.Models.SyncPlugin
        {
            Id = Guid.NewGuid(),
            Kind = "mcp_server",
            Name = "admin server",
            ConfigJson = """{"transport":"stdio","command":"pia-tests-no-such-command"}""",
            IsActive = true
        };
        await service.ApplyServerPluginsAsync([pushed], []);

        await service.RemoveLocalMcpAsync(pushed.Id);

        Assert.Contains(service.GetAllPluginConfigs(), p => p.Id == pushed.Id);
    }
}
