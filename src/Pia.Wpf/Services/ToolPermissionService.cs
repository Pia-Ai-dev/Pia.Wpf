using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Singleton owner of tool-permission state: the voice-mode auto-approve allowlist and the persisted
/// per-(PluginId, ToolName) "always allow" grants.
/// </summary>
/// <remarks>
/// The grant cache is updated directly on grant/revoke (the in-memory <see cref="AppSettings"/> instance is
/// mutated and saved); the <see cref="ISettingsService.SettingsChanged"/> handler covers external changes.
/// </remarks>
public class ToolPermissionService : IToolPermissionService
{
    /// <summary>Curated create-only set that voice may run unprompted; every other tool needs a grant.</summary>
    private static readonly HashSet<string> AutoApproveAllowlist = new(StringComparer.Ordinal)
    {
        "create_todo",
        "create_reminder"
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
        _sessionGrants.Changed += OnSessionGrantsChanged;
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
    /// Name heuristic for a delete/destructive tool: any <see cref="DestructiveStems"/> substring plus the
    /// literal <c>forget</c>, case-insensitive. It is what excludes a tool from the autonomy policy and from
    /// the unattended park; both grant tiers are offered and honoured regardless, and the Tool access page
    /// notes it on a row the user has ticked.
    /// </summary>
    /// <remarks>
    /// Substring — not token — matching is deliberate: a false positive (a hypothetical <c>dropbox_upload</c>)
    /// only ever adds friction, which is the safe direction here. <paramref name="serverDeclaredDestructive"/>
    /// is ORed in, so an MCP server's own hint can only widen this and never narrow it.
    /// </remarks>
    public static bool IsDeleteLike(string? toolName, bool serverDeclaredDestructive = false)
        => serverDeclaredDestructive
           || (toolName is not null
               && (toolName.Equals("forget", StringComparison.OrdinalIgnoreCase)
                   || DestructiveStems.Any(stem => toolName.Contains(stem, StringComparison.OrdinalIgnoreCase))));

    /// <summary>
    /// True for a <see cref="IsDeleteLike"/> name that is NOT one of our built-in destructive tools, i.e. a
    /// PRESUMED external/MCP destructive tool. Used where the plugin routes cannot be consulted: a scheduled
    /// job's grant list is authored long before fire time, so the model is refused such a name at creation.
    /// </summary>
    public static bool IsPresumedExternalDeleteLike(string? toolName)
        => IsDeleteLike(toolName) && !BuiltInDestructiveTools.Contains(toolName!);

    /// <summary>
    /// Tools whose ARGUMENTS ARE A GRANT LIST — calling one AUTHORS authority that some later, unattended run
    /// will exercise with nobody looking. Their <c>grantedTools</c> CSV becomes <c>ScheduledJob.GrantedTools</c>
    /// and reaches <see cref="ToolAutonomy.Resolve"/> as a NAMED grant, which auto-runs any tool it names —
    /// <c>delete_file</c> included. <see cref="IsPresumedExternalDeleteLike"/> is its only create-time filter.
    /// </summary>
    /// <remarks>Only <c>ScheduledJobToolHandler</c> takes a grant list today; add a name here if that
    /// changes. <c>create_routine_from_blueprint</c> is here despite taking no grant argument: the blueprint
    /// owns the grants, but the job it creates still exercises them unattended, which is what the caution is
    /// about.</remarks>
    private static readonly HashSet<string> AuthorityAuthoringTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "create_scheduled_research",
        "update_scheduled_research",
        "create_routine_from_blueprint"
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
    /// A tool that throws away uncommitted work without carrying a destructive stem. It gates no grant: it
    /// drives the card's red styling and its own warning copy, and the Tool access page's caution note.
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
    /// The MIDDLE tier, read here so a gate consults one owner for all three answers instead of growing a
    /// second injected dependency. A pass-through to the singleton <see cref="ISessionToolGrantStore"/>; it
    /// touches neither <see cref="AppSettings"/> nor the persisted cache above.
    /// </summary>
    public bool IsGrantedForSession(Guid pluginId, string toolName)
        => _sessionGrants.IsGranted(pluginId, toolName);

    /// <summary>
    /// Record a session grant. No settings write and nothing to await; <see cref="Changed"/> still reaches the
    /// settings list, which now shows this tier and can forget a row from it.
    /// </summary>
    public void GrantForSession(Guid pluginId, string toolName)
        => _sessionGrants.Grant(pluginId, toolName);

    public IReadOnlyList<ToolGrant> ListSessionGrants() => _sessionGrants.List();

    public void RevokeSessionGrant(Guid pluginId, string toolName)
        => _sessionGrants.Revoke(pluginId, toolName);

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

    // Re-raised untouched so subscribers keep one subscription for all tiers; the store already raised this
    // outside its lock, and the marshalling belongs to whoever is bound to it.
    private void OnSessionGrantsChanged(object? sender, EventArgs e) => RaiseChanged();

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
