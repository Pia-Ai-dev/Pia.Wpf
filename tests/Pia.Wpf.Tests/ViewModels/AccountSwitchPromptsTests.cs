namespace Pia.Tests.ViewModels;

using System.IO;
using System.Xml.Linq;
using NSubstitute;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

public class AccountSwitchPromptsTests
{
    private readonly ILocalDataResetService _reset = Substitute.For<ILocalDataResetService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly List<string?> _progress = [];
    private int _signOutCalls;

    public AccountSwitchPromptsTests()
    {
        _loc[Arg.Any<string>()].Returns(call => call.Arg<string>());
        _reset.UploadPendingChangesAsync().Returns(true);
    }

    private Task SignOutAsync(bool uploadFirst = true) =>
        AccountSwitchPrompts.SignOutAsync(_reset, _dialogs, _loc, uploadFirst, () =>
        {
            _signOutCalls++;
            return Task.CompletedTask;
        }, _progress.Add);

    private void Choose(DialogChoice choice) =>
        _dialogs.ShowChoiceDialogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(choice);

    [Fact]
    public async Task ConfirmSignIn_NoLocalData_ProceedsWithoutAsking()
    {
        _reset.HasSyncedLocalDataAsync().Returns(false);

        Assert.True(await AccountSwitchPrompts.ConfirmSignInAsync(_reset, _dialogs, _loc));

        await _dialogs.DidNotReceive().ShowChoiceDialogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ConfirmSignIn_Keep_Proceeds()
    {
        _reset.HasSyncedLocalDataAsync().Returns(true);
        Choose(DialogChoice.Primary);

        Assert.True(await AccountSwitchPrompts.ConfirmSignInAsync(_reset, _dialogs, _loc));

        _reset.DidNotReceive().ScheduleReset();
        await _dialogs.Received(1).ShowChoiceDialogAsync(
            "LocalReset_SignIn_Title", "LocalReset_SignIn_Message",
            "LocalReset_SignIn_Keep", "LocalReset_SignIn_Clear", "Common_Cancel");
    }

    [Fact]
    public async Task ConfirmSignIn_ClearFirst_SchedulesAndRestarts_AndStops()
    {
        _reset.HasSyncedLocalDataAsync().Returns(true);
        Choose(DialogChoice.Secondary);

        Assert.False(await AccountSwitchPrompts.ConfirmSignInAsync(_reset, _dialogs, _loc));

        Received.InOrder(() =>
        {
            _reset.ScheduleReset();
            _reset.RestartAsync();
        });
    }

    [Fact]
    public async Task ConfirmSignIn_Cancel_Stops()
    {
        _reset.HasSyncedLocalDataAsync().Returns(true);
        Choose(DialogChoice.Dismissed);

        Assert.False(await AccountSwitchPrompts.ConfirmSignInAsync(_reset, _dialogs, _loc));

        _reset.DidNotReceive().ScheduleReset();
        await _reset.DidNotReceive().RestartAsync();
    }

    [Fact]
    public async Task ConfirmSignIn_WhenResetPending_OffersRestartAndNeverSignsIn()
    {
        _reset.IsResetPending.Returns(true);
        _dialogs.ShowConfirmationDialogAsync("LocalReset_Pending_Title", "LocalReset_Pending_Message").Returns(true);

        Assert.False(await AccountSwitchPrompts.ConfirmSignInAsync(_reset, _dialogs, _loc));

        await _reset.Received(1).RestartAsync();
        await _reset.DidNotReceive().HasSyncedLocalDataAsync();
    }

    [Fact]
    public async Task SignOut_Clear_UploadsSchedulesSignsOutThenRestarts()
    {
        Choose(DialogChoice.Primary);
        var order = new List<string>();
        _reset.UploadPendingChangesAsync().Returns(_ =>
        {
            order.Add("upload");
            return true;
        });
        _reset.When(r => r.ScheduleReset()).Do(_ => order.Add("schedule"));
        _reset.RestartAsync().Returns(_ =>
        {
            order.Add("restart");
            return Task.CompletedTask;
        });

        await AccountSwitchPrompts.SignOutAsync(_reset, _dialogs, _loc, true, () =>
        {
            order.Add("signOut");
            return Task.CompletedTask;
        }, _ => { });

        Assert.Equal(["upload", "schedule", "signOut", "restart"], order);
        await _dialogs.Received(1).ShowChoiceDialogAsync(
            "LocalReset_SignOut_Title", "LocalReset_SignOut_Message",
            "LocalReset_SignOut_Clear", "LocalReset_SignOut_Keep", "Common_Cancel");
    }

    [Fact]
    public async Task SignOut_Keep_UploadsAndSignsOut_WithoutReset()
    {
        Choose(DialogChoice.Secondary);

        await SignOutAsync();

        await _reset.Received(1).UploadPendingChangesAsync();
        Assert.Equal(1, _signOutCalls);
        _reset.DidNotReceive().ScheduleReset();
        await _reset.DidNotReceive().RestartAsync();
    }

    [Fact]
    public async Task SignOut_ShowsUploadThenSignOutProgress_AndClearsIt()
    {
        Choose(DialogChoice.Secondary);

        await SignOutAsync();

        Assert.Equal(["LocalReset_Progress_Uploading", null, "LocalReset_Progress_SigningOut", null], _progress);
    }

    [Fact]
    public async Task SignOut_UploadFailedPrompt_IsNotShownUnderTheProgress()
    {
        Choose(DialogChoice.Primary);
        _reset.UploadPendingChangesAsync().Returns(false);
        string? progressWhenAsked = "unset";
        _dialogs.ShowConfirmationDialogAsync("LocalReset_UploadFailed_Title", "LocalReset_UploadFailed_Message")
            .Returns(_ =>
            {
                progressWhenAsked = _progress[^1];
                return false;
            });

        await SignOutAsync();

        Assert.Null(progressWhenAsked);
        Assert.Equal(["LocalReset_Progress_Uploading", null], _progress);
    }

    [Fact]
    public async Task SignOut_Keep_UploadFailed_StillSignsOut_WithoutAsking()
    {
        Choose(DialogChoice.Secondary);
        _reset.UploadPendingChangesAsync().Returns(false);

        await SignOutAsync();

        Assert.Equal(1, _signOutCalls);
        await _dialogs.DidNotReceive().ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SignOut_Clear_UploadFailed_Declined_DoesNothing()
    {
        Choose(DialogChoice.Primary);
        _reset.UploadPendingChangesAsync().Returns(false);
        _dialogs.ShowConfirmationDialogAsync("LocalReset_UploadFailed_Title", "LocalReset_UploadFailed_Message")
            .Returns(false);

        await SignOutAsync();

        Assert.Equal(0, _signOutCalls);
        _reset.DidNotReceive().ScheduleReset();
    }

    [Fact]
    public async Task SignOut_WithoutUpload_NeverUploads()
    {
        Choose(DialogChoice.Primary);

        await SignOutAsync(uploadFirst: false);

        await _reset.DidNotReceive().UploadPendingChangesAsync();
        Assert.Equal(1, _signOutCalls);
    }

    [Fact]
    public async Task SignOut_Cancel_DoesNothing()
    {
        Choose(DialogChoice.Dismissed);

        await SignOutAsync();

        await _reset.DidNotReceive().UploadPendingChangesAsync();
        Assert.Equal(0, _signOutCalls);
        _reset.DidNotReceive().ScheduleReset();
    }

    [Fact]
    public async Task OfferClearAfterDeletion_Clear_SchedulesAndRestarts()
    {
        Choose(DialogChoice.Primary);

        await AccountSwitchPrompts.OfferClearAfterDeletionAsync(_reset, _dialogs, _loc);

        Received.InOrder(() =>
        {
            _reset.ScheduleReset();
            _reset.RestartAsync();
        });
    }

    [Fact]
    public async Task OfferClearAfterDeletion_Keep_DoesNothing()
    {
        Choose(DialogChoice.Secondary);

        await AccountSwitchPrompts.OfferClearAfterDeletionAsync(_reset, _dialogs, _loc);

        _reset.DidNotReceive().ScheduleReset();
    }

    [Fact]
    public async Task OfferClearAfterDeletion_OffersOnlyClearAndKeep()
    {
        Choose(DialogChoice.Secondary);

        await AccountSwitchPrompts.OfferClearAfterDeletionAsync(_reset, _dialogs, _loc);

        await _dialogs.Received(1).ShowChoiceDialogAsync(
            "LocalReset_Deleted_Title", Arg.Any<string>(),
            "LocalReset_Deleted_Clear", "LocalReset_Deleted_Keep", "");
    }

    [Fact]
    public async Task OfferClearAfterDeletion_WithKeys_SaysTheyWillBeLost()
    {
        Choose(DialogChoice.Secondary);
        _reset.GetNoticesAsync().Returns(new LocalDataNotices(false, ApiKeyNotice.MayBeLost));

        await AccountSwitchPrompts.OfferClearAfterDeletionAsync(_reset, _dialogs, _loc);

        await _dialogs.Received(1).ShowChoiceDialogAsync(
            "LocalReset_Deleted_Title",
            Arg.Is<string>(m => m.EndsWith("LocalReset_Notice_KeysLost")),
            "LocalReset_Deleted_Clear", "LocalReset_Deleted_Keep", Arg.Any<string?>());
    }

    [Fact]
    public void ComposeMessage_AddsOnlyTheNoticesThatApply()
    {
        Assert.Equal("Base", AccountSwitchPrompts.ComposeMessage(
            _loc, "Base", new LocalDataNotices(false, ApiKeyNotice.None)));

        var message = AccountSwitchPrompts.ComposeMessage(
            _loc, "Base", new LocalDataNotices(true, ApiKeyNotice.MayBeLost));
        Assert.Equal(
            "Base\n\nLocalReset_Notice_Vault\n\nLocalReset_Notice_KeysMaybeLost",
            message.Replace("\r\n", "\n"));

        Assert.EndsWith("LocalReset_Notice_KeysLost", AccountSwitchPrompts.ComposeMessage(
            _loc, "Base", new LocalDataNotices(false, ApiKeyNotice.WillBeLost)));
    }

    [Fact]
    public void EveryLocalResetKey_ExistsInAllThreeLanguages()
    {
        var dir = FindStringsDirectory();
        var sets = new[] { "ViewStrings.resx", "ViewStrings.de.resx", "ViewStrings.fr.resx" }
            .Select(file => XDocument.Load(Path.Combine(dir, file)).Root!
                .Elements("data")
                .Where(d => ((string?)d.Attribute("name"))!.StartsWith("LocalReset_", StringComparison.Ordinal))
                .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value")))
            .ToList();

        Assert.Equal(21, sets[0].Count);
        foreach (var set in sets)
        {
            Assert.Equal(sets[0].Keys.OrderBy(k => k), set.Keys.OrderBy(k => k));
            Assert.All(set.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
        }
    }

    private static string FindStringsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Pia.Wpf", "Resources", "Strings");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Resources/Strings not found above the test output");
    }
}
