using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Paths;
using Pia.Services.Plugins.Sandbox.Runtimes;

namespace Pia.Services.Plugins.Sandbox;

public enum SandboxPathIssueKind
{
    NotRooted,
    UncPath,
    Missing,
    SystemFolder,
    ProfileOrAncestor,
    PiaDataFolder,
    SandboxRootItself,
    ReadonlyInsideReadwrite,
    NestedReadwrite,
    CommandFolderTooWide,
    WideFolder,
    TooManyFiles,
    CommandFolderTooLarge,
    ManagedRuntimeFolder,
    HostProgramFolder
}

/// <param name="Path">The path as the policy holds it, so a UI can mark the matching entry. The message never repeats it.</param>
public sealed record SandboxPathIssue(string Path, SandboxPathIssueKind Kind, string Message);

/// <summary>The machine facts the guard judges paths against; a seam so the rules test without a real profile.</summary>
/// <param name="HostProgramFolders">Folders whose programs run outside the sandbox; asked only when a read-write folder is judged.</param>
/// <param name="FinalPath">Where a read-write folder leads once links are followed; null when it can't or may not be resolved.</param>
public sealed record SandboxGuardEnvironment(
    string WindowsDirectory,
    IReadOnlyList<string> ProgramFilesDirectories,
    string ProfileRoot,
    IReadOnlyList<string> PiaDataRoots,
    string SandboxRoot,
    IReadOnlyList<string> WideFolders,
    Func<string, bool> PathExists,
    Func<string, int, int> CountEntries,
    Func<IReadOnlyList<string>>? HostProgramFolders = null,
    Func<string, string?>? FinalPath = null)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, long Ticks)> CountCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly long CountCacheTicks = TimeSpan.FromSeconds(20).Ticks;

    public static SandboxGuardEnvironment Current() => Current(HostProgramFolderCache.Shared);

    internal static SandboxGuardEnvironment Current(HostProgramFolderCache hostPrograms)
    {
        var profile = PiaPaths.UserProfileRoot;
        string[] piaRoots =
        [
            PiaPaths.RoamingDataDirectory,
            PiaPaths.LocalDataDirectory,
            Path.Combine(PiaPaths.RoamingProfileRoot, "Pia"),
            Path.Combine(PiaPaths.LocalProfileRoot, "Pia")
        ];

        return new SandboxGuardEnvironment(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)],
            profile,
            [.. piaRoots.Distinct(StringComparer.OrdinalIgnoreCase)],
            PiaPaths.McpSandboxRoot,
            [
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(profile, "Downloads"),
                .. new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
                    .Select(Environment.GetEnvironmentVariable)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => path!)
            ],
            path => Directory.Exists(path) || File.Exists(path),
            CountEntriesCached,
            hostPrograms.Get,
            hostPrograms.FinalPath);
    }

    // Typed into an editor, so the same folder is judged on every keystroke; a recount per keystroke would stall it.
    private static int CountEntriesCached(string path, int limit)
    {
        var now = DateTime.UtcNow.Ticks;
        if (CountCache.TryGetValue(path, out var cached) && now - cached.Ticks < CountCacheTicks && cached.Count < limit)
            return cached.Count;

        int count;
        if (File.Exists(path))
            count = 1;
        else if (!Directory.Exists(path))
            count = 0;
        else
            count = Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Take(limit).Count();

        if (count < limit)
            CountCache[path] = (count, now);
        return count;
    }
}

/// <summary>Reuses the host program folders briefly: the editor asks on the UI thread at every change.</summary>
internal sealed class HostProgramFolderCache(IRuntimeHostService host, Func<long> clockTicks)
{
    public static readonly long LifetimeTicks = TimeSpan.FromSeconds(20).Ticks;

    // Only reads the registry and the file system; elevation is never asked.
    public static HostProgramFolderCache Shared { get; } =
        new(new RuntimeHostService(new ElevationService(NullLogger<ElevationService>.Instance)), () => DateTime.UtcNow.Ticks);

    private readonly Lock _gate = new();
    private (IReadOnlyList<string> Folders, string Profile, long Ticks)? _entry;

    public IReadOnlyList<string> Get()
    {
        lock (_gate)
        {
            var now = clockTicks();
            // The fixed uv folders hang off the profile, which a test may redirect.
            var profile = host.UserProfile + "|" + host.LocalAppData;
            if (_entry is { } entry && entry.Profile == profile && now - entry.Ticks < LifetimeTicks)
                return entry.Folders;

            var folders = RuntimeCandidates.HostProgramFolders(host, IsOnThisPc);
            _entry = (folders, profile, now);
            return folders;
        }
    }

