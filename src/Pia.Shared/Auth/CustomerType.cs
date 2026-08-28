namespace Pia.Shared.Auth;

/// <summary>
/// Whether an account holder acts as a trader (§ 14 BGB) or a consumer (§ 13 BGB).
/// Pia Cloud is offered to traders only, and § 13 BGB attaches to the actual role rather than
/// to the terms — so the declaration has to be collected, not assumed.
/// </summary>
public enum CustomerType
{
    /// <summary>
    /// Not declared. The value for accounts that predate the question and for SSO sign-ups,
    /// which never see the registration form.
    /// </summary>
    Tbd = 0,
    Business = 1,
    Consumer = 2
}
