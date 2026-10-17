using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Exceptions;
using Pia.Services.Plugins.Sandbox;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>Real containers; skipped on a host that cannot create one.</summary>
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

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task ARealContainer_RunsTheProgram_AndHandsBackItsOutput()
    {
        var launcher = new MxcMcpLauncher(NullLogger<MxcMcpLauncher>.Instance);

        var launched = await launcher.LaunchAsync(Spec("/c", "echo", "hello-from-the-container"), TestContext.Current.CancellationToken);
        var output = await new StreamReader(launched.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        await launched.ShutdownAsync();

        Assert.Contains("hello-from-the-container", output);
    }

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task TheContainerName_IsFreeAgainOnceTheServerIsShutDown()
    {
        var launcher = new MxcMcpLauncher(NullLogger<MxcMcpLauncher>.Instance);
        var ct = TestContext.Current.CancellationToken;

        var first = await launcher.LaunchAsync(Spec("/c", "echo", "one"), ct);
        await Assert.ThrowsAsync<SandboxStartException>(() => launcher.LaunchAsync(Spec("/c", "echo", "two"), ct));
        await first.ShutdownAsync();

        var again = await launcher.LaunchAsync(Spec("/c", "echo", "three"), ct);
        await again.ShutdownAsync();
    }

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task AWorkspaceFile_IsWritableFromInside_AndNothingOutsideIs()
    {
        var launcher = new MxcMcpLauncher(NullLogger<MxcMcpLauncher>.Instance);
        var inside = Path.Combine(_workspace, "inside.txt");
        var outside = Path.Combine(Path.GetDirectoryName(_workspace)!, "pia-mxc-outside-" + Guid.NewGuid().ToString("N") + ".txt");

        var launched = await launcher.LaunchAsync(
            Spec("/s", "/c", $"echo in>{inside} & echo out>{outside}"), TestContext.Current.CancellationToken);
        await new StreamReader(launched.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        await launched.ShutdownAsync();

        Assert.True(File.Exists(inside));
        Assert.False(File.Exists(outside));
    }
}
