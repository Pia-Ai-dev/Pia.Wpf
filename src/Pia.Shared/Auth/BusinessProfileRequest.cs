namespace Pia.Shared.Auth;

/// <summary>
/// Completes the trader declaration for an account that skipped the registration form — single
/// sign-on creates the account straight from the identity provider, so § 14 BGB is never asked there.
/// The account is taken from the token, never from this body.
/// </summary>
public class BusinessProfileRequest
{
    public required string CompanyName { get; set; }

    /// <summary>Must be true; the explicit declaration is the point of the call.</summary>
    public bool ActingAsBusiness { get; set; }
}
