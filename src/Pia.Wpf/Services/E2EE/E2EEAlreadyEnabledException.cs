namespace Pia.Services.E2EE;

/// <summary>The server reports end-to-end encryption already on for the account, so this device has to join it.</summary>
public sealed class E2EEAlreadyEnabledException(string message) : InvalidOperationException(message);
