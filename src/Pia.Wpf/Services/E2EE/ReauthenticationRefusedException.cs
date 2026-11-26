namespace Pia.Services.E2EE;

/// <summary>The server's password (or code) re-check refused a protected call; the session itself is still valid.</summary>
public sealed class ReauthenticationRefusedException(string code)
    : InvalidOperationException("The server refused the password confirmation.")
{
    public string Code { get; } = code;
}
