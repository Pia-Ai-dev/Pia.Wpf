using Pia.Shared.Licensing;

namespace Pia.Infrastructure;

// Implements ILicenseErrorBus (defined in Pia.Shared.Licensing) so ViewModels can depend
// on the abstraction without crossing the Infrastructure boundary.

public class LicenseErrorBus : ILicenseErrorBus
{
    private readonly object _gate = new();

    public event EventHandler<LicenseErrorResponse>? OnLicenseError;

    public void Publish(LicenseErrorResponse error)
    {
        ArgumentNullException.ThrowIfNull(error);

        EventHandler<LicenseErrorResponse>? handler;
        lock (_gate)
        {
            handler = OnLicenseError;
        }
        handler?.Invoke(this, error);
    }
}
