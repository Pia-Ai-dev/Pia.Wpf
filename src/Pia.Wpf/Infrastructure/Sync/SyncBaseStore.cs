using System.IO;
using System.Text;

namespace Pia.Infrastructure.Sync;

/// <summary>
/// Persists the per-file last-synced <b>base</b> snapshot used by the section-aware 3-way merge
/// (memory-vault format spec §10). Each Pia-managed file's last reconciled content is retained here,
/// keyed by its frontmatter <c>id</c> GUID (C1), so the next pull can compute <c>merge(base, local,
/// remote)</c> rather than clobbering local edits.
/// <para>
/// Self-contained file I/O under <c>%LOCALAPPDATA%\Pia\SyncBase\&lt;id&gt;.md</c> by default (mirrors
/// <c>SqliteContext</c>/<c>VaultPathProvider</c>'s path pattern); an explicit-root ctor is provided for
/// tests. Lives in Infrastructure (not Services) so it carries no dependency on Pia.Services and the
/// "Store" suffix is outside the Services naming convention — like <c>VaultStore</c>.
/// </para>
/// </summary>
public sealed class SyncBaseStore
{
    // UTF-8 without BOM so the stored base round-trips byte-for-byte (a BOM would corrupt the merge's
    // RawText/splice contract, mirroring VaultStore).
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _root;

    public SyncBaseStore() : this(DefaultRoot())
    {
    }

    /// <summary>Use an explicit root directory (tests pass a temp dir).</summary>
    public SyncBaseStore(string root)
    {
        _root = root;
    }

    private static string DefaultRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Pia", "SyncBase");
    }

    /// <summary>The stored base content for <paramref name="id"/>, or <c>null</c> if none is retained.</summary>
    public async Task<string?> ReadBaseAsync(Guid id)
    {
        var path = PathFor(id);
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, Utf8NoBom);
    }

    /// <summary>Atomically (tmp + move) store <paramref name="content"/> as the base for <paramref name="id"/>.</summary>
    public async Task WriteBaseAsync(Guid id, string content)
    {
        Directory.CreateDirectory(_root);

        var path = PathFor(id);
        var tmpPath = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmpPath, content, Utf8NoBom);
            File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmpPath);
            throw;
        }
    }

    /// <summary>Remove the retained base for <paramref name="id"/> if present.</summary>
    public Task DeleteBaseAsync(Guid id)
    {
        var path = PathFor(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    // Lowercase canonical 8-4-4-4-12 form (C1), matching the on-disk frontmatter id.
    private string PathFor(Guid id) => Path.Combine(_root, id.ToString("D") + ".md");

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
