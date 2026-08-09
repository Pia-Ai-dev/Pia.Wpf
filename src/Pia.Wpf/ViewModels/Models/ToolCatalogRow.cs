using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Localization;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;

namespace Pia.ViewModels.Models;

/// <summary>Why a tool cannot be pre-approved at one or both tiers.</summary>
public enum ToolGrantRestriction
{
    None = 0,
    SessionOnly,
    Destructive,
    WorkDiscarding,
    AuthorityAuthoring,
}

/// <summary>The catalogue's tools under the plugin they belong to.</summary>
public sealed record ToolCatalogGroup(string PluginName, IReadOnlyList<ToolCatalogRow> Tools);

/// <summary>
/// One pre-approvable tool on the Tool access page. Both offerability flags come from the functions the
/// gate itself mints with, so a toggle this row offers can never be one the gate would ignore.
/// </summary>
public partial class ToolCatalogRow : ObservableObject
{
    private readonly Action<ToolCatalogRow, bool>? _sessionToggled;
    private readonly Action<ToolCatalogRow, bool>? _alwaysToggled;

    // Writing a grant raises Changed, which syncs these bools back inline — without this the setter would
    // fire again on state it just wrote, and an in-flight standing grant would be revoked by its own refresh.
    private bool _syncing;

    public Guid PluginId { get; }
    public string PluginName { get; }
    public string ToolName { get; }
    public bool CanGrantForSession { get; }
    public bool CanGrantAlways { get; }
    public ToolGrantRestriction Restriction { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeSession))]
    private bool _allowedForSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeAlways))]
    private bool _allowedAlways;

    public ToolCatalogRow(
        ToolCatalogEntry entry,
        ToolClass toolClass,
        bool isAllowlisted,
        Action<ToolCatalogRow, bool>? sessionToggled = null,
        Action<ToolCatalogRow, bool>? alwaysToggled = null)
    {
        PluginId = entry.PluginId;
        PluginName = entry.PluginName;
        ToolName = entry.ToolName;
        _sessionToggled = sessionToggled;
        _alwaysToggled = alwaysToggled;

        CanGrantForSession = ToolAutonomy.IsSessionGrantOfferable(
            entry.ToolName, entry.ServerDeclaredDestructive);
        CanGrantAlways = ToolAutonomy.IsStandingGrantOfferable(
            toolClass, entry.ToolName, isAllowlisted, entry.ServerDeclaredDestructive);
        Restriction = RestrictionFor(entry.ToolName, CanGrantForSession, CanGrantAlways);
    }

    // A live grant on a tool that stopped being offerable (a server adds destructiveHint to one you already
    // allowed) must still be revocable here, or the row shows a tick it also says is impossible.
    public bool CanChangeSession => CanGrantForSession || AllowedForSession;

    public bool CanChangeAlways => CanGrantAlways || AllowedAlways;

    public string? ReasonKey => ReasonKeyFor(Restriction);

    public bool HasReason => ReasonKey is not null;

    public string Reason => ReasonKey is null ? string.Empty : LocalizationSource.Instance[ReasonKey];

    /// <summary>Resolved in C#, so a language switch has to be told about it.</summary>
    public void NotifyReasonChanged() => OnPropertyChanged(nameof(Reason));

    /// <summary>Apply live grant state without re-running the toggle callbacks.</summary>
    public void SyncGrantState(bool allowedForSession, bool allowedAlways)
    {
        _syncing = true;
        try
        {
            AllowedForSession = allowedForSession;
            AllowedAlways = allowedAlways;
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnAllowedForSessionChanged(bool value)
    {
        if (_syncing) return;
        _sessionToggled?.Invoke(this, value);
    }

    partial void OnAllowedAlwaysChanged(bool value)
    {
        if (_syncing) return;
        _alwaysToggled?.Invoke(this, value);
    }

    private static ToolGrantRestriction RestrictionFor(
        string toolName, bool canGrantForSession, bool canGrantAlways)
    {
        if (canGrantForSession)
            return canGrantAlways ? ToolGrantRestriction.None : ToolGrantRestriction.SessionOnly;

        // The two tiers are independent, so a work-discarding MCP tool still offers Always. Naming a reason
        // beside a working toggle would tell the user it always asks while it does not.
        if (canGrantAlways)
            return ToolGrantRestriction.None;

        if (ToolPermissionService.IsAuthorityAuthoring(toolName))
            return ToolGrantRestriction.AuthorityAuthoring;

        if (ToolPermissionService.IsWorkDiscarding(toolName))
            return ToolGrantRestriction.WorkDiscarding;

        return ToolGrantRestriction.Destructive;
    }

    /// <summary>A helper, not literals, so a localization test can enumerate the keys this returns.</summary>
    public static string? ReasonKeyFor(ToolGrantRestriction restriction) => restriction switch
    {
        ToolGrantRestriction.SessionOnly => "ToolCatalog_Reason_SessionOnly",
        ToolGrantRestriction.Destructive => "ToolCatalog_Reason_Destructive",
        ToolGrantRestriction.WorkDiscarding => "ToolCatalog_Reason_WorkDiscarding",
        ToolGrantRestriction.AuthorityAuthoring => "ToolCatalog_Reason_AuthorityAuthoring",
        _ => null,
    };
}
