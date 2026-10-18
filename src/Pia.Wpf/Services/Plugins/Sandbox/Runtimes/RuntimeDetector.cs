using System.IO;
using System.Text.Json;

namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>Finds and classifies a runner's runtime from the registry, the file system and ACLs; never runs it.</summary>
public sealed class RuntimeDetector(IRuntimeHostService host, IRuntimeRootVerifier roots) : IRuntimeDetector
{
    private const int MinimumDnxSdkMajor = 10;
    private const string NetCoreApp = "Microsoft.NETCore.App";

    public RuntimeResolution Detect(RunnerRequest runner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);

        var family = FamilyOf(runner.Kind);
        var hasProgram = runner.Kind is RunnerKind.NodeScript or RunnerKind.DotnetDll;
        var program = hasProgram ? ResolveFile(runner.ScriptOrDll) : null;
        var frameworks = runner.Kind == RunnerKind.DotnetDll && program is not null
            ? DotnetRuntimeConfig.Read(host, program)
            : null;

        var runtime = FindRuntime(runner, family, frameworks, cancellationToken);
        if (runner.Kind == RunnerKind.DotnetDll)
            runtime = runtime with { Frameworks = frameworks };
        if (!hasProgram)
            return runtime;
        if (program is null)
            return Refused(runtime, runner.Kind == RunnerKind.NodeScript ? RuntimeIssue.ScriptMissing : RuntimeIssue.DllMissing);

        var folder = runner.Kind == RunnerKind.NodeScript ? PackageRoot(program) : Path.GetDirectoryName(program)!;
        if (IsUnderWindows(folder) || IsUnderProgramFiles(folder))
            return runtime;

