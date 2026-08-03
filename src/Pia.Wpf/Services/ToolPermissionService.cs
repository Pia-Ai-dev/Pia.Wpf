using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Singleton owner of tool-permission state: the deny-by-default eligibility
/// allowlist and the persisted per-(PluginId, ToolName) "always allow" grants.
/// </summary>
/// <remarks>
/// Eligibility is an explicit additive allowlist — deliberately NOT a
/// <c>ToolName.Contains("delete")</c> heuristic, which would misclassify the
/// overwrite-class <c>write_file</c> as safe (design §3, §5). The grant cache is
/// updated directly on grant/revoke (the in-memory <see cref="AppSettings"/>
/// instance is mutated and saved); the <see cref="ISettingsService.SettingsChanged"/>
/// handler covers external changes.
/// </remarks>
public class ToolPermissionService : IToolPermissionService
{
    /// <summary>
    /// Curated safe/additive set: create-only and append. Excludes every
    /// update_*, complete_todo, write_file (overwrite), and delete_* tool.
    /// </summary>
    private static readonly HashSet<string> AutoApproveAllowlist = new(StringComparer.Ordinal)
    {
        "create_object",
        "create_todo",
        "create_reminder",
        "append_to_list"
    };

    private readonly ISettingsService _settingsService;
    private readonly ISessionToolGrantStore _sessionGrants;
    private readonly object _lock = new();
    private HashSet<(Guid PluginId, string ToolName)> _grantedKeys = new();

    public event EventHandler? Changed;

    public ToolPermissionService(ISettingsService settingsService, ISessionToolGrantStore sessionGrants)
    {
        _settingsService = settingsService;
        _sessionGrants = sessionGrants;

        // Settings are loaded and cached by the time any service is constructed
        // (startup awaits GetSettingsAsync), so this returns immediately from cache.
        var settings = _settingsService.GetSettingsAsync().GetAwaiter().GetResult();
        ReloadCache(settings.AlwaysAllowedTools);

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public bool IsAutoApproveEligible(string toolName)
        => toolName is not null && AutoApproveAllowlist.Contains(toolName);

    /// <summary>
    /// Destructive stems. A server-defined MCP tool is just as irreversible when it is called
    /// <c>purge_records</c> or <c>wipe_index</c> as when it is called <c>delete_issue</c>, so the whole
    /// family is covered, not just "delete".
    /// </summary>
    private static readonly string[] DestructiveStems =
        ["delete", "remove", "purge", "drop", "wipe", "erase", "destroy", "truncate"];

    /// <summary>
    /// Built-in tools whose names are destructive. Used only by
    /// <see cref="IsPresumedExternalDeleteLike"/> to tell "the user granted our own delete tool" from
    /// "the user granted something destructive we do not ship" at a point where no plugin route exists yet.
    /// </summary>
    private static readonly HashSet<string> BuiltInDestructiveTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "delete_file",
        "delete_todo",
        "delete_reminder",
        "delete_scheduled_research",
        "forget"
    };

