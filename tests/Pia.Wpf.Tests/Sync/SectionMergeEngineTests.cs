using Pia.Infrastructure.Vault;
using Pia.Services.Sync;
using Xunit;

namespace Pia.Tests.Sync;

public class SectionMergeEngineTests
{
    private const string Id = "6f9c0b3e-7c1a-4f2e-9a8b-000000000001";

    private static readonly SectionMergeEngine Engine = new(new MarkdownVaultParser());

    private static string Doc(string updated, string preamble, string body)
    {
        return
            "---\n" +
            "pia: managed\n" +
            $"id: {Id}\n" +
            "type: contact_list\n" +
            "title: Contacts\n" +
            "created: 2026-06-07T09:00:00Z\n" +
            $"updated: {updated}\n" +
            "schemaVersion: 1\n" +
            "---\n" +
            preamble +
            body;
    }

    [Fact]
    public void DisjointEdits_MergesBoth_NoConflict()
    {
        var parser = new MarkdownVaultParser();
        var @base = parser.Parse(Doc("2026-06-07T09:00:00Z", "",
            "## John\n- email: john@base.com\n\n## Alice\n- email: alice@base.com\n"));
        var local = parser.Parse(Doc("2026-06-07T10:00:00Z", "",
            "## John\n- email: john@LOCAL.com\n\n## Alice\n- email: alice@base.com\n"));
        var remote = parser.Parse(Doc("2026-06-07T09:30:00Z", "",
            "## John\n- email: john@base.com\n\n## Alice\n- email: alice@REMOTE.com\n"));

        var result = Engine.Merge(@base, local, remote);

        Assert.Contains("john@LOCAL.com", result.Text);
        Assert.Contains("alice@REMOTE.com", result.Text);
        Assert.DoesNotContain("<<<<<<< local", result.Text);
        Assert.Empty(result.ConflictedSlugs);
    }

    // The fixture bodies deliberately do NOT end in \n, so the merge's newline insertion on each side is exercised.
    [Fact]
    public void ConcurrentEditSameSection_EmitsConflictMarker_AndFlags()
    {
        var parser = new MarkdownVaultParser();
        var @base = parser.Parse(Doc("2026-06-07T09:00:00Z", "", "## John\n- email: john@base.com"));
        var local = parser.Parse(Doc("2026-06-07T10:00:00Z", "", "## John\n- email: john@LOCAL.com"));
        var remote = parser.Parse(Doc("2026-06-07T09:30:00Z", "", "## John\n- email: john@REMOTE.com"));

        var result = Engine.Merge(@base, local, remote);

        const string localBody = "- email: john@LOCAL.com";
        const string remoteBody = "- email: john@REMOTE.com";
        var expectedConflictBody =
            "<<<<<<< local\n" + localBody + "\n" +
            "=======\n" + remoteBody + "\n" +
            ">>>>>>> remote\n";

        // John is the only section, so the conflict body is emitted verbatim right after its heading line.
        Assert.Contains("## John\n" + expectedConflictBody, result.Text);

        // Ordering: the local marker/body must appear before the remote marker/body.
        var localIdx = result.Text.IndexOf("<<<<<<< local\n", StringComparison.Ordinal);
        var separatorIdx = result.Text.IndexOf("=======\n", StringComparison.Ordinal);
        var remoteIdx = result.Text.IndexOf(">>>>>>> remote\n", StringComparison.Ordinal);
        var localBodyIdx = result.Text.IndexOf(localBody, StringComparison.Ordinal);
        var remoteBodyIdx = result.Text.IndexOf(remoteBody, StringComparison.Ordinal);
        Assert.True(localIdx >= 0 && localIdx < localBodyIdx);
        Assert.True(localBodyIdx < separatorIdx);
        Assert.True(separatorIdx < remoteBodyIdx);
        Assert.True(remoteBodyIdx < remoteIdx);

        Assert.Contains("john", result.ConflictedSlugs);
    }

