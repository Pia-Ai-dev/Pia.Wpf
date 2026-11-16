namespace Pia.Services.E2EE;

/// <summary>The server's recovery copy is unusable, so the recovery code was never tried against it.</summary>
public sealed class RecoveryCopyRejectedException(string message) : InvalidOperationException(message);
