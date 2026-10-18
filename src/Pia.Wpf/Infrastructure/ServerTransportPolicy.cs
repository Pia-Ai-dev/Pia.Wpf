using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Pia.Infrastructure;

/// <summary>Transport rules for the configured Pia server; a relaxed certificate check never reaches another host.</summary>
public static class ServerTransportPolicy
{
    public static Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>? ForServer(
        bool trustSelfSignedCertificates, string? serverUrl)
    {
        if (!trustSelfSignedCertificates || !TryParseServer(serverUrl, out var server))
            return null;

        return (request, _, _, errors) => errors == SslPolicyErrors.None || IsServer(request.RequestUri, server);
    }

    public static bool IsServer(Uri? requestUri, Uri server)
        => requestUri is { IsAbsoluteUri: true }
           && requestUri.Scheme == Uri.UriSchemeHttps
           && string.Equals(requestUri.IdnHost, server.IdnHost, StringComparison.OrdinalIgnoreCase)
           && requestUri.Port == server.Port;

    public static bool IsPlaintextRemote(string? serverUrl)
        => Uri.TryCreate(serverUrl, UriKind.Absolute, out var server)
           && server.Scheme == Uri.UriSchemeHttp
           && !server.IsLoopback;

    private static bool TryParseServer(string? serverUrl, out Uri server)
    {
        server = null!;
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
            return false;

        server = parsed;
        return true;
    }
}
