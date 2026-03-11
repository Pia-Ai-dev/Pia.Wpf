using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using System.Net.Http;
using System.Reflection;
using Xunit;

namespace Pia.Tests.Services;

public class SyncClientServiceTests
{
    private readonly IAuthService _authService = Substitute.For<IAuthService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly ITemplateService _templateService = Substitute.For<ITemplateService>();
    private readonly IProviderService _providerService = Substitute.For<IProviderService>();
    private readonly IHistoryService _historyService = Substitute.For<IHistoryService>();
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly SyncClientService _sut;

    public SyncClientServiceTests()
    {
        var dpapiHelper = Substitute.For<DpapiHelper>(
            NullLogger<DpapiHelper>.Instance);
        var mapper = new SyncMapper(dpapiHelper);

        _sut = new SyncClientService(
            _authService, _settingsService, _templateService,
            _providerService, _historyService, _memoryService,
            mapper, _httpClientFactory,
            NullLogger<SyncClientService>.Instance);
    }

    [Fact]
    public async Task SyncNowAsync_ReturnsNull_WhenNotLoggedIn()
    {
        _authService.IsLoggedIn.Returns(false);

        var result = await _sut.SyncNowAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task SyncNowAsync_ReturnsNull_WhenSyncDisabled()
    {
        _authService.IsLoggedIn.Returns(true);
        var settings = new AppSettings { SyncEnabled = false };
        _settingsService.GetSettingsAsync().Returns(settings);

        var result = await _sut.SyncNowAsync();

        result.Should().BeNull();
    }
}

public class SyncClientServiceDeviceRevokedTests
{
    private readonly IDeviceManagementService _deviceMgmt = Substitute.For<IDeviceManagementService>();
    private readonly IDeviceKeyService _deviceKeys = Substitute.For<IDeviceKeyService>();

    private SyncClientService CreateSut()
    {
        var dpapiHelper = Substitute.For<DpapiHelper>(
            NullLogger<DpapiHelper>.Instance);
        var mapper = new SyncMapper(dpapiHelper);

        return new SyncClientService(
            Substitute.For<IAuthService>(),
            Substitute.For<ISettingsService>(),
            Substitute.For<ITemplateService>(),
            Substitute.For<IProviderService>(),
            Substitute.For<IHistoryService>(),
            Substitute.For<IMemoryService>(),
            mapper,
            Substitute.For<IHttpClientFactory>(),
            NullLogger<SyncClientService>.Instance,
            deviceMgmt: _deviceMgmt,
            deviceKeys: _deviceKeys);
    }

    private static Task InvokeCheckForPendingDevicesAsync(SyncClientService sut)
    {
        var method = typeof(SyncClientService)
            .GetMethod("CheckForPendingDevicesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(sut, null)!;
    }

    [Fact]
    public async Task CheckForPendingDevices_RaisesCurrentDeviceRevoked_WhenDeviceNotInList()
    {
        var sut = CreateSut();
        _deviceKeys.GetDeviceId().Returns("device-123");
        _deviceMgmt.GetDevicesAsync().Returns(new DeviceListResponse
        {
            Devices = [
                new DeviceInfo
                {
                    DeviceId = "other-device",
                    DeviceName = "Other",
                    Status = DeviceStatus.Active,
                    AgreementPublicKey = "key1",
                    SigningPublicKey = "key2"
                }
            ]
        });

        bool eventRaised = false;
        sut.CurrentDeviceRevoked += (_, _) => eventRaised = true;

        await InvokeCheckForPendingDevicesAsync(sut);

        eventRaised.Should().BeTrue();
    }

    [Fact]
    public async Task CheckForPendingDevices_RaisesCurrentDeviceRevoked_WhenDeviceIsRevoked()
    {
        var sut = CreateSut();
        _deviceKeys.GetDeviceId().Returns("device-123");
        _deviceMgmt.GetDevicesAsync().Returns(new DeviceListResponse
        {
            Devices = [
                new DeviceInfo
                {
                    DeviceId = "device-123",
                    DeviceName = "This Device",
                    Status = DeviceStatus.Revoked,
                    AgreementPublicKey = "key1",
                    SigningPublicKey = "key2"
                }
            ]
        });

        bool eventRaised = false;
        sut.CurrentDeviceRevoked += (_, _) => eventRaised = true;

        await InvokeCheckForPendingDevicesAsync(sut);

        eventRaised.Should().BeTrue();
    }

    [Fact]
    public async Task CheckForPendingDevices_DoesNotRaiseCurrentDeviceRevoked_WhenDeviceIsActive()
    {
        var sut = CreateSut();
        _deviceKeys.GetDeviceId().Returns("device-123");
        _deviceMgmt.GetDevicesAsync().Returns(new DeviceListResponse
        {
            Devices = [
                new DeviceInfo
                {
                    DeviceId = "device-123",
                    DeviceName = "This Device",
                    Status = DeviceStatus.Active,
                    AgreementPublicKey = "key1",
                    SigningPublicKey = "key2"
                }
            ]
        });

        bool eventRaised = false;
        sut.CurrentDeviceRevoked += (_, _) => eventRaised = true;

        await InvokeCheckForPendingDevicesAsync(sut);

        eventRaised.Should().BeFalse();
    }
}
