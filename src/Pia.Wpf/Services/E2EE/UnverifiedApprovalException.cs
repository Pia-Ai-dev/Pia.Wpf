namespace Pia.Services.E2EE;

/// <summary>The server handed over a key no active device of the account signed for this device.</summary>
public sealed class UnverifiedApprovalException(string message) : InvalidOperationException(message);
