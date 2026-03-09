using System.Text.Json;
using System.Text.RegularExpressions;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public partial class StructuredPiiDetector : IPiiDetector
{
    // Known PII field names grouped by category
    private static readonly Dictionary<string, string> FieldCategoryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "Person",
        ["nickname"] = "Person",
        ["email"] = "Email",
        ["phone"] = "Phone",
        ["address"] = "Address",
        ["location"] = "Address",
        ["birthdate"] = "Date",
        ["birthday"] = "Date"
    };

    // Memory types that have PII in structured fields
    private static readonly HashSet<string> PiiMemoryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        MemoryObjectTypes.PersonalProfile,
        MemoryObjectTypes.ContactList
    };

    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\+?[\d\s\-().]{7,20}\d", RegexOptions.Compiled)]
    private static partial Regex PhoneRegex();

    public IReadOnlyList<PiiMatch> DetectPii(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var matches = new List<PiiMatch>();

        foreach (var match in EmailRegex().EnumerateMatches(text))
        {
            matches.Add(new PiiMatch(text.Substring(match.Index, match.Length), "Email", match.Index, match.Length));
        }

        foreach (var match in PhoneRegex().EnumerateMatches(text))
        {
            var value = text.Substring(match.Index, match.Length).Trim();
            // Avoid matching short numeric sequences that aren't phone numbers
            if (value.Count(char.IsDigit) >= 7)
                matches.Add(new PiiMatch(value, "Phone", match.Index, match.Length));
        }

        return matches;
    }

    public IReadOnlyList<PiiMatch> DetectPiiInStructured(string json, string memoryType)
    {
        if (!PiiMemoryTypes.Contains(memoryType))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var matches = new List<PiiMatch>();
            ExtractPiiFromElement(doc.RootElement, matches);
            return matches;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void ExtractPiiFromElement(JsonElement element, List<PiiMatch> matches)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (FieldCategoryMap.TryGetValue(property.Name, out var category))
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            var value = property.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                                matches.Add(new PiiMatch(value, category, 0, value.Length));
                        }
                    }
                    else
                    {
                        // Recurse into nested objects/arrays
                        ExtractPiiFromElement(property.Value, matches);
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ExtractPiiFromElement(item, matches);
                }
                break;
        }
    }
}
