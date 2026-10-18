namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>The English start errors for runner commands; they reach the release log, so none names a path.</summary>
public static class SandboxRuntimeMessages
{
    public const string NonGoalLauncher =
        "Pia can't sandbox servers started with {0}. npx, npm exec, uvx and dnx work; otherwise point the command at the server program itself.";

    public const string ScriptFile =
        "Pia can't sandbox this server: its command is a .cmd, .bat or .ps1 script. npx, npm exec, uvx and dnx work; otherwise point the command at the server program itself.";

    public const string NotAProgram =
        "Pia can't sandbox this server: its command is not a program (.exe). npx, npm exec, uvx and dnx work; otherwise point the command at the server program itself.";

    public const string CmdNotSystem = "Pia can't sandbox this server: 'cmd' must be the Windows command processor.";

    public const string CmdWithoutC = "Pia can't sandbox this server: only 'cmd /c' followed by a program can be unwrapped.";

    public const string CmdMetacharacters =
        "Pia can't sandbox this server: its cmd /c line uses cmd features (& | < > ^ % or quotes) a sandbox can't reproduce. Put the program in the command field and its arguments one per line.";

    public const string UnsupportedNpm = "Pia can sandbox npm only as npx or npm exec.";

    public const string UnsupportedUv = "Pia can sandbox uv only as uvx or uv tool run, with a command name it can read.";

    public const string UnsupportedDotnet = "Pia can sandbox dotnet only as dnx, dotnet dnx or dotnet <app>.dll.";

    public const string UnsupportedNode = "Pia can sandbox node only as node <script>.";

    public const string UnsupportedCommand = "Pia can't sandbox this server's command.";

    public const string UvxOption =
        "Pia can't sandbox this uvx call: the option {0} is not supported. Supported are --from, --with and --python.";

    public const string UvxValue =
        "Pia can't sandbox this uvx call: packages must be names from the package index, with an optional version, and --python takes a version number.";

    public const string RelativePath =
        "Pia can't sandbox this server: give the full path of the script or .dll, or set a working directory.";

    public const string NodeNotFound =
        "Node.js was not found. Install it for your user only (the server's settings show how), then start the server again.";

    public const string AdminOwned =
        "The only {0} found is installed for all users, and sandboxed apps can't read its folder. Run Pia as administrator once to allow it, or install it for your user only (the server's settings show how).";

    public const string DotnetSdkMissing =
        "dnx needs a .NET 10 SDK, and none was found. Install one for your user only (the server's settings show how).";

    public const string DotnetRuntimeMissing =
        "The .NET runtime this program needs was not found. Install it for your user only (the server's settings show how).";

    public const string UvNotFound = "uv was not found. Install it for your user only (the server's settings show how).";

    public const string ToolchainTooLarge =
        "The runtime's folder holds too many files to share with a sandbox. Install a separate copy for your user only (the server's settings show how).";

    public const string ToolchainTooWide =
        "The runtime sits directly on a drive, on a network share or in a folder Pia won't share (such as your profile, Desktop, Documents or Downloads). Install it into a folder of its own.";

    public const string ToolchainPathUnsupported = "The runtime's folder path contains a semicolon, which a sandbox can't use.";

    public const string ProgramFolderTooWide =
        "Put the program in its own folder: Pia would have to share all of the folder around it.";

    public const string ProgramFolderTooLarge =
        "The program's folder holds too many files to share with a sandboxed server. Put the program in a smaller folder.";

    public const string ScriptMissing = "The script was not found.";

    public const string DllMissing = "The .dll was not found.";

    public const string UvInWritableFolder =
        "The only uv found sits in a folder a sandboxed server can change, and Pia runs uv outside the sandbox. Install uv into a folder of its own, or stop sharing that folder read-write.";

    public const string DetectionUnavailable = "Pia can't sandbox this server: runtime detection is not available.";

    public const string CheckFailed = "Pia could not check this server's runtime.";

    public const string UserEnvironmentUnreadable =
        "Pia could not read your user environment, so it cannot keep its variables away from the server. The server was not started.";

    public static string For(RunnerParseResult parse) => parse.Refusal switch
    {
        RunnerRefusal.NonGoalLauncher => string.Format(NonGoalLauncher, Subject(parse.Subject)),
        RunnerRefusal.ScriptFile => ScriptFile,
        RunnerRefusal.CmdNotSystem => CmdNotSystem,
        RunnerRefusal.CmdWithoutC => CmdWithoutC,
        RunnerRefusal.CmdMetacharacters => CmdMetacharacters,
        RunnerRefusal.UnsupportedForm => parse.Subject switch
        {
            "npm" => UnsupportedNpm,
            "uv" => UnsupportedUv,
            "dotnet" => UnsupportedDotnet,
            "node" => UnsupportedNode,
            _ => UnsupportedCommand,
        },
        RunnerRefusal.UvxOption => string.Format(UvxOption, Subject(parse.Subject)),
        RunnerRefusal.UvxValue => UvxValue,
        RunnerRefusal.RelativePath => RelativePath,
        _ => UnsupportedCommand,
    };

    public static string For(RuntimeResolution resolution) => resolution.Issue switch
    {
        RuntimeIssue.NodeNotFound => NodeNotFound,
        RuntimeIssue.NodeAdminOwned => string.Format(AdminOwned, "Node.js"),
        RuntimeIssue.DotnetAdminOwned => string.Format(AdminOwned, ".NET"),
        RuntimeIssue.DotnetSdkMissing => DotnetSdkMissing,
        RuntimeIssue.DotnetRuntimeMissing => DotnetRuntimeMissing,
        RuntimeIssue.UvNotFound => UvNotFound,
        RuntimeIssue.ToolchainTooLarge => ToolchainTooLarge,
        RuntimeIssue.ToolchainTooWide => ToolchainTooWide,
        RuntimeIssue.ToolchainPathUnsupported => ToolchainPathUnsupported,
        RuntimeIssue.ProgramFolderTooWide => ProgramFolderTooWide,
        RuntimeIssue.ProgramFolderTooLarge => ProgramFolderTooLarge,
        RuntimeIssue.ScriptMissing => ScriptMissing,
        RuntimeIssue.DllMissing => DllMissing,
        RuntimeIssue.UvInWritableFolder => UvInWritableFolder,
        _ => CheckFailed,
    };

    // A subject is a launcher or option name; anything after a character a name never holds is cut, so no path slips in.
    private static string Subject(string? subject)
    {
        var value = subject ?? string.Empty;
        var end = 0;
        while (end < value.Length && (char.IsAsciiLetterOrDigit(value[end]) || value[end] is '-' or '_' or '.'))
            end++;
        return end == 0 ? "?" : value[..end];
    }
}
