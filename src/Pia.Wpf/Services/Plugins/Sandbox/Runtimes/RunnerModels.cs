namespace Pia.Services.Plugins.Sandbox.Runtimes;

public enum RunnerKind { None, Npx, NpmExec, NodeScript, Dnx, DotnetDll, Uvx }

public enum RunnerRefusal
{
    None, NonGoalLauncher, ScriptFile, CmdNotSystem, CmdWithoutC, CmdMetacharacters,
    UnsupportedForm, UvxOption, UvxValue, RelativePath
}

/// <param name="Package">What uv installs: the --from value, else the first token.</param>
/// <param name="CommandName">The console script to run.</param>
public sealed record UvxSpec(string Package, string CommandName, IReadOnlyList<string> With, string? Python);

/// <param name="LauncherPathHint">The full path the user named, tried before the usual order; null for a bare name.</param>
/// <param name="Arguments">What follows the launcher: npx args, script args, dnx args, server args.</param>
/// <param name="RuntimeOptions">node flags before the script.</param>
/// <param name="WritableFolders">Folders a sandboxed server can write; uv found under one is refused.</param>
public sealed record RunnerRequest(
    RunnerKind Kind,
    string? LauncherPathHint,
    IReadOnlyList<string> Arguments,
    string? ScriptOrDll,
    IReadOnlyList<string> RuntimeOptions,
    UvxSpec? Uvx,
    IReadOnlyList<string>? WritableFolders = null);

/// <param name="Subject">A launcher or option name, never a value.</param>
/// <param name="EffectiveCommand">The program after cmd /c unwrapping; for Kind None this is what Build launches.</param>
public sealed record RunnerParseResult(
    RunnerKind Kind,
    RunnerRequest? Runner,
    RunnerRefusal Refusal,
    string? Subject,
    string EffectiveCommand,
    IReadOnlyList<string> EffectiveArgs);
