using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.KnowledgeManager;
using Pia.Services.Operators;
using Pia.Services.Plugins;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>With local MCP servers disallowed by policy, none is added, probed or started on this device.</summary>
public sealed class LocalMcpPolicyTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "pia-local-mcp-policy-" + Guid.NewGuid().ToString("N") + ".db");

    private readonly List<SqliteContext> _contexts = [];

    public void Dispose()
    {
        foreach (var context in _contexts)
            context.Dispose();
        TempPath.RemoveFile(_dbPath);
    }

    [Fact]
    public async Task SavingAServer_IsRefused()
    {
        var service = CreateService(allowed: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveLocalMcpAsync(null, Definition(), TestContext.Current.CancellationToken));

        Assert.Empty(service.GetLocalMcpPlugins());
    }

    [Fact]
    public async Task AServerSavedBefore_DoesNotStart_AndSaysWhy()
    {
        var id = await CreateService(allowed: true)
            .SaveLocalMcpAsync(null, Definition(), TestContext.Current.CancellationToken);

        var restarted = CreateService(allowed: false);
        await restarted.InitializePersistedPluginsAsync();

        Assert.False(restarted.AreLocalMcpServersAllowed);
        var status = restarted.GetLocalMcpStatus(id);
        Assert.False(status.IsRunning);
        Assert.Contains("policy", status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheConnectionTest_IsRefused()
    {
        var result = await CreateService(allowed: false)
            .ProbeLocalMcpAsync(Definition(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("policy", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithdrawingThePermissionLater_SaysWhyTheServerStopped()
    {
        var settings = Settings(allowed: true);
        var service = CreateService(settings);
        var id = await service.SaveLocalMcpAsync(null, Definition(), TestContext.Current.CancellationToken);

        settings.SettingsChanged += Raise.Event<EventHandler<AppSettings>>(
            settings, new AppSettings { AllowLocalMcpServers = false });

        // The policy is applied off the event, so wait for it rather than racing it.
        var error = service.GetLocalMcpStatus(id).Error;
        for (var i = 0; i < 250 && error?.Contains("policy", StringComparison.OrdinalIgnoreCase) != true; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            error = service.GetLocalMcpStatus(id).Error;
        }

        Assert.False(service.AreLocalMcpServersAllowed);
        Assert.False(service.GetLocalMcpStatus(id).IsRunning);
        Assert.Contains("policy", error, StringComparison.OrdinalIgnoreCase);
    }

    private static LocalMcpDefinition Definition() =>
        new("GitHub", "pia-tests-no-such-command", ["--stdio"], new Dictionary<string, string>(),
            WorkingDirectory: null, LocalMcpConfig.DeriveToolPrefix("GitHub"), AllowedTools: null);

    private static ISettingsService Settings(bool allowed)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AllowLocalMcpServers = allowed });
        return settings;
    }

    private PluginService CreateService(bool allowed) => CreateService(Settings(allowed));

    /// <summary>A restart is a second service over the same database file.</summary>
    private PluginService CreateService(ISettingsService settings)
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
            Substitute.For<IKnowledgeManagerToolHandler>(),
            Substitute.For<IAssignmentSurfaceCache>(),
            Substitute.For<IKnowledgeManagerSurfaceCache>(),
            settings,
            NullLogger<PluginService>.Instance,
            sqlite,
            cabManager: null,
            dpapiHelper: new DpapiHelper(NullLogger<DpapiHelper>.Instance));
    }
}
