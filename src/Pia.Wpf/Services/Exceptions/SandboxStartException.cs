namespace Pia.Services.Exceptions;

/// <summary>A sandboxed server was refused or could not start; the message is written for the user.</summary>
public sealed class SandboxStartException : InvalidOperationException
{
    public SandboxStartException(string message) : base(message)
    {
    }

    public SandboxStartException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
