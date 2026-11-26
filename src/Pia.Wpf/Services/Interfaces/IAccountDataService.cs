using System.IO;
using Pia.Shared.Auth;

namespace Pia.Services.Interfaces;

public interface IAccountDataService
{
    /// <summary>Streams the server's ZIP export of the signed-in account into <paramref name="destination"/>.</summary>
    Task ExportAsync(Stream destination, CancellationToken ct = default);

    /// <summary>Writes the export to <paramref name="path"/>; a failed export leaves the path as it was.</summary>
    Task ExportToFileAsync(string path, CancellationToken ct = default);

    /// <summary>Asks the server to delete the signed-in account. Only local accounts send a re-check.</summary>
    Task<AccountDeletionOutcome> DeleteAsync(ReauthenticationFields? reauth, CancellationToken ct = default);
}

// Failed comes first so an unset outcome never reads as a deleted account.
public enum AccountDeletionOutcome
{
    Failed,
    InvalidPassword,
    Deleted,
    SecondFactorRequired,
    CodeAlreadyUsed,
    InvalidCode,
    AccountLocked,
    RateLimited,
}

public static class AccountDeletionOutcomes
{
    public static AccountDeletionOutcome FromErrorCode(string? code) => code switch
    {
        ReauthenticationErrorCodes.InvalidPassword => AccountDeletionOutcome.InvalidPassword,
        ReauthenticationErrorCodes.SecondFactorRequired => AccountDeletionOutcome.SecondFactorRequired,
        ReauthenticationErrorCodes.CodeAlreadyUsed => AccountDeletionOutcome.CodeAlreadyUsed,
        ReauthenticationErrorCodes.InvalidCode => AccountDeletionOutcome.InvalidCode,
        ReauthenticationErrorCodes.AccountLocked => AccountDeletionOutcome.AccountLocked,
        ReauthenticationErrorCodes.RateLimited => AccountDeletionOutcome.RateLimited,
        _ => AccountDeletionOutcome.Failed,
    };

    /// <summary>The re-check code a refusal stands for; null for Deleted, the generic error code for Failed.</summary>
    public static string? ToErrorCode(AccountDeletionOutcome outcome) => outcome switch
    {
        AccountDeletionOutcome.Deleted => null,
        AccountDeletionOutcome.InvalidPassword => ReauthenticationErrorCodes.InvalidPassword,
        AccountDeletionOutcome.SecondFactorRequired => ReauthenticationErrorCodes.SecondFactorRequired,
        AccountDeletionOutcome.CodeAlreadyUsed => ReauthenticationErrorCodes.CodeAlreadyUsed,
        AccountDeletionOutcome.InvalidCode => ReauthenticationErrorCodes.InvalidCode,
        AccountDeletionOutcome.AccountLocked => ReauthenticationErrorCodes.AccountLocked,
        AccountDeletionOutcome.RateLimited => ReauthenticationErrorCodes.RateLimited,
        _ => "failed",
    };
}
