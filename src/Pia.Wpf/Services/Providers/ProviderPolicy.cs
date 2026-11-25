using Pia.Models;

namespace Pia.Services.Providers;

/// <summary>The organization's limits on which AI providers this device may use. Pia Cloud is the
/// organization's own server, so it is always allowed.</summary>
public static class ProviderPolicy
{
    public static bool IsAllowed(AiProvider provider, AppSettings? settings) =>
        IsTypeAllowed(provider.ProviderType, settings)
        && IsEndpointAllowed(provider.ProviderType, provider.Endpoint, settings);

    public static bool IsTypeAllowed(AiProviderType type, AppSettings? settings) =>
        type == AiProviderType.PiaCloud
        || settings?.AllowedProviderTypes is not { Count: > 0 } allowed
        || allowed.Any(a => string.Equals(a?.Trim(), type.ToString(), StringComparison.OrdinalIgnoreCase));

    public static bool IsEndpointAllowed(AiProviderType type, string? endpoint, AppSettings? settings)
    {
        if (type == AiProviderType.PiaCloud || settings?.AllowedProviderEndpoints is not { Count: > 0 } patterns)
            return true;

        return Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri)
            && patterns.Any(p => HostMatches(uri.Host, p));
    }

    // "*.example.com" covers the subdomains of example.com, not example.com itself; anything else is an exact
    // host. A pattern written as a URL is reduced to its host.
    internal static bool HostMatches(string host, string? pattern)
    {
        var p = pattern?.Trim().TrimEnd('.') ?? string.Empty;
        if (p.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(p, UriKind.Absolute, out var asUri))
            p = asUri.Host;
        if (p.Length == 0)
            return false;

        return p.StartsWith("*.", StringComparison.Ordinal)
            ? host.Length > p.Length - 1 && host.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase)
            : string.Equals(host, p, StringComparison.OrdinalIgnoreCase);
    }
}
