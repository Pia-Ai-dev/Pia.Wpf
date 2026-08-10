using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Localization;
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
        // An external-route tool the session tier withholds by NAME, while the permanent tier takes it like
        // every other row.
        new(McpPlugin, "some-mcp-server", "git_stash", "Stash", IsExternalRoute: true, ServerDeclaredDestructive: false),
        // A benign built-in under an external-sounding plugin: only the tool name and the hint move an offer.
        new(PluginB, "renamed-by-the-server", "publish_note", null, IsExternalRoute: false, ServerDeclaredDestructive: false),
    ];

    private static (ToolPermissionsSettingsViewModel sut, IToolPermissionService permissions, IPluginService plugins) CreateWithCatalog(
        IReadOnlyList<ToolCatalogEntry>? catalog = null)
    {
        var permissions = Substitute.For<IToolPermissionService>();
        permissions.List().Returns([]);
        permissions.ListSessionGrants().Returns([]);

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

    /// <summary>Columns: tool, and the caution its row carries. Every row offers BOTH tiers.</summary>
    [Theory]
    [InlineData("write_file", ToolGrantCaution.None)]
    [InlineData("create_todo", ToolGrantCaution.None)]
    [InlineData("send_email", ToolGrantCaution.None)]
    [InlineData("delete_file", ToolGrantCaution.Destructive)]
    [InlineData("git_switch", ToolGrantCaution.WorkDiscarding)]
    [InlineData("create_scheduled_research", ToolGrantCaution.AuthorityAuthoring)]
    [InlineData("sync_index", ToolGrantCaution.Destructive)]
    [InlineData("git_stash", ToolGrantCaution.WorkDiscarding)]
    [InlineData("publish_note", ToolGrantCaution.None)]
    public void EachRowOffersBothTiers_AndClassifiesItsOwnCaution(string toolName, ToolGrantCaution caution)
    {
        var (sut, _, _) = CreateWithCatalog();
        var row = Row(sut, toolName);

        Assert.Equal(caution, row.Caution);
        // Untouched, so nothing is said yet — the classification alone must not put a line on the page.
        Assert.False(row.HasCaution);
    }

    /// <summary><c>sync_index</c> is a benign NAME on a non-delete-like MCP tool, so only the server's own
    /// hint moves it — and it now moves the caution rather than what the row offers.</summary>
    [Fact]
    public void AServerDeclaredDestructiveTool_GainsTheDestructiveCaution()
    {
        var mirror = new ToolCatalogEntry(
            McpPlugin, "some-mcp-server", "sync_index", "Sync", IsExternalRoute: true, ServerDeclaredDestructive: false);
        var (withoutHint, _, _) = CreateWithCatalog([mirror]);

        // Non-vacuity: the same name, same route, same allowlist answer — only the hint differs.
        Assert.Equal(ToolGrantCaution.None, Row(withoutHint, "sync_index").Caution);

        var (withHint, _, _) = CreateWithCatalog([mirror with { ServerDeclaredDestructive = true }]);

        Assert.Equal(ToolGrantCaution.Destructive, Row(withHint, "sync_index").Caution);
    }

    /// <summary>The note is advice on a choice already made, so it appears on exactly the rows that both carry a
    /// caution and hold a grant — at EITHER tier, since either one lets the tool run unasked.</summary>
    [Fact]
    public void TheCautionAppears_OnlyOnACautionedRowThatHoldsAGrant()
    {
        var (sut, _, _) = CreateWithCatalog();

        var rows = sut.ToolCatalog.SelectMany(g => g.Tools).ToList();
        // Both sides of the equivalence below are populated, so neither half of it is vacuous.
        Assert.Contains(rows, r => r.Caution != ToolGrantCaution.None);
        Assert.Contains(rows, r => r.Caution == ToolGrantCaution.None);
        Assert.All(rows, r => Assert.False(r.HasCaution));

        foreach (var row in rows)
        {
            var cautioned = row.Caution != ToolGrantCaution.None;

            row.AllowedForSession = true;
            Assert.Equal(cautioned, row.HasCaution);
            row.AllowedForSession = false;
            Assert.False(row.HasCaution);

            // "Always" alone is the tick a user makes on a delete-like tool, so it must raise it on its own.
            row.AllowedAlways = true;
            Assert.Equal(cautioned, row.HasCaution);
            row.AllowedAlways = false;
            Assert.False(row.HasCaution);
        }
    }

    /// <summary>Every row offers both tiers now, so the note's job changed with it: it describes what the tool
    /// does unsupervised, and must not name a box that is no longer missing.</summary>
    [Fact]
    public void ACautionedRow_ExplainsTheTool_RatherThanADisabledBox()
    {
        var (sut, _, _) = CreateWithCatalog();

        (string Tool, ToolGrantCaution Caution)[] cautioned =
        [
            ("delete_file", ToolGrantCaution.Destructive),
            ("git_switch", ToolGrantCaution.WorkDiscarding),
            ("create_scheduled_research", ToolGrantCaution.AuthorityAuthoring),
            ("sync_index", ToolGrantCaution.Destructive),
        ];

        var sessionLabel = LocalizationSource.Instance["ToolCatalog_UntilClose"];
        foreach (var (toolName, caution) in cautioned)
        {
            var row = Row(sut, toolName);
            Assert.Equal(caution, row.Caution);
            Assert.NotEmpty(row.CautionText);
            Assert.DoesNotContain(sessionLabel, row.CautionText, StringComparison.Ordinal);
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
    /// A grant outlives the metadata it was made under: an MCP server can add <c>destructiveHint</c> to a tool
    /// already granted for the session. The tick survives and the row gains the caution — where the old rule
    /// took the box away and left a tick beside a line saying that tick was impossible.
    /// </summary>
    [Fact]
    public void ASessionGrantOnAToolTheServerCallsDestructive_KeepsItsTick_AndCarriesTheCaution()
    {
        var (sut, permissions, _) = CreateWithCatalog();
        permissions.IsGrantedForSession(McpPlugin, "sync_index").Returns(true);
        permissions.Changed += Raise.Event<EventHandler>(permissions, EventArgs.Empty);

        var row = Row(sut, "sync_index");
        Assert.Equal(ToolGrantCaution.Destructive, row.Caution);
        Assert.True(row.AllowedForSession);
        Assert.True(row.HasCaution);

        // Turning it off must still reach the service, and the note leaves with the grant.
        row.AllowedForSession = false;

        Assert.False(row.HasCaution);
        permissions.Received(1).RevokeSessionGrant(McpPlugin, "sync_index");
    }

    /// <summary>Unticking "Always" must reach the service: the box is now enabled on every row, so a dead
    /// false-arm would leave a tick that clears in the UI over a grant that is never revoked.</summary>
    [Fact]
    public async Task UntickingAlways_RevokesTheStandingGrant()
    {
        var (sut, permissions, _) = CreateWithCatalog();
        var row = Row(sut, "send_email");

        row.AllowedAlways = true;
        await permissions.Received(1).GrantAsync(McpPlugin, "send_email");

        row.AllowedAlways = false;
        await permissions.Received(1).RevokeAsync(McpPlugin, "send_email");
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
