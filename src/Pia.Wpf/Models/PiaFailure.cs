using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pia.Models;

/// <summary>Which part of the system failed, so a card can name it instead of showing bare prose.</summary>
public enum FailureLayer
{
    /// <summary>Nothing better is known. Renders as the plain reason, exactly as before this type existed.</summary>
    Unclassified,
    App,
    Workspace,
    Provider,
    Endpoint,
    Tool,
    Cancelled,
}

/// <summary>
/// Travels ALONGSIDE the free-text failure reason, never instead of it — an unmapped message must still
/// reach the card unchanged.
/// </summary>
/// <param name="SafeToReRun">
/// Provably nothing spent and nothing written, which is <c>ScheduledJobService.IsPreModelFailure</c>'s
/// meaning — NOT "the call might succeed if repeated". A provider fault mid-run is the second and not the
/// first, and re-dispatching one duplicates whatever the run already wrote.
/// </param>
public sealed record PiaFailure(FailureLayer Layer, string Code, bool SafeToReRun)
{
    // The codec lives on the type so a writer and a reader cannot disagree about casing — they already did
    // once, and a mismatch reads as "no layer" rather than as an error.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Null for absent, unparseable, or written by a future version with a layer this one lacks.</summary>
    public static PiaFailure? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<PiaFailure>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
