using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Paths;
using Pia.Services.Plugins.Sandbox;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>The editor proposes a folder under the sandbox root without creating it; Pia owns that root, so the
/// start creates what is missing there and nothing else.</summary>
[Collection("PiaPathsStatic")]
public sealed class MxcClientTransportOwnedFolderTests : IDisposable
{
    private readonly string _local = Path.Combine(Path.GetTempPath(), "pia-owned-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempPath.Remove(_local);

    private static MxcClientTransport Transport(SandboxLaunchSpec spec)
    {
        var verifier = Substitute.For<ISandboxGrantVerifier>();
        verifier.Verify(Arg.Any<SandboxLaunchSpec>()).Returns(new SandboxGrantResult(true, 0, null));
        return new MxcClientTransport(spec, new FakeMxcProcessLauncher(), verifier, NullLogger.Instance, NullLoggerFactory.Instance);
    }

    private static SandboxLaunchSpec Spec(params string[] readwrite) =>
        new("pia-mcp-fs", @"C:\Tools\fs.exe", [], new Dictionary<string, string>(), @"C:\box", readwrite, [],
            AllowNetwork: false, GrantedCommandDirectory: null);

    [Fact]
    public async Task AMissingFolderUnderTheSandboxRoot_IsCreatedAtStart()
    {
        using var _ = PiaPaths.OverrideForTests(null, _local);
        var owned = Path.Combine(PiaPaths.McpSandboxRoot, "fs");

        await using var session = await Transport(Spec(owned)).ConnectAsync(TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(owned));
    }

    [Fact]
    public async Task TheProvisioner_RunsOnceTheOwnedFoldersExist_AndBeforeTheGrantCheck()
    {
        using var _ = PiaPaths.OverrideForTests(null, _local);
        var owned = Path.Combine(PiaPaths.McpSandboxRoot, "fs");
        var events = new List<string>();
        var verifier = Substitute.For<ISandboxGrantVerifier>();
        verifier.Verify(Arg.Any<SandboxLaunchSpec>()).Returns(call =>
        {
            events.Add("verify");
            return new SandboxGrantResult(true, 0, null);
        });
        var provisioner = Substitute.For<IRuntimeStateProvisioner>();
        provisioner.PrepareAsync(Arg.Any<SandboxLaunchSpec>(), Arg.Any<Action<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                events.Add(Directory.Exists(owned) ? "prepare, folders exist" : "prepare, folders missing");
                return Task.FromResult(call.Arg<SandboxLaunchSpec>());
            });
        var spec = Spec(owned) with { RuntimeStateDirectory = PiaPaths.McpRuntimeStateDirectory("fs"), RunnerKind = RunnerKind.Npx };
        var transport = new MxcClientTransport(spec, new FakeMxcProcessLauncher(), verifier, NullLogger.Instance,
            NullLoggerFactory.Instance, provisioner);

        await using var session = await transport.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["prepare, folders exist", "verify"], events);
    }

    [Fact]
    public async Task AMissingFolderAnywhereElse_IsLeftAlone()
    {
        using var _ = PiaPaths.OverrideForTests(null, _local);
        var elsewhere = Path.Combine(_local, "not-the-sandbox-root", "fs");

        await using var session = await Transport(Spec(elsewhere)).ConnectAsync(TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(elsewhere));
    }
}
