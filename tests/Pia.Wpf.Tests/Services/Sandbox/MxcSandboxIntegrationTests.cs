using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Pia.Infrastructure;
using Pia.Native;
using Pia.Services.Plugins;
using Pia.Services.Plugins.Sandbox;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>The test MCP server, started through the real factory, launcher and container, against a fixture tree:
/// <c>workspace</c> read-write, <c>protected</c> read-only, <c>secret</c> never granted. Verdicts come from the
/// files on disk and their hashes, never from what the server says. Skipped on a host that cannot spawn.</summary>
[Collection("MxcRuntimeEnvironment")]
public sealed class MxcSandboxIntegrationTests : IAsyncLifetime
{
    private const string SkipReason = "MXC cannot create a container on this host (see MxcTestHost.Reason).";
    private const string ProtectedText = "PROTECTED - must survive";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pia-t1-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefix = "t1-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "pia-t1-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly List<McpClient> _clients = [];
    private readonly List<SqliteContext> _contexts = [];
    private readonly List<PluginService> _services = [];
    private readonly List<string> _savedPrefixes = [];

    public ValueTask InitializeAsync()
    {
        Seed(_root);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();
        foreach (var service in _services)
            await service.ShutdownAllAsync();
        foreach (var context in _contexts)
            context.Dispose();

        var profiles = new AppContainerProfileService(NullLogger<AppContainerProfileService>.Instance);
        profiles.DeleteServerProfiles(_prefix);
        foreach (var prefix in _savedPrefixes)
            profiles.DeleteServerProfiles(prefix);

        TempPath.RemoveFile(_dbPath);
        TempPath.Remove(_root);
    }

