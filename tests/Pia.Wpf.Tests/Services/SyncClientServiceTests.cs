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

public class SyncClientServicePullSessionTests
{
    private readonly IHistoryService _historyService = Substitute.For<IHistoryService>();

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
            _historyService,
            Substitute.For<IMemoryService>(),
            mapper,
            Substitute.For<IHttpClientFactory>(),
            NullLogger<SyncClientService>.Instance);
    }

    private static async Task<(int Pulled, int DecryptionErrors, bool PullSucceeded)> InvokePullChangesAsync(
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

        var resultProperty = task.GetType().GetProperty("Result")!;
        var result = resultProperty.GetValue(task)!;
        var pulled = (int)result.GetType().GetField("Item1")!.GetValue(result)!;
        var errors = (int)result.GetType().GetField("Item2")!.GetValue(result)!;
        var pullOk = (bool)result.GetType().GetField("Item3")!.GetValue(result)!;
        return (pulled, errors, pullOk);
    }

    [Fact]
    public async Task PullSession_NewRemote_ShouldImport()
    {
        var sut = CreateSut();
        var sessionId = Guid.NewGuid();

        _historyService.GetSessionAsync(sessionId).Returns((OptimizationSession?)null);

        var pullResponse = new SyncPullResponse
        {
            Sessions = new SyncSessionChanges
            {
                Added = [new SyncSession
                {
                    Id = sessionId,
                    OriginalText = "Hello",
                    OptimizedText = "Hi there",
                    CreatedAt = DateTime.UtcNow
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _historyService.Received(1).AddSessionAsync(
            Arg.Is<OptimizationSession>(s => s.Id == sessionId));
    }

    [Fact]
    public async Task PullSession_AlreadyExists_ShouldSkip()
    {
        var sut = CreateSut();
        var sessionId = Guid.NewGuid();

        _historyService.GetSessionAsync(sessionId).Returns(new OptimizationSession
        {
            Id = sessionId,
            OriginalText = "Hello",
            OptimizedText = "Hi there"
        });

        var pullResponse = new SyncPullResponse
        {
            Sessions = new SyncSessionChanges
            {
                Added = [new SyncSession
                {
                    Id = sessionId,
                    OriginalText = "Hello",
                    OptimizedText = "Hi there",
                    CreatedAt = DateTime.UtcNow
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _historyService.DidNotReceive().AddSessionAsync(Arg.Any<OptimizationSession>());
    }

    [Fact]
    public async Task PullSession_Deleted_ShouldDelete()
    {
        var sut = CreateSut();
        var sessionId = Guid.NewGuid();

        var pullResponse = new SyncPullResponse
        {
            Sessions = new SyncSessionChanges
            {
                Deleted = [sessionId]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _historyService.Received(1).DeleteSessionAsync(sessionId);
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

public class SyncClientServicePullKanbanColumnTests
{
    private readonly IKanbanColumnService _columnService = Substitute.For<IKanbanColumnService>();

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
            columnService: _columnService);
    }

    private static async Task<(int Pulled, int DecryptionErrors, bool PullSucceeded)> InvokePullChangesAsync(
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

        var resultProperty = task.GetType().GetProperty("Result")!;
        var result = resultProperty.GetValue(task)!;
        var pulled = (int)result.GetType().GetField("Item1")!.GetValue(result)!;
        var errors = (int)result.GetType().GetField("Item2")!.GetValue(result)!;
        var pullOk = (bool)result.GetType().GetField("Item3")!.GetValue(result)!;
        return (pulled, errors, pullOk);
    }

    [Fact]
    public async Task PullKanbanColumn_NewRemote_ShouldImport()
    {
        var sut = CreateSut();
        var columnId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _columnService.GetAsync(columnId).Returns((KanbanColumn?)null);

        var pullResponse = new SyncPullResponse
        {
            KanbanColumns = new SyncEntityChanges<SyncKanbanColumn>
            {
                Upserted = [new SyncKanbanColumn
                {
                    Id = columnId,
                    Name = "In Progress",
                    SortOrder = 1,
                    UpdatedAt = now
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _columnService.Received(1).ImportAsync(
            Arg.Is<KanbanColumn>(c => c.Id == columnId && c.Name == "In Progress"));
    }

    [Fact]
    public async Task PullKanbanColumn_RemoteNewer_ShouldUpdate()
    {
        var sut = CreateSut();
        var columnId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _columnService.GetAsync(columnId).Returns(new KanbanColumn
        {
            Id = columnId,
            Name = "Old Name",
            UpdatedAt = now.AddMinutes(-5)
        });

        var pullResponse = new SyncPullResponse
        {
            KanbanColumns = new SyncEntityChanges<SyncKanbanColumn>
            {
                Upserted = [new SyncKanbanColumn
                {
                    Id = columnId,
                    Name = "New Name",
                    SortOrder = 2,
                    UpdatedAt = now
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _columnService.Received(1).ImportAsync(
            Arg.Is<KanbanColumn>(c => c.Id == columnId));
    }

    [Fact]
    public async Task PullKanbanColumn_RemoteOlder_ShouldSkip()
    {
        var sut = CreateSut();
        var columnId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _columnService.GetAsync(columnId).Returns(new KanbanColumn
        {
            Id = columnId,
            Name = "Newer local",
            UpdatedAt = now
        });

        var pullResponse = new SyncPullResponse
        {
            KanbanColumns = new SyncEntityChanges<SyncKanbanColumn>
            {
                Upserted = [new SyncKanbanColumn
                {
                    Id = columnId,
                    Name = "Older remote",
                    SortOrder = 1,
                    UpdatedAt = now.AddMinutes(-5)
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _columnService.DidNotReceive().ImportAsync(Arg.Any<KanbanColumn>());
    }

    [Fact]
    public async Task PullKanbanColumn_DeletedIds_ShouldNotProcessDeletes()
    {
        // Kanban columns don't process deletes — deletion is only allowed client-side for empty columns
        var sut = CreateSut();
        var columnId = Guid.NewGuid();

        var pullResponse = new SyncPullResponse
        {
            KanbanColumns = new SyncEntityChanges<SyncKanbanColumn>
            {
                Deleted = [columnId]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _columnService.DidNotReceive().DeleteAsync(Arg.Any<Guid>());
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

public class SyncClientServicePullDeletionTests
{
    private readonly ITemplateService _templateService = Substitute.For<ITemplateService>();
    private readonly IProviderService _providerService = Substitute.For<IProviderService>();
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly ITodoService _todoService = Substitute.For<ITodoService>();

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

    private static async Task InvokePullChangesAsync(
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
    }

    [Fact]
    public async Task PullTemplateDeleted_ShouldDelete()
    {
        var sut = CreateSut();
        var templateId = Guid.NewGuid();
        _templateService.GetTemplatesAsync().Returns(new List<OptimizationTemplate>());

        var pullResponse = new SyncPullResponse
        {
            Templates = new SyncEntityChanges<SyncTemplate>
            {
                Deleted = [templateId]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _templateService.Received(1).DeleteTemplateAsync(templateId);
    }

    [Fact]
    public async Task PullProviderDeleted_ShouldDelete()
    {
        var sut = CreateSut();
        var providerId = Guid.NewGuid();

        var pullResponse = new SyncPullResponse
        {
            Providers = new SyncEntityChanges<SyncProvider>
            {
                Deleted = [providerId]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _providerService.Received(1).DeleteProviderAsync(providerId);
    }

    [Fact]
    public async Task PullMemoryDeleted_ShouldDelete()
    {
        var sut = CreateSut();
        var memoryId = Guid.NewGuid();

        var pullResponse = new SyncPullResponse
        {
            Memories = new SyncEntityChanges<SyncMemory>
            {
                Deleted = [memoryId]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _memoryService.Received(1).DeleteObjectAsync(memoryId);
    }

    [Fact]
    public async Task PullTodoDeleted_ShouldDelete()
    {
        var sut = CreateSut();
        var todoId = Guid.NewGuid();

        var pullResponse = new SyncPullResponse
        {
            Todos = new SyncEntityChanges<SyncTodo>
            {
                Deleted = [todoId]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _todoService.Received(1).DeleteAsync(todoId);
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

public class SyncClientServicePullSettingsTests
{
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();

    private SyncClientService CreateSut()
    {
        var dpapiHelper = Substitute.For<DpapiHelper>(
            NullLogger<DpapiHelper>.Instance);
        var mapper = new SyncMapper(dpapiHelper);

        return new SyncClientService(
            Substitute.For<IAuthService>(),
            _settingsService,
            Substitute.For<ITemplateService>(),
            Substitute.For<IProviderService>(),
            Substitute.For<IHistoryService>(),
            Substitute.For<IMemoryService>(),
            mapper,
            Substitute.For<IHttpClientFactory>(),
            NullLogger<SyncClientService>.Instance);
    }

    private static async Task InvokePullChangesAsync(
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
    }

    [Fact]
    public async Task PullSettings_WhenPresent_ShouldApplyAndSave()
    {
        var sut = CreateSut();
        var currentSettings = new AppSettings { Theme = 0 };
        _settingsService.GetSettingsAsync().Returns(currentSettings);

        var pullResponse = new SyncPullResponse
        {
            Settings = new SyncSettings
            {
                Theme = 2,
                StartMinimized = true,
                ModifiedAt = DateTime.UtcNow
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _settingsService.Received(1).SaveSettingsAsync(
            Arg.Is<AppSettings>(s => s.Theme == 2 && s.StartMinimized));
    }

    [Fact]
    public async Task PullSettings_WhenNull_ShouldNotSaveSettings()
    {
        var sut = CreateSut();

        var pullResponse = new SyncPullResponse
        {
            Settings = null
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _settingsService.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
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

public class SyncClientServicePullCountsTests
{
    private readonly ITodoService _todoService = Substitute.For<ITodoService>();
    private readonly IKanbanColumnService _columnService = Substitute.For<IKanbanColumnService>();
    private readonly IHistoryService _historyService = Substitute.For<IHistoryService>();
    private readonly ITemplateService _templateService = Substitute.For<ITemplateService>();

    private SyncClientService CreateSut()
    {
        var dpapiHelper = Substitute.For<DpapiHelper>(
            NullLogger<DpapiHelper>.Instance);
        var mapper = new SyncMapper(dpapiHelper);

        return new SyncClientService(
            Substitute.For<IAuthService>(),
            Substitute.For<ISettingsService>(),
            _templateService,
            Substitute.For<IProviderService>(),
            _historyService,
            Substitute.For<IMemoryService>(),
            mapper,
            Substitute.For<IHttpClientFactory>(),
            NullLogger<SyncClientService>.Instance,
            todoService: _todoService,
            columnService: _columnService);
    }

    private static async Task<(int Pulled, int DecryptionErrors, bool PullSucceeded)> InvokePullChangesAsync(
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

        var resultProperty = task.GetType().GetProperty("Result")!;
        var result = resultProperty.GetValue(task)!;
        var pulled = (int)result.GetType().GetField("Item1")!.GetValue(result)!;
        var errors = (int)result.GetType().GetField("Item2")!.GetValue(result)!;
        var pullOk = (bool)result.GetType().GetField("Item3")!.GetValue(result)!;
        return (pulled, errors, pullOk);
    }

    [Fact]
    public async Task PullChanges_ReturnsPulledCount_AsSumOfAllUpsertedEntities()
    {
        var sut = CreateSut();
        var now = DateTime.UtcNow;

        _templateService.GetTemplatesAsync().Returns(new List<OptimizationTemplate>());
        _historyService.GetSessionAsync(Arg.Any<Guid>()).Returns((OptimizationSession?)null);
        _todoService.GetAsync(Arg.Any<Guid>()).Returns((TodoItem?)null);
        _columnService.GetAsync(Arg.Any<Guid>()).Returns((KanbanColumn?)null);

        var pullResponse = new SyncPullResponse
        {
            Templates = new SyncEntityChanges<SyncTemplate>
            {
                Upserted = [
                    new SyncTemplate { Id = Guid.NewGuid(), Name = "T1", Prompt = "p1", CreatedAt = now },
                    new SyncTemplate { Id = Guid.NewGuid(), Name = "T2", Prompt = "p2", CreatedAt = now }
                ]
            },
            Sessions = new SyncSessionChanges
            {
                Added = [new SyncSession { Id = Guid.NewGuid(), OriginalText = "a", OptimizedText = "b", CreatedAt = now }]
            },
            KanbanColumns = new SyncEntityChanges<SyncKanbanColumn>
            {
                Upserted = [new SyncKanbanColumn { Id = Guid.NewGuid(), Name = "Col1", UpdatedAt = now }]
            },
            Todos = new SyncEntityChanges<SyncTodo>
            {
                Upserted = [new SyncTodo { Id = Guid.NewGuid(), Title = "Todo1", UpdatedAt = now }]
            }
        };

        var (pulled, _, pullOk) = await InvokePullChangesAsync(sut, pullResponse);

        pulled.Should().Be(5); // 2 templates + 1 session + 1 kanban column + 1 todo
        pullOk.Should().BeTrue();
    }

    [Fact]
    public async Task PullChanges_HttpFailure_ReturnsPullSucceededFalse()
    {
        var sut = CreateSut();

        var handler = new MockHttpMessageHandler("", HttpStatusCode.InternalServerError);
        var client = new HttpClient(handler);
        var settings = new AppSettings { LastSyncTimestamp = DateTime.UtcNow.AddMinutes(-10) };

        var method = typeof(SyncClientService)
            .GetMethod("PullChangesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(sut, [client, "http://test", settings])!;
        await task;

        var resultProperty = task.GetType().GetProperty("Result")!;
        var result = resultProperty.GetValue(task)!;
        var pullOk = (bool)result.GetType().GetField("Item3")!.GetValue(result)!;

        pullOk.Should().BeFalse();
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseJson;
        private readonly HttpStatusCode _statusCode;

        public MockHttpMessageHandler(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseJson = responseJson;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseJson, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}

public class SyncClientServicePushRequestTests
{
    private readonly IAuthService _authService = Substitute.For<IAuthService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly ITemplateService _templateService = Substitute.For<ITemplateService>();
    private readonly IProviderService _providerService = Substitute.For<IProviderService>();
    private readonly IHistoryService _historyService = Substitute.For<IHistoryService>();
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly ITodoService _todoService = Substitute.For<ITodoService>();
    private readonly IKanbanColumnService _columnService = Substitute.For<IKanbanColumnService>();

    private SyncClientService CreateSut()
    {
        var dpapiHelper = Substitute.For<DpapiHelper>(
            NullLogger<DpapiHelper>.Instance);
        var mapper = new SyncMapper(dpapiHelper);

        return new SyncClientService(
            _authService,
            _settingsService,
            _templateService,
            _providerService,
            _historyService,
            _memoryService,
            mapper,
            Substitute.For<IHttpClientFactory>(),
            NullLogger<SyncClientService>.Instance,
            todoService: _todoService,
            columnService: _columnService);
    }

    private async Task<SyncPushRequest?> InvokePushChangesAsync(SyncClientService sut, AppSettings settings)
    {
        var handler = new CapturingHttpMessageHandler();
        var client = new HttpClient(handler);

        var method = typeof(SyncClientService)
            .GetMethod("PushChangesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(sut, [client, "http://test", settings])!;
        await task;

        if (handler.CapturedBody is not null)
        {
            return JsonSerializer.Deserialize<SyncPushRequest>(handler.CapturedBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }

        return null;
    }

    [Fact]
    public async Task PushChanges_ExcludesBuiltInTemplates()
    {
        var sut = CreateSut();
        var settings = new AppSettings
        {
            SyncEnabled = true,
            ServerUrl = "http://test",
            LastSyncTimestamp = DateTime.UtcNow.AddMinutes(-10)
        };

        _templateService.GetTemplatesAsync().Returns(new List<OptimizationTemplate>
        {
            new() { Id = Guid.NewGuid(), Name = "Custom", Prompt = "p", IsBuiltIn = false },
            new() { Id = Guid.NewGuid(), Name = "Built-in", Prompt = "p", IsBuiltIn = true }
        });
        _providerService.GetProvidersAsync().Returns(new List<AiProvider>());
        _historyService.SearchSessionsAsync(fromDate: Arg.Any<DateTime?>()).Returns(new List<OptimizationSession>());
        _memoryService.GetAllObjectsAsync().Returns(new List<MemoryObject>());
        _columnService.GetAllAsync().Returns(Array.Empty<KanbanColumn>());
        _todoService.GetAllAsync().Returns(new List<TodoItem>());

        var request = await InvokePushChangesAsync(sut, settings);

        request.Should().NotBeNull();
        request!.Templates.Upserted.Should().HaveCount(1);
        request.Templates.Upserted[0].Name.Should().Be("Custom");
    }

    [Fact]
    public async Task PushChanges_ExcludesPiaCloudProviders()
    {
        var sut = CreateSut();
        var settings = new AppSettings
        {
            SyncEnabled = true,
            ServerUrl = "http://test",
            LastSyncTimestamp = DateTime.UtcNow.AddMinutes(-10)
        };

        _templateService.GetTemplatesAsync().Returns(new List<OptimizationTemplate>());
        _providerService.GetProvidersAsync().Returns(new List<AiProvider>
        {
            new() { Id = Guid.NewGuid(), Name = "OpenAI", Endpoint = "https://api.openai.com", ProviderType = AiProviderType.OpenAI },
            new() { Id = Guid.NewGuid(), Name = "Pia Cloud", Endpoint = "https://cloud", ProviderType = AiProviderType.PiaCloud }
        });
        _historyService.SearchSessionsAsync(fromDate: Arg.Any<DateTime?>()).Returns(new List<OptimizationSession>());
        _memoryService.GetAllObjectsAsync().Returns(new List<MemoryObject>());
        _columnService.GetAllAsync().Returns(Array.Empty<KanbanColumn>());
        _todoService.GetAllAsync().Returns(new List<TodoItem>());

        var request = await InvokePushChangesAsync(sut, settings);

        request.Should().NotBeNull();
        request!.Providers.Upserted.Should().HaveCount(1);
        request.Providers.Upserted[0].Name.Should().Be("OpenAI");
    }

    [Fact]
    public async Task PushChanges_UsesSessionsSinceLastSync()
    {
        var sut = CreateSut();
        var lastSync = DateTime.UtcNow.AddMinutes(-10);
        var settings = new AppSettings
        {
            SyncEnabled = true,
            ServerUrl = "http://test",
            LastSyncTimestamp = lastSync
        };

        _templateService.GetTemplatesAsync().Returns(new List<OptimizationTemplate>());
        _providerService.GetProvidersAsync().Returns(new List<AiProvider>());
        _historyService.SearchSessionsAsync(fromDate: Arg.Any<DateTime?>()).Returns(new List<OptimizationSession>
        {
            new() { Id = Guid.NewGuid(), OriginalText = "a", OptimizedText = "b" }
        });
        _memoryService.GetAllObjectsAsync().Returns(new List<MemoryObject>());
        _columnService.GetAllAsync().Returns(Array.Empty<KanbanColumn>());
        _todoService.GetAllAsync().Returns(new List<TodoItem>());

        var request = await InvokePushChangesAsync(sut, settings);

        request.Should().NotBeNull();
        request!.Sessions.Added.Should().HaveCount(1);
        request.LastSyncTimestamp.Should().Be(lastSync);

        // Verify SearchSessionsAsync was called with the lastSync date
        await _historyService.Received(1).SearchSessionsAsync(fromDate: lastSync);
    }

    private class CapturingHttpMessageHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);

            var pushResponse = new SyncPushResponse
            {
                ServerTimestamp = DateTime.UtcNow,
                Conflicts = []
            };
            var json = JsonSerializer.Serialize(pushResponse, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}

public class SyncClientServiceTodoBackwardCompatTests
{
    private readonly ITodoService _todoService = Substitute.For<ITodoService>();
    private readonly IKanbanColumnService _columnService = Substitute.For<IKanbanColumnService>();

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
            todoService: _todoService,
            columnService: _columnService);
    }

    private static async Task InvokePullChangesAsync(
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
    }

    [Fact]
    public async Task PullTodo_NoColumnId_CompletedStatus_AssignsClosedColumn()
    {
        var sut = CreateSut();
        var todoId = Guid.NewGuid();
        var closedColumnId = Guid.NewGuid();

        _todoService.GetAsync(todoId).Returns((TodoItem?)null);
        _columnService.GetClosedColumnAsync().Returns(new KanbanColumn
        {
            Id = closedColumnId,
            Name = "Closed"
        });

        var pullResponse = new SyncPullResponse
        {
            Todos = new SyncEntityChanges<SyncTodo>
            {
                Upserted = [new SyncTodo
                {
                    Id = todoId,
                    Title = "Done todo",
                    Status = (int)TodoStatus.Completed,
                    UpdatedAt = DateTime.UtcNow
                    // No ColumnId
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _todoService.Received(1).ImportAsync(
            Arg.Is<TodoItem>(t => t.Id == todoId && t.ColumnId == closedColumnId));
    }

    [Fact]
    public async Task PullTodo_NoColumnId_PendingStatus_AssignsDefaultColumn()
    {
        var sut = CreateSut();
        var todoId = Guid.NewGuid();
        var defaultColumnId = Guid.NewGuid();

        _todoService.GetAsync(todoId).Returns((TodoItem?)null);
        _columnService.GetDefaultViewColumnAsync().Returns(new KanbanColumn
        {
            Id = defaultColumnId,
            Name = "To Do"
        });

        var pullResponse = new SyncPullResponse
        {
            Todos = new SyncEntityChanges<SyncTodo>
            {
                Upserted = [new SyncTodo
                {
                    Id = todoId,
                    Title = "New todo",
                    Status = (int)TodoStatus.Pending,
                    UpdatedAt = DateTime.UtcNow
                    // No ColumnId
                }]
            }
        };

        await InvokePullChangesAsync(sut, pullResponse);

        await _todoService.Received(1).ImportAsync(
            Arg.Is<TodoItem>(t => t.Id == todoId && t.ColumnId == defaultColumnId));
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
