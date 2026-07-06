using Pia.Infrastructure.Vault;
using Xunit;

namespace Pia.Tests.Vault;

public class VaultPathsTests
{
    // Housekeeping/scaffolding files and the immutable RAW layer are NOT user-facing memory records:
    // GUARD 2 and the view list must ignore them (Task A.1 / migration populated-vault bug).
    [Theory]
    [InlineData("memory/AGENTS.md")]
    [InlineData("memory/index.md")]
    [InlineData("memory/log.md")]
    [InlineData("memory/.archive/3f2a.json")]
    [InlineData("memory/.archive/note.md")]
    [InlineData("sources/raw.md")]
    [InlineData("memory/profile.txt")]
    public void IsRecordFile_is_false_for_non_records(string relativePath)
    {
        Assert.False(VaultPaths.IsRecordFile(relativePath));
    }

    // Real record locations: the three structured documents and the per-type freeform files.
    [Theory]
    [InlineData("memory/profile.md")]
    [InlineData("memory/contacts.md")]
    [InlineData("memory/preferences.md")]
    [InlineData("memory/notes/foo.md")]
    [InlineData("memory/projects/acme.md")]
    [InlineData("memory/topics/widgets.md")]
    // Edge case: a user note whose heading slugifies to "index" must NOT be mistaken for the catalog,
    // because housekeeping is matched by EXACT relative path, not bare basename.
    [InlineData("memory/notes/index.md")]
    public void IsRecordFile_is_true_for_records(string relativePath)
    {
        Assert.True(VaultPaths.IsRecordFile(relativePath));
    }

    // EnumerateAsync returns OS-separator paths on Windows; the predicate must normalize separators.
    [Fact]
    public void IsRecordFile_normalizes_backslash_separators()
    {
        Assert.True(VaultPaths.IsRecordFile(@"memory\notes\foo.md"));
        Assert.False(VaultPaths.IsRecordFile(@"memory\AGENTS.md"));
    }

    // Recall must NOT index Pia's housekeeping documents nor the recoverable .archive/ snapshots.
    [Theory]
    [InlineData("memory/AGENTS.md")]
    [InlineData("memory/index.md")]
    [InlineData("memory/log.md")]
    [InlineData("memory/.archive/note.md")]
    [InlineData(@"memory\.archive\note.md")]
    [InlineData("memory/profile.txt")] // not markdown
    public void IsRecallIndexable_is_false_for_housekeeping_archive_and_non_md(string relativePath)
    {
        Assert.False(VaultPaths.IsRecallIndexable(relativePath));
    }

    // Recall DOES index memory records AND the sources/ RAW layer (ingest made it recallable) — unlike
    // IsRecordFile, which excludes sources/. Records may also sit at the vault root.
    [Theory]
    [InlineData("memory/profile.md")]
    [InlineData("memory/notes/foo.md")]
    [InlineData("sources/business plan.md")]
    [InlineData("sources/refs/spec.md")]
    [InlineData("profile.md")] // a record at the vault root
    // A user note whose heading slugifies to "index" is a record, not the catalog (exact-path match).
    [InlineData("memory/notes/index.md")]
    public void IsRecallIndexable_is_true_for_records_and_sources(string relativePath)
    {
        Assert.True(VaultPaths.IsRecallIndexable(relativePath));
    }
}
