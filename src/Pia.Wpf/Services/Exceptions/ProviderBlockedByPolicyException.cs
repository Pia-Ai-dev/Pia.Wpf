namespace Pia.Services.Exceptions;

/// <summary>Thrown before any request to a provider the organization's policy does not allow.</summary>
public sealed class ProviderBlockedByPolicyException : InvalidOperationException
{
    public ProviderBlockedByPolicyException()
        : base("Your organization's policy does not allow this AI provider.")
    {
    }
}
