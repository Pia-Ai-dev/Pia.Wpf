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
    ILogger<SandboxRuntimeService> logger) : ISandboxRuntimeService
{
    // What LocalMcpConfig.DeriveToolPrefix yields for an empty name.
    private const string BlankNamePrefix = "mcp";

    private const string AllAppPackages = "*S-1-15-2-1";

    public bool CanGrantMachineWide => elevation.IsElevated;

    public async Task<RuntimeDetection> DetectAsync(string command, IReadOnlyList<string> args, string? workingDirectory,
        string toolPrefix, CancellationToken cancellationToken)
    {
        var parse = RunnerCommandParser.Parse(command, args, workingDirectory);
        if (parse.Refusal != RunnerRefusal.None || parse.Kind == RunnerKind.None || parse.Runner is not { } runner)
            return new RuntimeDetection(parse, null, null, false);

        var resolution = await Task.Run(() => detector.Detect(runner, cancellationToken), cancellationToken);
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
            return new MachineWideGrantResult(false, "Pia is not running as administrator.");
        if (IsUnderWindowsDirectory(root))
            return new MachineWideGrantResult(false, "Pia does not change permissions inside the Windows folder.");

        try
        {
            return await Task.Run(() =>
            {
                // A new grant would merge into the existing entry, and a later removal would take the old access away.
                if (acl.HasAllAppPackagesAce(root))
                    return new MachineWideGrantResult(false,
                        "The folder already carries a permission for all app packages; removing a new grant would also remove it.");

                acl.GrantAllAppPackagesRead(root);
                return new MachineWideGrantResult(true, null);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Granting all-app-packages read on a runtime folder failed: {ExceptionType}", ex.GetType().Name);
            logger.SensitiveDebug("Granting all-app-packages read failed: {Message}", ex.Message);
            return new MachineWideGrantResult(false, ex.Message);
        }
    }

    public RuntimeGuide GuideFor(RuntimeFamily family) => RuntimeInstallGuides.GuideFor(family);

    private bool IsUnderWindowsDirectory(string root) =>
        SandboxLaunchSpecBuilder.IsSameOrUnder(
            SandboxLaunchSpecBuilder.Normalize(root), SandboxLaunchSpecBuilder.Normalize(host.WindowsDirectory));
}
