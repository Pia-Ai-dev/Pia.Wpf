using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Paths;
using Pia.Services.Interfaces;

namespace Pia.Services.Plugins.Sandbox.Runtimes;

public sealed class SandboxRuntimeService(
    IRuntimeDetector detector,
    IToolchainAclService acl,
    IElevationService elevation,
    IRuntimeHostService host,
    IRuntimeRootVerifier roots,
    ILogger<SandboxRuntimeService> logger) : ISandboxRuntimeService
{
    // What LocalMcpConfig.DeriveToolPrefix yields for an empty name.
    private const string BlankNamePrefix = "mcp";

    private const string AllAppPackages = "*S-1-15-2-1";

    public bool CanGrantMachineWide => elevation.IsElevated;

    public async Task<RuntimeDetection> DetectAsync(string command, IReadOnlyList<string> args, string? workingDirectory,
        string toolPrefix, IReadOnlyList<string> sandboxedReadwriteFolders, CancellationToken cancellationToken)
    {
        var parse = RunnerCommandParser.Parse(command, args, workingDirectory);
        if (parse.Refusal != RunnerRefusal.None || parse.Kind == RunnerKind.None || parse.Runner is not { } runner)
            return new RuntimeDetection(parse, null, null, false);

        // The edited policy's folders are judged at start; the saved servers' folders and the sandbox root count already.
        var request = runner with { WritableFolders = [.. sandboxedReadwriteFolders, PiaPaths.McpSandboxRoot] };
        var resolution = await Task.Run(() => detector.Detect(request, cancellationToken), cancellationToken);
        var prefix = string.IsNullOrWhiteSpace(toolPrefix) ? BlankNamePrefix : toolPrefix;
        return new RuntimeDetection(parse, resolution, PiaPaths.McpRuntimeStateDirectory(prefix), parse.Kind == RunnerKind.Uvx);
    }

    public MachineWideGrantPreview PreviewMachineWideGrant(string root) => new(
        root,
        $"icacls \"{root}\" /grant {AllAppPackages}:(OI)(CI)(RX)",
        $"icacls \"{root}\" /remove:g {AllAppPackages}",
        acl.HasAllAppPackagesAce(root));

    public async Task<MachineWideGrantResult> GrantMachineWideReadAsync(string root, CancellationToken cancellationToken)
    {
        if (!elevation.IsElevated)
            return new MachineWideGrantResult(MachineWideGrantFailure.NotElevated);

        var folder = SandboxLaunchSpecBuilder.Normalize(root);
        if (SandboxLaunchSpecBuilder.IsVolumeRoot(folder))
            return new MachineWideGrantResult(MachineWideGrantFailure.TooWide);
        if (SandboxLaunchSpecBuilder.IsSameOrUnder(folder, SandboxLaunchSpecBuilder.Normalize(host.WindowsDirectory)))
            return new MachineWideGrantResult(MachineWideGrantFailure.WindowsFolder);

        try
        {
            return await Task.Run(() =>
            {
                // The entry is inheritable and has no undo in Pia, so a wide folder would open far more than one runtime.
                if (roots.Check(folder, false) == RuntimeIssue.ToolchainTooWide)
                    return new MachineWideGrantResult(MachineWideGrantFailure.TooWide);

                // A new grant would merge into the existing entry, and a later removal would take the old access away.
                if (acl.HasAllAppPackagesAce(root))
                    return new MachineWideGrantResult(MachineWideGrantFailure.ExistingAce);

                acl.GrantAllAppPackagesRead(root);
                return new MachineWideGrantResult(MachineWideGrantFailure.None);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Granting all-app-packages read on a runtime folder failed: {ExceptionType}", ex.GetType().Name);
            logger.SensitiveDebug("Granting all-app-packages read failed: {Message}", ex.Message);
            return new MachineWideGrantResult(MachineWideGrantFailure.Unexpected);
        }
    }

    public RuntimeGuide GuideFor(RuntimeFamily family) => RuntimeInstallGuides.GuideFor(family);

    public RuntimeGuide GuideFor(RuntimeResolution resolution) => RuntimeInstallGuides.GuideFor(resolution);
}
