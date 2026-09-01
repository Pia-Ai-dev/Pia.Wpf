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
    [InlineData("memory/templates.md")]
    [InlineData("memory/.archive/3f2a.json")]
    [InlineData("memory/.archive/note.md")]
    [InlineData("sources/raw.md")]
    [InlineData("memory/profile.txt")]
    public void IsRecordFile_is_false_for_non_records(string relativePath)
    {
        Assert.False(VaultPaths.IsRecordFile(relativePath));
    }

    // A foreign dot-folder under memory/ is not a record however it got there. Sharper than tidiness:
    // the migration's populated-vault guard counts records, so one Syncthing version file would make an
    // empty vault look populated and strand the legacy JSON. Kept in step with IsRecallIndexable.
    [Theory]
    [InlineData("memory/.stversions/profile~20260101.md")]
    [InlineData("memory/.obsidian/plugins/some-plugin/README.md")]
    [InlineData("memory/.trash/deleted-note.md")]
    [InlineData("memory/notes/.stversions/foo.md")]
    [InlineData("memory/.draft.md")]
    public void IsRecordFile_is_false_under_any_dot_prefixed_segment(string relativePath)
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

    // Recall must NOT index Pia's housekeeping documents, the recoverable .archive/ snapshots, nor the
    // raw sources/ layer — raw ingest inputs (any extension, including .md) reach recall only via their
    // synthesized memory/topics/ pages, so a sources/*.md is not embedded raw or duplicated.
    [Theory]
    [InlineData("memory/AGENTS.md")]
    [InlineData("memory/index.md")]
    [InlineData("memory/log.md")]
    [InlineData("memory/templates.md")]
    [InlineData("memory/.archive/note.md")]
    [InlineData(@"memory\.archive\note.md")]
    [InlineData("sources/business plan.md")]
    [InlineData("sources/refs/spec.md")]
    [InlineData(@"sources\refs\spec.md")]
    [InlineData("memory/profile.txt")] // not markdown
    public void IsRecallIndexable_is_false_for_housekeeping_archive_sources_and_non_md(string relativePath)
    {
        Assert.False(VaultPaths.IsRecallIndexable(relativePath));
    }

    // Other tools drop their own dot-folders straight into the vault root — Obsidian's config and its
    // own deleted-note trash, a stray .git if the vault is ever git-initialized, Syncthing's version
    // history — none of that is a memory record, however it got a .md extension.
    [Theory]
    [InlineData(".obsidian/plugins/some-plugin/README.md")]
    [InlineData(".trash/deleted-note.md")]
    [InlineData(@".trash\deleted-note.md")]
    [InlineData(".git/COMMIT_EDITMSG.md")]
    [InlineData("memory/.stversions/profile~20260101.md")]
    public void IsRecallIndexable_is_false_under_any_dot_prefixed_folder(string relativePath)
    {
        Assert.False(VaultPaths.IsRecallIndexable(relativePath));
    }

    // Recall DOES index memory records; unlike IsRecordFile these may also sit at the vault root.
    [Theory]
    [InlineData("memory/profile.md")]
    [InlineData("memory/notes/foo.md")]
    [InlineData("memory/topics/widgets.md")]
    [InlineData("profile.md")] // a record at the vault root
    // A user note whose heading slugifies to "index" is a record, not the catalog (exact-path match).
    [InlineData("memory/notes/index.md")]
    public void IsRecallIndexable_is_true_for_records(string relativePath)
    {
        Assert.True(VaultPaths.IsRecallIndexable(relativePath));
    }
}
