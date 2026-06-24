using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Covers the revocation surface VM: grants are projected from
/// <see cref="IToolPermissionService.List"/> with plugin display names resolved via
/// <see cref="IPluginService"/>, Revoke delegates to the service, and the list
/// refreshes when the grant store raises <see cref="IToolPermissionService.Changed"/>.
/// </summary>
public class ToolPermissionsSettingsViewModelTests
{
    private static readonly Guid PluginA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PluginB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static (ToolPermissionsSettingsViewModel sut, IToolPermissionService permissions, IPluginService plugins) Create(
        IReadOnlyList<ToolGrant>? grants = null,
        IReadOnlyList<SyncPlugin>? configs = null)
    {
        var permissions = Substitute.For<IToolPermissionService>();
        permissions.List().Returns(grants ?? []);

        var plugins = Substitute.For<IPluginService>();
        plugins.GetAllPluginConfigs().Returns(configs ?? []);

        var sut = new ToolPermissionsSettingsViewModel(permissions, plugins, NullLogger<SettingsViewModel>.Instance);
        return (sut, permissions, plugins);
    }

    [Fact]
    public void Ctor_BuildsGrants_ResolvingPluginName()
    {
        var grant = new ToolGrant(PluginA, "create_todo", DateTimeOffset.UtcNow);
        var config = new SyncPlugin { Id = PluginA, Name = "Memory" };

        var (sut, _, _) = Create([grant], [config]);

        var row = Assert.Single(sut.Grants);
        Assert.Equal(PluginA, row.PluginId);
        Assert.Equal("Memory", row.PluginName);
        Assert.Equal("create_todo", row.ToolName);
        Assert.True(sut.HasGrants);
    }

    [Fact]
    public void Ctor_FallsBackToPluginId_WhenConfigMissing()
    {
        var grant = new ToolGrant(PluginB, "create_reminder", DateTimeOffset.UtcNow);

        var (sut, _, _) = Create([grant], []);

        var row = Assert.Single(sut.Grants);
        Assert.Equal(PluginB.ToString(), row.PluginName);
    }

    [Fact]
    public void EmptyGrants_HasGrantsFalse()
    {
        var (sut, _, _) = Create([], []);

        Assert.Empty(sut.Grants);
        Assert.False(sut.HasGrants);
    }

    [Fact]
    public async Task Revoke_CallsRevokeAsync()
    {
        var grant = new ToolGrant(PluginA, "create_todo", DateTimeOffset.UtcNow);
        var (sut, permissions, _) = Create([grant], [new SyncPlugin { Id = PluginA, Name = "Memory" }]);
        var row = Assert.Single(sut.Grants);

        await sut.RevokeCommand.ExecuteAsync(row);

        await permissions.Received(1).RevokeAsync(PluginA, "create_todo");
    }

    [Fact]
    public void Changed_RefreshesGrants()
    {
        var initial = new ToolGrant(PluginA, "create_todo", DateTimeOffset.UtcNow);
        var (sut, permissions, _) = Create([initial], [new SyncPlugin { Id = PluginA, Name = "Memory" }]);
        Assert.Single(sut.Grants);

        // The store now reports two grants; raising Changed must reproject.
        permissions.List().Returns(
        [
            initial,
            new ToolGrant(PluginB, "create_reminder", DateTimeOffset.UtcNow),
        ]);
        permissions.Changed += Raise.Event<EventHandler>(permissions, EventArgs.Empty);

        Assert.Equal(2, sut.Grants.Count);
        Assert.True(sut.HasGrants);
    }
}
