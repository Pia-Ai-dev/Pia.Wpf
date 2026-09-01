using System.IO;
using Pia.Infrastructure;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Lists folders inside the assistant-files sandbox for the working-directory picker.
/// The sandbox root is read from <see cref="ISettingsService"/> (<c>AssistantFilesFolder</c>)
/// per call (cached in-memory by the settings service, mirroring how
/// <see cref="FilesToolHandler"/> obtains it). Only sandbox-contained, non-sensitive,
/// immediate child folders are surfaced.
/// </summary>
public sealed class WorkingDirectoryService : IWorkingDirectoryService
{
    private readonly ISettingsService _settingsService;

    public WorkingDirectoryService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public IReadOnlyList<string> ListSubfolders(string relativeParent)
    {
        var root = GetSandboxRoot();
        if (root is null) return [];

        // Resolve the relative parent inside the sandbox; an empty parent is the root itself.
        string parent;
        if (string.IsNullOrWhiteSpace(relativeParent))
        {
            parent = root;
        }
        else if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, relativeParent, out parent))
        {
            return [];
        }

        if (!Directory.Exists(parent)) return [];

        var names = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(parent))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name)) continue;

                // Re-verify containment of the child (catches junctions/symlinks that point
                // outside the sandbox) and skip sensitive/protected folders.
                if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, dir, out var resolvedChild))
                    continue;
                if (SensitivePathGuard.IsBlocked(resolvedChild, out _))
                    continue;

                names.Add(name);
            }
        }
        catch
        {
            return [];
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public string? EnsureSubfolder(string? relativePath)
    {
        // Empty/whitespace means the sandbox root — always valid, nothing to create.
        if (string.IsNullOrWhiteSpace(relativePath))
            return string.Empty;

        var root = GetSandboxRoot();
        if (root is null) return null;

        // Relative-only containment: rejects rooted (C:\...), UNC, and "..\" escapes so the
        // default working directory can never point outside the assistant files folder.
        if (!SafeFolderPath.TryResolveInside(root, relativePath, out var resolved))
            return null;

        // Never surface a sensitive/protected folder — mirrors ListSubfolders. Note the memory
        // vault is deliberately NOT in SensitivePathGuard (file tools need vault access), so it is
        // rejected explicitly below: a chat must not root its working directory at the vault.
        if (SensitivePathGuard.IsBlocked(resolved, out _))
            return null;

        var vaultRoot = AssistantWorkspace.VaultRootFor(root);
        var vaultWithSep = SafeFolderPath.WithTrailingSeparator(vaultRoot);
        if (resolved.Equals(vaultRoot, StringComparison.OrdinalIgnoreCase)
            || resolved.StartsWith(vaultWithSep, StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            Directory.CreateDirectory(resolved);
        }
        catch
        {
            return null;
        }

        // Normalize to a forward-slash relative path derived from the resolved absolute path.
        var relative = Path.GetRelativePath(root, resolved).Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(relative) ? string.Empty : relative;
    }

    public string? ResolveAbsolutePath(string? relativePath)
    {
        var root = GetSandboxRoot();
        if (root is null) return null;

        if (string.IsNullOrWhiteSpace(relativePath))
            return root;

        if (!SafeFolderPath.TryResolveInside(root, relativePath, out var resolved))
            return null;
        if (SensitivePathGuard.IsBlocked(resolved, out _))
            return null;

        return Directory.Exists(resolved) ? resolved : null;
    }

    private string? GetSandboxRoot()
    {
        try
        {
            var settings = _settingsService.GetSettingsAsync().GetAwaiter().GetResult();
            var folder = settings.AssistantFilesFolder;
            if (string.IsNullOrWhiteSpace(folder)) return null;
            var full = Path.GetFullPath(folder);
            return Directory.Exists(full) ? SafeFolderPath.Canonicalize(full) : null;
        }
        catch
        {
            return null;
        }
    }
}
