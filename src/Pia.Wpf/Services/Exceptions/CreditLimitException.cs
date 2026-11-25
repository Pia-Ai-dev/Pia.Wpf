namespace Pia.Services.Exceptions;

public sealed class CreditLimitException(string message) : InvalidOperationException(message);
