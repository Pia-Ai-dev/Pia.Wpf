namespace Pia.Services.Exceptions;

public sealed class LlmTruncatedException : Exception
{
    public string ProviderName { get; }
    public int PartialLength { get; }

    public LlmTruncatedException(string providerName, int partialLength, string? message = null)
        : base(message ?? $"Provider '{providerName}' returned a response truncated by the token limit (partial length: {partialLength}).")
    {
        ProviderName = providerName;
        PartialLength = partialLength;
    }
}
