using System.IO;

namespace Pia.Services.Plugins.Sandbox;

/// <summary>Turns a sandboxed transport request into the exact program, folders and environment the container gets.</summary>
public static class SandboxLaunchSpecBuilder
{
    public const string RuntimeDirectoryVariable = "MXC_FFI_DIR";

    private const string ScriptLauncherError =
        "Pia can't sandbox this server yet: '{0}' starts through a script launcher (npx, uvx, .cmd, .bat). "
        + "Point the command at the server's own .exe.";

    public static string ContainerName(string prefix, bool isProbe) =>
        isProbe ? $"pia-mcp-{prefix}-probe" : $"pia-mcp-{prefix}";

    public static SandboxLaunchPlan Build(McpTransportRequest request, string? pathVariable = null, Func<string, bool>? fileExists = null)
    {
        var policy = request.Sandbox ?? throw new ArgumentException("A sandboxed launch needs a policy.", nameof(request));
        var exists = fileExists ?? File.Exists;

        if (SandboxCommandRules.IsShimCommand(request.Command))
            return SandboxLaunchPlan.Fail(string.Format(ScriptLauncherError, Path.GetFileName(request.Command.Trim().Trim('"'))));

        var resolved = ResolveCommand(request.Command, pathVariable ?? Environment.GetEnvironmentVariable("PATH"), exists);
        if (resolved.Error is not null)
            return SandboxLaunchPlan.Fail(resolved.Error);

        var command = resolved.Path!;
        if (!string.Equals(Path.GetExtension(command), ".exe", StringComparison.OrdinalIgnoreCase)
            || SandboxCommandRules.IsShimCommand(command))
            return SandboxLaunchPlan.Fail(string.Format(ScriptLauncherError, Path.GetFileName(command)));

        var readwrite = Distinct(policy.ReadwritePaths.Select(Normalize));
        var readonlyPaths = Distinct(policy.ReadonlyPaths.Select(Normalize));
        var grantedCommandDirectory = CommandDirectoryToGrant(Normalize(Path.GetDirectoryName(command)!), readwrite, readonlyPaths);

        var prefix = string.IsNullOrWhiteSpace(request.ToolPrefix)
            ? LocalMcpConfig.DeriveToolPrefix(request.Name)
            : request.ToolPrefix;

        return SandboxLaunchPlan.Ok(new SandboxLaunchSpec(
            ContainerName(prefix, request.IsProbe),
            command,
            [.. request.Args],
            CopyEnvironment(request.Env),
            WorkingDirectoryFor(request.WorkingDirectory, readwrite, readonlyPaths, grantedCommandDirectory),
            readwrite,
            readonlyPaths,
            policy.AllowNetwork,
            grantedCommandDirectory));
    }

    private static (string? Path, string? Error) ResolveCommand(string command, string? pathVariable, Func<string, bool> exists)
    {
        command = command.Trim().Trim('"');
        if (command.Length == 0)
            return (null, "No command specified.");

        if (Path.IsPathRooted(command))
        {
            var full = Path.GetFullPath(command);
            return exists(full) ? (full, null) : (null, NotFound(command));
        }

        if (command.Contains('\\') || command.Contains('/'))
            return (null, "Give the full path to the program, e.g. C:\\Tools\\server\\server.exe.");

        string[] extensions = Path.HasExtension(command) ? [""] : [".exe", ".cmd", ".bat"];
        foreach (var directory in (pathVariable ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim('"'), command + extension);
                }
                catch (ArgumentException)
                {
                    break;
                }

                if (exists(candidate))
                    return (Path.GetFullPath(candidate), null);
            }
        }

        return (null, NotFound(command));
    }

    private static string NotFound(string command) => $"Command not found: {Path.GetFileName(command)}.";

    private static string? CommandDirectoryToGrant(string commandDirectory, IReadOnlyList<string> readwrite, IReadOnlyList<string> readonlyPaths)
    {
        if (SystemRoots().Any(root => IsSameOrUnder(commandDirectory, root)))
            return null;

        return readwrite.Concat(readonlyPaths).Any(granted => IsSameOrUnder(commandDirectory, granted))
            ? null
            : commandDirectory;
    }

    private static string WorkingDirectoryFor(
        string? requested,
        IReadOnlyList<string> readwrite,
        IReadOnlyList<string> readonlyPaths,
        string? grantedCommandDirectory)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var directory = Normalize(requested);
            var granted = readwrite.Concat(readonlyPaths).Append(grantedCommandDirectory);
            if (granted.Any(root => root is not null && IsSameOrUnder(directory, root)))
                return directory;
        }

        return readwrite.FirstOrDefault()
            ?? readonlyPaths.FirstOrDefault()
            ?? grantedCommandDirectory
            ?? Environment.SystemDirectory;
    }

    private static Dictionary<string, string> CopyEnvironment(IReadOnlyDictionary<string, string>? env) =>
        (env ?? new Dictionary<string, string>())
            .Where(e => !string.Equals(e.Key, RuntimeDirectoryVariable, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> SystemRoots()
    {
        foreach (var folder in new[]
        {
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86
        })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path))
                yield return Normalize(path);
        }
    }

    private static List<string> Distinct(IEnumerable<string> paths) =>
        [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        return full.Length > 3 ? full.TrimEnd('\\', '/') : full;
    }

    private static bool IsSameOrUnder(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}
