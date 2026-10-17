using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using NSubstitute;
using Pia.Services.Plugins;
using Pia.Services.Exceptions;
using Pia.Services.Plugins.Sandbox;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class McpTransportFactoryTests
{
    private static McpTransportRequest Request(McpSandboxPolicy? sandbox, string command = @"C:\Tools\fs\fs.exe", bool probe = false) =>
        new("Files", command, ["--x"], new Dictionary<string, string> { ["TOKEN"] = "t" }, null, sandbox, "fs", probe);

    private static readonly SandboxGuardEnvironment EverythingExists =
        SandboxGuardEnvironment.Current() with { PathExists = _ => true };

    private static McpTransportFactory WithLauncher(FakeMxcProcessLauncher? launcher = null) =>
        new(launcher ?? new FakeMxcProcessLauncher(), Substitute.For<ISandboxGrantVerifier>(),
            NullLoggerFactory.Instance, fileExists: _ => true, guardEnvironment: EverythingExists);

    [Theory]
    [InlineData(@"relative\folder")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"\\server\share")]
    public void ASandboxedRequest_WithAPathTheGuardRefuses_IsRefused_NotRunWithIt(string readwrite)
    {
        var ex = Assert.Throws<SandboxStartException>(
            () => WithLauncher().Create(Request(new McpSandboxPolicy([readwrite], [], false))));

        Assert.StartsWith("Pia can't sandbox this server:", ex.Message);
        Assert.DoesNotContain(readwrite, ex.Message);
    }

    [Fact]
    public void ASandboxedRequest_WithAProgramInAWideFolder_IsRefused()
    {
        var downloads = Path.Combine(SandboxGuardEnvironment.Current().ProfileRoot, "Downloads");

        var ex = Assert.Throws<SandboxStartException>(
            () => WithLauncher().Create(Request(McpSandboxPolicy.Empty, Path.Combine(downloads, "server.exe"))));

        Assert.Contains("Put the server in its own folder", ex.Message);
    }

    [Fact]
    public void ASandboxedRequest_WithNoFolders_IsAccepted()
    {
        Assert.IsType<MxcClientTransport>(WithLauncher().Create(Request(McpSandboxPolicy.Empty)));
    }

    [Fact]
    public void WithoutASandbox_ItIsAPlainStdioTransport()
    {
        var transport = new McpTransportFactory().Create(Request(sandbox: null));

        Assert.IsType<StdioClientTransport>(transport);
    }

    [Fact]
    public void WithASandbox_ItIsAContainerTransport()
    {
        var transport = WithLauncher().Create(Request(new McpSandboxPolicy([@"C:\box"], [], false)));

        var mxc = Assert.IsType<MxcClientTransport>(transport);
        Assert.Equal("mxc:pia-mcp-fs", mxc.Name);
    }

    [Fact]
    public void AProbeOfASandboxedServer_GetsItsOwnContainerName()
    {
        var transport = WithLauncher().Create(Request(new McpSandboxPolicy([], [], false), probe: true));

        Assert.Equal("mxc:pia-mcp-fs-probe", Assert.IsType<MxcClientTransport>(transport).Name);
    }

    /// <summary>The fail-closed rule: whatever is missing, a sandboxed request never degrades to a bare process.</summary>
    [Fact]
    public void ASandboxedRequest_WithNoLauncher_IsRefused_NotRunUncontained()
    {
        var factory = new McpTransportFactory(launcher: null, verifier: null, NullLoggerFactory.Instance, fileExists: _ => true);

        var ex = Assert.Throws<SandboxStartException>(() => factory.Create(Request(McpSandboxPolicy.Empty)));

        Assert.Contains("not available", ex.Message);
    }

    [Theory]
    [InlineData(@"C:\Tools\run.cmd")]
    [InlineData(@"C:\Tools\run.bat")]
    public void ASandboxedScriptLauncher_IsRefused_NotRunUncontained(string command)
    {
        var ex = Assert.Throws<SandboxStartException>(() => WithLauncher().Create(Request(McpSandboxPolicy.Empty, command)));

        Assert.Contains("script launcher", ex.Message);
    }

    [Fact]
    public void ASandboxedCommandThatIsNotThere_IsRefused()
    {
        var factory = new McpTransportFactory(new FakeMxcProcessLauncher(), Substitute.For<ISandboxGrantVerifier>(),
            NullLoggerFactory.Instance, fileExists: _ => false);

        var ex = Assert.Throws<SandboxStartException>(() => factory.Create(Request(McpSandboxPolicy.Empty)));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StdioOptions_NeverPassTheRuntimeDirectoryToTheChild()
    {
        var options = McpTransportFactory.BuildStdioOptions(Request(null));

        Assert.True(options.EnvironmentVariables!.ContainsKey("MXC_FFI_DIR"));
        Assert.Null(options.EnvironmentVariables["MXC_FFI_DIR"]);
        Assert.Equal("t", options.EnvironmentVariables["TOKEN"]);
    }

    [Fact]
    public void StdioOptions_DropAUserSuppliedRuntimeDirectoryToo()
    {
        var request = Request(null) with { Env = new Dictionary<string, string> { ["mxc_ffi_dir"] = @"C:\evil" } };

        var options = McpTransportFactory.BuildStdioOptions(request);

        Assert.All(options.EnvironmentVariables!.Where(e => e.Key.Equals("MXC_FFI_DIR", StringComparison.OrdinalIgnoreCase)),
            e => Assert.Null(e.Value));
    }

    [Fact]
    public void StdioOptions_KeepTheCommandNameAndArguments()
    {
        var options = McpTransportFactory.BuildStdioOptions(Request(null, command: "node"));

        Assert.Equal("Files", options.Name);
        Assert.Equal("node", options.Command);
        Assert.Equal(["--x"], options.Arguments);
    }
}
