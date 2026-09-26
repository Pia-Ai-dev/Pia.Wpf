namespace Pia.Tests.ViewModels;

using System.Net.Http;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

public class AccountDeletionViewModelTests
{
    private readonly IAccountDataService _accountData = Substitute.For<IAccountDataService>();
    private readonly IFileDialogService _fileDialogs = Substitute.For<IFileDialogService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();

    public AccountDeletionViewModelTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
    }

    [Fact]
    public void CanDelete_WaitsForTheAcknowledgement()
    {
        var sut = CreateSut(requiresPassword: false);

        Assert.False(sut.CanDelete);
        sut.IsUnderstood = true;
        Assert.True(sut.CanDelete);
    }

    [Fact]
    public void CanDelete_ForALocalAccount_AlsoWaitsForThePassword()
    {
        var sut = CreateSut(requiresPassword: true);
        sut.IsUnderstood = true;

        Assert.False(sut.CanDelete);
        sut.Password = "pw";
        Assert.True(sut.CanDelete);
    }

    [Fact]
    public async Task Export_WritesToTheChosenFile_AndSaysSo()
    {
        _fileDialogs.PromptSaveFile(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(@"C:\exports\pia.zip");
        var sut = CreateSut(requiresPassword: false);

        await sut.ExportCommand.ExecuteAsync(null);

        await _accountData.Received(1).ExportToFileAsync(@"C:\exports\pia.zip", Arg.Any<CancellationToken>());
        Assert.Equal("AccountDeletion_ExportDone", sut.ExportStatus);
    }

    [Fact]
    public async Task Export_WhenThePickerIsCancelled_DoesNothing()
    {
        _fileDialogs.PromptSaveFile(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns((string?)null);
        var sut = CreateSut(requiresPassword: false);

        await sut.ExportCommand.ExecuteAsync(null);

        await _accountData.DidNotReceiveWithAnyArgs()
            .ExportToFileAsync(default!, TestContext.Current.CancellationToken);
        Assert.Null(sut.ExportStatus);
    }

    [Fact]
    public async Task Export_WhenTheServerFails_SaysSo()
    {
        _fileDialogs.PromptSaveFile(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(@"C:\exports\pia.zip");
        _accountData.ExportToFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("502"));
        var sut = CreateSut(requiresPassword: false);

        await sut.ExportCommand.ExecuteAsync(null);

        Assert.Equal("AccountDeletion_ExportFailed", sut.ExportStatus);
    }

    [Fact]
    public async Task CanDelete_IsWithheldWhileTheExportRuns()
    {
        var export = new TaskCompletionSource();
        _fileDialogs.PromptSaveFile(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(@"C:\exports\pia.zip");
        _accountData.ExportToFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(export.Task);
        var sut = CreateSut(requiresPassword: false);
        sut.IsUnderstood = true;

        var running = sut.ExportCommand.ExecuteAsync(null);
        Assert.False(sut.CanDelete);

        export.SetResult();
        await running;
        Assert.True(sut.CanDelete);
    }

    [Fact]
    public async Task IsConfirmationIncomplete_StaysFalseWhileTheExportRuns()
    {
        var export = new TaskCompletionSource();
        _fileDialogs.PromptSaveFile(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(@"C:\exports\pia.zip");
        _accountData.ExportToFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(export.Task);
        var sut = CreateSut(requiresPassword: true);
        sut.IsUnderstood = true;
        sut.Password = "pw";

        var running = sut.ExportCommand.ExecuteAsync(null);

        Assert.False(sut.CanDelete);
        Assert.False(sut.IsConfirmationIncomplete);
        export.SetResult();
        await running;
    }

    [Fact]
    public async Task Export_WhenCancelled_StopsQuietly()
    {
        CancellationToken exportToken = default;
        _fileDialogs.PromptSaveFile(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(@"C:\exports\pia.zip");
        _accountData.ExportToFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            exportToken = ci.Arg<CancellationToken>();
            return Task.Delay(Timeout.Infinite, exportToken);
        });
        var sut = CreateSut(requiresPassword: false);

        var running = sut.ExportCommand.ExecuteAsync(null);
        sut.ExportCommand.Cancel();
        await running;

        Assert.True(exportToken.IsCancellationRequested);
        Assert.False(sut.IsExporting);
        Assert.Null(sut.ExportStatus);
    }

    private AccountDeletionViewModel CreateSut(bool requiresPassword) =>
        new(_accountData, _fileDialogs, _loc, requiresPassword);
}
