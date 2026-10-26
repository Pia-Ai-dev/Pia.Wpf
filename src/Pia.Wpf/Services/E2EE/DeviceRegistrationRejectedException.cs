namespace Pia.Services.E2EE;

/// <summary>Device registration was refused; <see cref="Exception.Message"/> is already readable.</summary>
public sealed class DeviceRegistrationRejectedException(string code, string message) : Exception(message)
{
    /// <summary>The server's <c>error</c> value (an <see cref="Pia.Shared.E2EE.E2EEErrorCodes"/> member).</summary>
    public string Code { get; } = code;
}
