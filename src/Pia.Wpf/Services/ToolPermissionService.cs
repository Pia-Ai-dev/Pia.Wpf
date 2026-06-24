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
