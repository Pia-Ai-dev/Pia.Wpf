namespace Pia.Shared.Auth;

/// <summary>Re-check sent alongside a protected call's own body; a code field only when MFA is on.</summary>
public sealed record ReauthenticationFields(string? Password, string? Code = null, string? RecoveryCode = null);

/// <summary>Values of the <c>error</c> field when a protected call's re-check refuses.</summary>
public static class ReauthenticationErrorCodes
{
    public const string InvalidPassword = "invalid_password";
    public const string SecondFactorRequired = "second_factor_required";
    public const string CodeAlreadyUsed = "code_already_used";
    public const string InvalidCode = "invalid_code";
    public const string AccountLocked = "account_locked";
    public const string RateLimited = "rate_limited";
    public const string Unavailable = "reauthentication_unavailable";

    /// <summary>True for a 401 that is a re-check answer, never a sign that the session ended.</summary>
    public static bool IsRecheckAnswer(string? code) =>
        code is InvalidPassword or SecondFactorRequired or CodeAlreadyUsed or InvalidCode;
}
