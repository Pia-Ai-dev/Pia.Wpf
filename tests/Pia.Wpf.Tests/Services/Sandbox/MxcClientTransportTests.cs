using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using NSubstitute;
using Pia.Services.Exceptions;
using Pia.Services.Plugins.Sandbox;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class MxcClientTransportTests
{
    // Leases are process-wide, so every test gets a container name no parallel test shares.
    private readonly SandboxLaunchSpec _spec = new(
        "pia-mcp-transport-" + Guid.NewGuid().ToString("N"), @"C:\Tools\fake.exe", [], new Dictionary<string, string>(), @"C:\box",
        [@"C:\box"], [], AllowNetwork: false, GrantedCommandDirectory: null);

    private static ISandboxGrantVerifier Grants(bool ok = true, string? reason = null)
    {
        var verifier = Substitute.For<ISandboxGrantVerifier>();
        verifier.Verify(Arg.Any<SandboxLaunchSpec>()).Returns(new SandboxGrantResult(ok, ok ? 0 : 3, reason));
        return verifier;
    }

    private MxcClientTransport Transport(FakeMxcProcessLauncher launcher, ISandboxGrantVerifier? verifier = null) =>
        new(_spec, launcher, verifier ?? Grants(), NullLogger.Instance, NullLoggerFactory.Instance);

    [Fact]
    public async Task ListsTheServersTools_ThroughTheContainerStreams()
    {
        var launcher = new FakeMxcProcessLauncher();
        var ct = TestContext.Current.CancellationToken;

        await using var client = await McpClient.CreateAsync(Transport(launcher), cancellationToken: ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);

        Assert.Equal(["echo"], tools.Select(t => t.Name));
        Assert.Equal(_spec, Assert.Single(launcher.Launched));
    }

    [Fact]
    public async Task Dispose_ClosesStdin_AndShutsTheContainerDownOnce_WithoutTheSdkFiveSecondWait()
    {
        var launcher = new FakeMxcProcessLauncher();
        var client = await McpClient.CreateAsync(Transport(launcher), cancellationToken: TestContext.Current.CancellationToken);

        var clock = Stopwatch.StartNew();
        await client.DisposeAsync();
        clock.Stop();

        Assert.True(launcher.StdinWasClosed);
        Assert.Equal(1, launcher.ShutdownCalls);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"dispose took {clock.Elapsed}");
    }

    [Fact]
    public async Task DisposingTwice_ShutsDownOnce()
    {
        var launcher = new FakeMxcProcessLauncher();
        var client = await McpClient.CreateAsync(Transport(launcher), cancellationToken: TestContext.Current.CancellationToken);

        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.Equal(1, launcher.ShutdownCalls);
    }

    [Fact]
    public async Task AGrantFailure_StopsBeforeAnythingIsLaunched_AndNamesTheReason()
    {
        var launcher = new FakeMxcProcessLauncher();
        var transport = Transport(launcher, Grants(ok: false, reason: "Pia can't sandbox this server: 3 file(s)."));

        var ex = await Assert.ThrowsAsync<SandboxStartException>(
            () => McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("3 file(s)", ex.Message);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task ALaunchFailure_Propagates_AndLeavesNothingRunning()
    {
        var launcher = new FakeMxcProcessLauncher { FailWith = new SandboxStartException("no container for you") };

        var ex = await Assert.ThrowsAsync<SandboxStartException>(
            () => McpClient.CreateAsync(Transport(launcher), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("no container for you", ex.Message);
        Assert.Equal(0, launcher.ShutdownCalls);
        Assert.False(SandboxContainerLease.IsActive(_spec.ContainerName));
    }

    [Fact]
    public async Task AServerThatDiesAtBoot_FailsTheHandshake_ShutsDown_AndKeepsTheLastStderrLines()
    {
        var launcher = new FakeMxcProcessLauncher(FakeMxcProcessLauncher.DiesAtBoot);
        var transport = Transport(launcher);

        await Assert.ThrowsAnyAsync<Exception>(
            () => McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, launcher.ShutdownCalls);
        var lines = await TailContaining(transport, "fatal: cannot load config");
        Assert.Equal(20, lines.Length);
        Assert.Equal("boot line 7", lines[0]);
        Assert.DoesNotContain("boot line 1", lines);
    }

    [Fact]
    public async Task ARequestToAServerThatDied_FailsWithoutTheStderrTailInTheException()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = SandboxedToolCallFailureTests.TransportWithInstallOutput(
            new FakeMxcProcessLauncher(SandboxedToolCallFailureTests.AnswersTheHandshakeThenExits));
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        await client.ListToolsAsync(cancellationToken: ct);

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => client.CallToolAsync("echo", cancellationToken: ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct));

        for (var inner = ex; inner is not null; inner = inner.InnerException)
            Assert.DoesNotContain("secret-pkg", inner.Message);
        Assert.Contains(SandboxedToolCallFailureTests.InstallLine, transport.StderrTail);
    }

    [Fact]
    public async Task StderrLines_AreReadableWhileTheServerRuns()
    {
        var launcher = new FakeMxcProcessLauncher();
        var transport = Transport(launcher);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("server booting", await TailContaining(transport, "server booting"));
    }

    [Fact]
    public async Task ConnectingTwice_IsRefused()
    {
        var transport = Transport(new FakeMxcProcessLauncher());
        var ct = TestContext.Current.CancellationToken;
        await using var session = await transport.ConnectAsync(ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.ConnectAsync(ct));
    }

    [Fact]
    public void Name_CarriesTheContainerName() =>
        Assert.Equal("mxc:" + _spec.ContainerName, Transport(new FakeMxcProcessLauncher()).Name);

    // The lock key is a normalized path; no folder is touched, so no profile redirection is needed.
    private SandboxLaunchSpec RunnerSpec(string state) =>
        _spec with { RuntimeStateDirectory = state, RunnerKind = RunnerKind.Npx };

    private static string NewStateDirectory() => Path.Combine(@"C:\pia-transport-tests", Guid.NewGuid().ToString("N") + ".runtime");

    private static MxcClientTransport RunnerTransport(
        SandboxLaunchSpec spec, IMxcProcessLauncher launcher, IRuntimeStateProvisioner provisioner, ISandboxGrantVerifier? verifier = null) =>
        new(spec, launcher, verifier ?? Grants(), NullLogger.Instance, NullLoggerFactory.Instance, provisioner);

    [Fact]
    public async Task ConnectAsync_ReturnsToItsCaller_WhileThePreparationStillRuns()
    {
        using var gate = new ManualResetEventSlim();
        var verifier = Substitute.For<ISandboxGrantVerifier>();
        verifier.Verify(Arg.Any<SandboxLaunchSpec>()).Returns(_ =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return new SandboxGrantResult(true, 0, null);
        });
        var transport = Transport(new FakeMxcProcessLauncher(), verifier);

        var connecting = transport.ConnectAsync(TestContext.Current.CancellationToken);
        var returnedEarly = !connecting.IsCompleted;
        gate.Set();
        await using var session = await connecting;

        Assert.True(returnedEarly);
    }

    [Fact]
    public async Task TheServersProbe_OnTheSameStateFolder_PreparesOnlyAfterTheServersLaunchReturned()
    {
        var ct = TestContext.Current.CancellationToken;
        var state = NewStateDirectory();
        var events = new List<string>();
        void Record(string e) { lock (events) events.Add(e); }
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstLauncher = new GatedLauncher(new FakeMxcProcessLauncher(), async () =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
        }, () => Record("first launched"));
        var server = RunnerSpec(state);
        var probe = server with { ContainerName = server.ContainerName + "-probe" };
        var first = RunnerTransport(server, firstLauncher, new FakeProvisioner(s => { Record("first prepared"); return s; }));
        var second = RunnerTransport(probe, new FakeMxcProcessLauncher(), new FakeProvisioner(s => { Record("second prepared"); return s; }));

        var firstConnecting = first.ConnectAsync(ct);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var secondConnecting = second.ConnectAsync(ct);
        await Task.Delay(300, ct);
        lock (events)
            Assert.DoesNotContain("second prepared", events);

        releaseFirst.SetResult();
        await using var firstSession = await firstConnecting;
        await using var secondSession = await secondConnecting;

        Assert.Equal(["first prepared", "first launched", "second prepared"], events);
    }

    [Fact]
    public async Task VerifyAndTheLauncher_GetTheProvisionersSpec_AndTheLockIsFreedAfterTheLaunch()
    {
        var state = NewStateDirectory();
        var prepared = RunnerSpec(state) with { Command = @"C:\state\tools\t\Scripts\python.exe", Args = ["-c", "code"] };
        var verifier = Grants();
        var launcher = new FakeMxcProcessLauncher();

        await using var session = await RunnerTransport(RunnerSpec(state), launcher, new FakeProvisioner(_ => prepared), verifier)
            .ConnectAsync(TestContext.Current.CancellationToken);

        verifier.Received(1).Verify(prepared);
        Assert.Equal(prepared, Assert.Single(launcher.Launched));
        using var free = RuntimeStateLocks.TryAcquire(state);
        Assert.NotNull(free);
    }

    [Fact]
    public async Task AProvisionerFailure_StartsNothing_KeepsItsOutputInTheTail_AndFreesTheLock()
    {
        var state = NewStateDirectory();
        var verifier = Grants();
        var launcher = new FakeMxcProcessLauncher();
        var provisioner = new FakeProvisioner((_, stderrLine) =>
        {
            stderrLine("error: no wheel for this platform");
            throw new SandboxStartException("uv could not install the package.");
        });
        var transport = RunnerTransport(RunnerSpec(state), launcher, provisioner, verifier);

        await Assert.ThrowsAsync<SandboxStartException>(() => transport.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Empty(launcher.Launched);
        verifier.DidNotReceive().Verify(Arg.Any<SandboxLaunchSpec>());
        Assert.Equal("error: no wheel for this platform", transport.StderrTail);
        using var free = RuntimeStateLocks.TryAcquire(state);
        Assert.NotNull(free);
        Assert.False(SandboxContainerLease.IsActive(_spec.ContainerName));
    }

    [Fact]
    public async Task AGrantFailureAfterPreparation_FreesTheLockAndTheLease()
    {
        var state = NewStateDirectory();
        var transport = RunnerTransport(RunnerSpec(state), new FakeMxcProcessLauncher(), new FakeProvisioner(s => s), Grants(ok: false));

        await Assert.ThrowsAsync<SandboxStartException>(() => transport.ConnectAsync(TestContext.Current.CancellationToken));

        using var free = RuntimeStateLocks.TryAcquire(state);
        Assert.NotNull(free);
        Assert.False(SandboxContainerLease.IsActive(_spec.ContainerName));
    }

    [Fact]
    public async Task ThePreparation_RunsUnderTheContainersLease_WhichTheContainerKeepsUntilItIsShutDown()
    {
        var ct = TestContext.Current.CancellationToken;
        bool? leasedWhilePreparing = null;
        var provisioner = new FakeProvisioner(s =>
        {
            leasedWhilePreparing = SandboxContainerLease.IsActive(s.ContainerName);
            return s;
        });

        await using (await RunnerTransport(RunnerSpec(NewStateDirectory()), new FakeMxcProcessLauncher(), provisioner).ConnectAsync(ct))
            Assert.True(SandboxContainerLease.IsActive(_spec.ContainerName));

        Assert.True(leasedWhilePreparing);
        Assert.False(SandboxContainerLease.IsActive(_spec.ContainerName));
    }

    // The provisioner writes the grant record; a start that cannot own the name must not touch it.
    [Fact]
    public async Task AStartUnderTheNameOfARunningContainer_IsRefused_BeforeItPreparesAnything()
    {
        var launcher = new FakeMxcProcessLauncher();
        var prepared = false;
        using var running = SandboxContainerLease.Acquire(_spec.ContainerName);

        var ex = await Assert.ThrowsAsync<SandboxStartException>(() =>
            RunnerTransport(RunnerSpec(NewStateDirectory()), launcher, new FakeProvisioner(s => { prepared = true; return s; }))
                .ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal($"The sandbox '{_spec.ContainerName}' is already running.", ex.Message);
        Assert.False(prepared);
        Assert.Empty(launcher.Launched);
        Assert.True(SandboxContainerLease.IsActive(_spec.ContainerName));
    }

    // A container that exits while this start is verified must not let the start run under that container's grant record.
    [Fact]
    public async Task ARunningContainerThatExitsWhileTheStartIsVerified_StillRefusesTheStart()
    {
        var launcher = new FakeMxcProcessLauncher();
        var running = SandboxContainerLease.Acquire(_spec.ContainerName);
        var prepared = false;
        var verifier = Substitute.For<ISandboxGrantVerifier>();
        verifier.Verify(Arg.Any<SandboxLaunchSpec>()).Returns(_ =>
        {
            running.Dispose();
            return new SandboxGrantResult(true, 0, null);
        });
        try
        {
            await Assert.ThrowsAsync<SandboxStartException>(() =>
                RunnerTransport(RunnerSpec(NewStateDirectory()), launcher, new FakeProvisioner(s => { prepared = true; return s; }), verifier)
                    .ConnectAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            running.Dispose();
        }

        Assert.False(prepared);
        Assert.Empty(launcher.Launched);
        Assert.False(SandboxContainerLease.IsActive(_spec.ContainerName));
    }

    private sealed class FakeProvisioner(Func<SandboxLaunchSpec, Action<string>, SandboxLaunchSpec> prepare) : IRuntimeStateProvisioner
    {
        public FakeProvisioner(Func<SandboxLaunchSpec, SandboxLaunchSpec> prepare)
            : this((spec, _) => prepare(spec))
        {
        }

        public Task<SandboxLaunchSpec> PrepareAsync(SandboxLaunchSpec spec, Action<string> stderrLine, CancellationToken cancellationToken) =>
            Task.FromResult(prepare(spec, stderrLine));
    }

    private sealed class GatedLauncher(IMxcProcessLauncher inner, Func<Task> before, Action after) : IMxcProcessLauncher
    {
        public async Task<LaunchedMxcProcess> LaunchAsync(SandboxLaunchSpec spec, SandboxContainerLease lease, CancellationToken cancellationToken)
        {
            await before();
            var launched = await inner.LaunchAsync(spec, lease, cancellationToken);
            after();
            return launched;
        }
    }

    private static async Task<string[]> TailContaining(MxcClientTransport transport, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var lines = transport.StderrTail.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Contains(expected)) return lines;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        return transport.StderrTail.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
