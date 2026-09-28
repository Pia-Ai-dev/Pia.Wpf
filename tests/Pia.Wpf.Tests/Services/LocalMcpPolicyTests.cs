using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.Interfaces;
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

    private static LocalMcpDefinition Definition() =>
        new("GitHub", "pia-tests-no-such-command", ["--stdio"], new Dictionary<string, string>(),
            WorkingDirectory: null, LocalMcpConfig.DeriveToolPrefix("GitHub"), AllowedTools: null);

    /// <summary>A restart is a second service over the same database file.</summary>
    private PluginService CreateService(bool allowed)
    {
        var sqlite = new SqliteContext(_dbPath);
        _contexts.Add(sqlite);
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AllowLocalMcpServers = allowed });

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
            settings,
            NullLogger<PluginService>.Instance,
            sqlite,
            cabManager: null,
            dpapiHelper: new DpapiHelper(NullLogger<DpapiHelper>.Instance));
    }
}
