using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Logging;
using Pia.Paths;
using Pia.Services.Exceptions;
using Pia.Services.Plugins.Sandbox.Runtimes;

namespace Pia.Services.Plugins.Sandbox;

/// <summary>Runners need the detector, the host facts and a provisioner together; any one missing refuses them.</summary>
public sealed record SandboxRuntimeContext(
    IRuntimeDetector? Detector,
    IRuntimeHostService? Host,
    bool CanProvision,
    IReadOnlyList<string> UserEnvironmentNames,
    CancellationToken CancellationToken = default);

/// <summary>Turns a sandboxed transport request into the exact program, folders and environment the container gets.</summary>
public static class SandboxLaunchSpecBuilder
{
    public const string RuntimeDirectoryVariable = "MXC_FFI_DIR";

    private const string ContainerPrefix = "pia-mcp-";

    private const string ProbeSuffix = "-probe";

    private const string AliasError =
        "Pia can't sandbox this server yet: '{0}' is a Microsoft Store alias or a link, not the program itself. "
        + "Point the command at the real {0}.";

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cmd", ".bat", ".ps1" };

    private static readonly IReadOnlyDictionary<string, string> NoVariables = new Dictionary<string, string>();

    private static bool IsReparsePoint(string path) =>
        File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    public static string ContainerName(string prefix, bool isProbe) =>
        isProbe ? ContainerPrefix + prefix + ProbeSuffix : ContainerPrefix + prefix;

    public static string[] ContainerNames(string prefix) => [ContainerName(prefix, false), ContainerName(prefix, true)];

    public static string PrefixOf(string containerName)
    {
        var name = containerName.StartsWith(ContainerPrefix, StringComparison.OrdinalIgnoreCase)
            ? containerName[ContainerPrefix.Length..]
            : containerName;
        return name.EndsWith(ProbeSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^ProbeSuffix.Length] : name;
    }

    internal static SandboxLaunchPlan Build(
        McpTransportRequest request,
        string? pathVariable = null,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? isReparsePoint = null) =>
        Build(request, new SandboxRuntimeContext(null, null, CanProvision: false, []), pathVariable, fileExists, isReparsePoint);

    public static SandboxLaunchPlan Build(
        McpTransportRequest request,
        SandboxRuntimeContext runtime,
        string? pathVariable = null,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? isReparsePoint = null,
        ILogger? logger = null)
    {
        var policy = request.Sandbox ?? throw new ArgumentException("A sandboxed launch needs a policy.", nameof(request));
        var exists = fileExists ?? File.Exists;
        var log = logger ?? NullLogger.Instance;

        var parse = RunnerCommandParser.Parse(request.Command, request.Args, request.WorkingDirectory, fileExists: exists);
        if (parse.Refusal != RunnerRefusal.None)
            return SandboxLaunchPlan.Fail(SandboxRuntimeMessages.For(parse));

        var readwrite = Distinct(policy.ReadwritePaths.Select(Normalize));
        var readonlyPaths = Distinct(policy.ReadonlyPaths.Select(Normalize));
        var prefix = string.IsNullOrWhiteSpace(request.ToolPrefix)
            ? LocalMcpConfig.DeriveToolPrefix(request.Name)
            : request.ToolPrefix;
        var folders = new PolicyFolders(ContainerName(prefix, request.IsProbe), prefix, readwrite, readonlyPaths, policy.AllowNetwork);

        var plan = parse.Kind == RunnerKind.None
            ? BuildProgram(request, parse, folders, runtime, pathVariable ?? Environment.GetEnvironmentVariable("PATH"), exists,
                isReparsePoint ?? IsReparsePoint)
            : BuildRunner(request, parse, folders, runtime, log);

        if (plan.Spec is { } spec)
            LogBlanking(log, spec, runtime.UserEnvironmentNames);
        return plan;
    }

