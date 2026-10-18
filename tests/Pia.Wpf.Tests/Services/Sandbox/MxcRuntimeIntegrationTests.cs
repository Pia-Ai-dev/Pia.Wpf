using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Pia.Native;
using Pia.Paths;
using Pia.Services.Plugins;
using Pia.Services.Plugins.Sandbox;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>Runners through the real factory, detector, provisioner and container; nothing is downloaded.</summary>
[Collection("MxcRuntimeEnvironment")]
public sealed class MxcRuntimeIntegrationTests : IAsyncDisposable
{
    private const string SkipReason = "MXC cannot create a container on this host (see MxcTestHost.Reason).";

    private readonly MxcRuntimeTestScope _scope = new("rti");

    public ValueTask DisposeAsync() => _scope.DisposeAsync();

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task ADotnetDll_StartsOnTheReadableDotnet_WithoutAToolchainGrant_AndSeesABlankedNameEmpty()
    {
        using var timeout = MxcRuntimeTestScope.Timeout(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        var host = MxcRuntimeTestScope.MachineWideDotnet(OverridingRuntimeHost.Real());
        // Under the test's own root, so the read-only grant of the dll's folder never touches the build output.
        var server = _scope.Folder("server");
        MxcRuntimeTestScope.CopyTree(Path.GetDirectoryName(TestMcpServerFixture.ExePath)!, server);
        var dll = Path.Combine(server, "Pia.TestMcpServer.dll");
        var resolution = MxcRuntimeTestScope.Detect(host, "dotnet", [dll], _scope.Readwrite);
        if (resolution is not { Outcome: RuntimeOutcome.Ready, Install.ReadableAlready: true })
            Assert.Skip("No container-readable .NET 10 runtime on this host");

        var client = await _scope.ConnectAsync(
            host, _scope.Request("dotnet", [dll], allowNetwork: false, new Dictionary<string, string> { ["OS"] = "" }), ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        var environment = await client.CallToolAsync("get_environment", new Dictionary<string, object?> { ["names"] = "OS" },
            cancellationToken: ct);

        Assert.Contains(tools, tool => tool.Name == "get_environment");
        var text = MxcRuntimeTestScope.Text(environment);
        Assert.True(text is "OS: empty" or "OS: absent", text);
        var spec = Assert.Single(_scope.Launcher.Specs);
        Assert.Equal(RunnerKind.DotnetDll, spec.RunnerKind);
        Assert.Empty(spec.Toolchain);
    }

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task ANodeScript_StartsOnACopiedNode_ThroughThePreload_AndLeavesNoAceOnThatCopy()
    {
        using var timeout = MxcRuntimeTestScope.Timeout(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        var real = OverridingRuntimeHost.Real();
        var source = MxcRuntimeTestScope.HostNodeFolder(real, needsNpx: false);
        if (source is null)
            Assert.Skip("No node.exe on this host.");

        var toolchain = _scope.Folder("node");
        File.Copy(Path.Combine(source, "node.exe"), Path.Combine(toolchain, "node.exe"));
        var npm = Directory.CreateDirectory(Path.Combine(toolchain, "node_modules", "npm")).FullName;
        File.WriteAllText(Path.Combine(npm, "package.json"), """{ "name": "npm", "version": "0.0.0" }""");
        // Under the test's own root, so the read-only grant of the script's folder never touches the build output.
        var script = Path.Combine(_scope.Folder("fixture"), "server.js");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "node-mcp", "server.js"), script);
        var host = new OverridingRuntimeHost(real, [toolchain]);

        var client = await _scope.ConnectAsync(host, _scope.Request("node", [script], allowNetwork: false), ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        var echo = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["text"] = "hello" }, cancellationToken: ct);

        Assert.Equal("echo", Assert.Single(tools).Name);
        Assert.Equal("hello", MxcRuntimeTestScope.Text(echo));
        var copy = MxcRuntimeTestScope.FinalPath(real, toolchain);
        var spec = Assert.Single(_scope.Launcher.Specs);
        Assert.Equal(RunnerKind.NodeScript, spec.RunnerKind);
        Assert.True(MxcRuntimeTestScope.SamePath(copy, Assert.Single(spec.Toolchain)), string.Join("|", spec.Toolchain));
        MxcRuntimeTestScope.AssertNothingUnderProgramFiles(real, spec);
        Assert.True(File.Exists(Path.Combine(PiaPaths.McpSandboxSupportDirectory, NodeSandboxPreload.FileName)));
        Assert.Contains(_scope.GrantRecordRoots(), root => MxcRuntimeTestScope.SamePath(root, copy));

        await _scope.StopAsync(client);

        Assert.All(MxcRuntimeTestScope.TreeOf(toolchain), entry => Assert.DoesNotContain(_scope.ContainerSid, MxcRuntimeTestScope.Sids(entry)));
    }
}

/// <summary>One test's throwaway Pia profile, folders and containers; disposing it leaves no profile, ACE or folder.</summary>
internal sealed class MxcRuntimeTestScope : IAsyncDisposable
{
    private readonly IDisposable _paths;
    private readonly List<McpClient> _clients = [];

    // Siblings, never nested: a folder that contains a Pia data root is refused as a grant.
    public MxcRuntimeTestScope(string tag)
    {
        Root = Path.Combine(Path.GetTempPath(), $"pia-{tag}-" + Guid.NewGuid().ToString("N"));
        Prefix = tag + "-" + Guid.NewGuid().ToString("N")[..8];
        Readwrite = Folder("rw");
        _paths = PiaPaths.OverrideForTests(Folder("roaming"), Folder("local"));
    }

    public string Root { get; }

    public string Prefix { get; }

    public string Readwrite { get; }

