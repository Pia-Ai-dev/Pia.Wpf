using System.IO;
using Pia.Services.Plugins;
using Pia.Services.Plugins.Sandbox;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class SandboxLaunchSpecBuilderTests
{
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    private static McpTransportRequest Request(
        string command,
        McpSandboxPolicy? policy = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? prefix = "fs",
        bool probe = false,
        IReadOnlyList<string>? args = null) =>
        new("Files", command, args ?? [], env, workingDirectory, policy ?? new McpSandboxPolicy([], [], false), prefix, probe);

    private static SandboxLaunchPlan Build(McpTransportRequest request, string? path = null) =>
        SandboxLaunchSpecBuilder.Build(request, path, fileExists: _ => true);

    [Fact]
    public void ExeOutsideTheSystemFolders_GrantsItsDirectoryReadOnly()
    {
        var plan = Build(Request(@"C:\Tools\fs\fs.exe"));

        Assert.Null(plan.Error);
        Assert.Equal(@"C:\Tools\fs", plan.Spec!.GrantedCommandDirectory);
        Assert.Equal(@"C:\Tools\fs\fs.exe", plan.Spec.Command);
    }

    [Fact]
    public void ExeUnderWindows_NeedsNoGrant()
    {
        var plan = Build(Request(Path.Combine(Windows, "System32", "cmd.exe")));

        Assert.Null(plan.Spec!.GrantedCommandDirectory);
    }

    [Fact]
    public void ExeUnderProgramFiles_NeedsNoGrant()
    {
        var plan = Build(Request(Path.Combine(ProgramFiles, "dotnet", "dotnet.exe")));

        Assert.Null(plan.Spec!.GrantedCommandDirectory);
    }

    [Fact]
    public void ExeDirectoryInsideAReadwritePath_IsNotGrantedAgain()
    {
        var plan = Build(Request(@"C:\Tools\fs\bin\fs.exe", new McpSandboxPolicy([@"C:\Tools\fs"], [], false)));

        Assert.Null(plan.Spec!.GrantedCommandDirectory);
    }

    [Fact]
    public void ExeDirectoryEqualToAReadonlyPath_IsNotGrantedAgain()
    {
        var plan = Build(Request(@"C:\Tools\fs\fs.exe", new McpSandboxPolicy([], [@"c:\tools\fs\"], false)));

        Assert.Null(plan.Spec!.GrantedCommandDirectory);
    }

    [Fact]
    public void BareName_ResolvesThroughPath()
    {
        var plan = SandboxLaunchSpecBuilder.Build(Request("fs"), @"C:\nowhere;C:\Tools\fs",
            fileExists: p => string.Equals(p, @"C:\Tools\fs\fs.exe", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(@"C:\Tools\fs\fs.exe", plan.Spec!.Command);
        Assert.Equal(@"C:\Tools\fs", plan.Spec.GrantedCommandDirectory);
    }

    [Fact]
    public void BareName_NotOnPath_IsNotFound()
    {
        var plan = SandboxLaunchSpecBuilder.Build(Request("fs"), @"C:\nowhere", fileExists: _ => false);

        Assert.Null(plan.Spec);
        Assert.Contains("not found", plan.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RootedPath_ThatDoesNotExist_IsNotFound()
    {
        var plan = SandboxLaunchSpecBuilder.Build(Request(@"C:\Tools\fs\fs.exe"), null, fileExists: _ => false);

        Assert.Null(plan.Spec);
        Assert.Contains("not found", plan.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"C:\Tools\run.cmd")]
    [InlineData(@"C:\Tools\run.bat")]
    [InlineData(@"C:\Tools\run.ps1")]
    public void ScriptLauncherPath_IsRefused(string command)
    {
        var plan = Build(Request(command));

        Assert.Null(plan.Spec);
        Assert.Contains("script launcher", plan.Error);
    }

    [Fact]
    public void Npx_ResolvingToACmdShim_IsRefused()
    {
        var plan = SandboxLaunchSpecBuilder.Build(Request("npx"), @"C:\Program Files\nodejs",
            fileExists: p => p.EndsWith("npx.cmd", StringComparison.OrdinalIgnoreCase));

        Assert.Null(plan.Spec);
        Assert.Contains("script launcher", plan.Error);
    }

    [Fact]
    public void ContainerName_IsStablePerServer_AndProbesGetTheirOwn()
    {
        Assert.Equal("pia-mcp-fs", Build(Request(@"C:\Tools\fs.exe")).Spec!.ContainerName);
        Assert.Equal("pia-mcp-fs-probe", Build(Request(@"C:\Tools\fs.exe", probe: true)).Spec!.ContainerName);
    }

    [Fact]
    public void ContainerName_WithoutAPrefix_IsDerivedFromTheName()
    {
        Assert.Equal("pia-mcp-files", Build(Request(@"C:\Tools\fs.exe", prefix: null)).Spec!.ContainerName);
    }

    [Fact]
    public void WorkingDirectory_InsideAGrantedPath_IsKept()
    {
        var plan = Build(Request(@"C:\Tools\fs.exe", new McpSandboxPolicy([@"C:\box\rw"], [], false), @"C:\box\rw\sub"));

        Assert.Equal(@"C:\box\rw\sub", plan.Spec!.WorkingDirectory);
    }

    [Fact]
    public void WorkingDirectory_OutsideEveryGrant_FallsBackToTheFirstReadwritePath()
    {
        var plan = Build(Request(@"C:\Tools\fs.exe", new McpSandboxPolicy([@"C:\box\rw"], [@"C:\box\ro"], false), @"C:\elsewhere"));

        Assert.Equal(@"C:\box\rw", plan.Spec!.WorkingDirectory);
    }

    [Fact]
    public void WorkingDirectory_WithoutAnyReadwritePath_FallsBackToTheFirstReadonlyPath()
    {
        var plan = Build(Request(@"C:\Tools\fs.exe", new McpSandboxPolicy([], [@"C:\box\ro"], false)));

        Assert.Equal(@"C:\box\ro", plan.Spec!.WorkingDirectory);
    }

    [Fact]
    public void WorkingDirectory_WithNoGrantsAtAll_IsTheSystemDirectory()
    {
        var plan = Build(Request(Path.Combine(Windows, "System32", "cmd.exe")));

        Assert.Equal(Environment.SystemDirectory, plan.Spec!.WorkingDirectory);
    }

    [Fact]
    public void Environment_IsCopied_ButNeverCarriesTheRuntimeDirectory()
    {
        var env = new Dictionary<string, string> { ["TOKEN"] = "t", ["mxc_ffi_dir"] = @"C:\evil" };

        var spec = Build(Request(@"C:\Tools\fs.exe", env: env)).Spec!;

        Assert.Equal("t", spec.Environment["TOKEN"]);
        Assert.DoesNotContain(spec.Environment.Keys, k => string.Equals(k, "MXC_FFI_DIR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PolicyPaths_AreNormalisedAndDeduplicated()
    {
        var policy = new McpSandboxPolicy([@"C:\box\rw\", @"c:\BOX\rw", @"C:\box\.\other"], [@"C:\box\ro"], AllowNetwork: true);

        var spec = Build(Request(@"C:\Tools\fs.exe", policy)).Spec!;

        Assert.Equal([@"C:\box\rw", @"C:\box\other"], spec.ReadwritePaths);
        Assert.Equal([@"C:\box\ro"], spec.ReadonlyPaths);
        Assert.True(spec.AllowNetwork);
    }

    [Fact]
    public void Arguments_AreKeptVerbatim()
    {
        var spec = Build(Request(@"C:\Tools\fs.exe", args: ["--root", @"C:\a b"])).Spec!;

        Assert.Equal(["--root", @"C:\a b"], spec.Args);
    }

    [Fact]
    public void ARequestWithoutAPolicy_IsRefused()
    {
        var request = Request(@"C:\Tools\fs.exe") with { Sandbox = null };

        Assert.Throws<ArgumentException>(() => SandboxLaunchSpecBuilder.Build(request, null, fileExists: _ => true));
    }
}
