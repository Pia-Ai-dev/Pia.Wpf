using System.IO;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Pia.Infrastructure;
using Pia.Services.Plugins;
using Pia.Services.Plugins.Sandbox;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>A runner's state folder lives as long as its server: deleted with it, on a prefix change and as an
/// orphan at startup, and never while a start of its prefix holds it.</summary>
public sealed class PluginServiceRuntimeStateTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "pia-runtime-state-" + Guid.NewGuid().ToString("N") + ".db");

    private readonly List<SqliteContext> _contexts = [];
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var context in _contexts)
            context.Dispose();
        TempPath.RemoveFile(_dbPath);
        foreach (var directory in _directories)
            TempPath.Remove(directory);
    }

    private static readonly McpSandboxPolicy Policy = new([@"C:\box\rw"], [], AllowNetwork: false);

    private static LocalMcpDefinition Definition(McpSandboxPolicy? sandbox, string name = "Files", bool enabled = true) =>
        new(name, @"C:\Tools\fs\fs.exe", [], new Dictionary<string, string>(), null,
            LocalMcpConfig.DeriveToolPrefix(name), null, DefaultEnabled: enabled, Sandbox: sandbox);

    private sealed class RecordingFactory : IMcpTransportFactory
    {
        public int Count;

        public IClientTransport Create(McpTransportRequest request)
        {
            Interlocked.Increment(ref Count);
            return new DeadTransport();
        }
    }

    private sealed class DeadTransport : IClientTransport
    {
        public string Name => "dead";

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
            throw new IOException("dead transport");
    }

    private sealed class RecordingProfiles(List<string> log) : IAppContainerProfileService
    {
        public List<string> Deleted { get; } = [];

        public bool DeleteServerProfiles(string toolPrefix)
        {
            Deleted.Add(toolPrefix);
            log.Add("profile:" + toolPrefix);
            return true;
        }
    }

    private sealed class RecordingSweeper(List<string> log) : IAceSweeper
    {
        public List<(List<string> Roots, List<string> Names)> Calls { get; } = [];

        public AceSweepResult Sweep(IEnumerable<string> roots, IEnumerable<string> containerNames)
        {
            Calls.Add(([.. roots], [.. containerNames]));
            log.Add("sweep");
            return new AceSweepResult(1, 0, 0);
        }
    }

    private sealed class RecordingStore(List<string> log) : IRuntimeStateStore
    {
        public IReadOnlyList<string> Orphans { get; init; } = [];

        /// <summary>Prefixes whose lock or lease is held, so TryDelete answers false.</summary>
        public HashSet<string> Busy { get; init; } = [];

        public Exception? OrphanFailure { get; init; }

        public List<IReadOnlySet<string>> KnownQueries { get; } = [];

        public static string StateRoot(string prefix) => $@"C:\state\{prefix}.runtime";

        public static string ToolchainRoot(string prefix) => $@"C:\toolchain\{prefix}";

        public void WriteGrantRecord(string containerName, IReadOnlyList<string> roots, bool stateReadOnly = false) =>
            throw new NotSupportedException();

        public bool? StateGrantedReadOnly(string containerName) => throw new NotSupportedException();

        public IReadOnlyList<string> SweepRoots(string prefix) => [StateRoot(prefix), ToolchainRoot(prefix)];

        public bool TryDelete(string prefix, Action? beforeDelete = null)
        {
            log.Add("trydelete:" + prefix);
            if (Busy.Contains(prefix))
                return false;

            beforeDelete?.Invoke();
            log.Add("deleted:" + prefix);
            return true;
        }

        public IReadOnlyList<string> OrphanPrefixes(IReadOnlySet<string> knownPrefixes)
        {
            KnownQueries.Add(knownPrefixes);
            return OrphanFailure is null ? Orphans : throw OrphanFailure;
        }
    }

    /// <summary>The profiles, the sweeper and the store write one log, so the order across them is visible.</summary>
    private sealed record Harness(
        List<string> Log,
        RecordingFactory Factory,
        RecordingProfiles Profiles,
        RecordingSweeper Sweeper,
        RecordingStore Store,
        PluginService Service)
    {
        public void Reset()
        {
            Log.Clear();
            Sweeper.Calls.Clear();
            Profiles.Deleted.Clear();
        }
    }

    private Harness Create(
        IReadOnlyList<string>? orphans = null,
        HashSet<string>? busy = null,
        Exception? orphanFailure = null,
        ILogger<PluginService>? logger = null)
    {
        var log = new List<string>();
        var sqlite = new SqliteContext(_dbPath);
        _contexts.Add(sqlite);

        var factory = new RecordingFactory();
        var profiles = new RecordingProfiles(log);
        var sweeper = new RecordingSweeper(log);
        var store = new RecordingStore(log) { Orphans = orphans ?? [], Busy = busy ?? [], OrphanFailure = orphanFailure };
        var service = SandboxPluginServices.Create(
            sqlite, factory, new ReadyMxcAvailability(), profiles, sweeper, logger, runtimeStateStore: store);

        return new Harness(log, factory, profiles, sweeper, store, service);
    }

    private Guid SeedRow(LocalMcpDefinition definition, bool enabled = true)
    {
        var id = Guid.NewGuid();
        var sqlite = new SqliteContext(_dbPath);
        _contexts.Add(sqlite);
        using var command = sqlite.GetConnection().CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO Plugins (Id, Kind, Name, Description, IconUrl, ConfigJson, Version, IsPreloaded, IsActive, UserEnabled, UpdatedAt)
            VALUES (@Id, 'mcp_server', @Name, NULL, NULL, @ConfigJson, '1.0.0', 0, 1, @UserEnabled, @UpdatedAt)
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@Name", definition.Name);
        command.Parameters.AddWithValue("@ConfigJson", LocalMcpConfig.ToConfigJson(definition, v => v));
        command.Parameters.AddWithValue("@UserEnabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        return id;
    }

    private string CreateExeFolder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pia-runtime-state-exe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        File.WriteAllBytes(Path.Combine(directory, "fs.exe"), []);
        return directory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RemovingAServer_DeletesItsRuntimeState_AfterTheSweepAndTheProfiles()
    {
        var h = Create();
        var id = await h.Service.SaveLocalMcpAsync(null, Definition(Policy), Ct);
        h.Reset();

        await h.Service.RemoveLocalMcpAsync(id);

        Assert.Equal(["sweep", "profile:files", "trydelete:files", "deleted:files"], h.Log);
        var roots = Assert.Single(h.Sweeper.Calls).Roots;
        Assert.Contains(RecordingStore.StateRoot("files"), roots);
        Assert.Contains(RecordingStore.ToolchainRoot("files"), roots);
    }

    /// <summary>Switching the sandbox off keeps the state, so the server that no longer uses it still owns it.</summary>
    [Fact]
    public async Task RemovingAnUncontainedServer_StillDeletesItsRuntimeState()
    {
        var h = Create();
        var id = await h.Service.SaveLocalMcpAsync(null, Definition(sandbox: null), Ct);
        h.Reset();

        await h.Service.RemoveLocalMcpAsync(id);

        Assert.Equal(["profile:files", "trydelete:files", "deleted:files"], h.Log);
    }

    [Fact]
    public async Task RemovingAServerWhoseStateIsInUse_SaysSo_AndStillRemovesTheServer()
    {
        var logger = new CapturingLogger<PluginService>();
        var h = Create(busy: ["files"], logger: logger);
        var id = await h.Service.SaveLocalMcpAsync(null, Definition(Policy), Ct);

        await h.Service.RemoveLocalMcpAsync(id);

        Assert.Empty(h.Service.GetLocalMcpPlugins());
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information && e.Message == "Runtime state for prefix files is in use; the next start removes it");
    }

    [Fact]
    public async Task RenamingAServer_DeletesTheStateOfTheOldPrefixOnly()
    {
        var h = Create();
        var id = await h.Service.SaveLocalMcpAsync(null, Definition(Policy, name: "Files"), Ct);
        h.Reset();

        await h.Service.SaveLocalMcpAsync(id, Definition(Policy, name: "Documents"), Ct);

        Assert.Equal(["sweep", "profile:files", "trydelete:files", "deleted:files"], h.Log);
        var roots = Assert.Single(h.Sweeper.Calls).Roots;
        Assert.Contains(RecordingStore.StateRoot("files"), roots);
        Assert.DoesNotContain(RecordingStore.StateRoot("documents"), roots);
    }

    /// <summary>The editor's Test connection may be using that state right now, under the same prefix.</summary>
    [Fact]
    public async Task ASamePrefixSave_DeletesNothing_AndKeepsTheStateRootsOutOfTheSweep()
    {
        var h = Create();
        var id = await h.Service.SaveLocalMcpAsync(
            null, Definition(new McpSandboxPolicy([@"C:\box\rw", @"C:\box\keep"], [], AllowNetwork: false)), Ct);
        h.Reset();

        await h.Service.SaveLocalMcpAsync(id, Definition(new McpSandboxPolicy([@"C:\box\keep"], [], AllowNetwork: false)), Ct);

        var roots = Assert.Single(h.Sweeper.Calls).Roots;
        Assert.Equal([@"C:\box\rw"], roots);
        Assert.DoesNotContain(h.Log, entry => entry.StartsWith("trydelete:", StringComparison.Ordinal));
        Assert.Empty(h.Profiles.Deleted);
    }

    [Fact]
    public async Task TurningTheSandboxOff_OnASamePrefixSave_SweepsTheStateRoots_ButKeepsTheState()
    {
        var h = Create();
        var id = await h.Service.SaveLocalMcpAsync(null, Definition(Policy), Ct);
        h.Reset();

        await h.Service.SaveLocalMcpAsync(id, Definition(sandbox: null), Ct);

        var roots = Assert.Single(h.Sweeper.Calls).Roots;
        Assert.Contains(RecordingStore.StateRoot("files"), roots);
        Assert.Contains(RecordingStore.ToolchainRoot("files"), roots);
        Assert.DoesNotContain(h.Log, entry => entry.StartsWith("trydelete:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AtStartup_TheSweepCoversTheStateRootsOfEverySandboxedServer_DisabledOnesToo()
    {
        SeedRow(Definition(Policy, name: "Files"));
        SeedRow(Definition(Policy, name: "Docs", enabled: false), enabled: false);
        SeedRow(Definition(sandbox: null, name: "Plain"));
        var h = Create();

        await h.Service.InitializePersistedPluginsAsync();

        var call = Assert.Single(h.Sweeper.Calls);
        Assert.Contains(RecordingStore.StateRoot("files"), call.Roots);
        Assert.Contains(RecordingStore.ToolchainRoot("docs"), call.Roots);
        Assert.DoesNotContain(RecordingStore.StateRoot("plain"), call.Roots);
    }

    [Fact]
    public async Task AtStartup_TheKnownPrefixes_AreEverySavedLocalServer_DisabledOnesToo()
    {
        SeedRow(Definition(Policy, name: "Files"));
        SeedRow(Definition(sandbox: null, name: "Docs", enabled: false), enabled: false);
        var h = Create();

        await h.Service.InitializePersistedPluginsAsync();

        var known = Assert.Single(h.Store.KnownQueries);
        Assert.True(known.SetEquals(["files", "docs"]));
    }

    [Fact]
    public async Task AtStartup_AnOrphan_IsSweptUnderBothContainerNames_AndLosesItsProfiles_InsideBeforeDelete()
    {
        var h = Create(orphans: ["probe"]);

        await h.Service.InitializePersistedPluginsAsync();

        Assert.Equal(["trydelete:probe", "sweep", "profile:probe", "deleted:probe"], h.Log);
        var call = Assert.Single(h.Sweeper.Calls);
        Assert.Equal([RecordingStore.StateRoot("probe"), RecordingStore.ToolchainRoot("probe")], call.Roots);
        Assert.Equal(["pia-mcp-probe", "pia-mcp-probe-probe"], call.Names);
    }

    [Fact]
    public async Task AtStartup_AnOrphanInUse_KeepsItsProfiles_AndGetsNoSweep()
    {
        var h = Create(orphans: ["probe"], busy: ["probe"]);

        await h.Service.InitializePersistedPluginsAsync();

        Assert.Equal(["trydelete:probe"], h.Log);
        Assert.Empty(h.Sweeper.Calls);
        Assert.Empty(h.Profiles.Deleted);
    }

    [Fact]
    public async Task AtStartup_AnOrphanASavedServerHoldsByThen_IsSkipped()
    {
        SeedRow(Definition(sandbox: null, name: "Files", enabled: false), enabled: false);
        var h = Create(orphans: ["files", "probe"]);

        await h.Service.InitializePersistedPluginsAsync();

        Assert.DoesNotContain("trydelete:files", h.Log);
        Assert.Contains("deleted:probe", h.Log);
        Assert.Equal(["probe"], h.Profiles.Deleted);
    }

    [Fact]
    public async Task AtStartup_ACleanupThatFails_LogsTheTypeOnly_AndTheServersStillRun()
    {
        var logger = new CapturingLogger<PluginService>();
        SeedRow(Definition(sandbox: null));
        var h = Create(orphanFailure: new IOException(@"Access to the path 'C:\Users\me\Secret' is denied."), logger: logger);

        await h.Service.InitializePersistedPluginsAsync();

        Assert.Equal(1, h.Factory.Count);
        var warning = Assert.Single(logger.Entries, e => e.Message.StartsWith("The runtime state cleanup failed", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Null(warning.Exception);
        Assert.Contains(nameof(IOException), warning.Message);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Information && e.Message.Contains("Secret"));
    }

    [Fact]
    public async Task ThePreview_SuffixesAPrefixAnotherServerHolds()
    {
        var h = Create();
        await h.Service.SaveLocalMcpAsync(null, Definition(sandbox: null, name: "Files"), Ct);

        Assert.Equal("files_2", h.Service.PreviewLocalMcpToolPrefix(null, "Files"));
        Assert.Equal("files_2", h.Service.PreviewLocalMcpToolPrefix(Guid.NewGuid(), "Files"));
        Assert.Equal("documents", h.Service.PreviewLocalMcpToolPrefix(null, "Documents"));
    }

    [Fact]
    public async Task ThePreview_ForTheServerItself_IsThePrefixItHolds()
    {
        var h = Create();
        var first = await h.Service.SaveLocalMcpAsync(null, Definition(sandbox: null, name: "Files"), Ct);
        var second = await h.Service.SaveLocalMcpAsync(null, Definition(sandbox: null, name: "Files"), Ct);

        Assert.Equal("files", h.Service.PreviewLocalMcpToolPrefix(first, "Files"));
        Assert.Equal("files_2", h.Service.PreviewLocalMcpToolPrefix(second, "Files"));
        Assert.Equal(h.Service.GetLocalMcpDefinition(second)!.ToolPrefix, h.Service.PreviewLocalMcpToolPrefix(second, "Files"));
    }

    [Fact]
    public async Task ACmdWrappedExe_ContributesTheExesFolderToTheSweep()
    {
        var folder = CreateExeFolder();
        SeedRow(
            Definition(Policy, enabled: false) with { Command = "cmd", Args = ["/c", Path.Combine(folder, "fs.exe")] },
            enabled: false);
        var h = Create();

        await h.Service.InitializePersistedPluginsAsync();

        Assert.Contains(SandboxLaunchSpecBuilder.Normalize(folder), Assert.Single(h.Sweeper.Calls).Roots);
    }

    [Fact]
    public async Task ACmdWrappedExe_SavedUnderTheSamePrefix_KeepsTheExesFolderOutOfTheSweep()
    {
        var folder = CreateExeFolder();
        var command = Definition(Policy) with { Command = "cmd", Args = ["/c", Path.Combine(folder, "fs.exe")] };
        var h = Create();
        var id = await h.Service.SaveLocalMcpAsync(
            null, command with { Sandbox = new McpSandboxPolicy([@"C:\box\rw", @"C:\box\keep"], [], AllowNetwork: false) }, Ct);
        h.Reset();

        await h.Service.SaveLocalMcpAsync(
            id, command with { Sandbox = new McpSandboxPolicy([@"C:\box\keep"], [], AllowNetwork: false) }, Ct);

        Assert.Equal([@"C:\box\rw"], Assert.Single(h.Sweeper.Calls).Roots);
    }
}
