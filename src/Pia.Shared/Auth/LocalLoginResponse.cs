namespace Pia.Shared.Auth;

public class LocalLoginResponse
{
    public required string AccessToken { get; set; }
    public required string RefreshToken { get; set; }
    public int ExpiresIn { get; set; }
    public required LocalLoginUser User { get; set; }
}

public class LocalLoginUser
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public required string Provider { get; set; }
}
