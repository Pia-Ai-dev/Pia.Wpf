using System.Text;
using System.Text.Json;

namespace Pia.Services.Migration;

/// <summary>
/// Converts a <see cref="Pia.Models.MemoryObject"/> JSON <c>Data</c> payload into vault
/// markdown body text. The rendering is lossless: every JSON leaf value appears in the
/// output. Unparseable or genuinely irregular payloads fall back to a verbatim fenced
/// <c>json</c> block so no information is dropped.
/// </summary>
public sealed class MemoryJsonRenderer
{
    private const string Indent = "  ";

    /// <summary>
    /// Renders <paramref name="json"/> as markdown bullet body text. On parse failure the
    /// raw input is preserved verbatim inside a fenced <c>json</c> block.
    /// </summary>
    public string RenderBody(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return RenderFence(json);
        }

        using (document)
        {
            var builder = new StringBuilder();
            var root = document.RootElement;

            switch (root.ValueKind)
            {
                case JsonValueKind.Object:
                    RenderObject(root, depth: 0, builder);
                    break;
                case JsonValueKind.Array:
                    RenderArray(root, depth: 0, builder);
                    break;
                default:
                    // Top-level scalar -> single line.
                    builder.Append(ScalarText(root));
                    break;
            }

            var text = builder.ToString().TrimEnd('\n');
            return text.Length == 0 ? RenderFence(json) : text;
        }
    }

    private void RenderObject(JsonElement element, int depth, StringBuilder builder)
    {
        var pad = new string(' ', depth * Indent.Length);
        foreach (var property in element.EnumerateObject())
        {
            if (IsScalar(property.Value))
            {
                builder.Append(pad).Append("- ").Append(property.Name).Append(": ")
                    .Append(ScalarText(property.Value)).Append('\n');
            }
            else
            {
                builder.Append(pad).Append("- ").Append(property.Name).Append(':').Append('\n');
                RenderContainer(property.Value, depth + 1, builder);
            }
        }
    }

    private void RenderArray(JsonElement element, int depth, StringBuilder builder)
    {
        var pad = new string(' ', depth * Indent.Length);
        foreach (var item in element.EnumerateArray())
        {
            if (IsScalar(item))
            {
                builder.Append(pad).Append("- ").Append(ScalarText(item)).Append('\n');
            }
            else
            {
                // Each non-scalar element becomes its own indented bullet group.
                builder.Append(pad).Append("-").Append('\n');
                RenderContainer(item, depth + 1, builder);
            }
        }
    }

    private void RenderContainer(JsonElement element, int depth, StringBuilder builder)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            RenderObject(element, depth, builder);
        }
        else
        {
            RenderArray(element, depth, builder);
        }
    }

    private static bool IsScalar(JsonElement element) =>
        element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array);

    private static string ScalarText(JsonElement element) => element.ValueKind switch
    {
        // Strings emitted as-is (no surrounding quotes), but with embedded line breaks flattened so a
        // value can never start a new line — see <see cref="FlattenLineBreaks"/>.
        JsonValueKind.String => FlattenLineBreaks(element.GetString() ?? string.Empty),
        // Numbers/bools/null rendered as their JSON text.
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText(),
    };

    /// <summary>
    /// Collapse embedded line breaks (<c>\r\n</c>, <c>\n</c>, <c>\r</c>) in a scalar string value to a
    /// single space. This keeps each rendered scalar on ONE physical line so an embedded newline followed
    /// by content (e.g. <c>"line1\n## Heading"</c>) can never emit a line that matches the vault's
    /// section-boundary regex <c>^## (.+)$</c> and split one record into spurious sections. A single
    /// space (rather than the literal <c>\n</c> escape) is used so the text reads naturally; every
    /// character of the original value is still present in the output.
    /// </summary>
    private static string FlattenLineBreaks(string value)
    {
        if (value.IndexOf('\n') < 0 && value.IndexOf('\r') < 0)
        {
            return value;
        }

        return value
            .Replace("\r\n", " ")
            .Replace('\n', ' ')
            .Replace('\r', ' ');
    }

    private static string RenderFence(string raw) =>
        new StringBuilder()
            .Append("```json").Append('\n')
            .Append(raw).Append('\n')
            .Append("```")
            .ToString();
}
