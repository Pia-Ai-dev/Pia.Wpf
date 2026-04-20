namespace Pia.Shared.Licensing;

/// <summary>
/// In-process event channel for license-error responses. Publishers include the
/// HTTP pipeline handler and the OAuth callback listener; subscribers render
/// user-facing notifications and degrade affected features.
/// </summary>
public interface ILicenseErrorBus
{
    event EventHandler<LicenseErrorResponse>? OnLicenseError;

    void Publish(LicenseErrorResponse error);
}