    public string? FinalPath(string folder) => IsOnThisPc(folder) ? host.ResolveFinalPath(folder) : null;

    // A UNC path or a mapped network drive can stall for seconds when its server is gone.
    internal static bool IsOnThisPc(string folder)
    {
        if (folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal))
            return false;

        try
        {
            return Path.GetPathRoot(folder) is { Length: > 0 } root
                && new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>Judges a sandbox policy, and the folder its program would have to share, before anything is shared.</summary>
public static class SandboxPathGuard
{
    /// <summary>The sandbox rewrites the ACL of every entry below a policy folder on each start, so a folder this
    /// large would make starts slow and churn the files.</summary>
    public const int MaxEntriesPerRoot = 20_000;

    public static IReadOnlyList<SandboxPathIssue> Validate(McpSandboxPolicy policy, string? command) =>
        Validate(policy, command, SandboxGuardEnvironment.Current());

    internal static IReadOnlyList<SandboxPathIssue> Validate(
        McpSandboxPolicy policy,
        string? command,
        SandboxGuardEnvironment environment,
        Func<string, bool>? fileExists = null)
    {
        var issues = new List<SandboxPathIssue>();
        var readwrite = new List<(string Raw, string Full)>();
        var readonlyPaths = new List<(string Raw, string Full)>();

        foreach (var raw in policy.ReadwritePaths)
            Check(raw, isReadwrite: true, environment, issues, readwrite);
        foreach (var raw in policy.ReadonlyPaths)
            Check(raw, isReadwrite: false, environment, issues, readonlyPaths);

        foreach (var (raw, full) in readonlyPaths)
        {
            if (readwrite.Any(rw => SandboxLaunchSpecBuilder.IsSameOrUnder(full, rw.Full)))
                issues.Add(new(raw, SandboxPathIssueKind.ReadonlyInsideReadwrite,
                    "This read-only folder is inside a read-write folder, so it would be writable. Move it out or remove it."));
        }

        for (var i = 0; i < readwrite.Count; i++)
        {
            var nested = readwrite.Where((other, j) => j != i
                && (j < i ? SandboxLaunchSpecBuilder.IsSameOrUnder(readwrite[i].Full, other.Full)
                          : SandboxLaunchSpecBuilder.IsStrictlyUnder(readwrite[i].Full, other.Full))).Any();
            if (nested)
                issues.Add(new(readwrite[i].Raw, SandboxPathIssueKind.NestedReadwrite,
                    "This folder is already covered by another read-write folder. Keep only one of them."));
        }

        CheckCommandFolder(command, readwrite.Select(p => p.Full).ToList(), readonlyPaths.Select(p => p.Full).ToList(),
            environment, fileExists ?? File.Exists, issues);
        return issues;
    }

    /// <summary>True when the guard refuses this folder whatever the rest of the policy says, so no container ever writes it.</summary>
    public static bool CanNeverBeGranted(string path) => CanNeverBeGranted(path, SandboxGuardEnvironment.Current());

    internal static bool CanNeverBeGranted(string path, SandboxGuardEnvironment environment)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(trimmed))
            return true;

        var full = SandboxLaunchSpecBuilder.Normalize(trimmed);
        var sandboxRoot = SandboxLaunchSpecBuilder.Normalize(environment.SandboxRoot);
        return FindProblem(full, sandboxRoot, SandboxLaunchSpecBuilder.IsStrictlyUnder(full, sandboxRoot), environment) is not null;
    }

    /// <summary>The program's folder when the sandbox is given read access to it and the guard accepts that; otherwise null.</summary>
    public static string? GrantedCommandFolder(McpSandboxPolicy policy, string? command) =>
        GrantedCommandFolder(policy, command, SandboxGuardEnvironment.Current(), File.Exists);

    internal static string? GrantedCommandFolder(
        McpSandboxPolicy policy,
        string? command,
        SandboxGuardEnvironment environment,
        Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var resolved = SandboxLaunchSpecBuilder.ResolveCommand(command, null, fileExists);
        if (resolved.Path is null)
            return null;

        var directory = SandboxLaunchSpecBuilder.Normalize(Path.GetDirectoryName(resolved.Path)!);
        var granted = SandboxLaunchSpecBuilder.CommandDirectoryToGrant(
            directory,
            SandboxLaunchSpecBuilder.Distinct(policy.ReadwritePaths.Where(IsUsable).Select(SandboxLaunchSpecBuilder.Normalize)),
            SandboxLaunchSpecBuilder.Distinct(policy.ReadonlyPaths.Where(IsUsable).Select(SandboxLaunchSpecBuilder.Normalize)),
            SystemRoots(environment));
        return granted is null || IsTooWide(granted, environment) || IsTooLarge(granted, environment) ? null : granted;
    }

