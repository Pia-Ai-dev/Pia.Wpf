using Pia.Services.Plugins.Sandbox.Runtimes;

namespace Pia.Services.Plugins.Sandbox;

/// <param name="ReadonlyPaths">The user's read-only folders; the command directory is not in here.</param>
/// <param name="GrantedCommandDirectory">The program's folder when the sandbox must be given read access to it, else null.</param>
public sealed record SandboxLaunchSpec(
    string ContainerName,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory,
    IReadOnlyList<string> ReadwritePaths,
    IReadOnlyList<string> ReadonlyPaths,
    bool AllowNetwork,
    string? GrantedCommandDirectory,
    IReadOnlyList<string>? ToolchainDirectories = null,
    string? RuntimeStateDirectory = null,
    bool RuntimeStateReadOnly = false,
    string? SupportDirectory = null,
    RunnerKind RunnerKind = RunnerKind.None,
    RuntimePreparation? Preparation = null)
{
    public IReadOnlyList<string> Toolchain => ToolchainDirectories ?? [];
}

/// <summary>Exactly one of <see cref="Spec"/> and <see cref="Error"/> is set.</summary>
public sealed record SandboxLaunchPlan(SandboxLaunchSpec? Spec, string? Error)
{
    public static SandboxLaunchPlan Ok(SandboxLaunchSpec spec) => new(spec, null);

    public static SandboxLaunchPlan Fail(string error) => new(null, error);
}
