using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pia.Models;

/// <summary>
/// Handles backwards-compatible deserialization of PiiKeywords.
/// Legacy format: ["word1", "word2"] → converts to PiiKeywordEntry with Category "Custom".
/// New format: [{"keyword": "word1", "category": "Person"}, ...] → deserializes directly.
/// </summary>
public class PiiKeywordsJsonConverter : JsonConverter<List<PiiKeywordEntry>>
{
    public override List<PiiKeywordEntry> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new List<PiiKeywordEntry>();

        if (reader.TokenType != JsonTokenType.StartArray)
            return result;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                // Legacy format: plain string
                result.Add(new PiiKeywordEntry { Keyword = reader.GetString()!, Category = "Custom" });
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                string keyword = string.Empty;
                string category = "Custom";

                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        var prop = reader.GetString();
                        reader.Read();
                        if (string.Equals(prop, "keyword", StringComparison.OrdinalIgnoreCase))
                            keyword = reader.GetString() ?? string.Empty;
                        else if (string.Equals(prop, "category", StringComparison.OrdinalIgnoreCase))
                            category = reader.GetString() ?? "Custom";
                    }
                }

                result.Add(new PiiKeywordEntry { Keyword = keyword, Category = category });
            }
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<PiiKeywordEntry> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var entry in value)
        {
            writer.WriteStartObject();
            writer.WriteString("keyword", entry.Keyword);
            writer.WriteString("category", entry.Category);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
