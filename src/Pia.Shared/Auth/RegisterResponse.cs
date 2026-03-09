namespace Pia.Shared.Auth;

public class RegisterResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public bool RequiresEmailVerification { get; set; }
}
