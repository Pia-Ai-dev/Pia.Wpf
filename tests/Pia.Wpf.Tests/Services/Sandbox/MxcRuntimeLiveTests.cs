using System.Diagnostics;
using System.IO;
using Pia.Paths;
using Pia.Services.Plugins;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>Runners that download their package from npm, NuGet or PyPI, in a real container.</summary>
[Collection("MxcRuntimeEnvironment")]
public sealed class MxcRuntimeLiveTests : IAsyncDisposable
{
    private const string SkipReason = "MXC cannot create a container on this host (see MxcTestHost.Reason).";
    private const string UvDirectoryVariable = "PIA_TEST_UV_DIR";

    private static readonly TimeSpan StartLimit = TimeSpan.FromMinutes(3);

    private readonly MxcRuntimeTestScope _scope = new("rtl");

    public ValueTask DisposeAsync() => _scope.DisposeAsync();

    [LiveApiFact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task Npx_StartsCold_ThenWarm_ThenWarmWithTheNetworkOff()
    {
        var real = OverridingRuntimeHost.Real();
        var source = MxcRuntimeTestScope.HostNodeFolder(real, needsNpx: true);
        if (source is null)
            Assert.Skip("No Node with npx on this host.");

        var toolchain = _scope.Folder("node");
        MxcRuntimeTestScope.CopyTree(source, toolchain);
        var host = new OverridingRuntimeHost(real, [toolchain]);
        string[] args = ["/c", "npx", "-y", "@modelcontextprotocol/server-everything"];

        var cold = await StartAndListToolsAsync(host, _scope.Request("cmd", args, allowNetwork: true));
        Assert.True(Directory.Exists(Path.Combine(_scope.StateDirectory, "npm-cache", "_npx")), "npx cached nothing on the cold start");
        var warm = await StartAndListToolsAsync(host, _scope.Request("cmd", args, allowNetwork: true));
        var offline = await StartAndListToolsAsync(host, _scope.Request("cmd", args, allowNetwork: false));

        Report($"npx cold {cold}, warm {warm}, warm with the network off {offline}");
        var copy = MxcRuntimeTestScope.FinalPath(real, toolchain);
        Assert.Equal(3, _scope.Launcher.Specs.Count);
        Assert.All(_scope.Launcher.Specs, spec =>
        {
            Assert.Equal(RunnerKind.Npx, spec.RunnerKind);
            Assert.True(MxcRuntimeTestScope.SamePath(copy, Assert.Single(spec.Toolchain)), string.Join("|", spec.Toolchain));
            MxcRuntimeTestScope.AssertNothingUnderProgramFiles(real, spec);
        });
    }

    [LiveApiFact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task Dnx_WithoutYesInTheArguments_StartsBecausePiaInsertsIt()
    {
        var host = MxcRuntimeTestScope.MachineWideDotnet(OverridingRuntimeHost.Real());
        string[] args = ["Community.Mcp.DotNet@1.*"];
        var resolution = MxcRuntimeTestScope.Detect(host, "dnx", args, _scope.Readwrite);
        if (resolution is not { Outcome: RuntimeOutcome.Ready, Install.ReadableAlready: true })
            Assert.Skip("No container-readable .NET 10 SDK on this host");

        var elapsed = await StartAndListToolsAsync(host, _scope.Request("dnx", args, allowNetwork: true));

        Report($"dnx {elapsed}");
        var spec = Assert.Single(_scope.Launcher.Specs);
        Assert.Equal(RunnerKind.Dnx, spec.RunnerKind);
        Assert.Contains("--yes", spec.Args);
    }

    [LiveApiFact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task Uvx_InstallsOnAnAllowStart_ThenStartsWithTheNetworkOff_AfterTheCachePrune()
    {
        var uv = UvFolder();
        if (uv is null)
            Assert.Skip($"No uv.exe in {UvDirectoryVariable}, the runtime spike folder or artifacts\\uv.");

        var host = new OverridingRuntimeHost(OverridingRuntimeHost.Real(), [uv]);
        string[] args = ["mcp-server-time"];

        var online = await StartAndListToolsAsync(host, _scope.Request("uvx", args, allowNetwork: true));
        Assert.False(Directory.Exists(Path.Combine(_scope.StateDirectory, "uv-cache")), "uv-cache was not pruned");
        Assert.False(Directory.Exists(Path.Combine(_scope.StateDirectory, "python-cache")), "python-cache was not pruned");
        var offline = await StartAndListToolsAsync(host, _scope.Request("uvx", args, allowNetwork: false));

        Report($"uvx install and start {online}, start with the network off {offline}");
        Assert.Equal(2, _scope.Launcher.Specs.Count);
        Assert.All(_scope.Launcher.Specs, spec =>
        {
            Assert.Equal(RunnerKind.Uvx, spec.RunnerKind);
            Assert.True(spec.RuntimeStateReadOnly);
            Assert.Empty(spec.Toolchain);
        });
    }

    private async Task<TimeSpan> StartAndListToolsAsync(IRuntimeHostService host, McpTransportRequest request)
    {
        using var timeout = MxcRuntimeTestScope.Timeout(StartLimit);
        var clock = Stopwatch.StartNew();
        var client = await _scope.ConnectAsync(host, request, timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        clock.Stop();

        Assert.NotEmpty(tools);
        await _scope.StopAsync(client);
        return clock.Elapsed;
    }

    private static string? UvFolder()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return new[]
            {
                Environment.GetEnvironmentVariable(UvDirectoryVariable),
                Path.Combine(PiaPaths.LocalProfileRoot, "PiaRuntimeSpike", "uv", "toolchain"),
                Path.Combine(repository, "artifacts", "uv"),
            }
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .FirstOrDefault(folder => File.Exists(Path.Combine(folder!, "uv.exe")));
    }

    private static void Report(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);
}
