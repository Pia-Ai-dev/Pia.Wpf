namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>What the server editor asks about runtimes; detection runs off the UI thread.</summary>
public interface ISandboxRuntimeService
{
    Task<RuntimeDetection> DetectAsync(string command, IReadOnlyList<string> args, string? workingDirectory,
        string toolPrefix, CancellationToken cancellationToken);
    bool CanGrantMachineWide { get; }
    MachineWideGrantPreview PreviewMachineWideGrant(string root);
    Task<MachineWideGrantResult> GrantMachineWideReadAsync(string root, CancellationToken cancellationToken);
    RuntimeGuide GuideFor(RuntimeFamily family);
    RuntimeGuide GuideFor(RuntimeResolution resolution);
}
