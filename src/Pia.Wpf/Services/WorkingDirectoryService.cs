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