    [Fact]
    public void AddOnRemoteOnly_KeepsAddedSection()
    {
        var parser = new MarkdownVaultParser();
        var @base = parser.Parse(Doc("2026-06-07T09:00:00Z", "", "## John\n- email: john@base.com\n"));
        var local = parser.Parse(Doc("2026-06-07T09:00:00Z", "", "## John\n- email: john@base.com\n"));
        var remote = parser.Parse(Doc("2026-06-07T10:00:00Z", "",
            "## John\n- email: john@base.com\n\n## Bob\n- email: bob@new.com\n"));

        var result = Engine.Merge(@base, local, remote);

        Assert.Contains("## Bob", result.Text);
        Assert.Contains("bob@new.com", result.Text);
        Assert.Empty(result.ConflictedSlugs);
    }

    [Fact]
    public void DeleteOfUnchanged_DropsSection_NoConflict()
    {
        var parser = new MarkdownVaultParser();
        var @base = parser.Parse(Doc("2026-06-07T09:00:00Z", "",
            "## John\n- email: john@base.com\n\n## Alice\n- email: alice@base.com\n"));
        // local leaves John byte-identical (and Alice too)
        var local = parser.Parse(Doc("2026-06-07T09:00:00Z", "",
            "## John\n- email: john@base.com\n\n## Alice\n- email: alice@base.com\n"));
        // remote deletes John
        var remote = parser.Parse(Doc("2026-06-07T10:00:00Z", "",
            "## Alice\n- email: alice@base.com\n"));

        var result = Engine.Merge(@base, local, remote);

        Assert.DoesNotContain("## John", result.Text);
        Assert.DoesNotContain("john@base.com", result.Text);
        Assert.Contains("## Alice", result.Text);
        Assert.Empty(result.ConflictedSlugs);
    }

    [Fact]
    public void EditVsDelete_KeepsEdit_AndFlags()
    {
        var parser = new MarkdownVaultParser();
        var @base = parser.Parse(Doc("2026-06-07T09:00:00Z", "", "## John\n- email: john@base.com\n"));
        // local edits John
        var local = parser.Parse(Doc("2026-06-07T10:00:00Z", "", "## John\n- email: john@LOCAL.com\n"));
        // remote deletes John (empty body region -> no sections)
        var remote = parser.Parse(Doc("2026-06-07T09:30:00Z", "", ""));

        var result = Engine.Merge(@base, local, remote);

        Assert.Contains("## John", result.Text);
        Assert.Contains("john@LOCAL.com", result.Text);
        Assert.Contains("john", result.ConflictedSlugs);
    }

    [Fact]
    public void LocalDeleteOfUnchanged_DropsSection_NoConflict()
    {
        var parser = new MarkdownVaultParser();
        var @base = parser.Parse(Doc("2026-06-07T09:00:00Z", "",
            "## John\n- email: john@base.com\n\n## Alice\n- email: alice@base.com\n"));
        // local deletes John
        var local = parser.Parse(Doc("2026-06-07T10:00:00Z", "",
            "## Alice\n- email: alice@base.com\n"));
        // remote leaves John (and Alice) byte-identical
        var remote = parser.Parse(Doc("2026-06-07T09:30:00Z", "",
            "## John\n- email: john@base.com\n\n## Alice\n- email: alice@base.com\n"));

        var result = Engine.Merge(@base, local, remote);

        Assert.DoesNotContain("## John", result.Text);
        Assert.DoesNotContain("john@base.com", result.Text);
        Assert.Contains("## Alice", result.Text);
        Assert.Empty(result.ConflictedSlugs);
    }

    [Fact]
    public void DeleteVsEdit_KeepsRemoteEdit_AndFlags()
    {
        var parser = new MarkdownVaultParser();
        var @base = parser.Parse(Doc("2026-06-07T09:00:00Z", "", "## John\n- email: john@base.com\n"));
        // local deletes John (empty body region -> no sections)
        var local = parser.Parse(Doc("2026-06-07T10:00:00Z", "", ""));
        // remote edits John
        var remote = parser.Parse(Doc("2026-06-07T09:30:00Z", "", "## John\n- email: john@REMOTE.com\n"));

        var result = Engine.Merge(@base, local, remote);

        Assert.Contains("## John", result.Text);
        Assert.Contains("john@REMOTE.com", result.Text);
        Assert.Contains("john", result.ConflictedSlugs);
    }

