using Pia.Services.Interfaces;

namespace Pia.ViewModels;

internal static class AccountSwitchPrompts
{
    /// <summary>False when the sign-in must not go ahead: cancelled, or a clear was scheduled.</summary>
    public static async Task<bool> ConfirmSignInAsync(
        ILocalDataResetService reset, IDialogService dialogs, ILocalizationService loc)
    {
        if (reset.IsResetPending)
        {
            if (await dialogs.ShowConfirmationDialogAsync(
                    loc["LocalReset_Pending_Title"], loc["LocalReset_Pending_Message"]))
            {
                await reset.RestartAsync();
            }
            return false;
        }

        if (!await reset.HasSyncedLocalDataAsync())
        {
            return true;
        }

        var notices = await reset.GetNoticesAsync();
        var choice = await dialogs.ShowChoiceDialogAsync(
            loc["LocalReset_SignIn_Title"],
            ComposeMessage(loc, "LocalReset_SignIn_Message", notices),
            loc["LocalReset_SignIn_Keep"],
            loc["LocalReset_SignIn_Clear"],
            loc["Common_Cancel"]);

        switch (choice)
        {
            case DialogChoice.Primary:
                return true;
            case DialogChoice.Secondary:
                reset.ScheduleReset();
                await reset.RestartAsync();
                return false;
            default:
                return false;
        }
    }

    public static async Task SignOutAsync(
        ILocalDataResetService reset, IDialogService dialogs, ILocalizationService loc,
        bool uploadFirst, Func<Task> signOut)
    {
        var notices = await reset.GetNoticesAsync();
        var choice = await dialogs.ShowChoiceDialogAsync(
            loc["LocalReset_SignOut_Title"],
            ComposeMessage(loc, "LocalReset_SignOut_Message", notices),
            loc["LocalReset_SignOut_Clear"],
            loc["LocalReset_SignOut_Keep"],
            loc["Common_Cancel"]);
        if (choice == DialogChoice.Dismissed)
        {
            return;
        }

        var clear = choice == DialogChoice.Primary;
        if (uploadFirst
            && !await reset.UploadPendingChangesAsync()
            && clear
            && !await dialogs.ShowConfirmationDialogAsync(
                loc["LocalReset_UploadFailed_Title"], loc["LocalReset_UploadFailed_Message"]))
        {
            return;
        }

        if (clear)
        {
            reset.ScheduleReset();
        }

        await signOut();

        if (clear)
        {
            await reset.RestartAsync();
        }
    }

    public static async Task OfferClearAfterDeletionAsync(
        ILocalDataResetService reset, IDialogService dialogs, ILocalizationService loc)
    {
        var notices = await reset.GetNoticesAsync();
        // The account is gone, so no encrypted copy of the keys survives on the server.
        if (notices.ApiKeys != ApiKeyNotice.None)
        {
            notices = notices with { ApiKeys = ApiKeyNotice.WillBeLost };
        }

        var choice = await dialogs.ShowChoiceDialogAsync(
            loc["LocalReset_Deleted_Title"],
            ComposeMessage(loc, "LocalReset_Deleted_Message", notices),
            loc["LocalReset_Deleted_Clear"],
            loc["LocalReset_Deleted_Keep"],
            closeText: "");

        if (choice == DialogChoice.Primary)
        {
            reset.ScheduleReset();
            await reset.RestartAsync();
        }
    }

    internal static string ComposeMessage(ILocalizationService loc, string messageKey, LocalDataNotices notices)
    {
        var parts = new List<string> { loc[messageKey] };
        if (notices.VaultHasMemories)
        {
            parts.Add(loc["LocalReset_Notice_Vault"]);
        }

        switch (notices.ApiKeys)
        {
            case ApiKeyNotice.WillBeLost:
                parts.Add(loc["LocalReset_Notice_KeysLost"]);
                break;
            case ApiKeyNotice.MayBeLost:
                parts.Add(loc["LocalReset_Notice_KeysMaybeLost"]);
                break;
        }

        return string.Join("\n\n", parts);
    }
}
