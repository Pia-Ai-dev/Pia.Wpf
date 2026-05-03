namespace Pia.Tests.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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
}
