using System.Net.Http;
using System.Net.Http.Headers;
using Pia.Services.E2EE;

namespace Pia.Tests.TestInfrastructure;

// Sends every request as the signer does for an old server, for tests that are not about signatures.
internal sealed class UnsignedRequests : IDeviceRequestSigner
{
    public Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string relativePath, byte[]? jsonBody, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(method, relativePath);
        if (jsonBody is not null)
        {
            request.Content = new ByteArrayContent(jsonBody);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }
        return client.SendAsync(request, ct);
    }
}
