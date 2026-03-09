namespace Pia.Shared.Auth;

public class LocalLoginRequest
{
    public required string Email { get; set; }
    public required string Password { get; set; }
}
