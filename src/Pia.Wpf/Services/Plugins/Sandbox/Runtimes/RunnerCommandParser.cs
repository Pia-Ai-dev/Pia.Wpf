using System.IO;
using System.Text.RegularExpressions;

namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>Pure apart from the fileExists seam: the editor runs it on every keystroke.</summary>
public static partial class RunnerCommandParser
{
    private static readonly HashSet<string> NonGoalLaunchers = new(StringComparer.Ordinal)
    {
        "pnpm", "pnpx", "yarn", "bunx", "bun", "deno", "pipx", "docker"
    };

    private static readonly HashSet<string> RunnerLaunchers = new(StringComparer.Ordinal)
    {
        "npx", "npm", "uvx", "uv", "dnx", "dotnet", "node"
    };

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cmd", ".bat", ".ps1"
    };

    private static readonly HashSet<string> CmdSwitches = new(StringComparer.OrdinalIgnoreCase) { "/d", "/s", "/q" };

    private static readonly HashSet<string> NodeOptionsWithValue = new(StringComparer.Ordinal)
    {
        "-r", "--require", "--import", "--loader", "--experimental-loader"
    };

    private static readonly HashSet<string> RefusedNodeOptions = new(StringComparer.Ordinal)
    {
        "-e", "-p", "--eval", "--print", "-i", "--interactive", "--test", "--run", "-"
    };

    private static readonly char[] CmdMetacharacters = ['&', '|', '<', '>', '^', '%'];

    private static readonly char[] CmdMetacharactersOrQuote = ['&', '|', '<', '>', '^', '%', '"'];

    private static readonly char[] VersionStart = ['@', '[', '=', '<', '>', '~', '!'];

    private static readonly string[] ArchiveExtensions =
    [
        ".whl", ".zip", ".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tbz", ".tar.xz", ".txz", ".tar.zst", ".tar.lz", ".tlz"
    ];

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?(\[[A-Za-z0-9._,-]+\])?(@[A-Za-z0-9.*+!_-]+|(===|==|>=|<=|~=|!=|<|>)[A-Za-z0-9.*+!_-]+(,(===|==|>=|<=|~=|!=|<|>)[A-Za-z0-9.*+!_-]+)*)?$")]
    private static partial Regex PackagePattern();

    [GeneratedRegex(@"^(cpython-?|python)?[0-9]+(\.[0-9]+){0,2}$")]
    private static partial Regex PythonPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex CommandNamePattern();

    public static RunnerParseResult Parse(
        string command,
        IReadOnlyList<string> args,
        string? workingDirectory,
        string? systemDirectory = null,
        Func<string, bool>? fileExists = null)
    {
        var effectiveCommand = Unquote(command.Trim()).Trim();
        IReadOnlyList<string> effectiveArgs = [.. args];

        if (IsCmd(effectiveCommand))
        {
            var (refusal, inner, innerArgs) = UnwrapCmd(
                effectiveCommand, effectiveArgs, systemDirectory ?? Environment.SystemDirectory, fileExists ?? File.Exists);
            if (refusal != RunnerRefusal.None)
                return Refused(refusal, null, effectiveCommand, effectiveArgs);

            effectiveCommand = inner;
            effectiveArgs = innerArgs;
            if (IsCmd(effectiveCommand))
                return Refused(RunnerRefusal.CmdWithoutC, null, effectiveCommand, effectiveArgs);
        }

        return Classify(effectiveCommand, effectiveArgs, workingDirectory);
    }

    private static (RunnerRefusal Refusal, string Command, IReadOnlyList<string> Args) UnwrapCmd(
        string command, IReadOnlyList<string> args, string systemDirectory, Func<string, bool> fileExists)
    {
        if (Path.IsPathRooted(command) && !IsSystemCmd(command, systemDirectory))
            return (RunnerRefusal.CmdNotSystem, command, args);

        var index = 0;
        while (index < args.Count && CmdSwitches.Contains(args[index].Trim()))
            index++;

        if (index >= args.Count || !string.Equals(args[index].Trim(), "/c", StringComparison.OrdinalIgnoreCase))
            return (RunnerRefusal.CmdWithoutC, command, args);

        var inner = args.Skip(index + 1).ToList();
        IReadOnlyList<string> items;
        if (inner.Count == 1)
        {
            var line = inner[0].Trim();
            if (Path.IsPathFullyQualified(line) && fileExists(line))
                items = [line];
            else if (line.IndexOfAny(CmdMetacharacters) >= 0)
                return (RunnerRefusal.CmdMetacharacters, command, args);
            else
                items = CommandLine.Split(line);
        }
        else
        {
            if (inner.Any(item => item.IndexOfAny(CmdMetacharactersOrQuote) >= 0))
                return (RunnerRefusal.CmdMetacharacters, command, args);
            items = inner;
        }

        if (items.Count == 0 || string.IsNullOrWhiteSpace(items[0]))
            return (RunnerRefusal.CmdWithoutC, command, args);

        return (RunnerRefusal.None, items[0].Trim(), [.. items.Skip(1)]);
    }

    private static RunnerParseResult Classify(string command, IReadOnlyList<string> args, string? workingDirectory)
    {
        var fileName = Path.GetFileName(command);
        var extension = Path.GetExtension(fileName);
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();

        var isLauncher = RunnerLaunchers.Contains(name) || NonGoalLaunchers.Contains(name);
        if (isLauncher && !IsLauncherExtension(extension))
            return Refused(RunnerRefusal.ScriptFile, null, command, args);

        switch (name)
        {
            case "npx":
                return Runner(RunnerKind.Npx, command, args, args);

            case "npm":
                return args.Count > 0 && args[0] is "exec" or "x"
                    ? Runner(RunnerKind.NpmExec, command, args, [.. args.Skip(1)])
                    : Refused(RunnerRefusal.UnsupportedForm, "npm", command, args);

            case "uvx":
                return ParseUvx(command, args, 0);

            case "uv":
                return args.Count > 1 && args[0] == "tool" && args[1] == "run"
                    ? ParseUvx(command, args, 2)
                    : Refused(RunnerRefusal.UnsupportedForm, "uv", command, args);

            case "dnx":
                return Runner(RunnerKind.Dnx, command, args, args);

            case "dotnet":
                return ParseDotnet(command, args, workingDirectory);

            case "node":
                return ParseNode(command, args, workingDirectory);
        }

        if (NonGoalLaunchers.Contains(name))
            return Refused(RunnerRefusal.NonGoalLauncher, name, command, args);

        if (ScriptExtensions.Contains(extension))
            return Refused(RunnerRefusal.ScriptFile, null, command, args);

        return new RunnerParseResult(RunnerKind.None, null, RunnerRefusal.None, null, command, args);
    }

    private static RunnerParseResult ParseUvx(string command, IReadOnlyList<string> args, int start)
    {
        string? from = null;
        string? python = null;
        var with = new List<string>();

        var index = start;
        for (; index < args.Count && args[index].StartsWith('-'); index++)
        {
            var arg = args[index];
            var equals = arg.IndexOf('=');
            var option = equals < 0 ? arg : arg[..equals];
            var inlineValue = equals < 0 ? null : arg[(equals + 1)..];

            if (inlineValue is null && option is "-q" or "--quiet" or "--isolated")
                continue;

            if (option is not ("--from" or "--with" or "--python" or "-p"))
                return Refused(RunnerRefusal.UvxOption, OptionName(arg), command, args);

            var value = inlineValue ?? (index + 1 < args.Count ? args[++index] : null);
            var isPython = option is "--python" or "-p";
            if (value is null || !IsFullMatch(isPython ? PythonPattern() : PackagePattern(), value) || (!isPython && LooksLikeArchive(value)))
                return Refused(RunnerRefusal.UvxValue, null, command, args);

            if (isPython)
                python = value;
            else if (option == "--from")
                from = value;
            else
                with.Add(value);
        }

        if (index >= args.Count)
            return Refused(RunnerRefusal.UnsupportedForm, "uv", command, args);

        var token = args[index];
        if ((from is null && !IsFullMatch(PackagePattern(), token)) || LooksLikeArchive(token))
            return Refused(RunnerRefusal.UvxValue, null, command, args);

        var versionStart = token.IndexOfAny(VersionStart);
        var commandName = versionStart < 0 ? token : token[..versionStart];
        if (!IsFullMatch(CommandNamePattern(), commandName))
            return Refused(RunnerRefusal.UnsupportedForm, "uv", command, args);

        var spec = new UvxSpec(from ?? token, commandName, with, python);
        return Runner(RunnerKind.Uvx, command, args, [.. args.Skip(index + 1)], uvx: spec);
    }

    private static RunnerParseResult ParseDotnet(string command, IReadOnlyList<string> args, string? workingDirectory)
    {
        if (args.Count > 0 && args[0] == "dnx")
            return Runner(RunnerKind.Dnx, command, args, [.. args.Skip(1)]);

        var dllIndex = args.Count > 0 && IsDll(args[0]) ? 0
            : args.Count > 1 && args[0] == "exec" && IsDll(args[1]) ? 1
            : -1;
        if (dllIndex < 0)
            return Refused(RunnerRefusal.UnsupportedForm, "dotnet", command, args);

        var dll = ResolveAgainst(args[dllIndex], workingDirectory);
        if (dll is null)
            return Refused(RunnerRefusal.RelativePath, null, command, args);

        return Runner(RunnerKind.DotnetDll, command, args, [.. args.Skip(dllIndex + 1)], scriptOrDll: dll);
    }

    private static RunnerParseResult ParseNode(string command, IReadOnlyList<string> args, string? workingDirectory)
    {
        var options = new List<string>();
        var index = 0;
        for (; index < args.Count && args[index].StartsWith('-'); index++)
        {
            var arg = args[index];
            var isLong = arg.StartsWith("--", StringComparison.Ordinal);
            var optionName = isLong && arg.IndexOf('=') is var equals and > 0 ? arg[..equals] : arg;

            if (RefusedNodeOptions.Contains(optionName))
                return Refused(RunnerRefusal.UnsupportedForm, "node", command, args);

            options.Add(arg);
            if (NodeOptionsWithValue.Contains(arg))
            {
                if (++index >= args.Count)
                    return Refused(RunnerRefusal.UnsupportedForm, "node", command, args);
                options.Add(args[index]);
            }
            else if (!isLong)
            {
                return Refused(RunnerRefusal.UnsupportedForm, "node", command, args);
            }
        }

        if (index >= args.Count)
            return Refused(RunnerRefusal.UnsupportedForm, "node", command, args);

        var script = ResolveAgainst(args[index], workingDirectory);
        if (script is null)
            return Refused(RunnerRefusal.RelativePath, null, command, args);

        return Runner(RunnerKind.NodeScript, command, args, [.. args.Skip(index + 1)], scriptOrDll: script, runtimeOptions: options);
    }

    private static RunnerParseResult Runner(
        RunnerKind kind,
        string command,
        IReadOnlyList<string> args,
        IReadOnlyList<string> arguments,
        string? scriptOrDll = null,
        IReadOnlyList<string>? runtimeOptions = null,
        UvxSpec? uvx = null)
    {
        var hint = Path.IsPathFullyQualified(command) ? FullPathOrNull(command, null) : null;
        var request = new RunnerRequest(kind, hint, arguments, scriptOrDll, runtimeOptions ?? [], uvx);
        return new RunnerParseResult(kind, request, RunnerRefusal.None, null, command, args);
    }

    private static RunnerParseResult Refused(RunnerRefusal refusal, string? subject, string command, IReadOnlyList<string> args) =>
        new(RunnerKind.None, null, refusal, subject, command, args);

    private static bool IsCmd(string command)
    {
        var fileName = Path.GetFileName(command);
        var extension = Path.GetExtension(fileName);
        return string.Equals(Path.GetFileNameWithoutExtension(fileName), "cmd", StringComparison.OrdinalIgnoreCase)
            && (extension.Length == 0 || string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSystemCmd(string command, string systemDirectory)
    {
        var full = FullPathOrNull(command, null);
        var system = FullPathOrNull(Path.Combine(systemDirectory, "cmd.exe"), null);
        if (full is null || system is null)
            return false;

        if (Path.GetExtension(full).Length == 0)
            full += ".exe";
        return string.Equals(full, system, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLauncherExtension(string extension) =>
        extension.Length == 0
        || string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase);

    private static bool IsDll(string arg) => arg.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    // The subject reaches messages and logs, so it never carries a value; a short option is one letter.
    private static string OptionName(string arg)
    {
        if (arg.StartsWith("--", StringComparison.Ordinal))
            return arg.IndexOf('=') is var equals and >= 0 ? arg[..equals] : arg;
        return arg.Length > 2 ? arg[..2] : arg;
    }

    // .NET's '$' also matches before a trailing newline, which must not slip through.
    private static bool IsFullMatch(Regex pattern, string value) =>
        pattern.Match(value) is { Success: true } match && match.Length == value.Length;

    // uv installs an archive name as a local file, read from its working directory: the state folder.
    private static bool LooksLikeArchive(string value)
    {
        var versionStart = value.IndexOfAny(VersionStart);
        var name = versionStart < 0 ? value : value[..versionStart];
        return ArchiveExtensions.Any(extension => value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveAgainst(string path, string? workingDirectory)
    {
        if (Path.IsPathFullyQualified(path))
            return FullPathOrNull(path, null);

        var baseDirectory = workingDirectory?.Trim();
        return string.IsNullOrEmpty(baseDirectory) || !Path.IsPathFullyQualified(baseDirectory)
            ? null
            : FullPathOrNull(path, baseDirectory);
    }

    private static string? FullPathOrNull(string path, string? baseDirectory)
    {
        try
        {
            return baseDirectory is null ? Path.GetFullPath(path) : Path.GetFullPath(path, baseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
