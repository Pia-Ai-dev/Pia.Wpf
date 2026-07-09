using System.Globalization;
using Pia.Converters;
using Xunit;

namespace Pia.Tests.Converters;

/// <summary>
/// The Memory inspector rewrites Obsidian <c>[[target]]</c> wikilinks into clickable <c>pia-memory:</c>
/// markdown links for display. The rewrite must map targets/aliases faithfully, leave non-wikilink content
/// (including single-bracket placeholder tokens) untouched, and never touch the stored body.
/// </summary>
public class WikiLinkConverterTests
{
    private static string Convert(string input) =>
        (string)new WikiLinkConverter().Convert(input, typeof(string), null!, CultureInfo.InvariantCulture)!;

    [Fact]
    public void Rewrites_bare_wikilink_to_pia_memory_link()
    {
        Assert.Equal(
            "see [topics/foo-bar](pia-memory:topics/foo-bar) for more",
            Convert("see [[topics/foo-bar]] for more"));
    }

    [Fact]
    public void Alias_form_uses_the_label_as_link_text_and_target_as_destination()
    {
        Assert.Equal(
            "[Foo Bar](pia-memory:topics/foo-bar)",
            Convert("[[topics/foo-bar|Foo Bar]]"));
    }

    [Fact]
    public void Rewrites_multiple_wikilinks_in_one_string()
    {
        Assert.Equal(
            "[topics/a](pia-memory:topics/a) and [topics/b](pia-memory:topics/b)",
            Convert("[[topics/a]] and [[topics/b]]"));
    }

    [Fact]
    public void Trims_inner_whitespace_around_target_and_alias()
    {
        Assert.Equal(
            "[Label](pia-memory:topics/foo)",
            Convert("[[ topics/foo | Label ]]"));
    }

    [Fact]
    public void Leaves_single_bracket_placeholder_tokens_untouched()
    {
        // The privacy placeholders (e.g. [Person_1]) are single-bracket and must survive verbatim.
        const string text = "Met [Person_1] at [Email_2] yesterday.";
        Assert.Equal(text, Convert(text));
    }

    [Fact]
    public void Leaves_content_without_wikilinks_untouched()
    {
        const string plain = "Plain prose with a [link](https://example.com).";
        Assert.Equal(plain, Convert(plain));
    }

    [Fact]
    public void Does_not_span_newlines()
    {
        // A dangling "[[" must not swallow the rest of the document across a line break.
        const string text = "start [[ not closed\nnext line ]] end";
        Assert.Equal(text, Convert(text));
    }

    [Fact]
    public void Leaves_wikilink_with_unsafe_target_unchanged()
    {
        // A target containing whitespace would break the generated markdown destination — left as-is.
        Assert.Equal("[[has space]]", Convert("[[has space]]"));
    }

    [Fact]
    public void Leaves_wikilink_inside_inline_code_untouched()
    {
        // A page documenting the vault's own [[...]] syntax (or a token like [[nodiscard]]) in code must
        // render the literal text, matching Obsidian.
        Assert.Equal("use `[[topics/foo]]` to link", Convert("use `[[topics/foo]]` to link"));
        Assert.Equal("the `[[nodiscard]]` attribute", Convert("the `[[nodiscard]]` attribute"));
    }

    [Fact]
    public void Leaves_wikilink_inside_fenced_block_untouched()
    {
        const string text = "```\nsee [[topics/foo]]\n```";
        Assert.Equal(text, Convert(text));
    }

    [Fact]
    public void Converts_wikilink_outside_code_even_when_code_is_present()
    {
        // The real link (outside code) converts; the one inside the inline span is protected.
        Assert.Equal(
            "[topics/a](pia-memory:topics/a) and `[[topics/b]]`",
            Convert("[[topics/a]] and `[[topics/b]]`"));
    }

    [Fact]
    public void Passes_through_null_and_empty()
    {
        var sut = new WikiLinkConverter();
        Assert.Null(sut.Convert(null, typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, sut.Convert(string.Empty, typeof(string), null!, CultureInfo.InvariantCulture));
    }
}
