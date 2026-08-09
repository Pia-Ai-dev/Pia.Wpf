using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Localization;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

/// <summary>
/// The mandatory revocation surface, plus the pre-approval catalogue: lists the standing "always allow"
/// grants (Revoke) and the process-scoped session grants (Forget), and offers every other tool through the
/// same grant calls a card would make. Ctor takes only interfaces (DI guardrail); injected fields are
/// readonly (MVVM guardrail). All three collections are built synchronously, so no async initialization is
/// needed.
/// </summary>
public partial class ToolPermissionsSettingsViewModel : UiThreadViewModel
{
    private readonly IToolPermissionService _permissions;
    private readonly IPluginService _pluginService;
    private readonly ILogger<SettingsViewModel> _logger;

    public ObservableCollection<ToolGrantRow> Grants { get; } = [];

    /// <summary>The process-scoped tier: same row shape, but forgetting one writes nothing to settings.</summary>
    public ObservableCollection<ToolGrantRow> SessionGrants { get; } = [];

    /// <summary>Every grantable tool, grouped by plugin, so a tool can be pre-approved before it is first called.</summary>
    public ObservableCollection<ToolCatalogGroup> ToolCatalog { get; } = [];

    [ObservableProperty]
    private bool _hasGrants;

    [ObservableProperty]
    private bool _hasSessionGrants;

    [ObservableProperty]
    private bool _hasCatalog;

    public ToolPermissionsSettingsViewModel(
        IToolPermissionService permissions,
        IPluginService pluginService,
        ILogger<SettingsViewModel> logger)
    {
        _permissions = permissions;
        _pluginService = pluginService;
        _logger = logger;

        _permissions.Changed += OnPermissionsChanged;
        _pluginService.PluginsChanged += OnPluginsChanged;
        // The reason line is the one string on this page resolved in C#, so no loc:Str binding re-reads it.
        LocalizationSource.Instance.PropertyChanged += (_, _) => PostOrRun(NotifyCatalogLanguageChanged);
        RefreshGrants();
        RebuildCatalog();
    }

    // The grant store's Changed may fire off-thread (an external SettingsChanged from a
    // background sync save, or a session grant minted on a run thread), so the bound-collection
    // rebuild is marshalled back — PostOrRun runs inline when already on (or lacking) the captured
    // UI context.
    private void OnPermissionsChanged(object? sender, EventArgs e) => PostOrRun(() =>
    {
        RefreshGrants();
        SyncCatalogState();
    });

    // Only the plugin set changes the catalogue's SHAPE. A grant alone syncs onto the existing rows, so
    // clicking a toggle does not tear down the row it was clicked on.
    private void OnPluginsChanged(object? sender, EventArgs e) => PostOrRun(RebuildCatalog);

    private void RefreshGrants()
    {
        var configs = _pluginService.GetAllPluginConfigs();

        Grants.Clear();
        foreach (var grant in _permissions.List())
        {
            Grants.Add(ToRow(grant, configs));
        }

        SessionGrants.Clear();
        foreach (var grant in _permissions.ListSessionGrants())
        {
            SessionGrants.Add(ToRow(grant, configs));
        }

        HasGrants = Grants.Count > 0;
        HasSessionGrants = SessionGrants.Count > 0;
    }

    private static ToolGrantRow ToRow(ToolGrant grant, IReadOnlyList<SyncPlugin> configs)
    {
        var name = configs.FirstOrDefault(p => p.Id == grant.PluginId)?.Name
                   ?? grant.PluginId.ToString();
        return new ToolGrantRow(grant.PluginId, name, grant.ToolName, grant.GrantedAt);
    }

    [RelayCommand]
    private async Task RevokeAsync(ToolGrantRow? row)
    {
        if (row is null) return;

        // Privacy: tool name + plugin id are non-sensitive (CLAUDE.md). No arguments.
        _logger.LogInformation(
            "Revoking tool grant {ToolName} for plugin {PluginId}", row.ToolName, row.PluginId);

        await _permissions.RevokeAsync(row.PluginId, row.ToolName);
    }

    [RelayCommand]
    private void ForgetSession(ToolGrantRow? row)
    {
        if (row is null) return;

        _logger.LogInformation(
            "Forgetting session tool grant {ToolName} for plugin {PluginId}", row.ToolName, row.PluginId);

        _permissions.RevokeSessionGrant(row.PluginId, row.ToolName);
    }

    private void RebuildCatalog()
    {
        ToolCatalog.Clear();

        var groups = _pluginService.GetToolCatalog()
            .GroupBy(entry => entry.PluginName, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var rows = group
                .OrderBy(entry => entry.ToolName, StringComparer.OrdinalIgnoreCase)
                .Select(BuildCatalogRow)
                .ToList();
            ToolCatalog.Add(new ToolCatalogGroup(group.Key, rows));
        }

        HasCatalog = ToolCatalog.Count > 0;
        SyncCatalogState();
    }

    private ToolCatalogRow BuildCatalogRow(ToolCatalogEntry entry) =>
        new(entry,
            // Route-first, never the name-only guess: a renamed built-in must not become grantable as external.
            ToolClassifier.Classify(entry.PluginName, entry.IsExternalRoute),
            _permissions.IsAutoApproveEligible(entry.ToolName),
            OnCatalogSessionToggled,
            OnCatalogAlwaysToggled);

    private void NotifyCatalogLanguageChanged()
    {
        foreach (var row in ToolCatalog.SelectMany(group => group.Tools))
        {
            row.NotifyReasonChanged();
        }
    }

    private void SyncCatalogState()
    {
        foreach (var row in ToolCatalog.SelectMany(group => group.Tools))
        {
            row.SyncGrantState(
                _permissions.IsGrantedForSession(row.PluginId, row.ToolName),
                _permissions.IsGranted(row.PluginId, row.ToolName));
        }
    }

    private void OnCatalogSessionToggled(ToolCatalogRow row, bool allowed)
    {
        _logger.LogInformation("Session tool grant {ToolName} on plugin {PluginId} set to {Allowed} from settings",
            row.ToolName, row.PluginId, allowed);

        if (allowed)
            _permissions.GrantForSession(row.PluginId, row.ToolName);
        else
            _permissions.RevokeSessionGrant(row.PluginId, row.ToolName);
    }

    private void OnCatalogAlwaysToggled(ToolCatalogRow row, bool allowed)
    {
        _logger.LogInformation("Standing tool grant {ToolName} on plugin {PluginId} set to {Allowed} from settings",
            row.ToolName, row.PluginId, allowed);

        // The settings write can fail (locked file), and the cache already moved, so an unobserved fault
        // would leave the row ticked over a grant that never persisted.
        (allowed
            ? _permissions.GrantAsync(row.PluginId, row.ToolName)
            : _permissions.RevokeAsync(row.PluginId, row.ToolName))
            .SafeFireAndForget(_logger);
    }
}
