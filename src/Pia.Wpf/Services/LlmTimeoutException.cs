namespace Pia.Services;

public sealed class LlmTimeoutException : System.TimeoutException
{
    public string ProviderName { get; }
    public double TimeoutSeconds { get; }

    public LlmTimeoutException(string providerName, double timeoutSeconds, string? message = null)
        : base(message ?? $"Request to provider '{providerName}' timed out after {timeoutSeconds} seconds")
    {
        ProviderName = providerName;
        TimeoutSeconds = timeoutSeconds;
    }
}
