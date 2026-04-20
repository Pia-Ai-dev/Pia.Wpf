using System.Net.Http;
using Pia.Shared.Licensing;

namespace Pia.Infrastructure;

/// <summary>
/// Sniffs every HTTP response for the Community Edition license-error JSON shapes and
/// forwards matches to <see cref="ILicenseErrorBus"/>. Never mutates or swallows the
/// response — the original payload still reaches the calling service.
/// </summary>
public class LicenseErrorHandler : DelegatingHandler
{
    private readonly ILicenseErrorBus _bus;

    public LicenseErrorHandler(ILicenseErrorBus bus)
    {
        _bus = bus;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        var licenseError = await LicenseErrorParser.TryParseAsync(response, cancellationToken);
        if (licenseError is not null)
        {
            _bus.Publish(licenseError);
        }

        return response;
    }
}
