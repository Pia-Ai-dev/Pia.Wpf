using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels;
using Pia.ViewModels.Models;
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

    /// <summary>Real store and real service, so the session tier's grant → Changed → reproject chain is covered.</summary>
    private static (ToolPermissionsSettingsViewModel sut, ToolPermissionService permissions) CreateOverRealStore()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(new AppSettings());
        var permissions = new ToolPermissionService(settingsService, new SessionToolGrantStore());

        var plugins = Substitute.For<IPluginService>();
        plugins.GetAllPluginConfigs().Returns([new SyncPlugin { Id = PluginA, Name = "Files" }]);

        var sut = new ToolPermissionsSettingsViewModel(permissions, plugins, NullLogger<SettingsViewModel>.Instance);
        return (sut, permissions);
    }

    [Fact]
    public void NoSessionGrants_HasSessionGrantsFalse()
    {
        var (sut, _, _) = Create([], []);

        Assert.Empty(sut.SessionGrants);
        Assert.False(sut.HasSessionGrants);
    }

    [Fact]
    public void SessionGrant_AddsARow_AndForgetRemovesIt()
    {
        var (sut, permissions) = CreateOverRealStore();
        Assert.False(sut.HasSessionGrants);

        permissions.GrantForSession(PluginA, "write_file");

        var row = Assert.Single(sut.SessionGrants);
        Assert.Equal("write_file", row.ToolName);
        Assert.Equal("Files", row.PluginName);
        Assert.True(sut.HasSessionGrants);
        // The tiers are separate lists over separate storage; a session grant must not appear as a standing one.
        Assert.Empty(sut.Grants);

        sut.ForgetSessionCommand.Execute(row);

        Assert.Empty(sut.SessionGrants);
        Assert.False(sut.HasSessionGrants);
        Assert.False(permissions.IsGrantedForSession(PluginA, "write_file"));
    }

    [Fact]
    public void OffThreadChanged_IsMarshalledBeforeTheBoundCollectionsMove()
    {
        var previous = SynchronizationContext.Current;
        var ui = new QueueingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            var (sut, permissions) = CreateOverRealStore();

            // A session grant is minted wherever the gate runs, which for an agent run is not the UI thread.
            var worker = new Thread(() => permissions.GrantForSession(PluginA, "write_file"));
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(10)));

            Assert.Empty(sut.SessionGrants);
            Assert.True(ui.Drain() > 0);
            Assert.Single(sut.SessionGrants);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    // ---- the pre-approval catalogue -------------------------------------------------------------------

    private static readonly Guid McpPlugin = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>Every shape the offer rules distinguish, so no test below reads a one-row catalogue.</summary>
    private static IReadOnlyList<ToolCatalogEntry> RealisticCatalog() =>
    [
        new(PluginA, "files", "write_file", "Write a file", IsExternalRoute: false, ServerDeclaredDestructive: false),
        new(PluginA, "files", "delete_file", "Delete a file", IsExternalRoute: false, ServerDeclaredDestructive: false),
        new(PluginB, "todo", "create_todo", "Create a todo", IsExternalRoute: false, ServerDeclaredDestructive: false),
        new(PluginB, "git", "git_switch", "Switch branch", IsExternalRoute: false, ServerDeclaredDestructive: false),
        new(PluginB, "scheduled-research", "create_scheduled_research", null, IsExternalRoute: false, ServerDeclaredDestructive: false),
        new(McpPlugin, "some-mcp-server", "send_email", "Send mail", IsExternalRoute: true, ServerDeclaredDestructive: false),
        new(McpPlugin, "some-mcp-server", "sync_index", "Sync", IsExternalRoute: true, ServerDeclaredDestructive: true),
        // The quadrant the two independent offer rules produce: an external tool the session rule withholds
        // by name while the standing rule still admits it.
        new(McpPlugin, "some-mcp-server", "git_stash", "Stash", IsExternalRoute: true, ServerDeclaredDestructive: false),
        // A built-in renamed through server metadata: no route calls it external, so no NAME may either.
        new(PluginB, "renamed-by-the-server", "publish_note", null, IsExternalRoute: false, ServerDeclaredDestructive: false),
    ];

    private static (ToolPermissionsSettingsViewModel sut, IToolPermissionService permissions, IPluginService plugins) CreateWithCatalog(
        IReadOnlyList<ToolCatalogEntry>? catalog = null)
    {
        var permissions = Substitute.For<IToolPermissionService>();
        permissions.List().Returns([]);
        permissions.ListSessionGrants().Returns([]);
        // The real four-name set, not a re-derived local copy.
        var allowlist = new ToolPermissionService(StubSettings(), new SessionToolGrantStore());
        permissions.IsAutoApproveEligible(Arg.Any<string>())
            .Returns(ci => allowlist.IsAutoApproveEligible(ci.Arg<string>()));

        var plugins = Substitute.For<IPluginService>();
        plugins.GetAllPluginConfigs().Returns([]);
        plugins.GetToolCatalog().Returns(catalog ?? RealisticCatalog());

        var sut = new ToolPermissionsSettingsViewModel(permissions, plugins, NullLogger<SettingsViewModel>.Instance);
        return (sut, permissions, plugins);
    }

    private static ISettingsService StubSettings()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        return settings;
    }

    private static ToolCatalogRow Row(ToolPermissionsSettingsViewModel sut, string toolName) =>
        sut.ToolCatalog.SelectMany(g => g.Tools).Single(r => r.ToolName == toolName);

    /// <summary>Columns: tool, offers "Until Pia closes", offers "Always", restriction shown when it does not.</summary>
    [Theory]
    [InlineData("write_file", true, false, ToolGrantRestriction.SessionOnly)]
    [InlineData("create_todo", true, true, ToolGrantRestriction.None)]
    [InlineData("send_email", true, true, ToolGrantRestriction.None)]
    [InlineData("delete_file", false, false, ToolGrantRestriction.Destructive)]
    [InlineData("git_switch", false, false, ToolGrantRestriction.WorkDiscarding)]
    [InlineData("create_scheduled_research", false, false, ToolGrantRestriction.AuthorityAuthoring)]
    [InlineData("sync_index", false, false, ToolGrantRestriction.Destructive)]
    // Offered permanently but not for the session, so no "always asks" line may sit beside its live toggle.
    [InlineData("git_stash", false, true, ToolGrantRestriction.None)]
    // Route-first: the name-only guess would call this plugin external and offer the standing tier.
    [InlineData("publish_note", true, false, ToolGrantRestriction.SessionOnly)]
    public void EachRowOffersExactlyTheTiersTheGateWouldHonour(
        string toolName, bool offersSession, bool offersAlways, ToolGrantRestriction restriction)
    {
        var (sut, _, _) = CreateWithCatalog();
        var row = Row(sut, toolName);

        Assert.Equal(offersSession, row.CanGrantForSession);
        Assert.Equal(offersAlways, row.CanGrantAlways);
        Assert.Equal(restriction, row.Restriction);
    }

    /// <summary>
    /// The button-that-does-nothing guard. <c>sync_index</c> is a benign NAME on a non-delete-like MCP tool:
    /// only the server's own hint withdraws both offers, so a dropped argument (both parameters default to
    /// false) shows up here and nowhere else.
    /// </summary>
    [Fact]
    public void AServerDeclaredDestructiveTool_OffersNeitherTier()
    {
        var mirror = new ToolCatalogEntry(
            McpPlugin, "some-mcp-server", "sync_index", "Sync", IsExternalRoute: true, ServerDeclaredDestructive: false);
        var (withoutHint, _, _) = CreateWithCatalog([mirror]);

        // Non-vacuity: the same name, same route, same allowlist answer — only the hint differs.
        Assert.True(Row(withoutHint, "sync_index").CanGrantForSession);
        Assert.True(Row(withoutHint, "sync_index").CanGrantAlways);

        var (withHint, _, _) = CreateWithCatalog([mirror with { ServerDeclaredDestructive = true }]);

        Assert.False(Row(withHint, "sync_index").CanGrantForSession);
        Assert.False(Row(withHint, "sync_index").CanGrantAlways);
    }

    [Fact]
    public void ARowOfferingAlways_IsAlwaysOneTheStandingRuleAdmits()
    {
        var (sut, _, _) = CreateWithCatalog();

        var offered = sut.ToolCatalog.SelectMany(g => g.Tools).Where(r => r.CanGrantAlways).ToList();
        Assert.NotEmpty(offered);

        foreach (var row in offered)
        {
            // A stated reason beside a working toggle claims the tool always asks while the gate honours the
            // grant the toggle mints.
            Assert.Equal(ToolGrantRestriction.None, row.Restriction);
            Assert.Null(row.ReasonKey);
        }

        // The tiers are not nested: the session rule is name-only, so it withholds a tool the standing rule
        // admits, and this row is the one that reaches the loop above through that quadrant.
        var stash = Row(sut, "git_stash");
        Assert.True(stash.CanGrantAlways);
        Assert.False(stash.CanGrantForSession);
        Assert.False(stash.HasReason);
    }

    [Fact]
    public void RowsThatCanBeGrantedAtNeitherTier_AreListedAndCarryAReason()
    {
        var (sut, _, _) = CreateWithCatalog();

        string[] neither = ["delete_file", "git_switch", "create_scheduled_research", "sync_index"];
        foreach (var toolName in neither)
        {
            var row = Row(sut, toolName);
            Assert.False(row.CanGrantForSession);
            Assert.False(row.CanGrantAlways);
            Assert.True(row.HasReason, $"{toolName} is shown with both toggles off and no stated reason");
            Assert.NotEqual(string.Empty, row.Reason);
        }
    }

    [Fact]
    public void TheCatalogIsGroupedByPlugin()
    {
        var (sut, _, _) = CreateWithCatalog();

        Assert.Equal(["files", "git", "renamed-by-the-server", "scheduled-research", "some-mcp-server", "todo"],
            sut.ToolCatalog.Select(g => g.PluginName));
        Assert.Equal(["delete_file", "write_file"], Group(sut, "files").Tools.Select(r => r.ToolName));
        Assert.True(sut.HasCatalog);
    }

    private static ToolCatalogGroup Group(ToolPermissionsSettingsViewModel sut, string pluginName) =>
        sut.ToolCatalog.Single(g => g.PluginName == pluginName);

    [Fact]
    public void TogglingUntilPiaCloses_MintsTheSameSessionGrantACardWould()
    {
        var settingsService = StubSettings();
        var permissions = new ToolPermissionService(settingsService, new SessionToolGrantStore());
        var plugins = Substitute.For<IPluginService>();
        plugins.GetAllPluginConfigs().Returns([new SyncPlugin { Id = PluginA, Name = "files" }]);
        plugins.GetToolCatalog().Returns(RealisticCatalog());

        var sut = new ToolPermissionsSettingsViewModel(permissions, plugins, NullLogger<SettingsViewModel>.Instance);
        var row = Row(sut, "write_file");
        Assert.False(row.AllowedForSession);

        row.AllowedForSession = true;

        Assert.True(permissions.IsGrantedForSession(PluginA, "write_file"));
        // The mint lands in the session tier, which is the same list a card's "Allow this session" fills.
        var listed = Assert.Single(sut.SessionGrants);
        Assert.Equal("write_file", listed.ToolName);
        Assert.True(Row(sut, "write_file").AllowedForSession);

        row.AllowedForSession = false;

        Assert.False(permissions.IsGrantedForSession(PluginA, "write_file"));
        Assert.Empty(sut.SessionGrants);
    }

    /// <summary>
    /// The standing toggle cannot await, so a refresh can land while the grant is still in flight. The row
    /// must follow the service without calling back into it, or that refresh revokes what it just granted.
    /// </summary>
    [Fact]
    public async Task TogglingAlways_GrantsOnce_AndARefreshDoesNotRevokeTheInFlightGrant()
    {
        var (sut, permissions, _) = CreateWithCatalog();
        permissions.IsGranted(Arg.Any<Guid>(), Arg.Any<string>()).Returns(false);
        var row = Row(sut, "send_email");

        row.AllowedAlways = true;

        await permissions.Received(1).GrantAsync(McpPlugin, "send_email");

        permissions.Changed += Raise.Event<EventHandler>(permissions, EventArgs.Empty);

        Assert.False(row.AllowedAlways);
        await permissions.Received(1).GrantAsync(McpPlugin, "send_email");
        await permissions.DidNotReceive().RevokeAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Fact]
    public void AGrantMadeElsewhere_ChecksTheRowWithoutRebuildingIt()
    {
        var (sut, permissions, _) = CreateWithCatalog();
        var row = Row(sut, "write_file");
        Assert.False(row.AllowedForSession);

        permissions.IsGrantedForSession(PluginA, "write_file").Returns(true);
        permissions.Changed += Raise.Event<EventHandler>(permissions, EventArgs.Empty);

        Assert.True(row.AllowedForSession);
        // Same instance: a Changed must not tear down the row a click is sitting on.
        Assert.Same(row, Row(sut, "write_file"));
    }

    /// <summary>
    /// A grant outlives its offer: an MCP server can add <c>destructiveHint</c> to a tool already granted.
    /// The row must stay revocable, or it shows a tick beside a line saying the tick is impossible.
    /// </summary>
    [Fact]
    public async Task AGrantOnATierNoLongerOnOffer_StaysRevocableFromTheRow()
    {
        var (sut, permissions, _) = CreateWithCatalog();
        permissions.IsGranted(McpPlugin, "sync_index").Returns(true);
        permissions.Changed += Raise.Event<EventHandler>(permissions, EventArgs.Empty);

        var row = Row(sut, "sync_index");
        Assert.False(row.CanGrantAlways);
        Assert.True(row.AllowedAlways);
        Assert.True(row.CanChangeAlways);

        // Turning it off is the only move available, and it must reach the service.
        row.AllowedAlways = false;

        Assert.False(row.CanChangeAlways);
        await permissions.Received(1).RevokeAsync(McpPlugin, "sync_index");
    }

    [Fact]
    public void EnablingAPlugin_RebuildsTheCatalog()
    {
        var (sut, _, plugins) = CreateWithCatalog([RealisticCatalog()[0]]);
        Assert.Single(sut.ToolCatalog.SelectMany(g => g.Tools));

        plugins.GetToolCatalog().Returns(RealisticCatalog());
        plugins.PluginsChanged += Raise.Event<EventHandler>(plugins, EventArgs.Empty);

        Assert.Equal(RealisticCatalog().Count, sut.ToolCatalog.SelectMany(g => g.Tools).Count());
    }

    /// <summary>Queues posted work for the test thread to drain, standing in for the WPF dispatcher.</summary>
    private sealed class QueueingSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queued = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queued) _queued.Enqueue((d, state));
        }

        public int Drain()
        {
            var ran = 0;
            while (true)
            {
                (SendOrPostCallback Callback, object? State) next;
                lock (_queued)
                {
                    if (_queued.Count == 0) return ran;
                    next = _queued.Dequeue();
                }

                next.Callback(next.State);
                ran++;
            }
        }
    }
}
