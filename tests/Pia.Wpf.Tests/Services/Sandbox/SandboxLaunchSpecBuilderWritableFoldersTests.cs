using System.IO;
using Pia.Paths;
using Pia.Services.Exceptions;
using Pia.Services.Plugins;
using Pia.Services.Plugins.Sandbox;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>Pia runs uv outside the sandbox, so the detector has to know every folder the container may write.</summary>
public sealed class SandboxLaunchSpecBuilderWritableFoldersTests
{
    private const string UvFolder = @"C:\Users\u\.local\bin";

    private static readonly FakeRuntimeHost Host = new();

    private static McpTransportRequest Uvx(McpSandboxPolicy policy) =>
        new("Time", "uvx", ["mcp-server-time"], null, null, policy, "time", false);

    private static SandboxLaunchPlan Build(McpTransportRequest request, IRuntimeDetector detector) =>
        SandboxLaunchSpecBuilder.Build(request, new SandboxRuntimeContext(detector, Host, CanProvision: true, []), fileExists: _ => true);

    private sealed class RecordingDetector(RuntimeResolution resolution) : IRuntimeDetector
    {
        public List<RunnerRequest> Calls { get; } = [];

        public RuntimeResolution Detect(RunnerRequest runner, CancellationToken cancellationToken)
        {
            Calls.Add(runner);
            return resolution;
        }
    }

    private static RuntimeResolution Refused() =>
        new(RunnerKind.Uvx, RuntimeOutcome.Refused, RuntimeIssue.UvInWritableFolder,
            new RuntimeInstall(RuntimeFamily.Uv, UvFolder, Path.Combine(UvFolder, "uv.exe"), "0.13.1", null, null, false, false),
            [], null);

    [Fact]
    public void TheDetector_HearsThePolicysReadwriteFolders_AndTheSandboxRoot()
    {
        var sandboxRoot = PiaPaths.McpSandboxRoot;
        var detector = new RecordingDetector(Refused());

        Build(Uvx(new McpSandboxPolicy([@"C:\Work\box\", @"D:\data"], [@"C:\Reference"], true)), detector);

        var runner = Assert.Single(detector.Calls);
        Assert.Equal([@"C:\Work\box", @"D:\data", sandboxRoot], runner.WritableFolders!);
        Assert.Equal(RunnerKind.Uvx, runner.Kind);
    }

    // Another server can plant the uv.exe this one names by a full path in a folder it may write.
    [Fact]
    public void TheDetector_AlsoHearsEverySavedSandboxedServersReadwriteFolders()
    {
        var detector = new RecordingDetector(Refused());
        var request = new McpTransportRequest("Time", @"D:\tools\uv\uvx.exe", ["mcp-server-time"], null, null,
            new McpSandboxPolicy([@"C:\Work\box"], [], true), "time", false, SandboxedReadwriteFolders: [@"D:\tools", @"E:\shared"]);

        var plan = Build(request, detector);

        var runner = Assert.Single(detector.Calls);
        Assert.Equal([@"C:\Work\box", @"D:\tools", @"E:\shared", PiaPaths.McpSandboxRoot], runner.WritableFolders!);
        Assert.Equal(@"D:\tools\uv\uvx.exe", runner.LauncherPathHint);
        Assert.Equal(SandboxRuntimeMessages.UvInWritableFolder, plan.Error);
    }

    [Fact]
    public void AUvInAWritableFolder_FailsTheStart_WithAPathFreeError()
    {
        var plan = Build(Uvx(new McpSandboxPolicy([UvFolder], [], true)), new RecordingDetector(Refused()));

        Assert.Null(plan.Spec);
        Assert.Equal(SandboxRuntimeMessages.UvInWritableFolder, plan.Error);
        PathFreeAssert.IsPathFree(new SandboxStartException(plan.Error!));
    }
}
