using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Covers the deny-by-default eligibility allowlist and the persisted
/// per-(PluginId, ToolName) grant store. The fake ISettingsService returns a
/// single controlled AppSettings instance, so mutate-and-readback works.
/// </summary>
public class ToolPermissionServiceTests
{
    private static readonly Guid PluginA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PluginB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static (ToolPermissionService sut, ISettingsService settings, AppSettings appSettings) Create(
        AppSettings? initial = null)
    {
        var appSettings = initial ?? new AppSettings();
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(appSettings);
        var sut = new ToolPermissionService(settings);
        return (sut, settings, appSettings);
    }

    [Theory]
    [InlineData("create_object")]
    [InlineData("create_todo")]
    [InlineData("create_reminder")]
    [InlineData("append_to_list")]
    public void IsAutoApproveEligible_TrueForSafeAdditiveSet(string toolName)
    {
        var (sut, _, _) = Create();
        Assert.True(sut.IsAutoApproveEligible(toolName));
    }

    [Theory]
    [InlineData("update_object")]
    [InlineData("complete_todo")]
    [InlineData("write_file")]      // overwrite-class: substring heuristic would miss this
    [InlineData("delete_object")]
    [InlineData("delete_file")]
    [InlineData("update_todo")]
    [InlineData("update_reminder")]
    public void IsAutoApproveEligible_FalseForEverythingElse(string toolName)
    {
        var (sut, _, _) = Create();
        Assert.False(sut.IsAutoApproveEligible(toolName));
    }

    [Fact]
    public async Task GrantAsync_PersistsAndIsGrantedReadsBack()
    {
        var (sut, settings, appSettings) = Create();

        await sut.GrantAsync(PluginA, "create_todo");

        Assert.True(sut.IsGranted(PluginA, "create_todo"));
        Assert.Contains(appSettings.AlwaysAllowedTools,
            g => g.PluginId == PluginA && g.ToolName == "create_todo");
        await settings.Received().SaveSettingsAsync(appSettings);
    }

    [Fact]
    public async Task RevokeAsync_RemovesGrant()
    {
        var (sut, _, appSettings) = Create();
        await sut.GrantAsync(PluginA, "create_todo");
        Assert.True(sut.IsGranted(PluginA, "create_todo"));

        await sut.RevokeAsync(PluginA, "create_todo");

        Assert.False(sut.IsGranted(PluginA, "create_todo"));
        Assert.DoesNotContain(appSettings.AlwaysAllowedTools,
            g => g.PluginId == PluginA && g.ToolName == "create_todo");
    }

    [Fact]
    public async Task IsGranted_IsKeyedByPluginId_NotToolNameAlone()
    {
        var (sut, _, _) = Create();

        await sut.GrantAsync(PluginA, "create_todo");

        Assert.True(sut.IsGranted(PluginA, "create_todo"));
        Assert.False(sut.IsGranted(PluginB, "create_todo"));
    }

    [Fact]
    public async Task GrantAsync_IsIdempotent_NoDuplicateRows()
    {
        var (sut, _, appSettings) = Create();

        await sut.GrantAsync(PluginA, "create_todo");
        await sut.GrantAsync(PluginA, "create_todo");

        Assert.Equal(1, appSettings.AlwaysAllowedTools.Count(
            g => g.PluginId == PluginA && g.ToolName == "create_todo"));
    }

    [Fact]
    public void Constructor_LoadsExistingGrantsFromSettings()
    {
        var preexisting = new AppSettings
        {
            AlwaysAllowedTools =
            {
                new ToolGrant(PluginA, "create_reminder", DateTimeOffset.UtcNow)
            }
        };
        var (sut, _, _) = Create(preexisting);

        Assert.True(sut.IsGranted(PluginA, "create_reminder"));
    }

    [Fact]
    public void SettingsChanged_ReloadsGrantCache()
    {
        var (sut, settings, appSettings) = Create();
        Assert.False(sut.IsGranted(PluginB, "create_object"));

        appSettings.AlwaysAllowedTools.Add(
            new ToolGrant(PluginB, "create_object", DateTimeOffset.UtcNow));
        settings.SettingsChanged +=
            Raise.Event<EventHandler<AppSettings>>(settings, appSettings);

        Assert.True(sut.IsGranted(PluginB, "create_object"));
    }

    [Fact]
    public async Task List_ReturnsGrants()
    {
        var (sut, _, _) = Create();
        await sut.GrantAsync(PluginA, "create_todo");

        var list = sut.List();

        Assert.Single(list);
        Assert.Equal(PluginA, list[0].PluginId);
        Assert.Equal("create_todo", list[0].ToolName);
    }

    [Fact]
    public async Task GrantAsync_RaisesChanged()
    {
        var (sut, _, _) = Create();
        var raised = false;
        sut.Changed += (_, _) => raised = true;

        await sut.GrantAsync(PluginA, "create_todo");

        Assert.True(raised);
    }
}
