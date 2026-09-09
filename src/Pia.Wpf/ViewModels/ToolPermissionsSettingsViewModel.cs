using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Localization;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Screen;
using Pia.Shared.Models;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

/// <summary>
/// The mandatory revocation surface, plus the pre-approval catalogue: lists the standing "always allow"
/// grants (Revoke) and the process-scoped session grants (Forget), and offers every other tool through the
/// same grant calls a card would make. Ctor takes only interfaces (DI guardrail); injected fields are
/// readonly (MVVM guardrail). The three grant collections are built synchronously; only the screen-capture
/// allowlist, which reads a file, needs <see cref="Initialization"/>.
/// </summary>
public partial class ToolPermissionsSettingsViewModel : UiThreadViewModel
{
    private readonly IToolPermissionService _permissions;
    private readonly IPluginService _pluginService;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IScreenCaptureAllowlistStore? _screenCaptureAllowlist;

    /// <summary>Kept so a language change can re-project the "any window title" label without a reload.</summary>
    private readonly List<ScreenCaptureAllowlistEntry> _allowlistEntries = [];

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

    /// <summary>The windows a run nobody is watching may capture.</summary>
    public ObservableCollection<ScreenCaptureAllowlistRow> ScreenCaptureAllowlist { get; } = [];

    [ObservableProperty]
    private bool _hasScreenCaptureAllowlistStore;

    [ObservableProperty]
    private bool _hasScreenCaptureAllowlistEntries;

    [ObservableProperty]
    private string _newAllowlistProcessName = string.Empty;

    [ObservableProperty]
    private string _newAllowlistTitleContains = string.Empty;

    /// <summary>Completes after the first allowlist load, so a test awaits it instead of racing the ctor.</summary>
    public Task Initialization { get; }

    public ToolPermissionsSettingsViewModel(
        IToolPermissionService permissions,
        IPluginService pluginService,
        ILogger<SettingsViewModel> logger,
        IScreenCaptureAllowlistStore? screenCaptureAllowlist = null)
    {
        _permissions = permissions;
        _pluginService = pluginService;
        _logger = logger;
        _screenCaptureAllowlist = screenCaptureAllowlist;

        _permissions.Changed += OnPermissionsChanged;
        _pluginService.PluginsChanged += OnPluginsChanged;
        // The catalogue's reason line and the allowlist's "any window title" label are resolved in C#, so no
        // loc:Str binding re-reads them on a language change.
        LocalizationSource.Instance.PropertyChanged += (_, _) => PostOrRun(() =>
        {
            NotifyCatalogLanguageChanged();
            RebuildAllowlistRows();
        });
        RefreshGrants();
        RebuildCatalog();

        HasScreenCaptureAllowlistStore = _screenCaptureAllowlist is not null;
        if (_screenCaptureAllowlist is not null)
            _screenCaptureAllowlist.Changed += (_, _) => PostOrRun(() => LoadAllowlistAsync().SafeFireAndForget(_logger));

        Initialization = _screenCaptureAllowlist is null ? Task.CompletedTask : LoadAllowlistAsync();
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
        new(entry, OnCatalogSessionToggled, OnCatalogAlwaysToggled);

    private void NotifyCatalogLanguageChanged()
    {
        foreach (var row in ToolCatalog.SelectMany(group => group.Tools))
        {
            row.NotifyCautionChanged();
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

    private async Task LoadAllowlistAsync()
    {
        if (_screenCaptureAllowlist is null) return;

        try
        {
            // Nothing awaits Initialization in production, so a read failure would otherwise be an unobserved
            // fault behind an empty-state message that says the list is empty.
            var entries = await _screenCaptureAllowlist.ListAsync();
            await PostAsync(() =>
            {
                _allowlistEntries.Clear();
                _allowlistEntries.AddRange(entries);
                RebuildAllowlistRows();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the screen capture allowlist");
        }
    }

    private void RebuildAllowlistRows()
    {
        ScreenCaptureAllowlist.Clear();
        foreach (var entry in _allowlistEntries)
        {
            var titleDisplay = string.IsNullOrWhiteSpace(entry.TitleContains)
                ? LocalizationSource.Instance["Settings_ToolPermissions_ScreenAllowlist_AnyTitle"]
                : entry.TitleContains;
            ScreenCaptureAllowlist.Add(
                new ScreenCaptureAllowlistRow(entry.Id, entry.ProcessName, titleDisplay));
        }

        HasScreenCaptureAllowlistEntries = ScreenCaptureAllowlist.Count > 0;
    }

    [RelayCommand]
    private async Task AddScreenCaptureAllowlistEntryAsync()
    {
        if (_screenCaptureAllowlist is null) return;

        // Privacy: the program name and the title fragment are user-named items, so only the id is logged.
        // The list itself is refreshed by the store's Changed event, so there is one reload path, not two.
        var added = await _screenCaptureAllowlist.AddAsync(NewAllowlistProcessName, NewAllowlistTitleContains);
        if (added is null) return;

        _logger.LogInformation("Added screen capture allowlist entry {EntryId}", added.Id);
        NewAllowlistProcessName = string.Empty;
        NewAllowlistTitleContains = string.Empty;
    }

    [RelayCommand]
    private async Task RemoveScreenCaptureAllowlistEntryAsync(ScreenCaptureAllowlistRow? row)
    {
        if (row is null || _screenCaptureAllowlist is null) return;

        _logger.LogInformation("Removing screen capture allowlist entry {EntryId}", row.Id);
        await _screenCaptureAllowlist.RemoveAsync(row.Id);
    }
}
