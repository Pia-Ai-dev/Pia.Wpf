using System.IO;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Saves composer attachments into the assistant-files sandbox. The sandbox root is read from
/// <see cref="ISettingsService"/> per call, mirroring <see cref="WorkingDirectoryService"/>, which also
/// owns creating and validating the working directory this copies into.
/// </summary>
public sealed class AttachedFileStore : IAttachedFileStore
{
    private const int MaxCollisionSuffix = 999;

    private readonly ISettingsService _settingsService;
    private readonly IWorkingDirectoryService _workingDirectoryService;
    private readonly ILogger<AttachedFileStore> _logger;

    public AttachedFileStore(
        ISettingsService settingsService,
        IWorkingDirectoryService workingDirectoryService,
        ILogger<AttachedFileStore> logger)
    {
        _settingsService = settingsService;
        _workingDirectoryService = workingDirectoryService;
        _logger = logger;
    }

    public string? SaveIntoWorkingDirectory(string sourcePath, string? workingDirectory)
    {
        var root = GetSandboxRoot();
        if (root is null || string.IsNullOrWhiteSpace(sourcePath)) return null;

        try
        {
            if (!File.Exists(sourcePath)) return null;

            // Already inside the sandbox (e.g. re-attached from the working directory): the file is
            // durable where it is, so hand back its own path rather than making a second copy. Rooted
            // only — a relative source would resolve AGAINST the sandbox and be called saved without
            // anything having been copied.
            if (Path.IsPathRooted(sourcePath)
                && SafeFolderPath.TryResolveInsideAllowingAbsolute(root, sourcePath, out var existing)
                && !AssistantWorkspace.IsAtOrInsideVaultOf(root, existing))
            {
                return ToRelative(root, existing);
            }

            var subfolder = _workingDirectoryService.EnsureSubfolder(workingDirectory);
            if (subfolder is null) return null;

            var targetDirectory = root;
            if (subfolder.Length > 0 && !SafeFolderPath.TryResolveInside(root, subfolder, out targetDirectory))
                return null;

            if (SensitivePathGuard.IsBlocked(targetDirectory, out _)) return null;
            if (AssistantWorkspace.IsAtOrInsideVaultOf(root, targetDirectory)) return null;

            Directory.CreateDirectory(targetDirectory);

            var target = FindFreeName(targetDirectory, Path.GetFileName(sourcePath));
            if (target is null) return null;

            // overwrite: false plus the probing above — a same-named file the user already has in the
            // working directory must never be clobbered by a drop.
            File.Copy(sourcePath, target, overwrite: false);

            var relative = ToRelative(root, target);
            _logger.LogInformation("Saved an attachment into the assistant files folder");
            _logger.SensitiveDebug("Saved attachment to {Path}", relative);
            return relative;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save an attachment into the assistant files folder");
            return null;
        }
    }

    public string? ResolveAbsolute(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;

        var root = GetSandboxRoot();
        if (root is null) return null;

        return SafeFolderPath.TryResolveInside(root, relativePath.Replace('/', Path.DirectorySeparatorChar), out var resolved)
            ? resolved
            : null;
    }

    private static string? FindFreeName(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 2; i <= MaxCollisionSuffix; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string ToRelative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/').Trim('/');

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
