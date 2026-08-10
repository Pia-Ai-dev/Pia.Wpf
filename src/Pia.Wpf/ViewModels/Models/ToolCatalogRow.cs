using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Localization;
using Pia.Services;
using Pia.Services.Interfaces;

namespace Pia.ViewModels.Models;

/// <summary>
/// What is worth knowing about pre-approving this tool. Every tool can be granted at both tiers; the note is
/// shown only once the user has actually ticked one, so a caution reads as advice on a choice already made
/// rather than as clutter on a row they were only scanning.
/// </summary>
/// <remarks>
/// All three are about what a MULTI-CALL grant can consent to: the card that collects one shows the arguments
/// of ONE call, and every later call's arguments are invisible. Hence a note for any destructive tool, for the
/// git trio that sheds uncommitted work, and for the tools whose ARGUMENTS ARE THEMSELVES A GRANT LIST.
/// </remarks>
public enum ToolGrantCaution
{
    None = 0,
    Destructive,
    WorkDiscarding,
    AuthorityAuthoring,
}

/// <summary>The catalogue's tools under the plugin they belong to.</summary>
public sealed record ToolCatalogGroup(string PluginName, IReadOnlyList<ToolCatalogRow> Tools);

/// <summary>One pre-approvable tool on the Tool access page. Both tiers are offered for every tool.</summary>
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
    public ToolGrantCaution Caution { get; }

    // Both tiers raise HasCaution: the note is advice about holding a grant, and either tier holds one.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCaution))]
    private bool _allowedForSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCaution))]
    private bool _allowedAlways;

    public ToolCatalogRow(
        ToolCatalogEntry entry,
        Action<ToolCatalogRow, bool>? sessionToggled = null,
        Action<ToolCatalogRow, bool>? alwaysToggled = null)
    {
        PluginId = entry.PluginId;
        PluginName = entry.PluginName;
        ToolName = entry.ToolName;
        _sessionToggled = sessionToggled;
        _alwaysToggled = alwaysToggled;

        Caution = CautionFor(entry.ToolName, entry.ServerDeclaredDestructive);
    }

    public string? CautionKey => CautionKeyFor(Caution);

    /// <summary>Shown only once a grant is actually held, at either tier — advice on a choice, not a warning
    /// label on every row.</summary>
    public bool HasCaution => CautionKey is not null && (AllowedForSession || AllowedAlways);

    public string CautionText => CautionKey is null ? string.Empty : LocalizationSource.Instance[CautionKey];

    /// <summary>Resolved in C#, so a language switch has to be told about it.</summary>
    public void NotifyCautionChanged() => OnPropertyChanged(nameof(CautionText));

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

    /// <summary>First match wins, and destructive is tested FIRST: it is the one a server can declare on a
    /// benign-looking name, and the strongest thing to say about a tool that carries more than one.</summary>
    private static ToolGrantCaution CautionFor(string toolName, bool serverDeclaredDestructive)
    {
        if (ToolPermissionService.IsDeleteLike(toolName, serverDeclaredDestructive))
            return ToolGrantCaution.Destructive;

        if (ToolPermissionService.IsWorkDiscarding(toolName))
            return ToolGrantCaution.WorkDiscarding;

        if (ToolPermissionService.IsAuthorityAuthoring(toolName))
            return ToolGrantCaution.AuthorityAuthoring;

        return ToolGrantCaution.None;
    }

    /// <summary>A helper, not literals, so a localization test can enumerate the keys this returns.</summary>
    public static string? CautionKeyFor(ToolGrantCaution caution) => caution switch
    {
        ToolGrantCaution.Destructive => "ToolCatalog_Caution_Destructive",
        ToolGrantCaution.WorkDiscarding => "ToolCatalog_Caution_WorkDiscarding",
        ToolGrantCaution.AuthorityAuthoring => "ToolCatalog_Caution_AuthorityAuthoring",
        _ => null,
    };
}
