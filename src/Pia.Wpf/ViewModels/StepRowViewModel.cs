using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Models;

namespace Pia.ViewModels;

/// <summary>Read-only row for one <see cref="AgentStep"/>. Title is SENSITIVE — bound to UI only, never logged.</summary>
public sealed partial class StepRowViewModel : ObservableObject
{
    public Guid StepId { get; init; }

    /// <summary>
    /// Batch 08 8b (W12): SETTABLE, not init-only. <c>RunProgressViewModel.SyncSteps</c>'s else-branch (the
    /// path taken when a step's Id survives — an EDIT preserves it, by design) assigns it directly, which is
    /// the only way an edited title ever repaints: rows are otherwise replaced only when a step Id changes
    /// (R23), and an edit changes nothing else about the row's identity.
    /// </summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>SENSITIVE (user content), like <see cref="Title"/>. Not bound to the read-only row display —
    /// carried only so a submitted plan mutation can round-trip every OTHER pending row's Intent verbatim
    /// while one row is being edited/inserted/reordered/skipped (the service takes the COMPLETE pending tail,
    /// never a diff).</summary>
    public string? Intent { get; set; }

    /// <summary>Same reason as <see cref="Intent"/> — round-tripped, not displayed.</summary>
    public string? ExpectedArtifact { get; set; }

    /// <summary>The persona the PLANNER assigned, or null. Kept as the raw fact; <see cref="PersonaId"/> and
    /// the other render values below are the resolved projection (Batch 07 §4.3).</summary>
    public Guid? AssignedPersonaId { get; init; }

    // SETTABLE, not init-only: RunProgressViewModel.ApplyPersonaAttribution must be able to (re)resolve
    // these once the persona map loads, or after the map is corrected on a later RunChanged (07 §4.3/§4.4).
    [ObservableProperty]
    private Guid _personaId; // Guid.Empty ⇒ no avatar (HasPersona false)

    [ObservableProperty]
    private string? _personaEmoji;

    [ObservableProperty]
    private string? _personaAccent; // #RRGGBB straight into HexToBrushConverter; null ⇒ no accent ring

    /// <summary>
    /// The persona's name, appended to the title as "· {name}" so the delegation is readable and not only
    /// hoverable. SETTABLE for the reason the three fields above are (see <see cref="PersonaId"/>). SENSITIVE:
    /// a persona is user-authored, so this is bound to UI only and never logged, exactly like
    /// <see cref="Title"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PersonaSuffix))]
    private string? _personaName;

    /// <summary>Rendered as a second Run inside the title's TextBlock, so the persona is the FIRST thing the
    /// ellipsis eats — the step's own title has to survive a narrow card. Null renders as a zero-width Run, which
    /// is why the row needs no "has a suffix" companion bool.</summary>
    public string? PersonaSuffix => string.IsNullOrEmpty(PersonaName) ? null : $" · {PersonaName}";

    /// <summary>
    /// True only when this step was genuinely delegated to a resolvable persona. Deliberately NOT a
    /// fallback to "the run persona": <c>AgentRun</c> has no persona column, so that value is not
    /// resolvable from the run row, and resolving "whatever persona is active right now" would be a guess
    /// that goes stale. An avatar that appears only when a step was actually assigned is a more honest
    /// signal than the always-empty box this replaces (§0.7).
    /// </summary>
    public bool HasPersona => PersonaId != Guid.Empty;

