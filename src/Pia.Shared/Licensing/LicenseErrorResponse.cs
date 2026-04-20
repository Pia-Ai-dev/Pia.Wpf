using System.Text.Json.Serialization;

namespace Pia.Shared.Licensing;

/// <summary>
/// Server-side 403 payload raised by Community Edition license gates:
/// <c>no_license</c> (server in setup mode), <c>feature_not_licensed</c> (feature disabled),
/// <c>user_limit_reached</c> (seat cap hit during OAuth).
/// </summary>
public class LicenseErrorResponse
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    [JsonPropertyName("feature")]
    public string? Feature { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("setupUrl")]
    public string? SetupUrl { get; set; }
}

public static class LicenseErrorKeys
{
    public const string NoLicense = "no_license";
    public const string FeatureNotLicensed = "feature_not_licensed";
    public const string UserLimitReached = "user_limit_reached";

    public static bool IsKnown(string? key) =>
        key is NoLicense or FeatureNotLicensed or UserLimitReached;
}
