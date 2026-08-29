using System.Net;
using System.Net.Http;
using System.Text;

namespace Pia.Tests.TestInfrastructure;

// Captures the outgoing request so a provider handler's wire format can be asserted, and answers
// every call with the same canned body.
internal sealed class CapturingRequestHandler : HttpMessageHandler
{
    private readonly string _responseBody;

    public Uri? LastRequestUri { get; private set; }

    public string? LastBody { get; private set; }

    public string? LastAuthorization { get; private set; }

    public CapturingRequestHandler(string responseBody = "{}")
    {
        _responseBody = responseBody;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        LastAuthorization = request.Headers.Authorization?.ToString();
        if (request.Content is not null)
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
        };
    }
}
