using Pia.Infrastructure.Vault;
using Pia.Services.Sync;
using Xunit;

namespace Pia.Tests.Sync;

/// <summary>
/// Compile-driven TDD for the section-aware 3-way merge engine (spec §10.1 oracle).
/// Covers every branch of the §10.1 decision table plus §10.2 reassembly.
/// </summary>
public class SectionMergeEngineTests
{
    private const string Id = "6f9c0b3e-7c1a-4f2e-9a8b-000000000001";

    private static readonly SectionMergeEngine Engine = new(new MarkdownVaultParser());

    /// <summary>Build a vault document with the shared id, a given updated timestamp, preamble and body.</summary>
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

    // ---- Disjoint edits: local edits John (Rule 3: only local changed), remote edits Alice
    //      (Rule 2: only remote changed) -> both edits survive, no conflict. (NOT Rule 1 — the two
    //      sections take different rule branches; Rule 1 is the identical-edit/both-add-same case.) ----
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

    // ---- Rule 4b: same-section concurrent edits -> conflict marker + flag ----
    // Exact-byte assertion against the §10.3 layout:
    //   "<<<<<<< local\n" + nl(L) + "=======\n" + nl(R) + ">>>>>>> remote\n"
    // where nl(s) = s if empty or ends with \n, else s + "\n". The fixture bodies deliberately do NOT
    // end in \n (the doc bodies omit the trailing newline), so the nl() insertion is exercised.
    [Fact]
    public void ConcurrentEditSameSection_EmitsConflictMarker_AndFlags()
    {
        var parser = new MarkdownVaultParser();
        // No trailing '\n' after the section content -> parsed body is "- email: john@*.com" (no newline).
        var @base = parser.Parse(Doc("2026-06-07T09:00:00Z", "", "## John\n- email: john@base.com"));
        var local = parser.Parse(Doc("2026-06-07T10:00:00Z", "", "## John\n- email: john@LOCAL.com"));
        var remote = parser.Parse(Doc("2026-06-07T09:30:00Z", "", "## John\n- email: john@REMOTE.com"));

        var result = Engine.Merge(@base, local, remote);

        const string localBody = "- email: john@LOCAL.com";
        const string remoteBody = "- email: john@REMOTE.com";
        // nl() inserts the trailing \n on each side because neither body already ends in \n.
        var expectedConflictBody =
            "<<<<<<< local\n" + localBody + "\n" +
            "=======\n" + remoteBody + "\n" +
            ">>>>>>> remote\n";

        // The merged section is reassembled as "## " + heading + "\n" + body (§10.2). John is the only
        // section, so the conflict body is emitted verbatim right after its heading line.
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

    // ---- Rule for add-on-one-side: remote adds Bob (absent base+local) -> Bob present, no conflict ----
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

    // ---- Rule 2: delete-of-UNCHANGED on remote -> section DROPPED (the plan reference got this wrong) ----
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

    // ---- Rule 4a: edit-vs-delete -> keep the edited (local) side, flag conflict ----
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

    // ---- Rule 3: delete-of-UNCHANGED on LOCAL -> section DROPPED (mirror of the remote-direction
    //      DeleteOfUnchanged test; here remote leaves John byte-identical, local deletes it) ----
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

    // ---- Rule 4a (remote-edit-vs-local-delete direction): base has John, LOCAL deletes it, REMOTE
    //      edits it -> the REMOTE edit is KEPT and the slug is flagged (mirror of EditVsDelete) ----
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

    // ---- Rule 1 (true): both sides ADD the same new section (absent in base) with byte-identical
    //      body -> it appears exactly ONCE, no conflict markers, no flag ----
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

    // ---- Rule 1 (both-absent): slug present in base, deleted byte-identically on BOTH sides
    //      -> dropped, no flag ----
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

    // ---- §10.2 reassembly: remote.updated > local.updated -> remote frontmatter/preamble wins ----
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

    // ---- §10.2 tie-break: equal updated -> local frontmatter/preamble wins ----
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
