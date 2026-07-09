using System.Collections.Generic;
using Pia.Infrastructure.Vault;
using Xunit;

namespace Pia.Tests.Vault;

/// <summary>
/// Unit tests for <see cref="WikiLinkReconciler"/> — the deterministic backstop that guarantees an ingested
/// topic-page body contains only wikilinks that resolve: kept links are canonicalized to the exact on-disk
/// slug, dangling links are stripped to plain text, and code spans / single-bracket placeholders are left
/// verbatim.
/// </summary>
public class WikiLinkReconcilerTests
{
    private static IReadOnlySet<string> Known(params string[] slugs)
        => new HashSet<string>(slugs);

    private static string Reconcile(string body, params string[] known)
        => WikiLinkReconciler.Reconcile(body, Known(known));

    [Fact]
    public void Dangling_link_without_label_is_stripped_to_humanized_text()
        => Assert.Equal(
            "Acme Corp partners with Globex Corp on logistics.",
            Reconcile("Acme Corp partners with [[topics/globex-corp]] on logistics.", "acme-corp"));

    [Fact]
    public void Dangling_link_with_label_is_stripped_to_its_label()
        => Assert.Equal(
            "See Globex Inc for more.",
            Reconcile("See [[topics/globex|Globex Inc]] for more.", "acme-corp"));

    [Fact]
    public void Existing_link_is_kept_verbatim()
        => Assert.Equal(
            "See [[topics/acme-corp]] here.",
            Reconcile("See [[topics/acme-corp]] here.", "acme-corp"));

    [Fact]
    public void Existing_link_preserves_its_label()
        => Assert.Equal(
            "[[topics/acme-corp|Acme]]",
            Reconcile("[[topics/acme-corp|Acme]]", "acme-corp"));

    [Fact]
    public void Accented_slug_drift_is_canonicalized_to_the_filename_slug()
        => Assert.Equal(
            "Meet at [[topics/cafe]].",
            Reconcile("Meet at [[topics/Café]].", "cafe"));

    [Fact]
    public void Punctuation_and_spacing_drift_is_canonicalized()
        => Assert.Equal(
            "[[topics/acme-corp]]",
            Reconcile("[[topics/Acme Corp]]", "acme-corp"));

    [Fact]
    public void Drifted_link_with_label_is_canonicalized_and_keeps_its_label()
        => Assert.Equal(
            "[[topics/cafe|the café]]",
            Reconcile("[[topics/Café|the café]]", "cafe"));

    [Fact]
    public void Bare_form_gets_the_topics_prefix_when_kept()
        => Assert.Equal(
            "[[topics/acme-corp]]",
            Reconcile("[[acme-corp]]", "acme-corp"));

    [Fact]
    public void Bare_form_is_stripped_when_missing()
        => Assert.Equal("Ghost", Reconcile("[[ghost]]"));

    [Fact]
    public void Leading_slash_is_trimmed_before_matching()
        => Assert.Equal(
            "[[topics/acme-corp]]",
            Reconcile("[[/topics/acme-corp]]", "acme-corp"));

    [Fact]
    public void Inline_code_span_is_left_verbatim()
        => Assert.Equal(
            "Use `[[topics/foo]]` to link.",
            Reconcile("Use `[[topics/foo]]` to link.", "acme-corp"));

    [Fact]
    public void Fenced_code_block_is_left_verbatim()
    {
        const string body = "Example:\n```\n[[topics/foo]]\n```\ndone.";
        Assert.Equal(body, Reconcile(body, "acme-corp"));
    }

    [Fact]
    public void Tilde_fenced_code_block_is_left_verbatim()
    {
        const string body = "Example:\n~~~\n[[topics/foo]]\n~~~\ndone.";
        Assert.Equal(body, Reconcile(body, "acme-corp"));
    }

    [Fact]
    public void Natural_language_dangling_target_keeps_its_own_words()
        => Assert.Equal(
            "Works at AT&T now.",
            Reconcile("Works at [[AT&T]] now.", "acme-corp"));

    [Fact]
    public void Single_bracket_placeholder_is_untouched()
        => Assert.Equal(
            "Contact [Person_1] at [Email_2] today.",
            Reconcile("Contact [Person_1] at [Email_2] today.", "acme-corp"));

    [Fact]
    public void Empty_known_set_strips_every_link()
        => Assert.Equal(
            "A and B here.",
            Reconcile("[[topics/a]] and [[topics/b|B]] here."));

    [Fact]
    public void Mixed_body_keeps_existing_and_strips_dangling()
        => Assert.Equal(
            "[[topics/acme-corp]] met Ghost Partner.",
            Reconcile("[[topics/acme-corp]] met [[topics/ghost-partner]].", "acme-corp"));

    [Fact]
    public void Empty_body_is_returned_unchanged()
        => Assert.Equal(string.Empty, Reconcile(string.Empty, "acme-corp"));
}
