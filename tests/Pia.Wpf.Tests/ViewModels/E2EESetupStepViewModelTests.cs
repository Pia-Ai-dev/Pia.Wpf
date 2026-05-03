namespace Pia.Tests.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

public class E2EESetupStepViewModelTests
{
    private readonly IDeviceManagementService _deviceMgmt;
    private readonly IDeviceKeyService _deviceKeys;
    private readonly ISyncClientService _syncService;
    private readonly IOutputService _outputService;

    public E2EESetupStepViewModelTests()
    {
        _deviceMgmt = Substitute.For<IDeviceManagementService>();
        _deviceKeys = Substitute.For<IDeviceKeyService>();
        _syncService = Substitute.For<ISyncClientService>();
        _outputService = Substitute.For<IOutputService>();

        _deviceKeys.GetFingerprint().Returns("ABCD-1234");
    }

    private E2EESetupStepViewModel CreateSut() => new(
        _deviceMgmt, _deviceKeys, _syncService, _outputService,
        NullLogger<E2EESetupStepViewModel>.Instance);

    [Fact]
    public void InitialState_ShouldBeChoice_WithToggleOn()
    {
        var sut = CreateSut();

        Assert.Equal(E2EESetupState.Choice, sut.State);
        Assert.True(sut.ShouldEnableE2EE);
        Assert.Null(sut.ErrorMessage);
        Assert.False(sut.IsBusy);
    }

    [Fact]
    public async Task Proceed_FromChoice_WithToggleOn_ShouldBootstrapAndEnterRecoveryState()
    {
        _deviceMgmt.BootstrapFirstDeviceAsync().Returns("XXXX-XXXX-XXXX-XXXX-XXXX-XXXX");

        var sut = CreateSut();
        Assert.True(sut.ShouldEnableE2EE);

        await sut.ProceedCommand.ExecuteAsync(null);

        await _deviceMgmt.Received(1).BootstrapFirstDeviceAsync();
        Assert.Equal(E2EESetupState.SavingRecoveryCode, sut.State);
        Assert.Equal("XXXX-XXXX-XXXX-XXXX-XXXX-XXXX", sut.RecoveryCode);
        Assert.False(sut.IsBusy);
        Assert.Null(sut.ErrorMessage);
    }

    [Fact]
    public async Task Proceed_FromChoice_WithToggleOff_ShouldEnterConfirmingOptOut()
    {
        bool? advanceRaisedWith = null;

        var sut = CreateSut();
        sut.ShouldEnableE2EE = false;
        sut.AdvanceRequested += enabled => advanceRaisedWith = enabled;

        await sut.ProceedCommand.ExecuteAsync(null);

        Assert.Equal(E2EESetupState.ConfirmingOptOut, sut.State);
        await _deviceMgmt.DidNotReceive().BootstrapFirstDeviceAsync();
        Assert.Null(advanceRaisedWith);
    }

    [Fact]
    public async Task Proceed_FromConfirmingOptOut_ShouldStartSyncAndSignalAdvance()
    {
        bool? advanceRaisedWith = null;

        var sut = CreateSut();
        sut.ShouldEnableE2EE = false;
        sut.AdvanceRequested += enabled => advanceRaisedWith = enabled;

        await sut.ProceedCommand.ExecuteAsync(null); // → ConfirmingOptOut
        await sut.ProceedCommand.ExecuteAsync(null); // → CompleteOptOutAsync → AdvanceRequested(false)

        Assert.False(advanceRaisedWith);
        await _deviceMgmt.DidNotReceive().BootstrapFirstDeviceAsync();
        await _syncService.Received(1).PerformFirstSyncMigrationAsync();
        _syncService.Received(1).StartBackgroundSync();
    }

    [Fact]
    public async Task OptOutGoBack_FromConfirmingOptOut_ShouldReturnToChoice()
    {
        var sut = CreateSut();
        sut.ShouldEnableE2EE = false;
        await sut.ProceedCommand.ExecuteAsync(null);
        Assert.Equal(E2EESetupState.ConfirmingOptOut, sut.State);

        sut.OptOutGoBackCommand.Execute(null);

        Assert.Equal(E2EESetupState.Choice, sut.State);
    }

    [Fact]
    public async Task Proceed_FromSavingRecoveryCode_WithoutCheckbox_ShouldNotAdvance()
    {
        _deviceMgmt.BootstrapFirstDeviceAsync().Returns("CODE");
        var advanceRaised = false;

        var sut = CreateSut();
        sut.AdvanceRequested += _ => advanceRaised = true;
        await sut.ProceedCommand.ExecuteAsync(null); // → SavingRecoveryCode

        Assert.Equal(E2EESetupState.SavingRecoveryCode, sut.State);
        Assert.False(sut.HasConfirmedRecoveryCode);

        await sut.ProceedCommand.ExecuteAsync(null);

        Assert.False(advanceRaised);
        Assert.Equal(E2EESetupState.SavingRecoveryCode, sut.State);
        _syncService.DidNotReceive().StartBackgroundSync();
    }

    [Fact]
    public async Task Proceed_FromSavingRecoveryCode_WithCheckbox_ShouldSignalAdvanceAndStartSync()
    {
        _deviceMgmt.BootstrapFirstDeviceAsync().Returns("CODE");
        bool? advanceRaisedWith = null;

        var sut = CreateSut();
        sut.AdvanceRequested += enabled => advanceRaisedWith = enabled;
        await sut.ProceedCommand.ExecuteAsync(null); // → SavingRecoveryCode
        sut.HasConfirmedRecoveryCode = true;

        await sut.ProceedCommand.ExecuteAsync(null);

        Assert.True(advanceRaisedWith);
        Assert.Equal(E2EESetupState.Completed, sut.State);
        await _syncService.Received(1).PerformFirstSyncMigrationAsync();
        _syncService.Received(1).StartBackgroundSync();
    }

    [Fact]
    public async Task Bootstrap_Failure_ShouldStayInChoice_WithErrorMessage()
    {
        _deviceMgmt.BootstrapFirstDeviceAsync().ThrowsAsync(new InvalidOperationException("server unreachable"));

        var sut = CreateSut();
        await sut.ProceedCommand.ExecuteAsync(null);

        Assert.Equal(E2EESetupState.Choice, sut.State);
        Assert.NotNull(sut.ErrorMessage);
        Assert.Contains("server unreachable", sut.ErrorMessage);
        Assert.False(sut.IsBusy);
        _syncService.DidNotReceive().StartBackgroundSync();
    }
}