    private static void Seed(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "workspace"));
        Directory.CreateDirectory(Path.Combine(root, "protected"));
        Directory.CreateDirectory(Path.Combine(root, "secret"));
        File.WriteAllText(Path.Combine(root, "workspace", "readme.txt"), "workspace readme");
        File.WriteAllText(Path.Combine(root, "workspace", "draft.txt"), "draft");
        File.WriteAllText(Path.Combine(root, "protected", "important.txt"), ProtectedText);
        File.WriteAllText(Path.Combine(root, "secret", "passwords.txt"), "hunter2");
    }

    private static string Sha(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "(missing)";

    private static McpTransportFactory Factory() =>
        new(new MxcMcpLauncher(NullLogger<MxcMcpLauncher>.Instance, MxcTestHost.Runtime), new SandboxGrantVerifier(), NullLoggerFactory.Instance);

    private McpSandboxPolicy Policy(string root, bool network = false) =>
        new([Path.Combine(root, "workspace")], [Path.Combine(root, "protected")], network);

    private McpTransportRequest Request(string root, McpSandboxPolicy? policy, bool probe = false) =>
        new("t1", TestMcpServerFixture.ExePath, [], null, Path.Combine(root, "workspace"), policy, _prefix, probe);

    private PluginService CreateService()
    {
        var sqlite = new SqliteContext(_dbPath);
        _contexts.Add(sqlite);
        var service = SandboxPluginServices.Create(sqlite, Factory(), new ReadyMxcAvailability());
        _services.Add(service);
        return service;
    }

    private async Task<string> SaveSandboxedAsync(PluginService service, string name)
    {
        var definition = new LocalMcpDefinition(name, TestMcpServerFixture.ExePath, [], new Dictionary<string, string>(),
            Path.Combine(_root, "workspace"), LocalMcpConfig.DeriveToolPrefix(name), null, Sandbox: Policy(_root));

        var id = await service.SaveLocalMcpAsync(null, definition, TestContext.Current.CancellationToken);

        var prefix = service.GetLocalMcpDefinition(id)!.ToolPrefix;
        _savedPrefixes.Add(prefix);
        var status = service.GetLocalMcpStatus(id);
        Assert.True(status.IsRunning, status.Error);
        Assert.NotEmpty(status.ActiveTools);
        return AppContainerNative.DeriveSid("pia-mcp-" + prefix)!;
    }

    private async Task<McpClient> ConnectAsync(McpTransportRequest request)
    {
        var client = await McpClient.CreateAsync(Factory().Create(request), cancellationToken: TestContext.Current.CancellationToken);
        _clients.Add(client);
        return client;
    }

    private static async Task<(bool IsError, string Text)> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
        return (result.IsError == true, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
    }

    private static List<string> ExplicitSids(string path)
    {
        FileSystemSecurity acl = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        return [.. acl.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Select(r => r.IdentityReference.Value)];
    }

    private string OurSid => AppContainerNative.DeriveSid("pia-mcp-" + _prefix)!;

    private IEnumerable<string> GrantedPaths(string root) =>
        [Path.Combine(root, "workspace"), Path.Combine(root, "protected"), Path.GetDirectoryName(TestMcpServerFixture.ExePath)!];

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task TheWorkspace_CanBeListedReadWrittenAndEmptied_FromInsideTheContainer()
    {
        var client = await ConnectAsync(Request(_root, Policy(_root)));
        var workspace = Path.Combine(_root, "workspace");

        var list = await CallAsync(client, "list_directory", new() { ["path"] = workspace });
        var read = await CallAsync(client, "read_file", new() { ["path"] = Path.Combine(workspace, "readme.txt") });
        var write = await CallAsync(client, "write_file", new() { ["path"] = Path.Combine(workspace, "note.txt"), ["content"] = "hello" });
        var delete = await CallAsync(client, "delete_file", new() { ["path"] = Path.Combine(workspace, "draft.txt") });

        Assert.All(new[] { list, read, write, delete }, r => Assert.False(r.IsError, r.Text));
        Assert.Contains("readme.txt", list.Text);
        Assert.Equal("workspace readme", read.Text);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(workspace, "note.txt")));
        Assert.False(File.Exists(Path.Combine(workspace, "draft.txt")));
    }

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task TheReadonlyFolder_CanBeReadButNeitherDeletedFromNorWrittenTo_AndKeepsItsHash()
    {
        var client = await ConnectAsync(Request(_root, Policy(_root)));
        var important = Path.Combine(_root, "protected", "important.txt");
        var before = Sha(important);

        var read = await CallAsync(client, "read_file", new() { ["path"] = important });
        var delete = await CallAsync(client, "delete_file", new() { ["path"] = important });
        var write = await CallAsync(client, "write_file", new() { ["path"] = Path.Combine(_root, "protected", "planted.txt"), ["content"] = "x" });
        var overwrite = await CallAsync(client, "write_file", new() { ["path"] = important, ["content"] = "tampered" });

        Assert.False(read.IsError, read.Text);
        Assert.Equal(ProtectedText, read.Text);
        Assert.All(new[] { delete, write, overwrite }, r => Assert.True(r.IsError, "denied: " + r.Text));
        Assert.Equal(before, Sha(important));
        Assert.False(File.Exists(Path.Combine(_root, "protected", "planted.txt")));
    }

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task AFolderThatWasNeverGranted_CannotBeReadListedOrDeletedFrom()
    {
        var client = await ConnectAsync(Request(_root, Policy(_root)));
        var passwords = Path.Combine(_root, "secret", "passwords.txt");
        var before = Sha(passwords);

        var read = await CallAsync(client, "read_file", new() { ["path"] = passwords });
        var list = await CallAsync(client, "list_directory", new() { ["path"] = Path.Combine(_root, "secret") });
        var delete = await CallAsync(client, "delete_file", new() { ["path"] = passwords });

        Assert.All(new[] { read, list, delete }, r => Assert.True(r.IsError, "denied: " + r.Text));
        Assert.DoesNotContain("hunter2", read.Text);
        Assert.Equal(before, Sha(passwords));
    }

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task NothingOutsideTheGrants_CanBeWritten()
    {
        var client = await ConnectAsync(Request(_root, Policy(_root)));
        var outside = Path.Combine(_root, "outside.txt");

        var write = await CallAsync(client, "write_file", new() { ["path"] = outside, ["content"] = "x" });

        Assert.True(write.IsError, write.Text);
        Assert.False(File.Exists(outside));
    }

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task WithTheNetworkOff_NothingLeavesTheMachine()
    {
        var client = await ConnectAsync(Request(_root, Policy(_root)));

        var export = await CallAsync(client, "export", new() { ["url"] = "http://1.1.1.1/", ["path"] = Path.Combine(_root, "workspace", "readme.txt") });

        Assert.True(export.IsError, export.Text);
        Assert.Contains("HttpRequestException", export.Text);
    }

    /// <summary>Proves the denials above are the container's: the same calls against a fresh copy, uncontained, do the damage.</summary>
    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task TheSameCallsUncontained_DoTheDamage()
    {
        var copy = Path.Combine(_root, "control");
        Seed(copy);
        var client = await ConnectAsync(Request(copy, policy: null));
        var important = Path.Combine(copy, "protected", "important.txt");
        var outside = Path.Combine(copy, "outside.txt");

        var read = await CallAsync(client, "read_file", new() { ["path"] = Path.Combine(copy, "secret", "passwords.txt") });
        var write = await CallAsync(client, "write_file", new() { ["path"] = outside, ["content"] = "x" });
        var delete = await CallAsync(client, "delete_file", new() { ["path"] = important });

        Assert.All(new[] { read, write, delete }, r => Assert.False(r.IsError, r.Text));
        Assert.Equal("hunter2", read.Text);
        Assert.True(File.Exists(outside));
        Assert.False(File.Exists(important));
    }

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task TheSandboxedServerRunsInAnAppContainer_AndTheUncontainedOneDoesNot()
    {
        var sandboxed = await ConnectAsync(Request(_root, Policy(_root)));
        var copy = Path.Combine(_root, "control");
        Seed(copy);
        var bare = await ConnectAsync(Request(copy, policy: null) with { ToolPrefix = _prefix + "x" });

        var inside = await CallAsync(sandboxed, "get_server_info", []);
        var outside = await CallAsync(bare, "get_server_info", []);

        Assert.True(JsonDocument.Parse(inside.Text).RootElement.GetProperty("inAppContainer").GetBoolean());
        Assert.False(JsonDocument.Parse(outside.Text).RootElement.GetProperty("inAppContainer").GetBoolean());
    }

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task ATestConnection_RunsBesideTheRealServer_BecauseItGetsItsOwnContainer()
    {
        var running = await ConnectAsync(Request(_root, Policy(_root)));
        var probe = await ConnectAsync(Request(_root, Policy(_root), probe: true));

        var fromRunning = await running.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var fromProbe = await probe.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(fromRunning);
        Assert.Equal(fromRunning.Count, fromProbe.Count);
    }

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task ShuttingDown_IsPrompt_FreesTheName_AndLeavesNoAceOnAnyGrantedPath()
    {
        var request = Request(_root, Policy(_root));
        var client = await ConnectAsync(request);
        await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.All(GrantedPaths(_root).Take(2), p => Assert.Contains(OurSid, ExplicitSids(p)));

        var clock = Stopwatch.StartNew();
        await client.DisposeAsync();
        clock.Stop();
        _clients.Remove(client);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"shutdown took {clock.Elapsed}");
        Assert.All(GrantedPaths(_root), p => Assert.DoesNotContain(OurSid, ExplicitSids(p)));
        await ConnectAsync(request);
    }

    /// <summary>Closing Pia goes through the service, not through a client the test disposes itself.</summary>
    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task ClosingPia_TakesASandboxedServerDown_PromptlyAndLeavesNoAceOfItsContainer()
    {
        var service = CreateService();
        var sid = await SaveSandboxedAsync(service, _prefix + "-a");
        Assert.All(GrantedPaths(_root).Take(2), p => Assert.Contains(sid, ExplicitSids(p)));

        var clock = Stopwatch.StartNew();
        await service.ShutdownAllAsync();
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"shutdown took {clock.Elapsed}");
        Assert.All(GrantedPaths(_root), p => Assert.DoesNotContain(sid, ExplicitSids(p)));
    }

    /// <summary>Two containers share the exe folder, so their ACE removals can touch the same DACL at once.</summary>
    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task ClosingPia_TakesTwoSandboxedServersDown_AndLeavesNoAceOfEitherContainer()
    {
        var service = CreateService();
        var first = await SaveSandboxedAsync(service, _prefix + "-a");
        var second = await SaveSandboxedAsync(service, _prefix + "-b");
        Assert.NotEqual(first, second);
        Assert.All(GrantedPaths(_root), p => Assert.Contains(first, ExplicitSids(p)));
        Assert.All(GrantedPaths(_root), p => Assert.Contains(second, ExplicitSids(p)));

        var clock = Stopwatch.StartNew();
        await service.ShutdownAllAsync();
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"shutdown took {clock.Elapsed}");
        Assert.All(GrantedPaths(_root), p =>
        {
            var sids = ExplicitSids(p);
            Assert.DoesNotContain(first, sids);
            Assert.DoesNotContain(second, sids);
        });
    }

    /// <summary>A host killed before it could dispose leaves the container's ACEs on the folders; the sweep finds and removes them.</summary>
    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task ALeakedAce_IsRemovedBySweepingTheFolders()
    {
        var plan = SandboxLaunchSpecBuilder.Build(Request(_root, Policy(_root)));
        var launcher = new MxcMcpLauncher(NullLogger<MxcMcpLauncher>.Instance, MxcTestHost.Runtime);
        var launched = await launcher.LaunchAsync(plan.Spec!, TestContext.Current.CancellationToken);
        try
        {
            using (var child = Process.GetProcessById(launched.ProcessId!.Value))
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
            }

            var leaked = GrantedPaths(_root).Where(p => ExplicitSids(p).Contains(OurSid)).ToList();
            Assert.NotEmpty(leaked);

            // A killed host's lease dies with it; this process still holds the one it launched with.
            var result = new AceSweepService(new PiaContainerSidResolver(isRunning: _ => false), NullLogger<AceSweepService>.Instance)
                .Sweep(GrantedPaths(_root), ["pia-mcp-" + _prefix]);

            Assert.Equal(0, result.Failed);
            Assert.True(result.Removed >= leaked.Count, $"removed {result.Removed}, leaked on {leaked.Count} paths");
            Assert.All(GrantedPaths(_root), p => Assert.DoesNotContain(OurSid, ExplicitSids(p)));
        }
        finally
        {
            await launched.ShutdownAsync();
        }
    }
}
