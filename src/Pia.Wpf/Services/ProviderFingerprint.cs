using Pia.Models;

namespace Pia.Services;

// Content-based identity for AI providers used to dedupe across sync.
//
// Two providers from different devices may carry different Guids but represent
// the same logical upstream connection (same type, endpoint, model). We use a
// normalized tuple of (ProviderType | endpoint | model | deployment) as the
// dedupe key. Name is intentionally NOT part of the fingerprint — users may
// legitimately keep two providers with the same name but different models.
//
// PiaCloud has a fixed well-known Id and is excluded from fingerprint dedupe.
public static class ProviderFingerprint
{
    public const string PiaCloudSentinel = "piacloud";

    public static string Compute(AiProvider provider)
    {
        if (provider.Id == ProviderService.PiaCloudProviderId
            || provider.ProviderType == AiProviderType.PiaCloud)
        {
            return PiaCloudSentinel;
        }

        var endpoint = Normalize(provider.Endpoint);
        var model = Normalize(provider.ModelName);
        var deployment = Normalize(provider.AzureDeploymentName);

        return $"{(int)provider.ProviderType}|{endpoint}|{model}|{deployment}";
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Trim().TrimEnd('/').ToLowerInvariant();
    }
}
