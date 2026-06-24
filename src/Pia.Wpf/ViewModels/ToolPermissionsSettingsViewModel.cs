using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

/// <summary>
/// The mandatory revocation surface (design §7): lists the standing per-(PluginId,
/// ToolName) "always allow" grants with a Revoke action, and refreshes itself when
/// the grant store changes. Ctor takes only interfaces (DI guardrail); injected
/// fields are readonly (MVVM guardrail). Grant rows are built synchronously from
/// <see cref="IToolPermissionService.List"/> so no async initialization is needed.
/// </summary>
public partial class ToolPermissionsSettingsViewModel : ObservableObject
{
    private readonly IToolPermissionService _permissions;
    private readonly IPluginService _pluginService;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly SynchronizationContext? _syncContext;

    public ObservableCollection<ToolGrantRow> Grants { get; } = [];

    [ObservableProperty]
    private bool _hasGrants;

    public ToolPermissionsSettingsViewModel(
        IToolPermissionService permissions,
        IPluginService pluginService,
        ILogger<SettingsViewModel> logger)
    {
        _permissions = permissions;
        _pluginService = pluginService;
        _logger = logger;
        // Captured on the construction thread (the UI thread in production). The
        // grant store's Changed may fire off-thread (external SettingsChanged from a
        // background sync save), so the bound-collection rebuild is marshalled back.
        _syncContext = SynchronizationContext.Current;

        _permissions.Changed += OnPermissionsChanged;
        RefreshGrants();
    }

    private void OnPermissionsChanged(object? sender, EventArgs e)
    {
        if (_syncContext is not null && _syncContext != SynchronizationContext.Current)
            _syncContext.Post(_ => RefreshGrants(), null);
        else
            RefreshGrants();
    }

    private void RefreshGrants()
    {
        Grants.Clear();

        var configs = _pluginService.GetAllPluginConfigs();
        foreach (var grant in _permissions.List())
        {
            var name = configs.FirstOrDefault(p => p.Id == grant.PluginId)?.Name
                       ?? grant.PluginId.ToString();
            Grants.Add(new ToolGrantRow(grant.PluginId, name, grant.ToolName, grant.GrantedAt));
        }

        HasGrants = Grants.Count > 0;
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
}