    private static SandboxLaunchPlan BuildProgram(
        McpTransportRequest request,
        RunnerParseResult parse,
        PolicyFolders folders,
        SandboxRuntimeContext runtime,
        string? pathVariable,
        Func<string, bool> exists,
        Func<string, bool> isReparsePoint)
    {
        var resolved = ResolveCommand(parse.EffectiveCommand, pathVariable, exists);
        if (resolved.Error is not null)
            return SandboxLaunchPlan.Fail(resolved.Error);

        var command = resolved.Path!;
        if (isReparsePoint(command))
            return SandboxLaunchPlan.Fail(string.Format(AliasError, Path.GetFileName(command)));

        var extension = Path.GetExtension(command);
        if (ScriptExtensions.Contains(extension))
            return SandboxLaunchPlan.Fail(SandboxRuntimeMessages.ScriptFile);
        if (!string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
            return SandboxLaunchPlan.Fail(SandboxRuntimeMessages.NotAProgram);

        var grantedCommandDirectory = CommandDirectoryToGrant(
            Normalize(Path.GetDirectoryName(command)!), folders.Readwrite, folders.Readonly);

        return SandboxLaunchPlan.Ok(new SandboxLaunchSpec(
            folders.ContainerName,
            command,
            [.. parse.EffectiveArgs],
            SandboxEnvironmentComposer.Compose(runtime.UserEnvironmentNames, NoVariables, request.Env),
            WorkingDirectoryFor(request.WorkingDirectory, folders.Readwrite, folders.Readonly, grantedCommandDirectory),
            folders.Readwrite,
            folders.Readonly,
            folders.AllowNetwork,
            grantedCommandDirectory));
    }

    private static SandboxLaunchPlan BuildRunner(
        McpTransportRequest request,
        RunnerParseResult parse,
        PolicyFolders folders,
        SandboxRuntimeContext runtime,
        ILogger log)
    {
        if (runtime.Detector is not { } detector || runtime.Host is not { } host || !runtime.CanProvision || parse.Runner is not { } runner)
            return SandboxLaunchPlan.Fail(SandboxRuntimeMessages.DetectionUnavailable);

        RuntimeLaunchPlan plan;
        string? programFolder;
        try
        {
            // Any sandboxed server may have planted the uv this one names, in its read-write folders or under the
            // sandbox root, which holds every server's own folder.
            var resolution = detector.Detect(
                runner with
                {
                    WritableFolders = [.. folders.Readwrite, .. request.SandboxedReadwriteFolders ?? [], PiaPaths.McpSandboxRoot]
                },
                runtime.CancellationToken);
            log.LogInformation("Runtime for {Container}: {Kind} {Outcome}, {Roots} toolchain root(s), readable already {ReadableAlready}",
                folders.ContainerName, parse.Kind, resolution.Outcome, resolution.ToolchainRoots.Count,
                resolution.Install?.ReadableAlready ?? false);
            log.SensitiveDebug("Runtime for {Container}: install {Root}, toolchain {Toolchain}, program folder {ProgramFolder}",
                folders.ContainerName, resolution.Install?.Root, string.Join("|", resolution.ToolchainRoots), resolution.ProgramFolder);
            if (resolution.Outcome != RuntimeOutcome.Ready)
                return SandboxLaunchPlan.Fail(SandboxRuntimeMessages.For(resolution));

            programFolder = resolution.ProgramFolder is { } folder
                ? CommandDirectoryToGrant(Normalize(folder), folders.Readwrite, folders.Readonly)
                : null;
            List<string> sandboxRoots = [.. folders.Readwrite, .. folders.Readonly];
            if (programFolder is not null)
                sandboxRoots.Add(programFolder);

            plan = RuntimeLaunchPlanner.Plan(new RuntimePlanInput(
                runner,
                resolution,
                folders.ContainerName,
                PiaPaths.McpRuntimeStateDirectory(folders.Prefix),
                PiaPaths.McpSandboxSupportDirectory,
                folders.AllowNetwork,
                Distinct(sandboxRoots),
                host.RegistryPath(),
                request.Env,
                host.MachineEnvironmentValue("NODE_OPTIONS")));
        }
        catch (Exception ex) when (ex is not SandboxStartException
            && !(ex is OperationCanceledException && runtime.CancellationToken.IsCancellationRequested))
        {
            log.LogWarning("Runtime check for {Container} failed: {Error}", folders.ContainerName, ex.GetType().Name);
            log.SensitiveDebug("Runtime check for {Container} failed: {Message}", folders.ContainerName, ex.Message);
            return SandboxLaunchPlan.Fail(SandboxRuntimeMessages.CheckFailed);
        }

        log.SensitiveDebug("Runtime state for {Container}: {State}, support {Support}",
            folders.ContainerName, plan.StateDirectory, plan.SupportDirectory);

        return SandboxLaunchPlan.Ok(new SandboxLaunchSpec(
            folders.ContainerName,
            plan.Command,
            [.. plan.Args],
            SandboxEnvironmentComposer.Compose(runtime.UserEnvironmentNames, plan.Environment, request.Env),
            WorkingDirectoryFor(request.WorkingDirectory, folders.Readwrite, folders.Readonly, programFolder),
            folders.Readwrite,
            folders.Readonly,
            folders.AllowNetwork,
            programFolder,
            ToolchainDirectories: plan.ToolchainRoots,
            RuntimeStateDirectory: plan.StateDirectory,
            RuntimeStateReadOnly: plan.StateReadOnly,
            SupportDirectory: plan.SupportDirectory,
            RunnerKind: parse.Kind,
            Preparation: plan.Preparation));
    }

    private static void LogBlanking(ILogger log, SandboxLaunchSpec spec, IReadOnlyList<string> userEnvironmentNames)
    {
        var blanked = SandboxEnvironmentComposer.NamesToBlank(userEnvironmentNames)
            .Where(name => spec.Environment.TryGetValue(name, out var value) && value.Length == 0)
            .ToList();
        log.LogInformation("Blanked {Count} user environment variable(s) for {Container}", blanked.Count, spec.ContainerName);
        log.SensitiveDebug("Blanked for {Container}: {Names}", spec.ContainerName, string.Join(", ", blanked));
    }

    internal static (string? Path, string? Error) ResolveCommand(string command, string? pathVariable, Func<string, bool> exists)
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
            return (null, "Pia can't sandbox this server: give the full path of the program.");

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

    /// <summary>Every folder the sandbox gives its container access to: the policy folders, the working directory and
    /// the program's own folder when it is granted.</summary>
    internal static IReadOnlyList<string> PolicyRoots(McpSandboxPolicy policy, string command, string? workingDirectory)
    {
        var readwrite = Distinct(policy.ReadwritePaths.Where(IsFullyQualified).Select(Normalize));
        var readonlyPaths = Distinct(policy.ReadonlyPaths.Where(IsFullyQualified).Select(Normalize));
        var roots = readwrite.Concat(readonlyPaths).ToList();

        if (IsFullyQualified(workingDirectory))
            roots.Add(Normalize(workingDirectory!));

        var resolved = ResolveCommand(command, null, File.Exists);
        if (resolved.Path is not null
            && CommandDirectoryToGrant(Normalize(Path.GetDirectoryName(resolved.Path)!), readwrite, readonlyPaths) is { } granted)
            roots.Add(granted);

        return Distinct(roots);
    }

    private static bool IsFullyQualified(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path.Trim());

    internal static string? CommandDirectoryToGrant(
        string commandDirectory,
        IReadOnlyList<string> readwrite,
        IReadOnlyList<string> readonlyPaths,
        IEnumerable<string>? systemRoots = null)
    {
        if ((systemRoots ?? SystemRoots()).Any(root => IsSameOrUnder(commandDirectory, root)))
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

    internal static List<string> Distinct(IEnumerable<string> paths) =>
        [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];

    internal static string Normalize(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        return full.Length > 3 ? full.TrimEnd('\\', '/') : full;
    }

    internal static bool IsSameOrUnder(string path, string root)
    {
        var parent = Comparable(root);
        if (parent.Length == 0)
            return false;

        var child = Comparable(path);
        return string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsStrictlyUnder(string path, string root) => IsSameOrUnder(path, root) && !SamePath(path, root);

    internal static bool SamePath(string left, string right) =>
        string.Equals(Comparable(left), Comparable(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a drive root such as D:\ and for a share root such as \\server\share.</summary>
    internal static bool IsVolumeRoot(string path) =>
        Path.GetPathRoot(path) is { Length: > 0 } root && SamePath(root, path);

    // A drive root keeps no separator, so D:\x still falls under D:\ once both sides are trimmed.
    private static string Comparable(string path) => path.Replace('/', '\\').TrimEnd('\\');

    private sealed record PolicyFolders(
        string ContainerName,
        string Prefix,
        IReadOnlyList<string> Readwrite,
        IReadOnlyList<string> Readonly,
        bool AllowNetwork);
}
