using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Mxc.Sdk.V1;
using NSubstitute;
using Pia.Services.Exceptions;
using Pia.Services.Plugins.Sandbox;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class MxcMcpLauncherTests
{
    private static SandboxLaunchSpec Spec(
        bool network = false,
        string? grantedCommandDirectory = @"C:\Tools\fs",
        IReadOnlyList<string>? ro = null,
        IReadOnlyDictionary<string, string>? env = null) =>
        new("pia-mcp-fs", @"C:\Tools\fs\fs.exe", ["--root", @"C:\a b"],
            env ?? new Dictionary<string, string> { ["TOKEN"] = "t" }, @"C:\box\rw",
            [@"C:\box\rw"], ro ?? [@"C:\box\ro"], network, grantedCommandDirectory);

    private sealed class UnavailableRuntime(string reason) : IMxcRuntime
    {
        public bool TryLoad(out string? why)
        {
            why = reason;
            return false;
        }
    }

    private sealed class LoadedRuntime : IMxcRuntime
    {
        public bool TryLoad(out string? why)
        {
            why = null;
            return true;
        }
    }

    [Fact]
    public async Task WithoutAVerifiedRuntime_NothingIsLaunched_AndTheContainerNameStaysFree()
    {
        var launcher = new MxcMcpLauncher(NullLogger<MxcMcpLauncher>.Instance, new UnavailableRuntime("The sandbox runtime is not installed."));
        var spec = Spec() with { ContainerName = "pia-mcp-unverified-" + Guid.NewGuid().ToString("N") };

        var ex = await Assert.ThrowsAsync<SandboxStartException>(
            () => launcher.LaunchAsync(spec, SandboxContainerLease.Acquire(spec.ContainerName), TestContext.Current.CancellationToken));

        Assert.Equal("The sandbox runtime is not installed.", ex.Message);
        SandboxContainerLease.Acquire(spec.ContainerName).Dispose();
    }

    [Fact]
    public async Task ALeaseForAnotherName_IsRefused_AndFreed()
    {
        var launcher = new MxcMcpLauncher(NullLogger<MxcMcpLauncher>.Instance, new LoadedRuntime());
        var spec = Spec() with { ContainerName = "pia-mcp-mine-" + Guid.NewGuid().ToString("N") };
        var other = "pia-mcp-other-" + Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<ArgumentException>(
            () => launcher.LaunchAsync(spec, SandboxContainerLease.Acquire(other), TestContext.Current.CancellationToken));

        Assert.False(SandboxContainerLease.IsActive(other));
        Assert.False(SandboxContainerLease.IsActive(spec.ContainerName));
    }

    [Fact]
    public void Request_CarriesTheCommandLineAndTheContainerIdentity()
    {
        var request = MxcMcpLauncher.BuildRequest(Spec());

        Assert.Equal("\"C:\\Tools\\fs\\fs.exe\" --root \"C:\\a b\"", request.Command);
        Assert.Equal("pia-mcp-fs", request.ContainerName);
        Assert.Equal(@"C:\box\rw", request.WorkingDirectory);
        Assert.Null(request.TimeoutMs);
    }

    [Fact]
    public void Request_GrantsReadwriteAndReadonlyPaths_PlusTheCommandDirectoryReadOnly()
    {
        var filesystem = MxcMcpLauncher.BuildRequest(Spec()).Filesystem!;

        Assert.Equal([@"C:\box\rw"], filesystem.ReadwritePaths);
        Assert.Equal([@"C:\box\ro", @"C:\Tools\fs"], filesystem.ReadonlyPaths);
    }

    [Fact]
    public void Request_WithNoCommandDirectoryGrant_GrantsOnlyThePolicyPaths()
    {
        var filesystem = MxcMcpLauncher.BuildRequest(Spec(grantedCommandDirectory: null)).Filesystem!;

        Assert.Equal([@"C:\box\ro"], filesystem.ReadonlyPaths);
    }

    [Theory]
    [InlineData(false, NetworkAction.Deny)]
    [InlineData(true, NetworkAction.Allow)]
    public void Request_MapsNetworkToTheEgressDefault(bool allow, NetworkAction expected)
    {
        var request = MxcMcpLauncher.BuildRequest(Spec(network: allow));

        Assert.Equal(expected, request.Network!.Egress!.Default);
    }

    [Fact]
    public void Request_KeepsTheUiOn_BecauseDotNetChildrenFailWithoutIt()
    {
        Assert.False(MxcMcpLauncher.BuildRequest(Spec()).Ui!.Disable);
    }

    [Fact]
    public void Request_LayersTheEnvironmentOnTheDefaultOne()
    {
        var request = MxcMcpLauncher.BuildRequest(Spec());

        Assert.True(request.InheritDefaultEnvironment);
        Assert.Equal("t", request.Environment!["TOKEN"]);
    }

    private static SandboxLaunchSpec RuntimeSpec(bool stateReadOnly = false) =>
        Spec() with
        {
            ToolchainDirectories = [@"C:\Users\me\node", @"C:\Users\me\dotnet"],
            SupportDirectory = @"C:\Users\me\AppData\Local\Pia\McpSandboxSupport",
            RuntimeStateDirectory = @"C:\Users\me\AppData\Local\Pia\McpSandbox\fs.runtime",
            RuntimeStateReadOnly = stateReadOnly,
            RunnerKind = Pia.Services.Plugins.Sandbox.Runtimes.RunnerKind.Npx
        };

    [Fact]
    public void Request_GrantsTheToolchainAndTheSupportFolderReadOnly_AndTheStateFolderReadWrite()
    {
        var filesystem = MxcMcpLauncher.BuildRequest(RuntimeSpec()).Filesystem!;

        Assert.Equal([@"C:\box\rw", @"C:\Users\me\AppData\Local\Pia\McpSandbox\fs.runtime"], filesystem.ReadwritePaths);
        Assert.Equal(
            [@"C:\box\ro", @"C:\Tools\fs", @"C:\Users\me\node", @"C:\Users\me\dotnet", @"C:\Users\me\AppData\Local\Pia\McpSandboxSupport"],
            filesystem.ReadonlyPaths);
    }

    [Fact]
    public void Request_GrantsTheStateFolderReadOnly_WhenTheSpecSaysSo()
    {
        var filesystem = MxcMcpLauncher.BuildRequest(RuntimeSpec(stateReadOnly: true)).Filesystem!;

        Assert.Equal([@"C:\box\rw"], filesystem.ReadwritePaths);
        Assert.Contains(@"C:\Users\me\AppData\Local\Pia\McpSandbox\fs.runtime", filesystem.ReadonlyPaths);
    }

    [Fact]
    public void Request_WithoutARuntime_GrantsNoRuntimeFolders()
    {
        var filesystem = MxcMcpLauncher.BuildRequest(Spec() with { RuntimeStateReadOnly = true }).Filesystem!;

        Assert.Equal([@"C:\box\rw"], filesystem.ReadwritePaths);
        Assert.Equal([@"C:\box\ro", @"C:\Tools\fs"], filesystem.ReadonlyPaths);
    }

    [Fact]
    public void Request_DoesNotLetTheCallerMutateTheSpec()
    {
        var env = new Dictionary<string, string> { ["A"] = "1" };
        var request = MxcMcpLauncher.BuildRequest(Spec(env: env));

        request.Environment!["B"] = "2";

        Assert.DoesNotContain("B", env.Keys);
    }

    private static (MxcMcpLauncher Launcher, CapturingLogger<MxcMcpLauncher> Logger) LoggingLauncher()
    {
        var logger = new CapturingLogger<MxcMcpLauncher>();
        return (new MxcMcpLauncher(logger, new UnavailableRuntime("unused")), logger);
    }

    private static void AssertLogsTheTypeOnly(CapturingLogger<MxcMcpLauncher> logger, Type failure)
    {
        var warnings = logger.Entries.Where(e => e.Level >= LogLevel.Warning).ToList();
        Assert.Contains(warnings, e => e.Message.Contains(failure.Name));
        Assert.All(warnings, e =>
        {
            Assert.Null(e.Exception);
            Assert.DoesNotContain("Secret", e.Message);
        });
    }

    /// <summary>A path in an exception message must not reach a release log; the type says what happened.</summary>
    [Fact]
    public async Task AShutdownThatFails_LogsTheExceptionTypeNotItsMessage()
    {
        var (launcher, logger) = LoggingLauncher();
        var process = Substitute.For<IMxcProcess>();
        process.WaitAsync(Arg.Any<CancellationToken>())
            .Returns<Task<WaitResult>>(_ => throw new InvalidOperationException(@"Lost 'C:\Users\me\Secret\box'."));
        using var lease = SandboxContainerLease.Acquire("pia-mcp-log-" + Guid.NewGuid().ToString("N"));

        await launcher.ShutdownAsync("pia-mcp-log", process, Stream.Null, lease);

        AssertLogsTheTypeOnly(logger, typeof(InvalidOperationException));
    }

    [Fact]
    public async Task AKillThatFails_LogsTheExceptionTypeNotItsMessage()
    {
        var (launcher, logger) = LoggingLauncher();
        var process = Substitute.For<IMxcProcess>();
        process.WaitAsync(Arg.Any<CancellationToken>()).Returns<Task<WaitResult>>(_ => throw new OperationCanceledException());
        process.When(p => p.Kill()).Do(_ => throw new IOException(@"Cannot end 'C:\Users\me\Secret\box'."));
        using var lease = SandboxContainerLease.Acquire("pia-mcp-log-" + Guid.NewGuid().ToString("N"));

        await launcher.ShutdownAsync("pia-mcp-log", process, Stream.Null, lease);

        AssertLogsTheTypeOnly(logger, typeof(IOException));
    }

    [Fact]
    public async Task TheDisposal_WaitsForTheAclGate()
    {
        var ct = TestContext.Current.CancellationToken;
        var (launcher, _) = LoggingLauncher();
        var process = Substitute.For<IMxcProcess>();
        process.WaitAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(default(WaitResult)));
        var lease = SandboxContainerLease.Acquire("pia-mcp-gate-" + Guid.NewGuid().ToString("N"));
        var gate = await MxcAclGate.EnterAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
        Task shutdown;
        try
        {
            shutdown = launcher.ShutdownAsync("pia-mcp-gate", process, Stream.Null, lease);
            await Task.Delay(100, ct);

            Assert.False(shutdown.IsCompleted);
            process.DidNotReceive().Dispose();
        }
        finally
        {
            gate.Dispose();
        }

        await shutdown.WaitAsync(TimeSpan.FromSeconds(30), ct);
        process.Received(1).Dispose();
    }

    [Fact]
    public async Task ACancelledSpawn_KeepsTheGateAndTheLease_UntilTheLateProcessIsDisposed()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = "pia-mcp-cancel-" + Guid.NewGuid().ToString("N");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawned = new TaskCompletionSource<IMxcProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new MxcMcpLauncher(NullLogger<MxcMcpLauncher>.Instance, new LoadedRuntime())
        {
            Spawn = _ =>
            {
                started.TrySetResult();
                return spawned.Task;
            }
        };
        Task<IDisposable>? gateProbe = null;
        var leaseHeldAtDispose = false;
        var gateHeldAtDispose = false;
        var process = Substitute.For<IMxcProcess>();
        process.When(p => p.Dispose()).Do(_ =>
        {
            leaseHeldAtDispose = SandboxContainerLease.IsActive(name);
            gateHeldAtDispose = gateProbe is { IsCompleted: false };
        });
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var launch = launcher.LaunchAsync(Spec() with { ContainerName = name }, SandboxContainerLease.Acquire(name), cancel.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            await cancel.CancelAsync();
            await Task.Delay(100, ct);

            Assert.False(launch.IsCompleted);
            Assert.True(SandboxContainerLease.IsActive(name));
            gateProbe = MxcAclGate.EnterAsync(ct);
            Assert.False(gateProbe.IsCompleted);

            spawned.SetResult(process);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launch.WaitAsync(TimeSpan.FromSeconds(30), ct));

            process.Received(1).Dispose();
            Assert.True(leaseHeldAtDispose);
            Assert.True(gateHeldAtDispose);
            Assert.False(SandboxContainerLease.IsActive(name));
            (await gateProbe.WaitAsync(TimeSpan.FromSeconds(30), ct)).Dispose();
        }
        finally
        {
            spawned.TrySetResult(process);
            ReleaseWhenEntered(gateProbe);
        }
    }

    private static void ReleaseWhenEntered(Task<IDisposable>? entry) =>
        _ = entry?.ContinueWith(
            entered => entered.Result.Dispose(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
}