    /// <summary>
    /// Name heuristic for a delete/destructive tool, shared by the card builder, the interactive gate and
    /// the unattended grant gate so a destructive external (MCP) tool is treated the same in all three:
    /// never auto-approvable and never executable unattended, even though MCP is otherwise
    /// grantable-as-a-class.
    /// <para>
    /// POLICY: any <see cref="DestructiveStems"/> substring (delete/remove/purge/drop/wipe/erase/destroy/
    /// truncate), case-insensitive, plus the literal <c>forget</c>. Substring — not token — matching is
    /// deliberate: it is what "delete" already did, and every false positive (e.g. a hypothetical
    /// <c>dropbox_upload</c>) only ever adds friction, which is the safe direction for this check.
    /// </para>
    /// <para>
    /// This is a NAME HEURISTIC, not a boundary: it cannot see what a server-defined tool actually does,
    /// and the built-in destructive tools are excluded from auto-approval by the allowlist regardless.
    /// The real containment lives in the gates that consult it. Future upgrade: MCP exposes
    /// <c>ToolAnnotations.DestructiveHint</c>/<c>ReadOnlyHint</c> on <c>McpClientTool.ProtocolTool</c>,
    /// which could override this heuristic in the MORE-restricted direction only (a server must not be
    /// able to declare itself safe) — but nothing plumbs it out of <c>McpPluginToolHandler</c> today, so
    /// there is no reachable hint to consume from here.
    /// </para>
    /// </summary>
    public static bool IsDeleteLike(string? toolName)
        => toolName is not null
           && (toolName.Equals("forget", StringComparison.OrdinalIgnoreCase)
               || DestructiveStems.Any(stem => toolName.Contains(stem, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// True for a <see cref="IsDeleteLike"/> name that is NOT one of our built-in destructive tools, i.e. a
    /// PRESUMED external/MCP destructive tool. For use where the plugin routes cannot be consulted — a
    /// scheduled job's grant list is authored long before fire time and the MCP server set can change in
    /// between — so such a grant is refused at creation instead. The execution gate still re-derives real
    /// MCP-ness from <c>IPluginService</c>; this is a create-time filter, never the boundary.
    /// </summary>
    public static bool IsPresumedExternalDeleteLike(string? toolName)
        => IsDeleteLike(toolName) && !BuiltInDestructiveTools.Contains(toolName!);

    /// <summary>
    /// Tools whose ARGUMENTS ARE A GRANT LIST — calling one AUTHORS authority that some later, unattended run
    /// will exercise with nobody looking. <c>create_scheduled_research</c> / <c>update_scheduled_research</c>
    /// take a <c>grantedTools</c> CSV that becomes <c>ScheduledJob.GrantedTools</c>, which
    /// <c>ScheduledJobBackgroundService.ExecuteAgentTaskAsync</c> hands to the run as <c>GrantedWrites</c>,
    /// which <see cref="ToolAutonomy.Resolve"/> honours as a NAMED grant — and the FLOOR there is
    /// External-only, so a named built-in <c>delete_file</c> auto-runs unattended by design.
    /// <see cref="IsPresumedExternalDeleteLike"/> is that argument's only create-time filter and it
    /// deliberately does not strip our own destructive names.
    /// <para>
    /// The complete set as of this writing: only <c>ScheduledJobToolHandler</c> takes a grant list. Sub-agent
    /// delegation is orchestrator-internal and exposes no such tool. Add a name here if that changes.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> AuthorityAuthoringTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "create_scheduled_research",
        "update_scheduled_research"
    };

    /// <summary>
    /// True for a tool one of whose arguments is itself future authority — see
    /// <see cref="AuthorityAuthoringTools"/>. Case-insensitive like the other two name tests.
    /// </summary>
    public static bool IsAuthorityAuthoring(string? toolName)
        => toolName is not null && AuthorityAuthoringTools.Contains(toolName);

    /// <summary>
    /// Tools that discard uncommitted work without carrying a destructive STEM in their name, so
    /// <see cref="IsDeleteLike"/> cannot see them. Case-insensitive like <see cref="IsDeleteLike"/> (the old
    /// inline copy in <c>ActionCardBuilder</c> was case-sensitive; widening it only ever adds friction, which
    /// is the safe direction for this check).
    /// </summary>
    private static readonly HashSet<string> WorkDiscardingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "git_switch",
        "git_restore",
        "git_stash"
    };

    /// <summary>
    /// A tool that throws away uncommitted work. Lifted out of <c>ActionCardBuilder</c> (where it was the
    /// card's own stricter destructive rule) so <see cref="ToolAutonomy.IsSessionGrantOfferable"/> and the card
    /// share ONE definition: hermes #15's session tier is minted at the gate, and a gate rule wider than the
    /// card's would let a forged card mint a grant the card never offers.
    /// </summary>
    public static bool IsWorkDiscarding(string? toolName)
        => toolName is not null && WorkDiscardingTools.Contains(toolName);

    public bool IsGranted(Guid pluginId, string toolName)
    {
        lock (_lock)
        {
            return _grantedKeys.Contains((pluginId, toolName));
        }
    }

    /// <summary>
    /// hermes #15. The MIDDLE tier, read here so the interactive and voice gates consult one owner for all
    /// three answers (eligibility, session, standing) instead of growing a second injected dependency. The
    /// state itself lives in the singleton <see cref="ISessionToolGrantStore"/> — this is a pass-through, and
    /// deliberately touches neither <see cref="AppSettings"/> nor the persisted cache above.
    /// </summary>
    public bool IsGrantedForSession(Guid pluginId, string toolName)
        => _sessionGrants.IsGranted(pluginId, toolName);

    /// <summary>
    /// Record a session grant. NO settings write, NO <c>SaveSettingsAsync</c>, NO
    /// <see cref="Changed"/> event: nothing durable changed, and raising Changed would tell the settings
    /// grant list to refresh for a grant it can neither show nor revoke. Synchronous for the same reason —
    /// there is nothing to await.
    /// </summary>
    public void GrantForSession(Guid pluginId, string toolName)
        => _sessionGrants.Grant(pluginId, toolName);

    public async Task GrantAsync(Guid pluginId, string toolName)
    {
        var settings = await _settingsService.GetSettingsAsync();

        var exists = settings.AlwaysAllowedTools.Any(g => g.Matches(pluginId, toolName));
        if (!exists)
        {
            settings.AlwaysAllowedTools.Add(
                new ToolGrant(pluginId, toolName, DateTimeOffset.UtcNow));
        }

        ReloadCache(settings.AlwaysAllowedTools);
        await _settingsService.SaveSettingsAsync(settings);
        RaiseChanged();
    }

    public async Task RevokeAsync(Guid pluginId, string toolName)
    {
        var settings = await _settingsService.GetSettingsAsync();

        settings.AlwaysAllowedTools.RemoveAll(g => g.Matches(pluginId, toolName));

        ReloadCache(settings.AlwaysAllowedTools);
        await _settingsService.SaveSettingsAsync(settings);
        RaiseChanged();
    }

    public IReadOnlyList<ToolGrant> List()
    {
        var settings = _settingsService.GetSettingsAsync().GetAwaiter().GetResult();
        return settings.AlwaysAllowedTools.ToList();
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        ReloadCache(settings.AlwaysAllowedTools);
        RaiseChanged();
    }

    private void ReloadCache(IEnumerable<ToolGrant> grants)
    {
        var rebuilt = new HashSet<(Guid, string)>();
        foreach (var grant in grants)
        {
            rebuilt.Add((grant.PluginId, grant.ToolName));
        }

        lock (_lock)
        {
            _grantedKeys = rebuilt;
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