    private static bool IsUsable(string path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path.Trim());

    private static void Check(
        string raw,
        bool isReadwrite,
        SandboxGuardEnvironment environment,
        List<SandboxPathIssue> issues,
        List<(string Raw, string Full)> accepted)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            issues.Add(new(raw, SandboxPathIssueKind.UncPath, "Network folders can't be shared with the sandbox yet. Use a folder on this PC."));
            return;
        }

        if (!Path.IsPathFullyQualified(trimmed))
        {
            issues.Add(new(raw, SandboxPathIssueKind.NotRooted, @"Use the full path of the folder, for example C:\Users\you\Documents\Reference."));
            return;
        }

        var full = SandboxLaunchSpecBuilder.Normalize(trimmed);
        var sandboxRoot = SandboxLaunchSpecBuilder.Normalize(environment.SandboxRoot);
        var insideSandboxRoot = SandboxLaunchSpecBuilder.IsStrictlyUnder(full, sandboxRoot);

        var problem = FindProblem(full, sandboxRoot, insideSandboxRoot, environment);
        if (problem is not null)
        {
            issues.Add(new(raw, problem.Value.Kind, problem.Value.Message));
            return;
        }

        if (insideSandboxRoot && IsManagedRuntimeFolder(full, sandboxRoot))
        {
            issues.Add(new(raw, SandboxPathIssueKind.ManagedRuntimeFolder,
                "Pia manages this folder for a server's runtime. Choose a different folder."));
            return;
        }

        if (environment.WideFolders.Any(wide => string.Equals(full, SandboxLaunchSpecBuilder.Normalize(wide), StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new(raw, SandboxPathIssueKind.WideFolder,
                "Share a subfolder instead: Pia would have to change the permissions of everything in this folder."));
            return;
        }

        // Whether or not a program is there yet: a server could plant one that the user or Pia then runs uncontained.
        if (isReadwrite && HoldsHostProgramFolder(full, environment))
        {
            issues.Add(new(raw, SandboxPathIssueKind.HostProgramFolder,
                "This folder holds programs that run outside the sandbox (a folder on your PATH, or one Pia runs uv from), so a sandboxed server can't be allowed to change it. Share it read-only or choose a different folder."));
            return;
        }

        if (!(isReadwrite && insideSandboxRoot) && !environment.PathExists(full))
        {
            issues.Add(new(raw, SandboxPathIssueKind.Missing, "This folder does not exist."));
            return;
        }

        if (!insideSandboxRoot && environment.CountEntries(full, MaxEntriesPerRoot + 1) > MaxEntriesPerRoot)
        {
            issues.Add(new(raw, SandboxPathIssueKind.TooManyFiles,
                "This folder holds too many files to share with a sandboxed server. Pick a smaller subfolder."));
            return;
        }

        accepted.Add((raw, full));
    }

    // Through its final path too: a folder that is a link writes into its target.
    private static bool HoldsHostProgramFolder(string full, SandboxGuardEnvironment environment)
    {
        if (environment.HostProgramFolders?.Invoke() is not { Count: > 0 } folders)
            return false;

        string[] spellings = environment.FinalPath?.Invoke(full) is { } final && Path.IsPathFullyQualified(final)
            ? [full, SandboxLaunchSpecBuilder.Normalize(final)]
            : [full];
        return folders.Any(folder => Path.IsPathFullyQualified(folder) && spellings.Any(path => ContainsOrIs(path, folder)));
    }

    private static bool IsManagedRuntimeFolder(string full, string sandboxRoot) =>
        full[(sandboxRoot.TrimEnd('\\').Length + 1)..].Split('\\')[0].EndsWith(PiaPaths.McpRuntimeStateSuffix, StringComparison.OrdinalIgnoreCase);

    /// <returns>Null when the normalized root may be granted, and under Windows or Program Files, which are never granted.</returns>
    internal static RuntimeIssue? JudgeRuntimeRoot(string root, bool isProgramFolder, SandboxGuardEnvironment environment)
    {
        if (root.Contains(';'))
            return RuntimeIssue.ToolchainPathUnsupported;

        if (SystemRoots(environment).Any(system => SandboxLaunchSpecBuilder.IsSameOrUnder(root, system)))
            return null;

        if (IsTooWide(root, environment))
            return isProgramFolder ? RuntimeIssue.ProgramFolderTooWide : RuntimeIssue.ToolchainTooWide;

        if (IsTooLarge(root, environment))
            return isProgramFolder ? RuntimeIssue.ProgramFolderTooLarge : RuntimeIssue.ToolchainTooLarge;

        return null;
    }

    private static (SandboxPathIssueKind Kind, string Message)? FindProblem(
        string full, string sandboxRoot, bool insideSandboxRoot, SandboxGuardEnvironment environment)
    {
        if (string.Equals(full, sandboxRoot, StringComparison.OrdinalIgnoreCase))
            return (SandboxPathIssueKind.SandboxRootItself, "This folder holds the data of every sandboxed server. Choose a folder inside it.");

        if (SystemRoots(environment).Any(root => SandboxLaunchSpecBuilder.IsSameOrUnder(full, root)))
            return (SandboxPathIssueKind.SystemFolder, "Windows and Program Files can't be shared with a sandbox.");

        if (ContainsOrIs(full, environment.ProfileRoot))
            return (SandboxPathIssueKind.ProfileOrAncestor, "This folder contains your whole user profile. Choose a folder inside it.");

        if (!insideSandboxRoot && environment.PiaDataRoots.Any(root =>
                SandboxLaunchSpecBuilder.IsSameOrUnder(full, SandboxLaunchSpecBuilder.Normalize(root)) || ContainsOrIs(full, root)))
            return (SandboxPathIssueKind.PiaDataFolder, "This folder holds Pia's own data. Choose a different folder.");

        return null;
    }

    private static void CheckCommandFolder(
        string? command,
        IReadOnlyList<string> readwrite,
        IReadOnlyList<string> readonlyPaths,
        SandboxGuardEnvironment environment,
        Func<string, bool> fileExists,
        List<SandboxPathIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        var resolved = SandboxLaunchSpecBuilder.ResolveCommand(command, null, fileExists);
        if (resolved.Path is null)
            return;

        var directory = SandboxLaunchSpecBuilder.Normalize(Path.GetDirectoryName(resolved.Path)!);
        var granted = SandboxLaunchSpecBuilder.CommandDirectoryToGrant(directory, readwrite, readonlyPaths, SystemRoots(environment));
        if (granted is null)
            return;

        if (IsTooWide(granted, environment))
        {
            issues.Add(new(granted, SandboxPathIssueKind.CommandFolderTooWide,
                "Put the server in its own folder: Pia would have to share that whole folder with it."));
        }
        else if (IsTooLarge(granted, environment))
        {
            issues.Add(new(granted, SandboxPathIssueKind.CommandFolderTooLarge,
                "The server's folder holds too many files to share with a sandboxed server. Put the server in a smaller folder."));
        }
    }

    private static bool IsTooLarge(string directory, SandboxGuardEnvironment environment) =>
        !SandboxLaunchSpecBuilder.IsStrictlyUnder(directory, SandboxLaunchSpecBuilder.Normalize(environment.SandboxRoot))
        && environment.CountEntries(directory, MaxEntriesPerRoot + 1) > MaxEntriesPerRoot;

    private static bool IsTooWide(string directory, SandboxGuardEnvironment environment)
    {
        var sandboxRoot = SandboxLaunchSpecBuilder.Normalize(environment.SandboxRoot);
        return ContainsOrIs(directory, environment.ProfileRoot)
            || environment.WideFolders.Any(folder => string.Equals(directory, SandboxLaunchSpecBuilder.Normalize(folder), StringComparison.OrdinalIgnoreCase))
            || (!SandboxLaunchSpecBuilder.IsStrictlyUnder(directory, sandboxRoot)
                && environment.PiaDataRoots.Any(root =>
                    SandboxLaunchSpecBuilder.IsSameOrUnder(directory, SandboxLaunchSpecBuilder.Normalize(root)) || ContainsOrIs(directory, root)));
    }

    private static IEnumerable<string> SystemRoots(SandboxGuardEnvironment environment) =>
        environment.ProgramFilesDirectories.Prepend(environment.WindowsDirectory)
            .Where(root => !string.IsNullOrEmpty(root))
            .Select(SandboxLaunchSpecBuilder.Normalize);

    /// <summary>True when <paramref name="path"/> is <paramref name="other"/> or a folder above it.</summary>
    private static bool ContainsOrIs(string path, string other) =>
        SandboxLaunchSpecBuilder.IsSameOrUnder(SandboxLaunchSpecBuilder.Normalize(other), path);
}