    public RecordingMxcLauncher Launcher { get; } =
        new(new MxcMcpLauncher(NullLogger<MxcMcpLauncher>.Instance, MxcTestHost.Runtime));

    public string ContainerName => SandboxLaunchSpecBuilder.ContainerName(Prefix, isProbe: false);

    public string ContainerSid => AppContainerNative.DeriveSid(ContainerName)!;

    public string StateDirectory => PiaPaths.McpRuntimeStateDirectory(Prefix);

    public string Folder(string name) => Directory.CreateDirectory(Path.Combine(Root, name)).FullName;

    public McpTransportRequest Request(
        string command, IReadOnlyList<string> args, bool allowNetwork, IReadOnlyDictionary<string, string>? env = null) =>
        new("runtime", command, args, env, Readwrite, new McpSandboxPolicy([Readwrite], [], allowNetwork), Prefix);

    public async Task<McpClient> ConnectAsync(IRuntimeHostService host, McpTransportRequest request, CancellationToken cancellationToken)
    {
        var client = await McpClient.CreateAsync(Factory(host).Create(request), cancellationToken: cancellationToken);
        _clients.Add(client);
        return client;
    }

    public async Task StopAsync(McpClient client)
    {
        await client.DisposeAsync();
        _clients.Remove(client);
    }

    public IReadOnlyList<string> GrantRecordRoots()
    {
        var record = Path.Combine(PiaPaths.McpSandboxGrantsDirectory, ContainerName + ".json");
        using var document = JsonDocument.Parse(File.ReadAllText(record));
        return [.. document.RootElement.GetProperty("roots").EnumerateArray().Select(root => root.GetString()!)];
    }

    // The sweep reads the grant records, so it runs while the profile override still points at them.
    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var client in _clients)
                await client.DisposeAsync();
            _clients.Clear();

            var store = new RuntimeStateStore(NullLogger<RuntimeStateStore>.Instance);
            List<string> roots =
            [
                .. Directory.EnumerateDirectories(Root),
                .. store.SweepRoots(Prefix),
                StateDirectory,
                PiaPaths.McpSandboxSupportDirectory,
            ];
            new AceSweepService(new PiaContainerSidResolver(), NullLogger<AceSweepService>.Instance)
                .Sweep(roots, SandboxLaunchSpecBuilder.ContainerNames(Prefix));
            new AppContainerProfileService(NullLogger<AppContainerProfileService>.Instance).DeleteServerProfiles(Prefix);
        }
        finally
        {
            _paths.Dispose();
            TempPath.Remove(Root);
        }
    }

    private McpTransportFactory Factory(IRuntimeHostService host) => new(
        Launcher,
        new SandboxGrantVerifier(),
        NullLoggerFactory.Instance,
        detector: new RuntimeDetector(host, new RuntimeRootVerifier()),
        host: host,
        provisioner: new RuntimeStateProvisioner(
            new RuntimeStateStore(NullLogger<RuntimeStateStore>.Instance),
            new UvToolRunner(NullLogger<UvToolRunner>.Instance),
            NullLogger<RuntimeStateProvisioner>.Instance));

    public static CancellationTokenSource Timeout(TimeSpan limit)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(limit);
        return source;
    }

    public static RuntimeResolution Detect(IRuntimeHostService host, string command, IReadOnlyList<string> args, string workingDirectory)
    {
        var parse = RunnerCommandParser.Parse(command, args, workingDirectory);
        Assert.Equal(RunnerRefusal.None, parse.Refusal);
        return new RuntimeDetector(host, new RuntimeRootVerifier()).Detect(parse.Runner!, CancellationToken.None);
    }

    // Machine-wide installs only, so a test can never grant the developer's user-scope .NET.
    public static OverridingRuntimeHost MachineWideDotnet(IRuntimeHostService real) =>
        new(real, [.. real.ProgramFilesDirectories.Select(folder => Path.Combine(folder, "dotnet"))]);

    /// <summary>A Node install on this machine, Program Files first; null when there is none.</summary>
    public static string? HostNodeFolder(IRuntimeHostService real, bool needsNpx)
    {
        var detected = new RuntimeDetector(real, new RuntimeRootVerifier())
            .Detect(new RunnerRequest(needsNpx ? RunnerKind.Npx : RunnerKind.NodeScript, null, [], null, [], null), CancellationToken.None)
            .Install?.Root;

        return real.ProgramFilesDirectories
            .Select(folder => Path.Combine(folder, "nodejs"))
            .Append(detected)
            .OfType<string>()
            .FirstOrDefault(folder => File.Exists(Path.Combine(folder, "node.exe"))
                && File.Exists(Path.Combine(folder, "node_modules", "npm", "package.json"))
                && (!needsNpx || File.Exists(Path.Combine(folder, "node_modules", "npm", "bin", "npx-cli.js"))));
    }

    public static void CopyTree(string source, string target)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var directory in Directory.EnumerateDirectories(source, "*", options))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", options))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
    }

    public static string Text(CallToolResult result) =>
        string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    // The detector keeps final paths, links resolved.
    public static string FinalPath(IRuntimeHostService host, string path) => host.ResolveFinalPath(path) ?? path;

    public static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);

    public static void AssertNothingUnderProgramFiles(IRuntimeHostService host, SandboxLaunchSpec spec)
    {
        var granted = spec.Toolchain.Concat(spec.ReadonlyPaths).Concat(spec.ReadwritePaths).Append(spec.GrantedCommandDirectory).OfType<string>();
        Assert.All(granted, path => Assert.DoesNotContain(host.ProgramFilesDirectories,
            folder => path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
    }

    public static IEnumerable<string> TreeOf(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root);

    public static IReadOnlyList<string> Sids(string path)
    {
        FileSystemSecurity acl = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        return [.. acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Select(rule => rule.IdentityReference.Value)];
    }
}
