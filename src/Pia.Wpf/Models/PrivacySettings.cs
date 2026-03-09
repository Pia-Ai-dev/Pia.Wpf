using System.Text.Json.Serialization;

namespace Pia.Models;

public class PrivacySettings
{
    public bool TokenizationEnabled { get; set; } = true;

    [JsonConverter(typeof(PiiKeywordsJsonConverter))]
    public List<PiiKeywordEntry> PiiKeywords { get; set; } = new();
}
