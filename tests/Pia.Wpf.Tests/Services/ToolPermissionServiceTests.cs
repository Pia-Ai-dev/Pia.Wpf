using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The fake ISettingsService hands back one shared AppSettings instance, so mutate-and-readback works.</summary>
public class ToolPermissionServiceTests
{
    private static readonly Guid PluginA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PluginB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static (ToolPermissionService sut, ISettingsService settings, AppSettings appSettings) Create(
        AppSettings? initial = null)
    {
        var (sut, settings, appSettings, _) = CreateWithSessionStore(initial);
        return (sut, settings, appSettings);
    }

    /// <summary>A real session store, not a substitute: "a fresh session inherits nothing" is a fact about its lifetime.</summary>
    private static (ToolPermissionService sut, ISettingsService settings, AppSettings appSettings,
        SessionToolGrantStore session) CreateWithSessionStore(AppSettings? initial = null,
        SessionToolGrantStore? session = null)
    {
        var appSettings = initial ?? new AppSettings();
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(appSettings);
        var store = session ?? new SessionToolGrantStore();
        var sut = new ToolPermissionService(settings, store);
        return (sut, settings, appSettings, store);
    }

    [Theory]
    [InlineData("create_todo")]
    [InlineData("create_reminder")]
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
    // Declared by no handler in the tree, so the allowlist must not carry them either.
    [InlineData("create_object")]
    [InlineData("append_to_list")]
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

    [Fact]
    public void GrantForSession_IsReadBack_PerPluginAndTool()
    {
        var (sut, _, _) = Create();

        sut.GrantForSession(PluginA, "write_file");

        Assert.True(sut.IsGrantedForSession(PluginA, "write_file"));
        Assert.False(sut.IsGrantedForSession(PluginB, "write_file"));
        Assert.False(sut.IsGrantedForSession(PluginA, "delete_file"));
        // Case-sensitive, matching the persisted keys, so this tier can never match a name the standing tier would not.
        Assert.False(sut.IsGrantedForSession(PluginA, "WRITE_FILE"));
    }

    /// <summary><c>Changed</c> does fire — the settings list shows this tier now — but nothing durable moves.</summary>
    [Fact]
    public void GrantForSession_TouchesNeitherAppSettingsNorTheStandingTier()
    {
        var (sut, settings, appSettings) = Create();
        var raised = false;
        sut.Changed += (_, _) => raised = true;

        sut.GrantForSession(PluginA, "write_file");

        Assert.Empty(appSettings.AlwaysAllowedTools);
        Assert.Empty(sut.List());
        Assert.False(sut.IsGranted(PluginA, "write_file"));
        Assert.True(raised);
        settings.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
    }

    [Fact]
    public void ListSessionGrants_ProjectsTheSessionTierOnly()
    {
        var (sut, _, _) = Create();

        sut.GrantForSession(PluginA, "write_file");

        var row = Assert.Single(sut.ListSessionGrants());
        Assert.Equal(PluginA, row.PluginId);
        Assert.Equal("write_file", row.ToolName);
        Assert.Empty(sut.List());
    }

    [Fact]
    public void RevokeSessionGrant_ForgetsItAndRaisesChanged()
    {
        var (sut, _, _) = Create();
        sut.GrantForSession(PluginA, "write_file");

        var raised = false;
        sut.Changed += (_, _) => raised = true;

        sut.RevokeSessionGrant(PluginA, "write_file");

        Assert.False(sut.IsGrantedForSession(PluginA, "write_file"));
        Assert.Empty(sut.ListSessionGrants());
        Assert.True(raised);
    }

    /// <summary>The session IS the store instance; the shared <see cref="AppSettings"/> is what makes this about scope rather
    /// than about an empty set.</summary>
    [Fact]
    public async Task AFreshSession_InheritsNoSessionGrant_ButKeepsThePersistedOnes()
    {
        var (first, _, appSettings, _) = CreateWithSessionStore();
        await first.GrantAsync(PluginA, "create_todo");   // the persisted tier
        first.GrantForSession(PluginA, "write_file");     // the session tier
        Assert.True(first.IsGrantedForSession(PluginA, "write_file"));

        // A new store over the same settings document stands in for the next launch of the app.
        var (next, _, _, _) = CreateWithSessionStore(appSettings);

        Assert.False(next.IsGrantedForSession(PluginA, "write_file"));
        Assert.False(next.IsGranted(PluginA, "write_file"));
        Assert.True(next.IsGranted(PluginA, "create_todo"));
    }

    /// <summary>Revoking the standing grant leaves the session grant standing, and neither implies the other.</summary>
    [Fact]
    public async Task TheTwoTiersAreIndependent()
    {
        var (sut, _, _) = Create();

        await sut.GrantAsync(PluginA, "create_todo");
        Assert.True(sut.IsGranted(PluginA, "create_todo"));
        Assert.False(sut.IsGrantedForSession(PluginA, "create_todo"));

        sut.GrantForSession(PluginA, "create_todo");
        await sut.RevokeAsync(PluginA, "create_todo");
        Assert.False(sut.IsGranted(PluginA, "create_todo"));
        Assert.True(sut.IsGrantedForSession(PluginA, "create_todo"));
    }

    /// <summary>The set lives on the service rather than in <c>ActionCardBuilder</c>, so the gate's check cannot end up wider
    /// than the card's offer.</summary>
    [Theory]
    [InlineData("git_switch", true)]
    [InlineData("git_restore", true)]
    [InlineData("git_stash", true)]
    [InlineData("GIT_STASH", true)]
    [InlineData("git_commit", false)]
    [InlineData("write_file", false)]
    [InlineData("delete_file", false)]
    public void IsWorkDiscarding_CoversTheGitTrioOnly(string toolName, bool expected)
        => Assert.Equal(expected, ToolPermissionService.IsWorkDiscarding(toolName));
}
