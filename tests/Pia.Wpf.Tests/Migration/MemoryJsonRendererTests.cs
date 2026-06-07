using Pia.Services.Migration;
using Xunit;

namespace Pia.Tests.Migration;

public class MemoryJsonRendererTests
{
    private readonly MemoryJsonRenderer _renderer = new();

    [Fact]
    public void Flat_object_renders_scalar_bullets()
    {
        var body = _renderer.RenderBody("{\"email\":\"a@x\",\"phone\":\"555\"}");

        Assert.Contains("- email: a@x", body);
        Assert.Contains("- phone: 555", body);
    }

    [Fact]
    public void Nested_object_indents_two_spaces_per_level()
    {
        var body = _renderer.RenderBody("{\"address\":{\"city\":\"NYC\",\"zip\":\"10001\"}}");

        Assert.Contains("- address:", body);
        Assert.Contains("  - city: NYC", body);
        Assert.Contains("  - zip: 10001", body);
    }

    [Fact]
    public void Array_values_all_appear()
    {
        var body = _renderer.RenderBody("{\"tags\":[\"a\",\"b\"]}");

        Assert.Contains("a", body);
        Assert.Contains("b", body);
    }

    [Fact]
    public void Lossless_complex_nested_every_leaf_appears()
    {
        // 8 distinct leaf values: strings, numbers, bool, null, nested object, array, array-of-objects.
        const string json = """
            {
              "name": "Ada Lovelace",
              "age": 36,
              "active": true,
              "nickname": null,
              "address": { "city": "London", "postcode": "EC1A" },
              "tags": ["math", "computing"],
              "projects": [
                { "title": "Analytical Engine", "year": 1843 }
              ]
            }
            """;

        var body = _renderer.RenderBody(json);

        foreach (var leaf in new[]
                 {
                     "Ada Lovelace", "36", "true", "null",
                     "London", "EC1A", "math", "computing",
                     "Analytical Engine", "1843"
                 })
        {
            Assert.Contains(leaf, body);
        }
    }

    [Fact]
    public void Embedded_newline_in_scalar_does_not_forge_a_section_heading()
    {
        // A string value with an embedded newline followed by "## ..." must NOT emit a line that the
        // section-boundary regex (^## (.+)$) would treat as a new section, or one record would split.
        var body = _renderer.RenderBody("{\"note\":\"line1\\n## Fake Heading\"}");

        foreach (var line in body.Split('\n'))
        {
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(line.TrimEnd('\r'), "^## (.+)$"),
                $"Line should not match a section boundary: '{line}'");
        }

        // The text content is still present (lossless flattening, not truncation).
        Assert.Contains("line1", body);
        Assert.Contains("Fake Heading", body);
    }

    [Fact]
    public void Unparseable_input_emits_json_fence_with_raw_text()
    {
        const string raw = "{not valid json";

        var body = _renderer.RenderBody(raw);

        Assert.Contains("```json", body);
        Assert.Contains(raw, body);
    }
}
