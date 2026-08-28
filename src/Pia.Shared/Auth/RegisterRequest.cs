namespace Pia.Shared.Auth;

public class RegisterRequest
{
    public required string Email { get; set; }
    public required string Password { get; set; }
    public string? DisplayName { get; set; }

    /// <summary>
    /// Defaults to <see cref="CustomerType.Tbd"/> so older clients keep registering; servers that
    /// set <c>LocalAuth:RequireBusinessDeclaration</c> reject that value instead.
    /// </summary>
    public CustomerType CustomerType { get; set; } = CustomerType.Tbd;

    /// <summary>Required alongside <see cref="CustomerType.Business"/> where the declaration is enforced.</summary>
    public string? CompanyName { get; set; }
}
