using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Exceptions;
using Pia.Services.Plugins.Sandbox;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>Real containers; skipped on a host that cannot create one.</summary>
[Collection("MxcRuntimeEnvironment")]
public sealed class MxcMcpLauncherIntegrationTests : IDisposable
{
    private const string SkipReason = "MXC cannot create a container on this host (see MxcTestHost.Reason).";

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "pia-mxc-launch-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefix = "test-" + Guid.NewGuid().ToString("N")[..8];

    public MxcMcpLauncherIntegrationTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        new AppContainerProfileService(NullLogger<AppContainerProfileService>.Instance).DeleteServerProfiles(_prefix);
        TempPath.Remove(_workspace);
    }

    private SandboxLaunchSpec Spec(params string[] args) =>
        new(SandboxLaunchSpecBuilder.ContainerName(_prefix, isProbe: false),
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), args,
            new Dictionary<string, string>(), _workspace, [_workspace], [], AllowNetwork: false,
            GrantedCommandDirectory: null);

    private static Task<LaunchedMxcProcess> Launch(MxcMcpLauncher launcher, SandboxLaunchSpec spec, CancellationToken cancellationToken) =>
        launcher.LaunchAsync(spec, SandboxContainerLease.Acquire(spec.ContainerName), cancellationToken);

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task ARealContainer_RunsTheProgram_AndHandsBackItsOutput()
    {
        var launcher = new MxcMcpLauncher(NullLogger<MxcMcpLauncher>.Instance, MxcTestHost.Runtime);

        var launched = await Launch(launcher, Spec("/c", "echo", "hello-from-the-container"), TestContext.Current.CancellationToken);
        var output = await new StreamReader(launched.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        await launched.ShutdownAsync();

        Assert.Contains("hello-from-the-container", output);
    }

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task TheContainerName_IsFreeAgainOnceTheServerIsShutDown()
    {
        var launcher = new MxcMcpLauncher(NullLogger<MxcMcpLauncher>.Instance, MxcTestHost.Runtime);
        var ct = TestContext.Current.CancellationToken;

        var first = await Launch(launcher, Spec("/c", "echo", "one"), ct);
        await Assert.ThrowsAsync<SandboxStartException>(() => Launch(launcher, Spec("/c", "echo", "two"), ct));
        await first.ShutdownAsync();

        var again = await Launch(launcher, Spec("/c", "echo", "three"), ct);
        await again.ShutdownAsync();
    }

    /// <summary>The runtime directory is Pia's own loading detail; a contained server must never see it.</summary>
    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task TheContainedServer_DoesNotInheritPiasRuntimeDirectory()
    {
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MXC_FFI_DIR")), "the test host must have it set");
        var launcher = new MxcMcpLauncher(NullLogger<MxcMcpLauncher>.Instance, MxcTestHost.Runtime);

        var launched = await Launch(launcher, Spec("/c", "echo [%MXC_FFI_DIR%]"), TestContext.Current.CancellationToken);
        var output = await new StreamReader(launched.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        await launched.ShutdownAsync();

        Assert.Contains("[%MXC_FFI_DIR%]", output);
    }

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task AWorkspaceFile_IsWritableFromInside_AndNothingOutsideIs()
    {
        var launcher = new MxcMcpLauncher(NullLogger<MxcMcpLauncher>.Instance, MxcTestHost.Runtime);
        var inside = Path.Combine(_workspace, "inside.txt");
        var outside = Path.Combine(Path.GetDirectoryName(_workspace)!, "pia-mxc-outside-" + Guid.NewGuid().ToString("N") + ".txt");

        var launched = await Launch(
            launcher, Spec("/s", "/c",$"echo in>{inside} & echo out>{outside}"), TestContext.Current.CancellationToken);
        await new StreamReader(launched.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        await launched.ShutdownAsync();

        Assert.True(File.Exists(inside));
        Assert.False(File.Exists(outside));
    }

    /// <summary>MXC names the missing folder in its message; neither the thrown error nor a warning may carry it.</summary>
    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task AMissingFolder_FailsTheSpawn_WithAPathFreeError()
    {
        var logger = new CapturingLogger<MxcMcpLauncher>();
        var launcher = new MxcMcpLauncher(logger, MxcTestHost.Runtime);
        var missing = Path.Combine(_workspace, "missing");

        var ex = await Assert.ThrowsAsync<SandboxStartException>(
            () => Launch(launcher, Spec("/c", "exit", "0") with { ReadonlyPaths = [missing] }, TestContext.Current.CancellationToken));

        Assert.Equal(
            "The sandbox could not start the server: a folder it needs is missing. Open the server's settings and detect runtimes again.",
            ex.Message);
        PathFreeAssert.IsPathFree(ex);
        Assert.All(logger.Entries.Where(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Information),
            e => Assert.DoesNotContain(_workspace, e.Message, StringComparison.OrdinalIgnoreCase));
    }
}
