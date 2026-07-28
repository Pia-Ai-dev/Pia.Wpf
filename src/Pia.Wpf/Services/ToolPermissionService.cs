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
    private readonly object _lock = new();
    private HashSet<(Guid PluginId, string ToolName)> _grantedKeys = new();

    public event EventHandler? Changed;

    public ToolPermissionService(ISettingsService settingsService)
    {
        _settingsService = settingsService;

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

    public bool IsGranted(Guid pluginId, string toolName)
    {
        lock (_lock)
        {
            return _grantedKeys.Contains((pluginId, toolName));
        }
    }

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
