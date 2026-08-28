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

    /// <summary>
    /// The account still owes its trader declaration — single sign-on skips the registration form.
    /// Until it is supplied, the server answers everything but the auth surface with 403.
    /// </summary>
    public bool RequiresBusinessProfile { get; set; }
}
