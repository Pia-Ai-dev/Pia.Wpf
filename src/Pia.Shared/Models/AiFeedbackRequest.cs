using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pia.Shared.Models;

/// <summary>A user's rating or complaint about one Pia Cloud answer — the body of <c>POST /api/ai-feedback</c>.</summary>
public class AiFeedbackRequest
{
    public const int CurrentSchemaVersion = 1;
    public const string RatingUp = "up";
    public const string RatingDown = "down";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Guid MessageId { get; set; }
    public Guid? ChatId { get; set; }

    /// <summary><see cref="RatingUp"/> or <see cref="RatingDown"/>.</summary>
    public string Rating { get; set; } = RatingDown;

    public string? Comment { get; set; }

    /// <summary>Only present when the user ticked "include the answer" in the report dialog.</summary>
    public string? AnswerText { get; set; }

    /// <summary>True when personal data in <see cref="Comment"/> and <see cref="AnswerText"/> was replaced by the client's PII placeholders.</summary>
    public bool PiiTokenized { get; set; }

    public string? Model { get; set; }
    public DateTime AnsweredAt { get; set; }
    public DateTime ReportedAt { get; set; }
    public string? AppVersion { get; set; }
    public string? Locale { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