        cancellationToken.ThrowIfCancellationRequested();
        if (roots.Check(folder, true) is { } issue && issue != RuntimeIssue.None)
            return Refused(runtime, issue);
        return runtime with { ProgramFolder = folder };
    }

    private RuntimeResolution FindRuntime(
        RunnerRequest runner,
        RuntimeFamily family,
        IReadOnlyList<DotnetRuntimeConfig.Framework>? frameworks,
        CancellationToken cancellationToken)
    {
        RuntimeInstall? adminOwned = null;
        (RuntimeInstall Install, RuntimeIssue Issue)? refused = null;
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var writable = family == RuntimeFamily.Uv ? FinalFolders(runner.WritableFolders) : [];

        foreach (var candidate in Candidates(runner, family))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Path.IsPathFullyQualified(candidate)
                || host.ResolveFinalPath(candidate) is not { } folder
                || !seenFolders.Add(Path.TrimEndingDirectorySeparator(folder))
                || Probe(runner.Kind, family, folder, frameworks) is not { } install
                || !seenRoots.Add(install.Root))
                continue;

            if (family == RuntimeFamily.Uv)
            {
                // uv runs outside the sandbox, so a server that can replace it would run its own program at Pia's level.
                if (IsUnderAny([folder, install.Root, install.Executable], writable))
                {
                    refused ??= (install, RuntimeIssue.UvInWritableFolder);
                    continue;
                }

                return Ready(runner.Kind, install, []);
            }

            if (IsReadable(install))
                return Ready(runner.Kind, install with { ReadableAlready = true }, []);
            if (IsUnderWindows(install.Root))
                continue;
            if (IsUnderProgramFiles(install.Root))
            {
                adminOwned ??= install with { AdminOwned = true };
                continue;
            }

            // Judged before ownership too: a too-wide root must never reach the machine-wide grant.
            var issue = SandboxLaunchSpecBuilder.IsVolumeRoot(install.Root)
                ? RuntimeIssue.ToolchainTooWide
                : roots.Check(install.Root, false) ?? RuntimeIssue.None;
            if (issue is RuntimeIssue.ToolchainTooWide or RuntimeIssue.ToolchainPathUnsupported)
            {
                refused ??= (install, issue);
                continue;
            }

            if (!host.CanUserWriteDacl(install.Root))
            {
                adminOwned ??= install with { AdminOwned = true };
                continue;
            }

            if (issue != RuntimeIssue.None)
            {
                refused ??= (install, issue);
                continue;
            }

            return Ready(runner.Kind, install, [install.Root]);
        }

        if (adminOwned is not null)
        {
            var issue = family == RuntimeFamily.Node ? RuntimeIssue.NodeAdminOwned : RuntimeIssue.DotnetAdminOwned;
            return new RuntimeResolution(runner.Kind, RuntimeOutcome.NeedsAdminFix, issue, adminOwned, [], null);
        }

        return refused is { } first
            ? new RuntimeResolution(runner.Kind, RuntimeOutcome.Refused, first.Issue, first.Install, [], null)
            : new RuntimeResolution(runner.Kind, RuntimeOutcome.NotFound, NotFoundIssue(runner.Kind), null, [], null);
    }

    private IEnumerable<string> Candidates(RunnerRequest runner, RuntimeFamily family)
    {
        if (runner.LauncherPathHint is { } hint && Path.GetDirectoryName(hint) is { Length: > 0 } hintFolder)
            yield return hintFolder;

        if (host.CandidateOverride is { } overrides)
        {
            foreach (var folder in overrides)
                yield return folder;
            yield break;
        }

        var windowsApps = Path.Combine(host.LocalAppData, "Microsoft", "WindowsApps");
        foreach (var entry in host.RegistryPathEntries())
        {
            var folder = entry.Trim().Trim('"');
            if (folder.Length == 0
                || SandboxLaunchSpecBuilder.SamePath(folder, windowsApps)
                || host.FileExists(Path.Combine(folder, "volta-shim.exe")))
                continue;
            yield return folder;
        }

        foreach (var location in RuntimeCandidates.For(family, host))
            yield return location;
    }

    private RuntimeInstall? Probe(
        RunnerKind kind,
        RuntimeFamily family,
        string folder,
        IReadOnlyList<DotnetRuntimeConfig.Framework>? frameworks)
    {
        if (ResolveFile(Path.Combine(folder, ExecutableName(family))) is not { } executable)
            return null;

        var root = Path.GetDirectoryName(executable)!;
        return family switch
        {
            RuntimeFamily.Node => ProbeNode(kind, root, executable),
            RuntimeFamily.Dotnet when kind == RunnerKind.Dnx => ProbeSdk(root, executable),
            RuntimeFamily.Dotnet => ProbeSharedRuntime(root, executable, frameworks),
            _ => new RuntimeInstall(RuntimeFamily.Uv, root, executable, host.FileVersion(executable), null, null, false, false)
        };
    }

    private RuntimeInstall? ProbeNode(RunnerKind kind, string root, string executable)
    {
        var npm = Path.Combine(root, "node_modules", "npm");
        var package = Path.Combine(npm, "package.json");
        if (!host.FileExists(package))
            return null;

        string? entryScript = null;
        if (kind is RunnerKind.Npx or RunnerKind.NpmExec)
        {
            entryScript = ResolveFile(Path.Combine(npm, "bin", kind == RunnerKind.Npx ? "npx-cli.js" : "npm-cli.js"));
            if (entryScript is null)
                return null;
        }

        return new RuntimeInstall(
            RuntimeFamily.Node, root, executable, host.FileVersion(executable), NpmVersion(package), entryScript, false, false);
    }

    private RuntimeInstall? ProbeSdk(string root, string executable)
    {
        if (SdkVersion.HighestUsableSdk(host, root) is not { } sdk
            || SdkVersion.MajorOf(Path.GetFileName(sdk)) is not >= MinimumDnxSdkMajor
            || ResolveFile(Path.Combine(sdk, "dotnet.dll")) is not { } entryScript)
            return null;

        return new RuntimeInstall(RuntimeFamily.Dotnet, root, executable, Path.GetFileName(sdk), null, entryScript, false, false);
    }

    private RuntimeInstall? ProbeSharedRuntime(string root, string executable, IReadOnlyList<DotnetRuntimeConfig.Framework>? frameworks)
    {
        var satisfied = frameworks is { Count: > 0 }
            ? frameworks.All(framework => HasFramework(root, framework))
            : FrameworkVersions(root, NetCoreApp).Any();
        if (!satisfied)
            return null;

        var version = FrameworkVersions(root, NetCoreApp).MaxBy(name => name, SdkVersion.Comparer);
        return new RuntimeInstall(RuntimeFamily.Dotnet, root, executable, version, null, null, false, false);
    }

    private bool HasFramework(string root, DotnetRuntimeConfig.Framework framework) =>
        SdkVersion.MajorOf(framework.Version) is { } major
        && FrameworkVersions(root, framework.Name)
            .Any(version => SdkVersion.MajorOf(version) == major && SdkVersion.Compare(version, framework.Version) >= 0);

    private IEnumerable<string> FrameworkVersions(string root, string name)
    {
        if (!IsPlainName(name))
            return [];

        var folder = Path.Combine(root, "shared", name);
        return host.DirectoryExists(folder)
            ? host.EnumerateDirectories(folder, "*").Select(version => Path.GetFileName(version))
            : [];
    }

    private string PackageRoot(string script)
    {
        var scriptFolder = Path.GetDirectoryName(script)!;
        var folder = scriptFolder;
        while (Path.GetDirectoryName(folder) is { } parent && !SandboxLaunchSpecBuilder.SamePath(folder, host.UserProfile))
        {
            if (host.FileExists(Path.Combine(folder, "package.json")))
                return folder;
            folder = parent;
        }

        return scriptFolder;
    }

    private string? NpmVersion(string packageJson)
    {
        if (host.ReadAllText(packageJson) is not { } text)
            return null;

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string? ResolveFile(string? path) =>
        path is not null
        && Path.IsPathFullyQualified(path)
        && host.ResolveFinalPath(path) is { } final
        && host.FileExists(final)
            ? final
            : null;

    // Through the final path too, or a junction inside a writable folder would hide what it reaches.
    private List<string> FinalFolders(IReadOnlyList<string>? folders)
    {
        var result = new List<string>();
        foreach (var raw in folders ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw) || !Path.IsPathFullyQualified(raw.Trim()))
                continue;

            var folder = SandboxLaunchSpecBuilder.Normalize(raw);
            result.Add(folder);
            if (host.ResolveFinalPath(folder) is { } final)
                result.Add(final);
        }

        return result;
    }

    private static bool IsUnderAny(IEnumerable<string> paths, IReadOnlyList<string> folders) =>
        paths.Any(path => folders.Any(folder => SandboxLaunchSpecBuilder.IsSameOrUnder(path, folder)));

    private bool IsReadable(RuntimeInstall install) =>
        host.IsContainerReadable(install.Root)
        && host.IsContainerReadable(install.Executable)
        && (install.EntryScript is null || host.IsContainerReadable(install.EntryScript));

    private bool IsUnderWindows(string path) => IsUnderHostFolder(path, host.WindowsDirectory);

    private bool IsUnderProgramFiles(string path) => host.ProgramFilesDirectories.Any(folder => IsUnderHostFolder(path, folder));

    private static RuntimeResolution Ready(RunnerKind kind, RuntimeInstall install, IReadOnlyList<string> toolchainRoots) =>
        new(kind, RuntimeOutcome.Ready, RuntimeIssue.None, install, toolchainRoots, null);

    private static RuntimeResolution Refused(RuntimeResolution runtime, RuntimeIssue issue) =>
        runtime with { Outcome = RuntimeOutcome.Refused, Issue = issue, ToolchainRoots = [], ProgramFolder = null };

    public static RuntimeFamily FamilyOf(RunnerKind kind) => kind switch
    {
        RunnerKind.Npx or RunnerKind.NpmExec or RunnerKind.NodeScript => RuntimeFamily.Node,
        RunnerKind.Dnx or RunnerKind.DotnetDll => RuntimeFamily.Dotnet,
        RunnerKind.Uvx => RuntimeFamily.Uv,
        _ => throw new ArgumentException("A runner kind is required.", nameof(kind))
    };

    private static RuntimeIssue NotFoundIssue(RunnerKind kind) => kind switch
    {
        RunnerKind.Dnx => RuntimeIssue.DotnetSdkMissing,
        RunnerKind.DotnetDll => RuntimeIssue.DotnetRuntimeMissing,
        RunnerKind.Uvx => RuntimeIssue.UvNotFound,
        _ => RuntimeIssue.NodeNotFound
    };

    private static string ExecutableName(RuntimeFamily family) => family switch
    {
        RuntimeFamily.Node => "node.exe",
        RuntimeFamily.Dotnet => "dotnet.exe",
        _ => "uv.exe"
    };

    private static bool IsUnderHostFolder(string path, string folder) =>
        !string.IsNullOrWhiteSpace(folder) && SandboxLaunchSpecBuilder.IsSameOrUnder(path, SandboxLaunchSpecBuilder.Normalize(folder));

    private static bool IsPlainName(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
