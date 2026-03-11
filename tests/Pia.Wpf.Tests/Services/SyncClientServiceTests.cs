using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Pia.Shared.Models;
using Pia.Shared.Sync;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
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

public class SyncClientServicePullConflictTests
{
    private readonly ITodoService _todoService = Substitute.For<ITodoService>();
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly ITemplateService _templateService = Substitute.For<ITemplateService>();
    private readonly IProviderService _providerService = Substitute.For<IProviderService>();

    private SyncClientService CreateSut()
    {
        var dpapiHelper = Substitute.For<DpapiHelper>(
            NullLogger<DpapiHelper>.Instance);
        var mapper = new SyncMapper(dpapiHelper);

        return new SyncClientService(
            Substitute.For<IAuthService>(),
            Substitute.For<ISettingsService>(),
            _templateService,
            _providerService,
            Substitute.For<IHistoryService>(),
            _memoryService,
            mapper,
            Substitute.For<IHttpClientFactory>(),
            NullLogger<SyncClientService>.Instance,
            todoService: _todoService);
    }

    private static async Task<(int Pulled, int DecryptionErrors)> InvokePullChangesAsync(
        SyncClientService sut, SyncPullResponse pullResponse)
    {
        var json = JsonSerializer.Serialize(pullResponse, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var handler = new MockHttpMessageHandler(json);
        var client = new HttpClient(handler);
        var settings = new AppSettings { LastSyncTimestamp = DateTime.UtcNow.AddMinutes(-10) };

        var method = typeof(SyncClientService)
            .GetMethod("PullChangesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(sut, [client, "http://test", settings])!;
        await task;

        // Extract the tuple result from Task<(int, int)>
        var resultProperty = task.GetType().GetProperty("Result")!;
        var result = resultProperty.GetValue(task)!;
        var pulled = (int)result.GetType().GetField("Item1")!.GetValue(result)!;
        var errors = (int)result.GetType().GetField("Item2")!.GetValue(result)!;
        return (pulled, errors);
    }

    [Fact]
    public async Task PullTodo_RemoteNewer_ShouldApplyUpdate()
    {
        var sut = CreateSut();
        var todoId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _todoService.GetAsync(todoId).Returns(new TodoItem
        {
            Id = todoId,
            Title = "Old local",
            UpdatedAt = now.AddMinutes(-5)
        });

        var pullResponse = new SyncPullResponse
        {
            Todos = new SyncEntityChanges<SyncTodo>
            {
                Upserted = [new SyncTodo
                {
                    Id = todoId,
                    Title = "Updated remote",
                    UpdatedAt = now
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _todoService.Received(1).ImportAsync(Arg.Is<TodoItem>(t => t.Id == todoId));
        await _todoService.DidNotReceive().UpdateAsync(Arg.Any<TodoItem>());
    }

    [Fact]
    public async Task PullTodo_RemoteOlder_ShouldSkip()
    {
        var sut = CreateSut();
        var todoId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _todoService.GetAsync(todoId).Returns(new TodoItem
        {
            Id = todoId,
            Title = "Newer local",
            UpdatedAt = now
        });

        var pullResponse = new SyncPullResponse
        {
            Todos = new SyncEntityChanges<SyncTodo>
            {
                Upserted = [new SyncTodo
                {
                    Id = todoId,
                    Title = "Older remote",
                    UpdatedAt = now.AddMinutes(-5)
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _todoService.DidNotReceive().ImportAsync(Arg.Any<TodoItem>());
        await _todoService.DidNotReceive().UpdateAsync(Arg.Any<TodoItem>());
    }

    [Fact]
    public async Task PullTodo_NewRemote_ShouldImport()
    {
        var sut = CreateSut();
        var todoId = Guid.NewGuid();

        _todoService.GetAsync(todoId).Returns((TodoItem?)null);

        var pullResponse = new SyncPullResponse
        {
            Todos = new SyncEntityChanges<SyncTodo>
            {
                Upserted = [new SyncTodo
                {
                    Id = todoId,
                    Title = "Brand new",
                    UpdatedAt = DateTime.UtcNow
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _todoService.Received(1).ImportAsync(Arg.Is<TodoItem>(t => t.Id == todoId));
    }

    [Fact]
    public async Task PullMemory_RemoteNewer_ShouldApplyUpdate()
    {
        var sut = CreateSut();
        var memoryId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _memoryService.GetObjectAsync(memoryId).Returns(new MemoryObject
        {
            Id = memoryId,
            Label = "Old local",
            UpdatedAt = now.AddMinutes(-5)
        });

        var pullResponse = new SyncPullResponse
        {
            Memories = new SyncEntityChanges<SyncMemory>
            {
                Upserted = [new SyncMemory
                {
                    Id = memoryId,
                    Type = "note",
                    Label = "Updated remote",
                    Data = "{}",
                    UpdatedAt = now
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _memoryService.Received(1).UpdateObjectDataAsync(memoryId, "Updated remote", "{}");
    }

    [Fact]
    public async Task PullMemory_RemoteOlder_ShouldSkip()
    {
        var sut = CreateSut();
        var memoryId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _memoryService.GetObjectAsync(memoryId).Returns(new MemoryObject
        {
            Id = memoryId,
            Label = "Newer local",
            UpdatedAt = now
        });

        var pullResponse = new SyncPullResponse
        {
            Memories = new SyncEntityChanges<SyncMemory>
            {
                Upserted = [new SyncMemory
                {
                    Id = memoryId,
                    Type = "note",
                    Label = "Older remote",
                    Data = "{}",
                    UpdatedAt = now.AddMinutes(-5)
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _memoryService.DidNotReceive().UpdateObjectDataAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task PullMemory_NewRemote_ShouldImport()
    {
        var sut = CreateSut();
        var memoryId = Guid.NewGuid();

        _memoryService.GetObjectAsync(memoryId).Returns((MemoryObject?)null);

        var pullResponse = new SyncPullResponse
        {
            Memories = new SyncEntityChanges<SyncMemory>
            {
                Upserted = [new SyncMemory
                {
                    Id = memoryId,
                    Type = "note",
                    Label = "Brand new",
                    Data = "{}",
                    UpdatedAt = DateTime.UtcNow
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _memoryService.Received(1).ImportObjectAsync(Arg.Is<MemoryObject>(m => m.Id == memoryId));
    }

    [Fact]
    public async Task PullTemplate_RemoteNewer_ShouldApplyUpdate()
    {
        var sut = CreateSut();
        var templateId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var existing = new OptimizationTemplate
        {
            Id = templateId,
            Name = "Old local",
            Prompt = "old",
            ModifiedAt = now.AddMinutes(-5)
        };
        _templateService.GetTemplatesAsync().Returns(new List<OptimizationTemplate> { existing });

        var pullResponse = new SyncPullResponse
        {
            Templates = new SyncEntityChanges<SyncTemplate>
            {
                Upserted = [new SyncTemplate
                {
                    Id = templateId,
                    Name = "Updated remote",
                    Prompt = "new",
                    CreatedAt = now.AddHours(-1),
                    ModifiedAt = now
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _templateService.Received(1).UpdateTemplateAsync(
            Arg.Is<OptimizationTemplate>(t => t.Id == templateId));
    }

    [Fact]
    public async Task PullTemplate_RemoteOlder_ShouldSkip()
    {
        var sut = CreateSut();
        var templateId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var existing = new OptimizationTemplate
        {
            Id = templateId,
            Name = "Newer local",
            Prompt = "newer",
            ModifiedAt = now
        };
        _templateService.GetTemplatesAsync().Returns(new List<OptimizationTemplate> { existing });

        var pullResponse = new SyncPullResponse
        {
            Templates = new SyncEntityChanges<SyncTemplate>
            {
                Upserted = [new SyncTemplate
                {
                    Id = templateId,
                    Name = "Older remote",
                    Prompt = "older",
                    CreatedAt = now.AddHours(-1),
                    ModifiedAt = now.AddMinutes(-5)
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _templateService.DidNotReceive().UpdateTemplateAsync(Arg.Any<OptimizationTemplate>());
    }

    [Fact]
    public async Task PullTemplate_NewRemote_ShouldAdd()
    {
        var sut = CreateSut();
        var templateId = Guid.NewGuid();

        _templateService.GetTemplatesAsync().Returns(new List<OptimizationTemplate>());

        var pullResponse = new SyncPullResponse
        {
            Templates = new SyncEntityChanges<SyncTemplate>
            {
                Upserted = [new SyncTemplate
                {
                    Id = templateId,
                    Name = "Brand new",
                    Prompt = "prompt",
                    CreatedAt = DateTime.UtcNow
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _templateService.Received(1).AddTemplateAsync(
            Arg.Is<OptimizationTemplate>(t => t.Id == templateId));
    }

    [Fact]
    public async Task PullProvider_RemoteNewer_ShouldApplyUpdate()
    {
        var sut = CreateSut();
        var providerId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _providerService.GetProviderAsync(providerId).Returns(new AiProvider
        {
            Id = providerId,
            Name = "Old local",
            Endpoint = "https://old",
            UpdatedAt = now.AddMinutes(-5)
        });

        var pullResponse = new SyncPullResponse
        {
            Providers = new SyncEntityChanges<SyncProvider>
            {
                Upserted = [new SyncProvider
                {
                    Id = providerId,
                    Name = "Updated remote",
                    Endpoint = "https://new",
                    UpdatedAt = now
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _providerService.Received(1).UpdateProviderAsync(
            Arg.Is<AiProvider>(p => p.Id == providerId), Arg.Any<string?>());
    }

    [Fact]
    public async Task PullProvider_RemoteOlder_ShouldSkip()
    {
        var sut = CreateSut();
        var providerId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _providerService.GetProviderAsync(providerId).Returns(new AiProvider
        {
            Id = providerId,
            Name = "Newer local",
            Endpoint = "https://newer",
            UpdatedAt = now
        });

        var pullResponse = new SyncPullResponse
        {
            Providers = new SyncEntityChanges<SyncProvider>
            {
                Upserted = [new SyncProvider
                {
                    Id = providerId,
                    Name = "Older remote",
                    Endpoint = "https://older",
                    UpdatedAt = now.AddMinutes(-5)
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _providerService.DidNotReceive().UpdateProviderAsync(
            Arg.Any<AiProvider>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task PullProvider_NewRemote_ShouldAdd()
    {
        var sut = CreateSut();
        var providerId = Guid.NewGuid();

        _providerService.GetProviderAsync(providerId).Returns((AiProvider?)null);

        var pullResponse = new SyncPullResponse
        {
            Providers = new SyncEntityChanges<SyncProvider>
            {
                Upserted = [new SyncProvider
                {
                    Id = providerId,
                    Name = "Brand new",
                    Endpoint = "https://new",
                    UpdatedAt = DateTime.UtcNow
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _providerService.Received(1).AddProviderAsync(
            Arg.Is<AiProvider>(p => p.Id == providerId), Arg.Any<string?>());
    }

    private class MockHttpMessageHandler(string responseJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
