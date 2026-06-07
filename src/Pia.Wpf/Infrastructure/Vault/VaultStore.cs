using System.IO;
using System.Text;
using Pia.Models.Vault;

namespace Pia.Infrastructure.Vault;

/// <summary>
/// Atomic, splice-aware file access to the memory vault (format spec v1). Writes go through a
/// tmp-then-rename so a crash never leaves a half-written file, and section edits are byte-range
/// splices (§3.1) so frontmatter, unknown keys, and sibling sections are preserved byte-for-byte.
/// </summary>
public class VaultStore : IVaultStore
{
    // UTF-8 without BOM so RawText round-trips byte-for-byte (a BOM would corrupt the splice contract).
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly MarkdownVaultParser _parser;

    public VaultStore(string root, MarkdownVaultParser parser)
    {
        Root = root;
        _parser = parser;
    }

    /// <inheritdoc />
    public string Root { get; }

    /// <inheritdoc />
    public async Task<VaultDocument?> ReadAsync(string relativePath)
    {
        var fullPath = ResolvePath(relativePath);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(fullPath, Utf8NoBom);
        return _parser.Parse(text);
    }

    /// <inheritdoc />
    public async Task WriteAtomicAsync(string relativePath, string content)
    {
        var fullPath = ResolvePath(relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tmpPath = fullPath + ".tmp";
        try
        {
            await WriteFileAsync(tmpPath, content);
            File.Move(tmpPath, fullPath, overwrite: true);
        }
        catch
        {
            // Never leave a half-written tmp behind; the original file (if any) is untouched
            // because the move is the only step that replaces it.
            TryDelete(tmpPath);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task SpliceSectionAsync(string relativePath, string slug, string newBody)
    {
        var doc = await ReadAsync(relativePath);
        if (doc is null)
        {
            throw new FileNotFoundException(
                "Cannot splice a section into a vault file that does not exist.", relativePath);
        }

        VaultSection? section = null;
        foreach (var candidate in doc.Sections)
        {
            if (candidate.Slug == slug)
            {
                section = candidate;
                break;
            }
        }

        if (section is null)
        {
            // Documented behavior: a missing slug is an error rather than a silent no-op, so callers
            // never believe an upsert landed when it did not.
            throw new KeyNotFoundException(
                $"Section slug '{slug}' was not found in the vault document.");
        }

        // §3.1 byte-range splice: everything outside [BodyStart, BodyEnd) is preserved verbatim.
        var newFile = doc.RawText[..section.BodyStart] + newBody + doc.RawText[section.BodyEnd..];
        await WriteAtomicAsync(relativePath, newFile);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> EnumerateAsync(string globUnderRoot)
    {
        if (!Directory.Exists(Root))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        // Split the glob into a directory portion (literal, walked recursively) and a file pattern.
        var pattern = Path.GetFileName(globUnderRoot);
        if (string.IsNullOrEmpty(pattern))
        {
            pattern = "*.md";
        }

        var subDir = Path.GetDirectoryName(globUnderRoot);
        var searchRoot = string.IsNullOrEmpty(subDir) ? Root : Path.Combine(Root, subDir);
        if (!Directory.Exists(searchRoot))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var rootFull = Path.GetFullPath(Root);
        var results = new List<string>();
        foreach (var file in Directory.EnumerateFiles(searchRoot, pattern, SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(rootFull, Path.GetFullPath(file));
            results.Add(relative);
        }

        results.Sort(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<string>>(results);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string relativePath)
    {
        var fullPath = ResolvePath(relativePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The single write seam routed through by <see cref="WriteAtomicAsync"/>; writes the tmp file as
    /// UTF-8 without BOM. Tests override this to simulate a mid-write failure.
    /// </summary>
    protected virtual Task WriteFileAsync(string fullPath, string content)
        => File.WriteAllTextAsync(fullPath, content, Utf8NoBom);

    private string ResolvePath(string relativePath)
    {
        // Normalize separators so callers may pass either '/' or '\\'.
        var normalized = relativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(Root, normalized);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; never mask the original failure.
        }
    }
}
