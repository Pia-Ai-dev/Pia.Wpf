using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using System.Net.Http;
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
