namespace Pia.Services.E2EE;

/// <summary>Keeps the server's explanation separate from the safe exception message.</summary>
public sealed class DeviceRegistrationRejectedException(string code, string userMessage, int? statusCode = null)
    : Exception($"Device registration was refused (HTTP status: {statusCode}).")
{
    public string Code { get; } = code;
    public string UserMessage { get; } = userMessage;
    public int? StatusCode { get; } = statusCode;
}