    partial void OnPersonaIdChanged(Guid value) => OnPropertyChanged(nameof(HasPersona));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMutable))]
    private AgentStepStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTokens))]
    private long _inputTokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTokens))]
    private long _outputTokens;

    /// <summary>What the step COST as a localized "N tokens" figure, input plus output — the same arithmetic
    /// the header ledger does, so the column and the ledger cannot be read as two different measures of the
    /// same run. The owner composes it when the ledger lands; the bare number alone read as an id.</summary>
    [ObservableProperty]
    private string? _tokensLabel;

    public bool HasTokens => InputTokens + OutputTokens > 0;

    public bool IsRunning => Status == AgentStepStatus.Running;

    /// <summary>The row draws its own 7px ring for Pending and its own pulsing dot for Running; only the other
    /// three statuses use a glyph from the icon set. Three mutually exclusive bools rather than one status
    /// converter per element: the shapes differ in SIZE, not just in colour, and a size converter would be a
    /// fourth mapping over the same five statuses.</summary>
    public bool IsPending => Status == AgentStepStatus.Pending;

    public bool HasStatusGlyph => Status is AgentStepStatus.Done or AgentStepStatus.Failed or AgentStepStatus.Skipped;

    public bool IsSkipped => Status == AgentStepStatus.Skipped;

    /// <summary>
    /// Folded away by the plan window (<c>RunProgressViewModel.ApplyStepWindow</c>) — the row stays in
    /// <c>Steps</c> so the progress strip, the ledger and the trace's step attribution all keep seeing it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInWindow))]
    [NotifyPropertyChangedFor(nameof(ShowInList))]
    private bool _isWindowedOut;

    public bool IsInWindow => !IsWindowedOut;

    /// <summary>The plan's last row renders BELOW its own fold (the list copy hidden, the outside copy shown)
    /// while the window hides the tail — see <c>RunProgressViewModel.ApplyStepWindow</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInList))]
    private bool _renderedOutside;

    public bool ShowInList => !IsWindowedOut && !RenderedOutside;

    /// <summary>
    /// Batch 08 D3: gates the row's five plan-mutation buttons' <c>IsEnabled</c> — a settled step (Done,
    /// Skipped or Failed) never offers to be edited, inserted after, reordered or skipped again (a skip is
    /// ONE-WAY). This is independent of <see cref="RunProgressViewModel.CanMutatePlan"/>, which gates the
    /// SAME buttons' visibility and each command's own <c>CanExecute</c> at the run level — a live run hides
    /// the whole row-button group; a paused run still greys out a settled row's group via this property.
    /// </summary>
    public bool IsMutable => Status == AgentStepStatus.Pending;

    /// <summary>Re-raise what this row's brush bindings read from, so their converters resolve against the new
    /// theme. See <c>RunProgressViewModel.RefreshThemeBrushes</c> for why a raise is the only mechanism.</summary>
    internal void RefreshThemeBrushes()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsRunning));
    }

    partial void OnStatusChanged(AgentStepStatus value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(HasStatusGlyph));
        OnPropertyChanged(nameof(IsSkipped));
    }

    /// <summary>True while this row's inline editor (Title/Intent) is open. Batch 08 D3: inline, never a
    /// dialog — the panel is embedded in a chat.</summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>The editor's working copy of <see cref="Title"/>, seeded by <c>EditStep</c> and discarded by
    /// <c>CancelStepEdit</c> — <see cref="Title"/> itself is never touched until <c>SaveStepEdit</c> actually
    /// lands.</summary>
    [ObservableProperty]
    private string _editTitle = string.Empty;

    /// <summary>The editor's working copy of <see cref="Intent"/>, same discipline as <see cref="EditTitle"/>.</summary>
    [ObservableProperty]
    private string? _editIntent;

    /// <summary>"Editing step {n}" — the editor replaces the row, so this is the only thing left on screen saying
    /// WHICH step is being edited. Written by <c>RunProgressViewModel.EditStep</c>, which is the only opener.</summary>
    [ObservableProperty]
    private string? _editorEyebrow;

    public static StepRowViewModel From(AgentStep step) => new()
    {
        StepId = step.Id,
        Title = step.Title,
        Intent = step.Intent,
        ExpectedArtifact = step.ExpectedArtifact,
        AssignedPersonaId = step.AssignedPersonaId,
        Status = step.Status,
    };
}
