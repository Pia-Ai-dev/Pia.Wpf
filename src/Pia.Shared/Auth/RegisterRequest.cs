namespace Pia.Shared.Auth;

public class RegisterRequest
{
    public required string Email { get; set; }
    public required string Password { get; set; }
    public string? DisplayName { get; set; }
}
