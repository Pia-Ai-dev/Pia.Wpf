namespace Pia.Services.Plugins.Sandbox.Runtimes;

public enum RuntimeFamily { Node, Dotnet, Uv }

public enum RuntimeOutcome { Ready, NeedsAdminFix, NotFound, Refused }

public enum RuntimeIssue
{
    None, NodeNotFound, NodeAdminOwned, DotnetSdkMissing, DotnetRuntimeMissing, DotnetAdminOwned,
    UvNotFound, ToolchainTooLarge, ToolchainTooWide, ToolchainPathUnsupported,
    ProgramFolderTooLarge, ProgramFolderTooWide, ScriptMissing, DllMissing
}

/// <param name="Root">Final path (links resolved) of the folder holding node.exe, dotnet.exe or uv.exe.</param>
/// <param name="EntryScript">npx-cli.js, npm-cli.js or sdk\&lt;v&gt;\dotnet.dll; null when the kind needs none.</param>
public sealed record RuntimeInstall(
    RuntimeFamily Family,
    string Root,
    string Executable,
    string? Version,
    string? NpmVersion,
    string? EntryScript,
    bool ReadableAlready,
    bool AdminOwned);

/// <param name="Install">The chosen install when Ready, the first admin-owned one when NeedsAdminFix, the refused one when Refused.</param>
/// <param name="ToolchainRoots">Read-only grants per start; empty when readable already and for uv.</param>
/// <param name="ProgramFolder">The script's package root or the dll's folder; null for the package runners.</param>
public sealed record RuntimeResolution(
    RunnerKind Kind,
    RuntimeOutcome Outcome,
    RuntimeIssue Issue,
    RuntimeInstall? Install,
    IReadOnlyList<string> ToolchainRoots,
    string? ProgramFolder);

public sealed record PreparedFile(string Path, string Content);

/// <param name="StateDirectory">Holds tools, tool-bin, the caches and the spec record.</param>
public sealed record UvPrepareRequest(
    string ContainerName,
    string UvExecutable,
    UvxSpec Spec,
    IReadOnlyDictionary<string, string> Environment,
    bool Offline,
    string StateDirectory,
    IReadOnlyList<string> ServerArgs,
    IReadOnlyList<string> CacheDirectoriesToPrune);

public sealed record RuntimePreparation(
    IReadOnlyList<string> Directories,
    IReadOnlyList<PreparedFile> Files,
    UvPrepareRequest? Uv);

public sealed record RuntimePlanInput(
    RunnerRequest Runner,
    RuntimeResolution Resolution,
    string ContainerName,
    string StateDirectory,
    string SupportDirectory,
    bool AllowNetwork,
    IReadOnlyList<string> SandboxRoots,
    string RegistryPath,
    IReadOnlyDictionary<string, string>? UserEnvironment);

public sealed record RuntimeLaunchPlan(
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> ToolchainRoots,
    string StateDirectory,
    bool StateReadOnly,
    string? SupportDirectory,
    RuntimePreparation Preparation);

public sealed record RuntimeDetection(RunnerParseResult Parse, RuntimeResolution? Resolution, string? StateDirectory, bool StateReadOnly);

public sealed record MachineWideGrantPreview(string Root, string GrantCommand, string RemoveCommand, bool HasExistingAce);

public sealed record MachineWideGrantResult(bool Ok, string? Error);

public sealed record RuntimeGuide(RuntimeFamily Family, string Command, string? AlternativeCommand);
