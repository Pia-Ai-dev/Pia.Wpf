using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Pia.Logging;

namespace Pia.Services.Plugins.Sandbox;

/// <param name="Roots">Folders looked at (refused and missing ones are not counted).</param>
/// <param name="Removed">Leaked ACEs removed.</param>
/// <param name="Failed">Entries that could not be cleaned; their root keeps its ACE so the next sweep retries.</param>
public sealed record AceSweepResult(int Roots, int Removed, int Failed);

public interface IAceSweeper
{
    AceSweepResult Sweep(IEnumerable<string> roots, IEnumerable<string> containerNames);
}

/// <summary>Removes the ACEs a hard-killed host leaves behind: Tier 3 adds the container's SID to every policy path and
/// only a clean shutdown takes it off. Only Pia's own container SIDs are touched.</summary>
public sealed class AceSweepService : IAceSweeper
{
    private readonly IPiaContainerSidResolver _sids;
    private readonly ILogger<AceSweepService> _logger;
    private readonly Action<string>? _beforeEntry;

    public AceSweepService(IPiaContainerSidResolver sids, ILogger<AceSweepService> logger)
        : this(sids, logger, beforeEntry: null)
    {
    }

    internal AceSweepService(IPiaContainerSidResolver sids, ILogger<AceSweepService> logger, Action<string>? beforeEntry)
    {
        _sids = sids;
        _logger = logger;
        _beforeEntry = beforeEntry;
    }

    public AceSweepResult Sweep(IEnumerable<string> roots, IEnumerable<string> containerNames)
    {
        var sids = _sids.Resolve(containerNames);
        var swept = 0;
        var removed = 0;
        var failed = 0;

        if (sids.Count > 0)
        {
            foreach (var root in roots.Select(SandboxLaunchSpecBuilder.Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (IsSystemPath(root))
                {
                    _logger.LogWarning("ACE sweep refused a system folder");
                    _logger.SensitiveDebug("ACE sweep refused {Path}", root);
                    continue;
                }

                if (!Directory.Exists(root) && !File.Exists(root))
                    continue;

                swept++;
                var (rootRemoved, rootFailed) = SweepRoot(root, sids);
                removed += rootRemoved;
                failed += rootFailed;
            }
        }

        _logger.LogInformation("ACE sweep: {Roots} folder(s) checked, {Removed} leaked sandbox ACE(s) removed, {Failed} entry(ies) failed",
            swept, removed, failed);
        return new AceSweepResult(swept, removed, failed);
    }

    // Shallow first: a root without our ACE is trusted, so a clean start costs one ACL read per root. A root that has
    // one is cleaned last, after everything below it, so an interrupted sweep never hides what is left.
    private (int Removed, int Failed) SweepRoot(string root, IReadOnlySet<string> sids)
    {
        if (!CarriesOurAce(root, sids))
            return (0, 0);

        var removed = 0;
        var failed = 0;

        if (File.Exists(root))
        {
            Process(root, sids, ref removed, ref failed);
            return (removed, failed);
        }

        var directories = new List<string>();
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            directories.Add(directory);
            foreach (var child in SafeEnumerate(() => Directory.EnumerateDirectories(directory)))
            {
                if (!IsLink(child))
                    pending.Push(child);
            }
        }

        // A directory is always discovered after its parent, so walking the list backwards cleans children first.
        for (var i = directories.Count - 1; i >= 0; i--)
        {
            var directory = directories[i];
            foreach (var file in SafeEnumerate(() => Directory.EnumerateFiles(directory)).Where(f => !IsLink(f)))
                Process(file, sids, ref removed, ref failed);

            if (i > 0)
                Process(directory, sids, ref removed, ref failed);
        }

        if (failed == 0)
            Process(root, sids, ref removed, ref failed);

        return (removed, failed);
    }

    private void Process(string path, IReadOnlySet<string> sids, ref int removed, ref int failed)
    {
        try
        {
            _beforeEntry?.Invoke(path);
            removed += RemoveOurAces(path, sids);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            failed++;
            _logger.SensitiveDebug("ACE sweep could not clean {Path}: {Error}", path, ex.Message);
        }
    }

    private static bool CarriesOurAce(string path, IReadOnlySet<string> sids)
    {
        try
        {
            return OurRules(ReadAcl(path), sids).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private int RemoveOurAces(string path, IReadOnlySet<string> sids)
    {
        var acl = ReadAcl(path);
        var rules = OurRules(acl, sids).ToList();
        if (rules.Count == 0)
            return 0;

        foreach (var rule in rules)
            acl.RemoveAccessRuleSpecific(rule);

        if (acl is DirectorySecurity directorySecurity)
            new DirectoryInfo(path).SetAccessControl(directorySecurity);
        else
            new FileInfo(path).SetAccessControl((FileSecurity)acl);

        _logger.SensitiveDebug("ACE sweep removed {Count} ACE(s) from {Path}", rules.Count, path);
        return rules.Count;
    }

    private static FileSystemSecurity ReadAcl(string path) =>
        Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access);

    private static IEnumerable<FileSystemAccessRule> OurRules(FileSystemSecurity acl, IReadOnlySet<string> sids) =>
        acl.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => sids.Contains(rule.IdentityReference.Value));

    private static IEnumerable<string> SafeEnumerate(Func<IEnumerable<string>> enumerate)
    {
        try
        {
            return enumerate().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsSystemPath(string path) =>
        new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(root => !string.IsNullOrEmpty(root))
            .Any(root => SandboxLaunchSpecBuilder.IsSameOrUnder(path, SandboxLaunchSpecBuilder.Normalize(root)));
}
