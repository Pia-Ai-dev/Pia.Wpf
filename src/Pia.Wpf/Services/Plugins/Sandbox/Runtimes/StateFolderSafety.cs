using System.IO;
using System.IO.Enumeration;

namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>Host-side writes, walks and deletes in a folder a server may write, none of which follow a link it planted.</summary>
internal static class StateFolderSafety
{
    private static readonly EnumerationOptions NoLinks = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    private static readonly EnumerationOptions Everything = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
    };

    /// <summary>Removes a link at root, at any folder between root and path, or at path itself; removed folders come back empty.</summary>
    public static void EnsureNoReparsePoints(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(path));
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
            throw new ArgumentException("The path does not lie under the root.", nameof(path));

        string[] segments = relative == "." ? [] : relative.Split(Path.DirectorySeparatorChar);
        var current = fullRoot;
        for (var i = 0; i <= segments.Length; i++)
        {
            if (i > 0)
                current = Path.Combine(current, segments[i - 1]);

            var attributes = AttributesOf(current);
            if (attributes is null)
                return;

            if ((attributes.Value & FileAttributes.ReparsePoint) == 0)
                continue;

            RemoveLink(current, attributes.Value);
            if (i < segments.Length)
                Directory.CreateDirectory(current);
        }
    }

    /// <summary>Every file and folder below root, never through a link.</summary>
    public static IEnumerable<string> EnumerateNoLinks(string root) =>
        Directory.Exists(root) ? Directory.EnumerateFileSystemEntries(root, "*", NoLinks) : [];

    public static void DeleteTree(string root)
    {
        var attributes = AttributesOf(root);
        if (attributes is null)
            return;

        if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
        {
            RemoveLink(root, attributes.Value);
            return;
        }

        try
        {
            RemoveLinksBelow(root);
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            foreach (var entry in EnumerateNoLinks(root).ToList())
                ClearReadOnly(entry);

            RemoveLinksBelow(root);
            Directory.Delete(root, recursive: true);
        }
    }

    // Directory.Delete(dir, true) throws on a junction inside it, so links go first, each without descending.
    private static void RemoveLinksBelow(string root)
    {
        var links = new FileSystemEnumerable<(string Path, FileAttributes Attributes)>(
            root, (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.Attributes), Everything)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) != 0,
            ShouldRecursePredicate = (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0,
        };

        foreach (var (path, attributes) in links.ToList())
            RemoveLink(path, attributes);
    }

    private static void RemoveLink(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);

        if ((attributes & FileAttributes.Directory) != 0)
            Directory.Delete(path, recursive: false);
        else
            File.Delete(path);
    }

    private static void ClearReadOnly(string path)
    {
        var attributes = AttributesOf(path);
        if (attributes is { } value && (value & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, value & ~FileAttributes.ReadOnly);
    }

    private static FileAttributes? AttributesOf(string path)
    {
        var attributes = new FileInfo(path).Attributes;
        return (int)attributes == -1 ? null : attributes;
    }
}