    [Fact]
    public void BothAddSameSection_AppearsOnce_NoConflict()
    {
        var parser = new MarkdownVaultParser();
        // John exists everywhere; Bob is ADDED identically by both local and remote, absent in base.
        var @base = parser.Parse(Doc("2026-06-07T09:00:00Z", "", "## John\n- email: john@base.com\n"));
        var local = parser.Parse(Doc("2026-06-07T10:00:00Z", "",
            "## John\n- email: john@base.com\n\n## Bob\n- email: bob@new.com\n"));
        var remote = parser.Parse(Doc("2026-06-07T09:30:00Z", "",
            "## John\n- email: john@base.com\n\n## Bob\n- email: bob@new.com\n"));

        var result = Engine.Merge(@base, local, remote);

        // Bob appears exactly once (both-add-same collapses to a single section, no marker).
        var firstBob = result.Text.IndexOf("## Bob", StringComparison.Ordinal);
        Assert.True(firstBob >= 0);
        Assert.Equal(-1, result.Text.IndexOf("## Bob", firstBob + 1, StringComparison.Ordinal));
        Assert.Contains("bob@new.com", result.Text);
        Assert.DoesNotContain("<<<<<<< local", result.Text);
        Assert.Empty(result.ConflictedSlugs);
    }

    [Fact]
    public void BothDeleteSameSection_Dropped_NoConflict()
    {
        var parser = new MarkdownVaultParser();
        var @base = parser.Parse(Doc("2026-06-07T09:00:00Z", "",
            "## John\n- email: john@base.com\n\n## Alice\n- email: alice@base.com\n"));
        // both sides delete John, keep Alice byte-identical
        var local = parser.Parse(Doc("2026-06-07T10:00:00Z", "",
            "## Alice\n- email: alice@base.com\n"));
        var remote = parser.Parse(Doc("2026-06-07T09:30:00Z", "",
            "## Alice\n- email: alice@base.com\n"));

        var result = Engine.Merge(@base, local, remote);

        Assert.DoesNotContain("## John", result.Text);
        Assert.Contains("## Alice", result.Text);
        Assert.Empty(result.ConflictedSlugs);
    }

    [Fact]
    public void Reassembly_NewerRemote_TakesRemotePreamble()
    {
        var parser = new MarkdownVaultParser();
        var @base = parser.Parse(Doc("2026-06-07T09:00:00Z", "Base preamble.\n\n",
            "## John\n- email: john@base.com\n"));
        var local = parser.Parse(Doc("2026-06-07T09:30:00Z", "Local preamble.\n\n",
            "## John\n- email: john@base.com\n"));
        var remote = parser.Parse(Doc("2026-06-07T11:00:00Z", "Remote preamble.\n\n",
            "## John\n- email: john@base.com\n"));

        var result = Engine.Merge(@base, local, remote);

        Assert.Contains("Remote preamble.", result.Text);
        Assert.DoesNotContain("Local preamble.", result.Text);
        Assert.Contains("updated: 2026-06-07T11:00:00Z", result.Text);
    }

    [Fact]
    public void Reassembly_EqualUpdated_TakesLocalPreamble()
    {
        var parser = new MarkdownVaultParser();
        var @base = parser.Parse(Doc("2026-06-07T09:00:00Z", "Base preamble.\n\n",
            "## John\n- email: john@base.com\n"));
        var local = parser.Parse(Doc("2026-06-07T10:00:00Z", "Local preamble.\n\n",
            "## John\n- email: john@base.com\n"));
        var remote = parser.Parse(Doc("2026-06-07T10:00:00Z", "Remote preamble.\n\n",
            "## John\n- email: john@base.com\n"));

        var result = Engine.Merge(@base, local, remote);

        Assert.Contains("Local preamble.", result.Text);
        Assert.DoesNotContain("Remote preamble.", result.Text);
    }
}
