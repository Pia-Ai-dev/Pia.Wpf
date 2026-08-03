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
        var (sut, settings, appSettings, _) = CreateWithSessionStore(initial);
        return (sut, settings, appSettings);
    }

    /// <summary>
    /// hermes #15. The same fixture, with the process-scoped session store visible — a real store, not a
    /// substitute, because "a fresh session inherits nothing" is a fact about the store's LIFETIME and a
    /// substitute would be asserting the mock's default.
    /// </summary>
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

    // ------------------------------------------------------ hermes #15, THE SESSION TIER

    /// <summary>
    /// T-SESS-1. A session grant is READ BACK by the same owner the gates ask, and is keyed per
    /// (plugin, tool) exactly like the persisted tier — a grant for one plugin's tool authorizes neither
    /// another plugin's same-named tool nor another tool of the same plugin.
    /// </summary>
    [Fact]
    public void GrantForSession_IsReadBack_PerPluginAndTool()
    {
        var (sut, _, _) = Create();

        sut.GrantForSession(PluginA, "write_file");

        Assert.True(sut.IsGrantedForSession(PluginA, "write_file"));
        Assert.False(sut.IsGrantedForSession(PluginB, "write_file"));
        Assert.False(sut.IsGrantedForSession(PluginA, "delete_file"));
        // Case-sensitive on the name, deliberately identical to the persisted grant keys, so this tier can
        // never match a name the standing tier would not.
        Assert.False(sut.IsGrantedForSession(PluginA, "WRITE_FILE"));
    }

    /// <summary>
    /// T-SESS-2, THE NO-LEAK FACT. A session grant writes NOTHING durable: no settings save, no
    /// <c>AlwaysAllowedTools</c> row, no standing grant, and no <c>Changed</c> event (which drives the
    /// settings grant list — announcing a grant it can neither show nor revoke would be a lie).
    /// <para>
    /// <b>Red demo (inject the defect, do not delete a mechanism):</b> make
    /// <c>ToolPermissionService.GrantForSession</c> also call
    /// <c>GrantAsync(pluginId, toolName).GetAwaiter().GetResult()</c> → the four assertions below red.
    /// </para>
    /// </summary>
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
        Assert.False(raised);
        settings.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
    }

    /// <summary>
    /// T-SESS-3, THE SCOPE FACT. The session IS the store instance, so a FRESH session inherits nothing —
    /// asserted over the SAME <see cref="AppSettings"/> object that still carries a persisted grant, which is
    /// what makes it a statement about scope rather than about an empty <c>HashSet</c>: the standing grant
    /// survives the new "process", the session grant does not.
    /// </summary>
    [Fact]
    public async Task AFreshSession_InheritsNoSessionGrant_ButKeepsThePersistedOnes()
    {
        var (first, _, appSettings, _) = CreateWithSessionStore();
        await first.GrantAsync(PluginA, "create_todo");   // the persisted tier
        first.GrantForSession(PluginA, "write_file");     // the session tier
        Assert.True(first.IsGrantedForSession(PluginA, "write_file"));

        // A NEW store over the SAME settings document = the next launch of the app.
        var (next, _, _, _) = CreateWithSessionStore(appSettings);

        Assert.False(next.IsGrantedForSession(PluginA, "write_file"));
        // …and it left NO durable trace for the next session to inherit through the other tier either, which
        // is the assertion that makes this a fact about the leak and not just about a fresh HashSet.
        Assert.False(next.IsGranted(PluginA, "write_file"));
        Assert.True(next.IsGranted(PluginA, "create_todo"));
    }

    /// <summary>
    /// T-SESS-4. The two tiers are independent in both directions: a standing grant is not a session grant
    /// (nothing reads it as one) and a session grant is not revoked by revoking the standing one — there is no
    /// revoke short of closing the app, which is what the button promises.
    /// </summary>
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

    /// <summary>
    /// T-SESS-5. The work-discarding set the session tier and the action card now SHARE. It lives here (beside
    /// <c>IsDeleteLike</c>) rather than inline in <c>ActionCardBuilder</c>, so the gate's mint check cannot end
    /// up wider than the card's offer.
    /// </summary>
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
