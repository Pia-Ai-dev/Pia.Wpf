using System.Net.Http;

namespace Pia.Services.E2EE;

public interface IDeviceRequestSigner
{
    /// <summary>Signs with this device's key when it holds one and the server issues challenges; otherwise sends unsigned.</summary>
    Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string relativePath, byte[]? jsonBody, CancellationToken ct = default);
}
