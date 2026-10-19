namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>What the server editor asks about runtimes; detection runs off the UI thread.</summary>
public interface ISandboxRuntimeService
{
    /// <param name="sandboxedReadwriteFolders">Every saved sandboxed server's read-write folders; a uv under one is refused.</param>
    Task<RuntimeDetection> DetectAsync(string command, IReadOnlyList<string> args, string? workingDirectory,
        string toolPrefix, IReadOnlyList<string> sandboxedReadwriteFolders, CancellationToken cancellationToken);
    bool CanGrantMachineWide { get; }
    MachineWideGrantPreview PreviewMachineWideGrant(string root);
    Task<MachineWideGrantResult> GrantMachineWideReadAsync(string root, CancellationToken cancellationToken);
    RuntimeGuide GuideFor(RuntimeFamily family);
    RuntimeGuide GuideFor(RuntimeResolution resolution);
}
