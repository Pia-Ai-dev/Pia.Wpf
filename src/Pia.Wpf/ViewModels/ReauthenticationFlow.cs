using Pia.Services.Interfaces;
using Pia.Shared.Auth;

namespace Pia.ViewModels;

public enum ReauthenticationOutcome
{
    Cancelled,
    Succeeded,
    Refused,
}

/// <summary>A protected call's answer: null when it went through, otherwise the server's error code.</summary>
public delegate Task<string?> ReauthenticatedAttempt(ReauthenticationFields fields);

public static class ReauthenticationFlow
{
    /// <summary>Asks for the password (and a code when the server wants one) until the call goes through or the user cancels.</summary>
    /// <param name="knownPassword">A password the user already typed for this action; tried before any prompt.</param>
    public static async Task<(ReauthenticationOutcome Outcome, string? ErrorCode)> RunAsync(
        IDialogService dialogs,
        ILocalizationService localization,
        ReauthenticatedAttempt attempt,
        string? knownPassword = null)
    {
        var prompt = new ReauthenticationViewModel { Password = knownPassword ?? "" };
        var needsPrompt = string.IsNullOrEmpty(knownPassword);

        while (true)
        {
            if (needsPrompt && !await dialogs.ShowReauthenticationDialogAsync(prompt))
                return (ReauthenticationOutcome.Cancelled, null);
            needsPrompt = true;

            var code = await attempt(prompt.ToFields());
            switch (code)
            {
                case null:
                    return (ReauthenticationOutcome.Succeeded, null);
                case ReauthenticationErrorCodes.SecondFactorRequired:
                    prompt.IsCodeRequired = true;
                    prompt.ErrorMessage = null;
                    break;
                case ReauthenticationErrorCodes.CodeAlreadyUsed:
                    prompt.Code = "";
                    prompt.ErrorMessage = localization["Reauth_CodeAlreadyUsed"];
                    break;
                case ReauthenticationErrorCodes.InvalidCode:
                    prompt.Code = "";
                    prompt.ErrorMessage = localization["Reauth_InvalidCode"];
                    break;
                case ReauthenticationErrorCodes.InvalidPassword:
                    prompt.Password = "";
                    prompt.ErrorMessage = localization["Reauth_InvalidPassword"];
                    break;
                default:
                    return (ReauthenticationOutcome.Refused, code);
            }
        }
    }

    /// <summary>Message for a refusal the dialog cannot fix by asking again.</summary>
    public static string RefusalMessage(ILocalizationService localization, string? code) => code switch
    {
        ReauthenticationErrorCodes.AccountLocked => localization["Reauth_AccountLocked"],
        ReauthenticationErrorCodes.RateLimited => localization["Reauth_RateLimited"],
        ReauthenticationErrorCodes.Unavailable => localization["Reauth_Unavailable"],
        _ => localization["Reauth_Failed"],
    };
}
