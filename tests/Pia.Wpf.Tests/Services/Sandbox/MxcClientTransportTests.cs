using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using NSubstitute;
using Pia.Services.Exceptions;
using Pia.Services.Plugins.Sandbox;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class MxcClientTransportTests
{
    private static readonly SandboxLaunchSpec Spec = new(
        "pia-mcp-fake", @"C:\Tools\fake.exe", [], new Dictionary<string, string>(), @"C:\box",
        [@"C:\box"], [], AllowNetwork: false, GrantedCommandDirectory: null);

    private static ISandboxGrantVerifier Grants(bool ok = true, string? reason = null)
    {
        var verifier = Substitute.For<ISandboxGrantVerifier>();
        verifier.Verify(Arg.Any<SandboxLaunchSpec>()).Returns(new SandboxGrantResult(ok, ok ? 0 : 3, reason));
        return verifier;
    }

    private static MxcClientTransport Transport(FakeMxcProcessLauncher launcher, ISandboxGrantVerifier? verifier = null) =>
        new(Spec, launcher, verifier ?? Grants(), NullLogger.Instance, NullLoggerFactory.Instance);

    [Fact]
    public async Task ListsTheServersTools_ThroughTheContainerStreams()
    {
        var launcher = new FakeMxcProcessLauncher();
        var ct = TestContext.Current.CancellationToken;

        await using var client = await McpClient.CreateAsync(Transport(launcher), cancellationToken: ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);

        Assert.Equal(["echo"], tools.Select(t => t.Name));
        Assert.Equal(Spec, Assert.Single(launcher.Launched));
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
    public void Name_CarriesTheContainerName() => Assert.Equal("mxc:pia-mcp-fake", Transport(new FakeMxcProcessLauncher()).Name);

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
