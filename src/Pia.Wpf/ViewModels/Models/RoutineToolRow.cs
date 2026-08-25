using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Services.Interfaces;

namespace Pia.ViewModels.Models;

/// <summary>The picker's tools under the plugin they belong to; the trailing group carries stored grants this
/// build has no catalog row for.</summary>
public sealed record RoutineToolGroup(string Header, bool IsUnavailableGroup, IReadOnlyList<RoutineToolRow> Tools);

/// <summary>
/// One tool a routine may be allowed to use. Unlike <see cref="ToolCatalogRow"/> this is a single tick against
/// one routine's grant list, not a permission tier — nothing here writes to a grant store.
/// </summary>
public sealed class RoutineToolRow : ObservableObject
{
    private readonly Action<RoutineToolRow, bool>? _selectionChanged;
    private bool _isSelected;

    private RoutineToolRow(
        Guid pluginId,
        string pluginName,
        string toolName,
        string? description,
        ToolGrantCaution caution,
        bool isUnavailable,
        ILocalizationService localization,
        Action<RoutineToolRow, bool>? selectionChanged)
    {
        PluginId = pluginId;
        PluginName = pluginName;
        ToolName = toolName;
        Description = description ?? string.Empty;
        Caution = caution;
        IsUnavailable = isUnavailable;
        _selectionChanged = selectionChanged;

        // Resolved once, like every other label this editor builds, rather than through the localization
        // singleton: the VM tests inject a key-echoing localizer and a singleton read would bypass it.
        CautionText = CautionKey is null ? string.Empty : localization[CautionKey];
        UnavailableReason = isUnavailable ? localization["Routines_Field_Tools_Missing_Hint"] : string.Empty;
    }

    public static RoutineToolRow FromCatalog(
        ToolCatalogEntry entry,
        ILocalizationService localization,
        Action<RoutineToolRow, bool>? selectionChanged = null) =>
        new(entry.PluginId, entry.PluginName, entry.ToolName, entry.Description,
            ToolCatalogRow.CautionFor(entry.ToolName, entry.ServerDeclaredDestructive),
            isUnavailable: false, localization, selectionChanged);

    /// <summary>A stored grant with no catalog row — carried through the save rather than dropped.</summary>
    public static RoutineToolRow Unavailable(
        string toolName,
        ILocalizationService localization,
        Action<RoutineToolRow, bool>? selectionChanged = null) =>
        new(Guid.Empty, string.Empty, toolName, description: null,
            ToolCatalogRow.CautionFor(toolName, serverDeclaredDestructive: false),
            isUnavailable: true, localization, selectionChanged);

    public Guid PluginId { get; }
    public string PluginName { get; }
    public string ToolName { get; }
    public string Description { get; }
    public bool HasDescription => Description.Length > 0;
    public ToolGrantCaution Caution { get; }

    /// <summary>Nothing on this device provides the tool, but the routine still names it.</summary>
    public bool IsUnavailable { get; }

    public string UnavailableReason { get; }

    public string? CautionKey => RoutineCautionKeyFor(Caution);
    public string CautionText { get; }

    /// <summary>Advice on a choice already made, so it stays off the rows the user is only scanning.</summary>
    public bool HasCaution => CautionKey is not null && IsSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value == _isSelected) return;

            // An unavailable row may be turned OFF but never back ON: it is a grant the user already holds,
            // and re-offering a tool this build cannot route would be a promise nothing keeps.
            if (value && IsUnavailable)
            {
                OnPropertyChanged();
                return;
            }

            SetProperty(ref _isSelected, value);
            OnPropertyChanged(nameof(HasCaution));
            _selectionChanged?.Invoke(this, value);
        }
    }

    /// <summary>Apply selection state without re-running the callback.</summary>
    public void SyncSelection(bool selected)
    {
        if (selected == _isSelected) return;
        SetProperty(ref _isSelected, selected, nameof(IsSelected));
        OnPropertyChanged(nameof(HasCaution));
    }

    /// <summary>Routine-scoped caution copy. The Tool access page's wording ends "leave it unticked to be asked
    /// each time", which is false here: an unattended run refuses an unnamed delete-like or external tool
    /// outright rather than parking to ask.</summary>
    public static string? RoutineCautionKeyFor(ToolGrantCaution caution) => caution switch
    {
        ToolGrantCaution.Destructive => "ToolCatalog_Caution_Routine_Destructive",
        ToolGrantCaution.WorkDiscarding => "ToolCatalog_Caution_Routine_WorkDiscarding",
        ToolGrantCaution.AuthorityAuthoring => "ToolCatalog_Caution_Routine_AuthorityAuthoring",
        _ => null,
    };
}
