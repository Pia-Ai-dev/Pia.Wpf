using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Converters;
using Pia.Helpers;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

/// <summary>View-facing run states (R12). The four rendered states, the distinct truncated-Completed
/// variant, and — new in Phase 2 — the budget-pause WaitingForInput state (a "continue?" affordance)
/// plus the reserved user-initiated Paused state. Verifying renders as the Running chip (via the
/// MapState default) plus a "Checking the work…" current-activity line.</summary>
public enum RunProgressState
{
    Planning,
    Running,
    Completed,
    TruncatedCompleted,
    Failed,
    WaitingForInput, // budget-paused, awaiting the user's Continue
    Paused,          // reserved: user-initiated pause (Phase 4) — rendered, never driven this round

    /// <summary>Batch 07: the parent of a fan-out, parked while its child runs work. Appended — this enum is
    /// a view-facing projection and is NEVER persisted, so appending costs nothing. Renders as its own chip
    /// with the spinner LIT (children are working) and offers no Continue: the run is not parked for the
    /// user, and <c>TryBeginResumeAsync</c> would not accept it anyway.</summary>
    WaitingForChildren,
}

/// <summary>
/// How loudly one tool decision has to read on the audit surface. The five user-facing decision categories
/// collapse to three, because the only question the reader has is "does this need me?": <c>Awaiting</c> does,
/// <c>Refused</c> explains a step that did less than it was asked to, and everything else is bookkeeping.
/// <para>
/// Awaiting is deliberately its own tier and NOT folded into <see cref="Refused"/>: it renders in the warning
/// palette, never the danger one, because the call was not turned down — it is waiting for an answer.
/// </para>
/// </summary>
public enum RunDecisionSeverity
{
    Routine,
    Awaiting,
    Refused,
}

/// <summary>
/// Read-only projection of a live/selected <see cref="AgentRun"/> for the run-progress panel (§15.1/15.2).
/// The FIRST consumer of <see cref="IAgentRunService.RunChanged"/> (dormant since 1.1): that event may fire
/// off the UI thread (the orchestrator uses ConfigureAwait(false) + SafeFireAndForget), so every handler
/// marshals to the captured UI <see cref="SynchronizationContext"/> before touching bound collections (G3).
/// Constructed on the UI thread by <see cref="AssistantViewModel"/>, not DI-registered (mirrors LiveTurnExecutor).
/// </summary>
public sealed partial class RunProgressViewModel : ObservableObject, IDisposable
{
    // The writer (AgentRunService) serializes the ledger camelCase (F5) — match it here.
    private static readonly JsonSerializerOptions LedgerJsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IAgentRunService _runService;
    private readonly Guid _runId;
    private readonly SynchronizationContext _uiContext;
    private readonly ILocalizationService _localization;
    private readonly IAgentRunResumeService _resumeService;
    private readonly IAgentTimelineService? _timelineService;
    private readonly IRunWorkspaceService? _workspaces;
    private readonly IPersonaService? _personaService;
    private readonly IAgentRunSteeringService? _steering;
    private readonly IThemeService? _themeService;
    private readonly ITimelineWatcher? _timelineWatcher;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>
    /// Batch 07 §4.4: lazily loaded, once per VM, and left NULL (not an empty dictionary) on a faulted
    /// read — a transient fault must not permanently blank every step's persona attribution for the run's
    /// whole life; the next <see cref="IAgentRunService.RunChanged"/> retries. A null persona service ⇒ this
    /// stays null forever ⇒ <see cref="ApplyPersonaAttribution"/> always renders "no persona", i.e. today's panel.
    /// </summary>
    private Dictionary<Guid, Persona>? _personas;

    public Guid RunId => _runId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    // The visibility halves of the same two buttons (see ShowDenyButton for why each button needs a pair).
    [NotifyPropertyChangedFor(nameof(ShowContinueButton))]
    [NotifyPropertyChangedFor(nameof(ShowPauseButton))]
    [NotifyPropertyChangedFor(nameof(ShowPauseFirstNote))]
    [NotifyPropertyChangedFor(nameof(CanMutatePlan))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    // DeclineToolCommand is deliberately NOT here: its answer keys off IsToolApprovalPause (set in Project
    // on the same RunChanged that moves State), and notifying it on a bare State flip that leaves the park
    // reason untouched would raise CanExecuteChanged over an unchanged answer.
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    // Batch 08 F4. EVERY command gated on CanMutatePlan must be listed here, and EditStepCommand was the one
    // that was not: CommunityToolkit's RelayCommand has no CommandManager integration, so CanExecuteChanged
    // fires only from an explicit notify and ButtonBase caches _canExecute until it arrives. A row realized
    // while the run was live hooked "Edit step" at CanExecute == false and never heard otherwise — so after a
    // pause the other four verbs lit up and Edit stayed dead on every pre-existing row, for the VM's whole
    // life. Pinned count-wise (not name-wise) by RunProgressViewModelPlanMutationTests, so a seventh verb
    // added without its entry here reds instead of shipping greyed out.
    [NotifyCanExecuteChangedFor(nameof(EditStepCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveStepEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(InsertStepBelowCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveStepUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveStepDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(SkipStepCommand))]
    private RunProgressState _state;

    /// <summary>Raised on the live-to-terminal State change only — a VM constructed over an already-settled
    /// run never raises it, so the host composer's lever fallback cannot fire for history.</summary>
    public event Action? RunSettled;

    private bool _wasLive;

    partial void OnStateChanged(RunProgressState value)
    {
        var terminal = value is RunProgressState.Completed or RunProgressState.TruncatedCompleted
            or RunProgressState.Failed;
        if (terminal && _wasLive) RunSettled?.Invoke();
        _wasLive = !terminal;
    }

    /// <summary>True while a resume is being launched — gates the Continue button against a double-click
    /// (the CAS in the resume service is the hard guard; this is the UI-visible affordance).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeclineToolCommand))]
    private bool _isResuming;

    /// <summary>True while the run is parked asking to use a named tool — the one pause where a person's
    /// question has a yes AND a no, so the one offering Deny beside Continue.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDenyButton))]
    [NotifyCanExecuteChangedFor(nameof(DeclineToolCommand))]
    private bool _isToolApprovalPause;

    /// <summary>The tool the parked run asked to use; the approval copy names it.</summary>
    [ObservableProperty]
    private string? _approvalToolName;

    public bool CanDeclineTool => IsToolApprovalPause && !IsResuming;

    /// <summary>
    /// The band's action slot is driven by a pair of predicates per button, never one: <c>Show…</c> answers
    /// "does this state offer the action" and gates VISIBILITY, while <c>Can…</c> adds the in-flight term and
    /// gates <c>CanExecute</c>, i.e. ENABLEDNESS. One predicate for both would collapse the button the instant it
    /// is pressed — which re-lays out the whole band (the action column is <c>Auto</c> beside a <c>*</c> text
    /// column), removes the only acknowledgement that the click registered, and makes
    /// <see cref="PauseLabel"/>'s "Pausing…" state unrenderable, since it would only ever be pushed to a
    /// collapsed element. The in-flight window is not a flicker: <see cref="IsPausing"/> stays true until
    /// <see cref="Project"/> observes the run actually leaving the pausable state.
    /// </summary>
    public bool ShowDenyButton => IsToolApprovalPause;

    /// <summary>The budget-pause Continue affordance, widened by Batch 08 D1 item 8 to the user-pause state
    /// too: <see cref="RunProgressState.Paused"/> is the CAS's own target, and both states offer the identical
    /// Continue command (<see cref="IAgentRunResumeService.ResumeAsync"/> claims either via the row's own
    /// state). Trips nothing per Ground E, and the <c>!IsTerminal</c> form this could also be written as would
    /// red <c>RunProgressViewModelChildrenTests.cs</c>'s WaitingForChildren fact — this two-member set does not.</summary>
    public bool CanContinue => IsResumableState && !IsResuming;

    /// <summary>Extracted so <see cref="CanContinue"/> and <see cref="ShowContinueButton"/> cannot drift into two
    /// different state sets — the pair discipline is documented on <see cref="ShowDenyButton"/>.</summary>
    private bool IsResumableState => State is RunProgressState.WaitingForInput or RunProgressState.Paused;

    /// <summary>See <see cref="ShowDenyButton"/>: visibility is the state gate, enabledness the in-flight one.
    /// The steering note rides on this too, so a resume in flight does not yank the box out from under a note the
    /// user is still reading (<c>Continue()</c> keeps <see cref="NudgeText"/> unless the resume actually started).</summary>
    public bool ShowContinueButton => IsResumableState;

    /// <summary>True while a user pause request is in flight — gates the Pause button against a double-click
    /// the same way <see cref="IsResuming"/> gates Continue. Cleared only once <see cref="Project"/> observes
    /// the run having left the state the pause was requested FROM (see the clearing site below), never in a
    /// bare <c>finally</c> after the request call: <see cref="IAgentRunSteeringService.PauseAsync"/> returns as
    /// soon as the intent is recorded and the cancel is fired, well before the run's own loop actually lands
    /// the row at <see cref="RunProgressState.Paused"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    [NotifyPropertyChangedFor(nameof(ShowPauseFirstNote))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    private bool _isPausing;

    partial void OnIsPausingChanged(bool value) => OnPropertyChanged(nameof(PauseLabel));

    /// <summary>PARENTHESIZE THE OR-PATTERN. Both predicates mix a pattern combinator with <c>&amp;&amp;</c>,
    /// and while <c>is</c> binds tighter than <c>&amp;&amp;</c> so the bare form does compile as
    /// <c>(State is A or B) &amp;&amp; …</c>, the unbracketed reading is exactly the kind a builder "fixes" by
    /// guessing — and the wrong guess yields a button visible on a terminal run.
    /// <para>
    /// Explicit set, never a range (D7): <see cref="RunProgressState.Running"/> covers real <c>Running</c>
    /// AND <c>Verifying</c> (which folds into it in <see cref="MapState"/>); <c>Planning</c> is excluded per §1
    /// D1 item 8 (a resume skips planning entirely); <see cref="RunProgressState.WaitingForChildren"/> is D6's
    /// cascade.
    /// </para>
    /// <para><c>_steering is not null</c> is the trailing-optional guard: a build with no steering service
    /// injected renders exactly the pre-Batch-08 panel (no Pause button, ever).</para>
    /// </summary>
    public bool CanPause => IsPausableState && !IsPausing;

    /// <summary>Extracted so <see cref="CanPause"/> and <see cref="ShowPauseButton"/> cannot drift into two
    /// different state sets; the pair discipline is documented on <see cref="ShowDenyButton"/>.</summary>
    private bool IsPausableState =>
        _steering is not null
        && (State is RunProgressState.Running or RunProgressState.WaitingForChildren);

    /// <summary>See <see cref="ShowDenyButton"/>. This is the half that makes <see cref="PauseLabel"/>'s
    /// "Pausing…" reachable: the button stays on screen, disabled, for the whole in-flight window.</summary>
    public bool ShowPauseButton => IsPausableState;

    /// <summary><c>Run_Action_Pausing</c> while a request is in flight, <c>Run_Action_Pause</c>
    /// otherwise. Notified from <see cref="OnIsPausingChanged"/> rather than a
    /// <c>[NotifyPropertyChangedFor]</c> attribute on <see cref="IsPausing"/>, so both notifications (the
    /// bool's own CanPause/CanExecute pair and this derived string) read as one intentional list rather than
    /// four attributes stacked on the field.</summary>
    public string PauseLabel => IsPausing ? _localization["Run_Action_Pausing"] : _localization["Run_Action_Pause"];

    /// <summary>
    /// Batch 08 D3/D4: every row-level plan mutation (edit/insert/reorder/skip) is refused unless the run is
    /// PAUSED — one state, never a set and never a range (D7), matching
    /// <see cref="IAgentRunService.ApplyPlanMutationAsync"/>'s own gate exactly. Gates each row command's
    /// <c>CanExecute</c> AND is bound directly in the row template (via <c>AncestorType=ItemsControl</c>) to
    /// hide the whole per-row button group while the run is live — <see cref="StepRowViewModel.IsMutable"/> is
    /// the OTHER, independent half: it greys out a settled row's buttons even while this is true. The
    /// service's own state read is the hard guard; this is the UI-visible affordance.
    /// </summary>
    public bool CanMutatePlan => State == RunProgressState.Paused;

    /// <summary>
    /// Batch 08 F12: gates the "Pause the run to change its plan." note. It used to be the INVERSE of
    /// <see cref="CanMutatePlan"/>, which is true in every state except <c>Paused</c> — so a run that parked
    /// at its budget (<c>WaitingForInput</c>) showed the instruction next to a Continue button and no Pause
    /// button to press, and a run that completed an hour ago carried it forever. The impl spec §13 8b states
    /// the condition as "whenever the run is LIVE", and the panel already has the exact predicate for that:
    /// a run is live-and-steerable precisely when it offers a Pause button.
    /// <para>
    /// Deliberately <c>=&gt; CanPause</c> rather than a second copy of its state set, so the note and the
    /// button it tells the user to press can never disagree — including the <see cref="IsPausing"/> term,
    /// which correctly hides the note while a pause is already in flight (there is nothing left to press).
    /// Both <c>_state</c> and <c>_isPausing</c> notify it, for the same reason both notify
    /// <see cref="CanPause"/>.
    /// </para>
    /// </summary>
    public bool ShowPauseFirstNote => CanPause;

    /// <summary>The muted result line of the last plan mutation — the <see cref="PublishNote"/> shape. Null
    /// when there is nothing to say; cleared on a successful mutation (the re-projected plan speaks for
    /// itself) so the panel never shows a stale rejection over a plan that has since changed.</summary>
    [ObservableProperty]
    private string? _planMutationNote;

    /// <summary>Batch 08 F6: the muted line for a pause the service REFUSED — the same
    /// <see cref="PublishNote"/> shape, and deliberately not <see cref="PlanMutationNote"/>, which
    /// <see cref="Project"/> wipes on any state but <c>Paused</c> (a refused pause is by definition a run that
    /// is still live, so that note would be erased before it rendered). Cleared by the next
    /// <see cref="Pause"/> attempt and by the projection that sees the run leave the pausable states.</summary>
    [ObservableProperty]
    private string? _pauseNote;

    [ObservableProperty]
    private bool _isTruncated;

    /// <summary>
    /// Localized chip text explaining WHY a Completed run is not a clean completion — read from the
    /// truncation <c>reason</c> in <c>ExtraJson</c>, never assumed. Since the budget cap parks the run
    /// (<see cref="RunProgressState.WaitingForInput"/>) instead of truncating it, the only reason the
    /// current code produces is <c>"unverified"</c> (the verify pass exhausted its replans); the
    /// budget wording survives only for runs persisted before that change. Null when not truncated.
    /// Always rendered MUTED, never in the danger brush (R5).
    /// </summary>
    [ObservableProperty]
    private string? _truncationNote;

    /// <summary>
    /// The current-activity line (design D1): the running step's title while Running, or a "building a
    /// plan" note while Planning; null (line hidden) otherwise. The live per-tool micro-status
    /// (<c>StatusText</c>) stays on the adjacent streaming transcript by design — this panel is
    /// plan-level, the transcript is token-level. Step title is SENSITIVE — bound to UI only, never logged.
    /// </summary>
    [ObservableProperty]
    private string? _currentActivity;

    public bool HasCurrentActivity => !string.IsNullOrEmpty(CurrentActivity);

    partial void OnCurrentActivityChanged(string? value) => OnPropertyChanged(nameof(HasCurrentActivity));

    public ObservableCollection<StepRowViewModel> Steps { get; } = [];

    /// <summary>
    /// The signal band's second line: the state name, the run's position in its plan and its elapsed time,
    /// composed by <see cref="ComposeSubLine"/>. Separate from <see cref="CurrentActivity"/> (which is the
    /// band's LEAD line) because the two answer different questions — "what is it doing" and "where is it".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubLine))]
    private string? _subLine;

    public bool HasSubLine => !string.IsNullOrEmpty(SubLine);

    /// <summary>
    /// The same row instances <see cref="Steps"/> holds, wrapped so the whole-plan progress strip can bind to
    /// its own source. A wrapper and not a second collection on purpose: a copy would need its own diffing pass
    /// and could disagree with the list about a step's status, and the strip is the one element that must be
    /// right even when the list below it is windowed.
    /// </summary>
    public ReadOnlyObservableCollection<StepRowViewModel> PlanSegments { get; }

    /// <summary>The strip is a LIVE instrument: it shows where the work is, so a parked or settled run drops it
    /// (the band's sub-line carries the position instead).</summary>
    [ObservableProperty]
    private bool _showProgressSegments;

    /// <summary>Placeholder rows while the planner is still writing the plan, so the card does not jump in
    /// height the moment the plan lands.</summary>
    [ObservableProperty]
    private bool _showPlanSkeleton;

    /// <summary>
    /// Above this many steps the list windows to the running step ±1 and the rest fold away. The card lives
    /// inside a chat transcript, so it may not grow without bound and it may not introduce an inner scrollbar.
    /// </summary>
    private const int StepWindowLimit = 7;

    /// <summary>Latched by <see cref="ExpandStepWindowCommand"/> and never reset: a user who asked to see the
    /// whole plan does not want it re-folded under them on the next step transition.</summary>
    [ObservableProperty]
    private bool _isStepWindowExpanded;

    [ObservableProperty]
    private int _earlierFoldCount;

    [ObservableProperty]
    private int _laterFoldCount;

    [ObservableProperty]
    private string? _earlierFoldLabel;

    [ObservableProperty]
    private string? _laterFoldLabel;

    /// <summary>The plan's last row, rendered BELOW its own fold while the window hides the tail — a run's
    /// goal must not be one of the steps the fold swallows. Null whenever the list is not windowed (short
    /// plan, paused, unfolded), then the list renders the row itself.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastStepRow))]
    [NotifyPropertyChangedFor(nameof(LastStepView))]
    private StepRowViewModel? _lastStepRow;

    public bool HasLastStepRow => LastStepRow is not null;

    /// <summary>One-element view over <see cref="LastStepRow"/>: the row below its fold rides its OWN
    /// ItemsControl (sharing the list's template instance) so the row's ItemsControl-ancestor bindings keep
    /// resolving — a ContentControl would break them.</summary>
    public IEnumerable<StepRowViewModel> LastStepView => LastStepRow is null ? [] : [LastStepRow];

    [ObservableProperty]
    private long _totalInputTokens;

    [ObservableProperty]
    private long _totalOutputTokens;

    [ObservableProperty]
    private long _wallClockMs;

    public string LedgerSummary => FormatLedger();

    /// <summary>
    /// Rows of the run's tool-decision trace (Batch 03). Loaded ON EACH EXPAND, and never on
    /// <c>RunChanged</c>: the timeline deliberately does not participate in live projection, which is what
    /// keeps ~500 emits per run off the projection path.
    /// </summary>
    public ObservableCollection<TimelineRowViewModel> Timeline { get; } = [];

    /// <summary>The band's chevron: folds everything below the signal band. Default open.</summary>
    [ObservableProperty]
    private bool _isCardExpanded = true;

    [ObservableProperty]
    private bool _isTimelineExpanded;

    [ObservableProperty]
    private bool _isTimelineTruncated;

    [ObservableProperty]
    private string? _timelineNote;

    /// <summary>
    /// Drives the "nothing recorded" line. A BOOL the VM owns, not an inverse converter: the panel already
    /// uses <c>BooleanToVisibilityConverter</c>, and an unresolved <c>StaticResource</c> inside a
    /// <c>DataTemplate</c> throws at TEMPLATE INSTANTIATION — i.e. the first time a user expands this — which
    /// no test in the suite reaches.
    /// <para>
    /// "Nothing was recorded" is a POSITIVE claim about the run, so it is only ever made about a read that
    /// SUCCEEDED. A read that faulted sets <see cref="HasTimelineReadError"/> instead.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _hasNoTimeline = true;

    /// <summary>Drives the "could not be read" line — the other half of <see cref="HasNoTimeline"/>. A trace
    /// the store refused to hand over is not a run that recorded nothing.</summary>
    [ObservableProperty]
    private bool _hasTimelineReadError;

    /// <summary>
    /// One pill per non-empty decision category, exceptions first — the summary that turns the trace from a log
    /// dump into an audit. Rebuilt with the rows, so it can never disagree with them.
    /// </summary>
    public ObservableCollection<DecisionPillViewModel> DecisionPills { get; } = [];

    /// <summary>
    /// The one count that has to be legible WITHOUT expanding: a parked or refused call. Null when the trace
    /// holds none (and before the first read), so the badge simply is not there rather than reading "0".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimelineExceptionBadge))]
    private string? _timelineExceptionBadge;

    public bool HasTimelineExceptionBadge => !string.IsNullOrEmpty(TimelineExceptionBadge);

    /// <summary>Which palette <see cref="TimelineExceptionBadge"/> renders in.</summary>
    [ObservableProperty]
    private RunDecisionSeverity _timelineExceptionSeverity;

    /// <summary>
    /// Guards the ONE trace read this VM does outside a user expand. A settled run's trace cannot change again,
    /// so reading it once is enough — and it is what lets the collapsed header say "1 awaiting approval" about a
    /// run that ended parked, which is the whole point of surfacing the count there. Deliberately NOT a read on
    /// every <c>RunChanged</c>: ~500 emits per run may not reach the projection path (see <see cref="Timeline"/>).
    /// </summary>
    private bool _settledTraceRead;

    /// <summary>
    /// Batch 06 G4 / plan D3: a settled run whose isolated workspace still holds files nobody promoted —
    /// usually a FAILED or CANCELLED run, because a clean one promotes automatically before it is marked
    /// Completed. <b>Not only those</b>: a clean copy-mode run whose promotion hit a CONFLICT keeps its
    /// workspace too (the run's version of that file was deliberately not written and exists nowhere else), so
    /// a Completed run can legitimately raise this offer. Drives the offer line and the Publish button.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPublish))]
    [NotifyPropertyChangedFor(nameof(ShowPublishButton))]
    [NotifyCanExecuteChangedFor(nameof(PublishCommand))]
    private bool _hasUnpublishedFiles;

    /// <summary>True while a publish is in flight — gates the button against a double-click, exactly as
    /// <see cref="IsResuming"/> gates Continue.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPublish))]
    [NotifyCanExecuteChangedFor(nameof(PublishCommand))]
    private bool _isPublishing;

    /// <summary>The localized result line of the last publish attempt; null when there is nothing to say.</summary>
    [ObservableProperty]
    private string? _publishNote;

    /// <summary>
    /// Worktree mode only (plan D5b): the run branch its output lives on. The panel must SAY this, or the
    /// honest user question after a run that "worked" is "where is my file?" — there is deliberately no merge
    /// and therefore no conflict handling on an unattended path.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutputBranch))]
    [NotifyPropertyChangedFor(nameof(OutputBranchNote))]
    private string? _outputBranchName;

    public bool HasOutputBranch => !string.IsNullOrEmpty(OutputBranchName);

    /// <summary>The rendered branch line. Formatted HERE rather than by a converter in the panel: the format
    /// argument is app-owned, the string is localized, and the layer rule keeps this kind of decision in the
    /// ViewModel.</summary>
    public string? OutputBranchNote =>
        HasOutputBranch ? _localization.Format("Run_Output_Branch", OutputBranchName!) : null;

    public bool CanPublish => HasUnpublishedFiles && !IsPublishing;

    /// <summary>See <see cref="ShowDenyButton"/>: the offer stands (and the button stays on screen, disabled)
    /// while a publish is in flight — the files really are still in the workspace until it lands.</summary>
    public bool ShowPublishButton => HasUnpublishedFiles;

    /// <summary>
    /// The run's delegated CHILD runs (Batch 07 D17), one row each, refreshed in place on every projection.
    /// Empty for every ordinary run — a build with no persona roster never produces one.
    /// <para>
    /// <b>NO MERGED TIMELINE, and this is not an omission.</b> Each row expands to load THAT run's own trace.
    /// The events cannot be interleaved: <c>Seq</c> is monotonic only WITHIN a run id, each child gets its own
    /// fresh <c>Seq</c> space and its own 500-event cap, and <c>CreatedAt</c> is explicitly rejected as an
    /// ordering source by the store's own schema comment. A single merged view needs a new cross-run ordering
    /// key, designed as its own work — do not "finish" this by sorting two runs' rows together.
    /// </para>
    /// </summary>
    public ObservableCollection<ChildRunRowViewModel> Children { get; } = [];

    [ObservableProperty]
    private bool _hasChildren;

    /// <summary>The sub-agents section's own disclosure. Unlike the trace's, expanding it reads nothing — each
    /// CHILD row owns its trace load — so this is presentation state and nothing else.</summary>
    [ObservableProperty]
    private bool _isChildrenExpanded;

    /// <summary>The "N of M finished" line, or null when this run delegated nothing.</summary>
    [ObservableProperty]
    private string? _childrenNote;

    /// <summary>
    /// The child run ids, as an IMMUTABLE snapshot REPLACED (never mutated) inside <see cref="Project"/> on the
    /// UI thread. <see cref="OnRunChanged"/> reads it from a POOL thread — <c>RunChanged</c> fires off-thread,
    /// which is this VM's whole premise — so a mutable <c>HashSet</c> here would be the exact data race
    /// <c>ChatSessionManager</c> documents for its own <c>_ownRunIds</c>. Reference assignment is atomic, so an
    /// off-thread reader always sees one consistent generation. The <c>e.RunId != _runId</c> term needs no such
    /// care: <c>_runId</c> is readonly.
    /// </summary>
    private ImmutableHashSet<Guid> _childRunIds = ImmutableHashSet<Guid>.Empty;

    /// <summary>
    /// The in-flight (or last) load, exposed so a fact can await the fire-and-forget the expand kicks off.
    /// The read itself is off-thread now, so a test that set <see cref="IsTimelineExpanded"/> and asserted
    /// immediately would race it.
    /// </summary>
    internal Task? TimelineLoadTask { get; private set; }

    partial void OnIsTimelineExpandedChanged(bool value)
    {
        if (!value) return;

        // Re-read on EVERY expand, not once. A load-once latch made the panel state a falsehood: a trace read
        // while step 1 was still planning would keep rendering "no tool decisions were recorded" for the rest
        // of the session, because nothing else in this VM ever re-reads it (RunChanged deliberately does not),
        // AssistantViewModel.SyncRunProgress keeps the same VM for the run's whole life, and there is no
        // refresh command. One indexed read per user click is the cheaper mistake. Routed through the same
        // gate as the live reloads so two loads can never apply concurrently.
        RequestTimelineReload();
    }

    /// <summary>The broker fires once per accepted event on a pool thread; at most one reload runs at a time
    /// and events arriving mid-load mark it dirty for ONE follow-up — a burst costs two reads, not one per
    /// call, which is what keeps the audit stream off the projection path.</summary>
    private readonly object _timelineReloadGate = new();
    private bool _timelineReloadRunning;
    private bool _timelineReloadDirty;
    private bool _liveTracePrimed;

    private void OnTimelineAppended(Guid runId)
    {
        if (runId != _runId || _timelineService is null || _settledTraceRead) return;
        RequestTimelineReload();
    }

    private void RequestTimelineReload()
    {
        lock (_timelineReloadGate)
        {
            if (_timelineReloadRunning)
            {
                _timelineReloadDirty = true;
                return;
            }

            _timelineReloadRunning = true;
        }

        TimelineLoadTask = DrainTimelineReloadsAsync();
        TimelineLoadTask.SafeFireAndForget(_logger);
    }

    private async Task DrainTimelineReloadsAsync()
    {
        while (true)
        {
            await LoadTimelineAsync();
            lock (_timelineReloadGate)
            {
                if (!_timelineReloadDirty)
                {
                    _timelineReloadRunning = false;
                    return;
                }

                _timelineReloadDirty = false;
            }
        }
    }

    public RunProgressViewModel(IAgentRunService runService, Guid runId, ILocalizationService localization,
        IAgentRunResumeService resumeService, ILogger logger,
        // Batch 03. Trailing and defaulted because this type is hand-constructed with a POSITIONAL argument
        // list in production and in its tests; null ⇒ the trace renders as empty and reads nothing.
        IAgentTimelineService? timelineService = null,
        // Batch 06 G4, same discipline for the same reason — LAST, and defaulted. Null ⇒ no publish
        // affordance and no branch line, i.e. the panel is byte-identical to the pre-Batch-06 one.
        IRunWorkspaceService? workspaces = null,
        // Batch 07 G7, same discipline again — now LAST (06 took the 7th slot first). Null ⇒ _personas
        // never loads ⇒ every step row's HasPersona is false ⇒ the panel renders exactly as before this batch.
        IPersonaService? personaService = null,
        // Batch 08 D1/§5.4 — LAST again, same discipline. Null ⇒ CanPause is always false ⇒ no Pause button,
        // i.e. the panel is byte-for-byte the pre-Batch-08 one. AssistantViewModel.cs constructs this VM
        // positionally, which is exactly why every one of these keeps landing at the tail.
        IAgentRunSteeringService? steering = null,
        // Theme-awareness — LAST again, same trailing-and-defaulted discipline as the five above. Null means the
        // panel never re-resolves its brushes, i.e. exactly the pre-fix behaviour. See RefreshThemeBrushes for why
        // a notification is the only mechanism that works here.
        IThemeService? themeService = null,
        // Live tool-activity — LAST again, same discipline. Null ⇒ the trace reads on expand and at settle only,
        // i.e. the pre-live-panel behaviour; the pills then stay empty until the first expand.
        ITimelineWatcher? timelineWatcher = null)
    {
        _themeService = themeService;
        _timelineWatcher = timelineWatcher;
        if (_timelineWatcher is not null)
            _timelineWatcher.TimelineAppended += OnTimelineAppended;
        _timelineService = timelineService;
        _workspaces = workspaces;
        _personaService = personaService;
        _steering = steering;
        _runService = runService;
        _runId = runId;
        _localization = localization;
        _resumeService = resumeService;
        _logger = logger;
        PlanSegments = new ReadOnlyObservableCollection<StepRowViewModel>(Steps);
        // Captured on the construction (UI) thread; may be null in a headless test → run inline.
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _runService.RunChanged += OnRunChanged;
        if (_themeService is not null)
            _themeService.ThemeChanged += OnThemeChanged;
        RefreshAsync().SafeFireAndForget(_logger); // initial projection
    }

    private void OnRunChanged(object? sender, AgentRunChangedEventArgs e)
    {
        // Batch 07 D17: a CHILD run's state changes are ours too, or the children list never live-updates —
        // every child event would be dropped and the rows would freeze at whatever the first projection saw.
        // _childRunIds is an immutable snapshot for a reason; see its declaration.
        if (e.RunId != _runId && !_childRunIds.Contains(e.RunId)) return;
        RefreshAsync().SafeFireAndForget(_logger);   // the read may run off-thread; Project marshals (G3)
    }

    private void OnThemeChanged(object? sender, EventArgs e) =>
        _uiContext.Post(_ => RefreshThemeBrushes(), null);

    /// <summary>
    /// Ask every brush binding on the card to resolve again, because a theme swap cannot reach the ones a converter
    /// produced.
    /// <para>
    /// <b>Why this is needed at all.</b> A dozen-odd colours here come from converters that resolve a theme brush
    /// by key: the band's tint, its hairline, the card outline, every state and status foreground, the progress
    /// strip, the decision pills. A converter re-runs only when its SOURCE VALUE changes, so what it returned is a
    /// snapshot of the outgoing theme — and the swap cannot fix that snapshot in place, because WPF freezes
    /// freezables once their dictionary is owned. Both in-place recolouring and a <c>DynamicResource</c> colour on
    /// the brush were measured and are dead ends. Re-raising the source property is what makes the converters run
    /// again, and it is safe precisely BECAUSE nothing about the run changed: <c>[NotifyPropertyChangedFor]</c>
    /// chains fire from the generated setters, never from a manual raise.
    /// </para>
    /// <para>
    /// The rows are visited one by one because their brush bindings read <c>Status</c> / <c>Severity</c> /
    /// <c>State</c> off the ROW, so a raise on this VM would not reach them.
    /// </para>
    /// </summary>
    private void RefreshThemeBrushes()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(TimelineExceptionSeverity));

        foreach (var row in Steps) row.RefreshThemeBrushes();
        foreach (var row in Timeline) row.RefreshThemeBrushes();
        foreach (var pill in DecisionPills) pill.RefreshThemeBrushes();
        foreach (var child in Children)
        {
            child.RefreshThemeBrushes();
            foreach (var row in child.Timeline) row.RefreshThemeBrushes();
        }
    }

    /// <summary>Re-reads the run and projects it onto the bound collections on the UI thread.</summary>
    internal async Task RefreshAsync()
    {
        var run = await _runService.GetAsync(_runId);
        if (run is null) return;

        // Batch 07 D17: read the children OFF the projection's UI hop, in their own guarded block, and hand the
        // list to Project so the whole projection is one UI-thread mutation. One indexed query
        // (IX_AgentRuns_ParentRunId) per RunChanged — cheaper than the workspace describe below, and unlike it
        // the answer changes while the run is live, so it cannot be deferred to a terminal state. A read fault
        // leaves the rows exactly as they were.
        IReadOnlyList<AgentRun>? children = null;
        try
        {
            children = await _runService.GetChildRunsAsync(_runId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} child runs could not be read", _runId);
        }

        // Batch 07 §4.4: load the persona map ONCE per VM, before Project (which SyncSteps reads it from).
        // Off the projection's UI hop like the children read above — it is a full persona list, not an
        // indexed run query, and RunChanged fires far too often to pay for it more than once.
        if (_personas is null && _personaService is not null)
        {
            try
            {
                _personas = (await _personaService.GetPersonasAsync()).ToDictionary(p => p.Id);
            }
            catch (Exception ex)
            {
                // _personas stays null so the NEXT RunChanged retries rather than latching an empty map.
                _logger.LogWarning(ex, "Run {RunId} persona map could not be read", _runId);
            }
        }

        _uiContext.Post(_ => Project(run, children), null); // marshal the mutation to the UI thread (G3)

        // Batch 06 G4: the workspace outcome is read in its OWN terminal-only branch, deliberately not folded
        // into Project above. DescribeAsync does a file read plus a directory enumeration, and RunChanged
        // fires on every step, every state flip and every ledger write — putting that on the projection path
        // would pay for it dozens of times per run to answer a question only a settled run can be asked.
        if (_workspaces is not null && IsTerminal(run.State))
            await LoadWorkspaceOutcomeAsync().ConfigureAwait(false);

        // The settled run's trace, ONCE — the same terminal-only shape the workspace read above uses, and for a
        // sharper reason: this one is latched, because a run that will not change again cannot record another
        // decision. It is what puts the exception count on the collapsed header of a run the user is reading
        // back out of chat history. A live run's header stays quiet until the first expand, by design.
        if (_timelineService is not null && !_settledTraceRead)
        {
            if (IsTerminal(run.State))
            {
                _settledTraceRead = true;
                RequestTimelineReload();
                await TimelineLoadTask!.ConfigureAwait(false);
            }
            else if (!_liveTracePrimed)
            {
                // The pills ride in the always-visible section header now, so a live run gets one priming
                // read; the broker's per-event reloads keep them current from there.
                _liveTracePrimed = true;
                RequestTimelineReload();
            }
        }
    }

    /// <summary>
    /// A run that will not change again. Written as an explicit set rather than "not one of the live states"
    /// so a state a future build appends is treated as NON-terminal — the direction that merely defers the
    /// publish offer instead of offering to publish a workspace a live run is still writing into.
    /// </summary>
    private static bool IsTerminal(AgentRunState state) =>
        state is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled;

    /// <summary>
    /// Read the settled run's workspace outcome (plan D3/D5b) OFF the dispatcher and apply it through
    /// <see cref="_uiContext"/>, the same mechanism <see cref="ApplyTimelineAsync"/> uses. Failure-isolated:
    /// a fault leaves the panel with no offer and no branch line, which is the pre-Batch-06 panel.
    /// </summary>
    private async Task LoadWorkspaceOutcomeAsync()
    {
        RunWorkspaceOutcome? outcome = null;
        try
        {
            // DescribeAsync does its own filesystem work inside a Task.Run and never throws by contract; the
            // catch is here because "never throws" is a promise about today's implementation, not a type rule.
            outcome = await _workspaces!.DescribeAsync(_runId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} workspace outcome could not be read", _runId);
        }

        await ApplyWorkspaceOutcomeAsync(outcome).ConfigureAwait(false);
    }

    /// <summary>The ONE place the publish affordance's bound state is set, always on the UI thread (G3).</summary>
    private Task ApplyWorkspaceOutcomeAsync(RunWorkspaceOutcome? outcome)
    {
        var done = new TaskCompletionSource();
        _uiContext.Post(_ =>
        {
            try
            {
                // Never re-arm an offer the user already answered in this session: a publish clears
                // HasUnpublishedFiles and tears the workspace down, and the next RunChanged would otherwise
                // read a describe that raced the teardown.
                if (!IsPublishing)
                {
                    HasUnpublishedFiles = outcome?.HasUnpublishedFiles ?? false;

                    // The AUTOMATIC promotion's conflict count, announced on completion (Lens A 5 / Lens B 3's
                    // remaining half). The promotion itself sets no ViewModel state and never will — it runs in
                    // a DI scope this panel knows nothing about, and the panel is routinely opened from history
                    // LONG after the run settled, when an event raised at completion would be gone. So the
                    // count travels the channel the panel already reads: the workspace metadata document, which
                    // exists exactly as long as the retained workspace the count is about.
                    //
                    // Only when there is something to say, and never over a note the user's own publish just
                    // produced — that one is more recent and it is theirs.
                    if (PublishNote is null && outcome is { Conflicts: > 0 })
                        PublishNote = _localization.Format("Run_Publish_Conflicts", outcome.Conflicts);
                }

                OutputBranchName = outcome?.BranchName;
            }
            finally
            {
                done.TrySetResult();
            }
        }, null);

        return done.Task;
    }

    /// <summary>
    /// Publish what a settled run left in its workspace (plan D3, the "else offer to publish" half). Declining
    /// is doing nothing: the workspace is retained and then swept by the launcher's terminal retention rule,
    /// so an unanswered offer cannot pin a workspace forever.
    /// <para>
    /// <b>RETAINWORKSPACE IS OBEYED HERE, exactly as the automatic path obeys it</b> (Phase 3 consolidation).
    /// The two paths are symmetric on purpose: a promotion that reports "the workspace still holds work I could
    /// not move" — a copy-mode CONFLICT whose resolution kept the user's newer file (B7), or a worktree whose
    /// run-branch commit did not take — is the case where the workspace holds the ONLY copy of the run's
    /// version of a file, and tearing it down destroys it. Being user-initiated does not make that recoverable.
    /// The offer therefore STAYS STANDING on a retaining publish, and that is not a stale offer: the note above
    /// it says how many files were left alone, the workspace really does still hold them, and the offer is
    /// actionable — a user who moves their own copy aside turns the conflict into "destination missing" and the
    /// next click copies the run's version out.
    /// </para>
    /// <para>
    /// Worktree mode CAN reach here since the consolidation pass: a run whose branch never received a commit
    /// describes with <see cref="HasUnpublishedFiles"/> set, so the button appears and publishing RETRIES the
    /// commit (B15's "worktree mode offers no publish button" is no longer absolute — that was the arm with no
    /// recovery path at all).
    /// </para>
    /// <para>
    /// <see cref="RunPromotionResult.Skipped"/> is deliberately NOT surfaced: it is the byte-identical no-op
    /// case, and there is nothing to tell the user about a file that was already correct.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPublish))]
    private async Task Publish()
    {
        if (_workspaces is null) return;

        IsPublishing = true;
        try
        {
            var result = await _workspaces.PromoteAsync(_runId, CancellationToken.None);
            if (result is null)
            {
                // Nothing was promoted and the workspace is intact — the offer stays standing so the user can
                // retry after fixing whatever the service logged (a relocated assistant folder, typically).
                PublishNote = _localization["Run_Publish_Failed"];
                return;
            }

            if (result.RetainWorkspace)
            {
                // The workspace keeps the work this promotion could not move, and the offer above it keeps
                // pointing at it. Run id only — a promotion's paths are user content.
                _logger.LogInformation(
                    "Run {RunId} publish retained the workspace: it still holds work the promotion did not move",
                    _runId);
            }
            else
            {
                await _workspaces.TearDownAsync(_runId, CancellationToken.None);
                HasUnpublishedFiles = false;
            }

            // Nothing moved and nothing was deliberately left alone, on a promotion that nonetheless asked for
            // the workspace to be kept: the promotion could not do anything at all — a worktree whose
            // run-branch commit is still failing is the case this arm exists for. "Published 0 file(s)" would
            // read as success; Run_Publish_Failed is the line that says the files are still in the workspace.
            if (result is { RetainWorkspace: true, Promoted: 0, Conflicts: 0 })
            {
                PublishNote = _localization["Run_Publish_Failed"];
                return;
            }

            var note = _localization.Format("Run_Publish_Done", result.Promoted);
            if (result.Conflicts > 0)
                note += " " + _localization.Format("Run_Publish_Conflicts", result.Conflicts);
            PublishNote = note;
        }
        catch (Exception ex)
        {
            // Run id only — a promotion's paths are user content and this logger is release-attachable.
            _logger.LogWarning(ex, "Run {RunId} publish failed from panel", _runId);
            PublishNote = _localization["Run_Publish_Failed"];
        }
        finally
        {
            IsPublishing = false;
        }
    }

    private void Project(AgentRun run, IReadOnlyList<AgentRun>? children = null)
    {
        var truncation = ReadTruncation(run);
        (State, IsTruncated) = MapState(run, truncation.Truncated);

        // Clear a pending user-pause request only once the run has left the state it was requested FROM —
        // CanPause's own explicit set, not a bare `State != Running`: a WaitingForChildren parent's cascade
        // keeps re-projecting WaitingForChildren while its children pause one at a time (each child's
        // RunChanged is ours too, per D17), and clearing on the first such event would re-enable the button
        // before the parent's own request has landed at Paused. Delegated sub-choice (not literally the
        // impl spec's "a non-Running state" prose, which describes the common Running path only): this is
        // the same predicate CanPause already uses, so the two can never disagree.
        if (State is not (RunProgressState.Running or RunProgressState.WaitingForChildren))
        {
            IsPausing = false;
            // Batch 08 F6: "this run could not be paused" is only true of a run that is still in the states a
            // pause is offered from. Once it has left them the line has nothing left to describe, so it goes
            // with the button — one predicate, both clears.
            PauseNote = null;
        }

        // A rejection note ("the plan can only be changed while paused") must not survive past the pause it
        // was about — the PublishNote precedent guards itself the same way (`if (PublishNote is null && …)`
        // in ApplyWorkspaceOutcomeAsync); here the guard is simpler because ONLY a successful mutation clears
        // it otherwise, and a run that has since resumed and moved on has nothing left to say about a plan
        // edit that happened, or didn't, in a state it no longer occupies.
        if (State != RunProgressState.Paused)
            PlanMutationNote = null;

        TruncationNote = IsTruncated ? DescribeTruncation(truncation.Reason) : null;
        SyncSteps(run.Plan);
        CurrentActivity = ComputeActivity(run);

        // The deny beside Continue exists only for a tool-approval park — the one pause whose question a
        // person can answer "no". Read here, not in the XAML, so the button and the activity line agree.
        var approvalTool = run.State == AgentRunState.WaitingForInput
            && RunPauseEnvelope.ReadReason(run) == AgentRunOrchestrator.ToolApprovalReason
            ? RunPauseEnvelope.ReadApprovalTool(run)
            : null;
        IsToolApprovalPause = approvalTool is not null;
        ApprovalToolName = approvalTool;
        if (children is not null)
            SyncChildren(children);

        var ledger = TryParseLedger(run.LedgerJson);
        if (ledger is not null)
        {
            TotalInputTokens = ledger.InputTokens;
            TotalOutputTokens = ledger.OutputTokens;
            WallClockMs = ledger.WallClockMs;
            ApplyPerStepLedger(ledger);
            OnPropertyChanged(nameof(LedgerSummary));
        }

        // LAST, deliberately: all three read the step list, the child rows and the ledger this projection just
        // wrote, so composing them earlier would render the PREVIOUS projection's numbers under this one's state.
        ShowPlanSkeleton = State == RunProgressState.Planning && Steps.Count == 0;
        ShowProgressSegments = Steps.Count > 0
            && State is RunProgressState.Running or RunProgressState.WaitingForChildren;
        ApplyStepWindow();
        SubLine = ComposeSubLine();
    }

    /// <summary>
    /// The band's sub-line. Assembled from small localized fragments joined by a middle dot rather than one
    /// format string per state: the separator is punctuation, not grammar, so a translator never has to carry a
    /// clause they cannot see the value of, and a fragment whose value is missing (no ledger yet, no plan yet)
    /// simply drops out instead of rendering "step 0 of 0".
    /// </summary>
    private string? ComposeSubLine()
    {
        var total = Steps.Count;
        var parts = new List<string>();

        switch (State)
        {
            case RunProgressState.Completed:
            case RunProgressState.TruncatedCompleted:
                if (total > 0) parts.Add(_localization.Format("Run_Sub_Steps", SettledStepCount, total));
                if (WallClockMs > 0) parts.Add(FormatDuration(WallClockMs));
                // Only the CLEAN finish spends a clause on the token figure. The truncated card already carries a
                // reason chip beside its label, and a third number there reads as noise over the one fact that
                // matters — that the result is not what was asked for.
                var tokens = TotalInputTokens + TotalOutputTokens;
                if (State == RunProgressState.Completed && tokens > 0)
                    parts.Add(_localization.Format("Run_Sub_Tokens", tokens.ToString("N0")));
                break;

            case RunProgressState.Failed:
                // StoppedStepOrdinal, not CurrentStepOrdinal: a run that failed with every step already settled
                // (the verify pass rejected the result) has no step to point at, and CurrentStepOrdinal's
                // all-settled fallback would have the card claim "Stopped at step 4 of 4" over a plan that
                // finished all four. Fall back to the plain step tally instead.
                if (StoppedStepOrdinal > 0 && total > 0)
                    parts.Add(_localization.Format("Run_Sub_StoppedAtStep", StoppedStepOrdinal, total));
                else if (total > 0)
                    parts.Add(_localization.Format("Run_Sub_Steps", SettledStepCount, total));
                else
                    parts.Add(StateName);
                if (WallClockMs > 0) parts.Add(FormatDuration(WallClockMs));
                break;

            case RunProgressState.WaitingForChildren:
                // The children's progress replaces the step position here: the parent's own plan is parked, and
                // "step 3 of 4" beside a spinner would claim work this run is not doing.
                parts.Add(StateName);
                if (ChildrenNote is { } childrenNote) parts.Add(childrenNote);
                if (WallClockMs > 0) parts.Add(_localization.Format("Run_Sub_Elapsed", FormatDuration(WallClockMs)));
                break;

            case RunProgressState.Paused:
                // No elapsed time: a paused run's clock is not moving, and a frozen number invites the reader to
                // wonder whether the panel is stale. The invitation to edit the plan takes the slot instead.
                parts.Add(StateName);
                if (CurrentStepOrdinal > 0 && total > 0)
                    parts.Add(_localization.Format("Run_Sub_Step", CurrentStepOrdinal, total));
                parts.Add(_localization["Run_Sub_PlanEditable"]);
                break;

            default: // Planning / Running / WaitingForInput
                parts.Add(StateName);
                if (CurrentStepOrdinal > 0 && total > 0)
                    parts.Add(_localization.Format("Run_Sub_Step", CurrentStepOrdinal, total));
                if (WallClockMs > 0) parts.Add(_localization.Format("Run_Sub_Elapsed", FormatDuration(WallClockMs)));
                break;
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>Resolved through the label converter's own mapping rather than a second copy of it — a copy is
    /// how a state ends up named one thing in the band's lead line and another in its sub-line.</summary>
    private string StateName => _localization[RunStateToLabelConverter.LabelKey(State)];

    /// <summary>Whole-and-a-bit seconds under a minute, minutes and seconds above, hours and minutes past an
    /// hour — "246,6s" made the reader do the arithmetic. The ledger strip uses the same helper, so the two
    /// surfaces never print the same elapsed time two ways.</summary>
    private string FormatDuration(long milliseconds)
    {
        var seconds = milliseconds / 1000.0;
        if (seconds < 60) return $"{seconds:0.#}s";

        var whole = (long)(milliseconds / 1000);
        var minutes = whole / 60;
        return minutes < 60
            ? _localization.Format("Run_Duration_MinSec", minutes, whole % 60)
            : _localization.Format("Run_Duration_HourMin", minutes / 60, minutes % 60);
    }

    private int SettledStepCount =>
        Steps.Count(r => r.Status is AgentStepStatus.Done or AgentStepStatus.Failed or AgentStepStatus.Skipped);

    /// <summary>
    /// The 1-based step the run is ON, or 0 when the plan is empty. Running wins, then Failed (a failed run
    /// stopped AT that step, which is what the band says), then the first step still to come.
    /// </summary>
    private int CurrentStepOrdinal
    {
        get
        {
            for (var i = 0; i < Steps.Count; i++)
                if (Steps[i].Status == AgentStepStatus.Running) return i + 1;
            for (var i = 0; i < Steps.Count; i++)
                if (Steps[i].Status == AgentStepStatus.Failed) return i + 1;
            for (var i = 0; i < Steps.Count; i++)
                if (Steps[i].Status == AgentStepStatus.Pending) return i + 1;
            return Steps.Count;
        }
    }

    /// <summary>The 1-based step a stopped run stopped AT, or 0 when there is no such step — every row already
    /// settled, which is what a verify-pass failure over a completed plan looks like. Distinct from
    /// <see cref="CurrentStepOrdinal"/> precisely because that one has to answer for a LIVE run and therefore
    /// falls back to the last step; here a fallback would be a false claim.</summary>
    private int StoppedStepOrdinal
    {
        get
        {
            for (var i = 0; i < Steps.Count; i++)
                if (Steps[i].Status is AgentStepStatus.Failed or AgentStepStatus.Running) return i + 1;
            return 0;
        }
    }

    /// <summary>
    /// Fold a long plan down to the running step ±1, with a summary row at each end. The card is pinned above a
    /// chat transcript, so an unbounded list would push the conversation off screen and an inner scrollbar would
    /// trap the transcript's own wheel events.
    /// <para>
    /// A PAUSED run is never windowed: that is the one state whose per-row buttons can rewrite the plan, and
    /// hiding the rows a user paused in order to edit would be the panel working against them.
    /// </para>
    /// </summary>
    private void ApplyStepWindow()
    {
        var total = Steps.Count;
        if (IsStepWindowExpanded || CanMutatePlan || total <= StepWindowLimit)
        {
            foreach (var row in Steps)
            {
                row.IsWindowedOut = false;
                row.RenderedOutside = false;
            }
            EarlierFoldCount = 0;
            LaterFoldCount = 0;
            EarlierFoldLabel = null;
            LaterFoldLabel = null;
            LastStepRow = null;
            return;
        }

        var anchor = Math.Max(CurrentStepOrdinal - 1, 0);
        var first = Math.Max(0, anchor - 1);
        var last = Math.Min(total - 1, anchor + 1);

        // A fold that would hide exactly ONE step folds nothing: the fold row is as tall as the row it replaces,
        // so it buys no height, it costs the reader the step's title, and it is the only case where the count
        // reaches 1 — which every locale's plural copy would then get wrong ("1 earlier steps"). The tail count
        // excludes the always-visible last row.
        if (first == 1) first = 0;
        if (total - 2 - last == 1) last = total - 1;

        // The last row never joins the fold: it renders BELOW its own fold row instead, so a windowed run still
        // shows the step it is working toward.
        var lastOutside = last < total - 1;
        for (var i = 0; i < total; i++)
        {
            Steps[i].IsWindowedOut = i < first || (i > last && i < total - 1);
            Steps[i].RenderedOutside = lastOutside && i == total - 1;
        }
        LastStepRow = lastOutside ? Steps[total - 1] : null;

        EarlierFoldCount = first;
        LaterFoldCount = lastOutside ? total - 2 - last : 0;

        // Two variants per end, and the qualifier is claimed only when it is TRUE: "all done" over a fold that
        // hides a failed or skipped step is the panel telling the user the run went better than it did.
        if (EarlierFoldCount == 0)
            EarlierFoldLabel = null;
        else if (Steps.Take(first).All(r => r.Status == AgentStepStatus.Done))
            EarlierFoldLabel = _localization.Format("Run_Plan_Fold_Earlier", EarlierFoldCount);
        else
            EarlierFoldLabel = _localization.Format("Run_Plan_Fold_EarlierMixed", EarlierFoldCount);

        if (LaterFoldCount == 0)
            LaterFoldLabel = null;
        else if (Steps.Skip(last + 1).Take(LaterFoldCount).All(r => r.Status == AgentStepStatus.Pending))
            LaterFoldLabel = _localization.Format("Run_Plan_Fold_Later", LaterFoldCount);
        else
            LaterFoldLabel = _localization.Format("Run_Plan_Fold_LaterMixed", LaterFoldCount);
    }

    /// <summary>Unfold the whole plan in place. One-way (see <see cref="IsStepWindowExpanded"/>) — the fold rows
    /// disappear with the fold, so there is no button left to re-collapse it and none is offered.</summary>
    [RelayCommand]
    private void ExpandStepWindow()
    {
        IsStepWindowExpanded = true;
        ApplyStepWindow();
    }

    // R12 mapping. Verifying intentionally folds into Running here (keeps the spinner lit) while
    // ComputeActivity supplies its own "Checking the work…" line; WaitingForInput/Paused now render
    // as their own distinct (non-spinning) states with a Continue affordance. Cancelled folds into
    // the Failed-family visual.
    private static (RunProgressState, bool) MapState(AgentRun run, bool truncated) => run.State switch
    {
        AgentRunState.Planning => (RunProgressState.Planning, false),
        AgentRunState.Running => (RunProgressState.Running, false),
        AgentRunState.Failed => (RunProgressState.Failed, false),
        AgentRunState.Cancelled => (RunProgressState.Failed, false),
        AgentRunState.WaitingForInput => (RunProgressState.WaitingForInput, false), // budget pause — offer Continue
        AgentRunState.Paused => (RunProgressState.Paused, false),                   // reserved user pause (Phase 4)
        // 07 G8: an EXPLICIT arm, not the default. Falling through would render a delegating parent as the
        // plain Running chip and hide the fact that the work moved to its children. CanContinue stays false
        // (it is `State == WaitingForInput`), which is correct: this park is not a user affordance.
        AgentRunState.WaitingForChildren => (RunProgressState.WaitingForChildren, false),
        AgentRunState.Completed => truncated
            ? (RunProgressState.TruncatedCompleted, true)
            : (RunProgressState.Completed, false),
        _ => (RunProgressState.Running, false), // Verifying folds to Running (spinner)
    };

    // The truncation vocabulary is written by the run loop, not by a user or a model, so it is a fixed
    // set of app-owned tokens (never user content). An unknown/absent reason must NOT fall back to the
    // budget wording: that is exactly the lie this mapping removes — say "ended early" instead.
    private string DescribeTruncation(string? reason) => reason switch
    {
        "unverified" => _localization["Run_Unverified"],                             // verify pass never passed
        "budget" or "step-cap" or "wall-clock" => _localization["Run_StoppedAtBudget"], // pre-pause legacy rows
        _ => _localization["Run_EndedEarly"],
    };

    // The pause vocabulary, like the truncation vocabulary above, is a fixed set of APP-OWNED tokens written by
    // the run loop and the startup reconcile — never user content. An unknown or absent reason keeps the budget
    // wording, because that is what every pause the loop itself writes actually is ("step-cap"/"wall-clock").
    // Batch 08 G2's "user" arm is reachable now: ComputeActivity routes Paused here too, because the band's lead
    // line is the only place a paused run can say who paused it. It is deliberately NOT mapped to the existing
    // Run_State_Paused label: a mapping arm that borrows a string written for another control reads fine and
    // breaks on the next copy edit.
    // hermes #16 takes the RUN, not just the reason: the approval arm has to name the tool, and a resx KEY
    // cannot carry one. Every other arm ignores the extra argument.
    private string DescribePause(AgentRun run) => RunPauseEnvelope.ReadReason(run) switch
    {
        // The tool name is app/plugin-defined and never user content (the same property that lets the Flow
        // body key on the reason token), so it may be rendered. A tool-approval envelope whose name did not
        // survive formats an EMPTY name rather than falling through to the budget wording — "waiting for
        // approval" with a blank tool is degraded, "stopped at its budget" would be false.
        AgentRunOrchestrator.ToolApprovalReason =>
            _localization.Format("Run_Activity_WaitingForToolApproval", RunPauseEnvelope.ReadApprovalTool(run) ?? string.Empty),
        AgentRunOrchestrator.ChildrenParkedReason => _localization["Run_Activity_ChildrenParked"],
        AgentRunService.ChildrenInterruptedReason => _localization["Run_Activity_ChildrenInterrupted"],
        // On the interactive path the card is suppressed for the watched chat, so this panel line is the only
        // surface for these two reasons; the question text itself lives in the chat message, never here.
        AgentRunOrchestrator.NeedsGoalReason => _localization["Run_Activity_NeedsGoal"],
        AgentRunOrchestrator.NeedsInputReason => _localization["Run_Activity_NeedsInput"],
        AgentRunService.UserPausedReason => _localization["Run_Activity_UserPaused"],
        // Batch 08 F19: a resume that claimed the row and then never reached the orchestrator. Reachable
        // TODAY, unlike the "user" arm above — the re-park writes WaitingForInput, which is exactly the state
        // this mapping renders for — and it was announcing "Stopped at budget" for a run that had reached no
        // budget at all, including one the user had paused by hand a moment earlier.
        HeadlessRunLauncher.ResumeInterruptedReason => _localization["Run_Activity_ResumeInterrupted"],
        _ => _localization["Run_Activity_WaitingAtBudget"],
    };

    // Current-activity line (D1): the active step's title while Running (falls back to a generic
    // "working" note if no step is marked Running yet), a "building a plan" note while Planning, and
    // nothing on a terminal state (the header state chip already carries it).
    private string? ComputeActivity(AgentRun run) => run.State switch
    {
        AgentRunState.Planning => _localization["Run_Activity_Planning"],
        AgentRunState.Running =>
            run.Plan.FirstOrDefault(s => s.Status == AgentStepStatus.Running)?.Title
            ?? _localization["Run_Activity_Working"],
        AgentRunState.Verifying => _localization["Run_Activity_Verifying"],
        // WaitingForInput is now reached for THREE reasons and only one of them is a budget. Read the reason the
        // pause envelope carries instead of asserting the budget: a parent parked because a CHILD hit its own
        // halved budget, or because the app restarted mid-fan-out, was told "Stopped at budget — continue?" and
        // would sensibly raise its own budgets in Settings to prevent a recurrence, changing nothing.
        AgentRunState.WaitingForInput => DescribePause(run),
        AgentRunState.WaitingForChildren => _localization["Run_Activity_WaitingForChildren"], // 07 G8
        // A user-paused run says so in words. It used to say nothing here because the old header's state chip was
        // the only carrier; the band's lead line is now the carrier, and a blank lead beside a Continue button
        // would leave the reader guessing whether the run stopped itself or they did. This is the change the
        // "user" arm of DescribePause was written in advance for.
        AgentRunState.Paused => DescribePause(run),
        _ => null, // terminal — the band's own state name is the lead line there
    };

    /// <summary>
    /// Batch 08 D4: an optional steering note typed while the run sits paused, carried into the NEXT dispatch
    /// only and then cleared — never persisted (the scope-to-dispatch sub-choice, W7). SENSITIVE (user
    /// content): bound to the panel's TextBox, never logged. A null/blank value is inert — the box being 8b's
    /// job is what keeps this property here in 8a rather than idle.
    /// </summary>
    [ObservableProperty]
    private string? _nudgeText;

    /// <summary>
    /// Resume a budget-paused OR user-paused run (§7.2 / Batch 08 D1 item 8's CanContinue widening). The
    /// resume service CAS-claims internally by the row's own state, so a double-click or a panel+Flow race is
    /// safe; a real resume flips State→Running via RunChanged, which clears CanContinue. Carries
    /// <see cref="NudgeText"/> (null on an ordinary budget-continue) and clears it ONLY when
    /// <see cref="IAgentRunResumeService.ResumeAsync"/> returns <c>true</c> — that return means THIS call
    /// actually started the dispatch (a CAS win), so a lost race or an already-claimed run (<c>false</c>, not
    /// an exception — the <c>catch</c> below never sees it) leaves the box exactly as the user left it: a
    /// resume that never started must not silently destroy the note before the retry that follows. Logs the
    /// run id only; <see cref="NudgeText"/> is user content and never appears in a log.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task Continue()
    {
        IsResuming = true;
        try
        {
            if (await _resumeService.ResumeAsync(_runId, NudgeText))
                NudgeText = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} resume failed from panel", _runId);
        }
        finally
        {
            IsResuming = false;
        }
    }

    /// <summary>
    /// The deny beside Continue on a tool-approval park: resumes the run with the parked tool recorded in
    /// its denial list, so the re-run step hears "declined — adapt" instead of re-parking. Same double-click
    /// gate as <see cref="Continue"/>; the CAS in the resume service is the hard guard.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeclineTool))]
    private async Task DeclineTool()
    {
        IsResuming = true;
        try
        {
            await _resumeService.DeclineAsync(_runId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} decline failed from panel", _runId);
        }
        finally
        {
            IsResuming = false;
        }
    }

    /// <summary>
    /// Batch 08 D1: request a USER pause of this run. Refused (a no-op) when no steering service was injected
    /// — the trailing-optional guard, mirroring <see cref="Publish"/>'s <c>if (_workspaces is null) return;</c>
    /// rather than relying on <see cref="CanPause"/> alone, which only gates the bound button's
    /// <c>CanExecute</c> and is not a hard guard against a programmatic <c>ExecuteAsync</c>.
    /// <para>
    /// <see cref="IAgentRunSteeringService.PauseAsync"/> writes no row and returns as soon as the intent is
    /// recorded and the cancel is fired — well before the run's own loop actually lands the row at
    /// <see cref="RunProgressState.Paused"/>. On an ACCEPTED pause <see cref="IsPausing"/> therefore does NOT
    /// clear in a <c>finally</c> here; it stays true until <see cref="Project"/> observes the run having left
    /// the state the pause was requested from (see that clearing site's own comment) — a pause that has been
    /// asked for but not yet landed must not re-enable the button.
    /// </para>
    /// <para>
    /// <b>Batch 08 F6: a REFUSED pause is a different thing and is no longer treated as a slow one.</b> The
    /// <c>bool</c> used to be discarded, so every refusal — the run is not pausable, it is not dispatched in
    /// THIS process, the read faulted, or the service threw — left the button reading "Pausing…" and disabled
    /// for the VM's whole life, with nothing said and no way to retry. It is now the one case that clears
    /// <see cref="IsPausing"/> here and puts a muted line on the panel, because the request provably never
    /// existed: nothing is coming that <see cref="Project"/> could observe.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task Pause()
    {
        if (_steering is null) return;

        PauseNote = null;
        IsPausing = true;
        try
        {
            if (await _steering.PauseAsync(_runId))
                return; // accepted: the row's own move to Paused is what clears IsPausing (see Project)
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} pause failed from panel", _runId);
        }

        // Refused or faulted — give the button back and say so. Not a `finally`: the accepted path above
        // returns through it, and clearing IsPausing there is exactly the flicker this VM avoids.
        PauseNote = _localization["Run_Pause_Error_Refused"];
        IsPausing = false;
    }

    /// <summary>Opens <paramref name="row"/>'s inline editor, seeded from its CURRENT (persisted) Title/Intent
    /// — never from a stale prior edit. Purely local state: no service call, so this needs no try/catch and
    /// touches nothing but the one row.</summary>
    [RelayCommand(CanExecute = nameof(CanMutatePlan))]
    private void EditStep(StepRowViewModel row)
    {
        row.EditTitle = row.Title;
        row.EditIntent = row.Intent;
        // The editor replaces the row, so the row's own position is the only thing left saying WHICH step is being
        // edited. Computed at open time and not re-derived: submitting THIS row's edit closes the editor, so the
        // number can only drift if a reorder lands on ANOTHER row while this one is open — cosmetic, and cheaper
        // to accept than a live ordinal on every row.
        row.EditorEyebrow = _localization.Format("Run_Plan_Editor_Eyebrow", Steps.IndexOf(row) + 1);
        row.IsEditing = true;
    }

    /// <summary>Closes <paramref name="row"/>'s inline editor without submitting anything. Deliberately NOT
    /// gated on <see cref="CanMutatePlan"/>: a user mid-edit when the run stops being pausable (a rare race,
    /// not a mutation) must still be able to dismiss their own open editor.</summary>
    [RelayCommand]
    private void CancelStepEdit(StepRowViewModel row) => row.IsEditing = false;

    /// <summary>Submits the edited Title/Intent for <paramref name="row"/> as part of the FULL pending tail —
    /// every other currently-Pending row rides along verbatim (D3: the service takes the complete list, never
    /// a diff). Closes the editor unconditionally: on success the re-projection shows the saved text, on
    /// rejection it shows the UNCHANGED persisted text plus <see cref="PlanMutationNote"/> explaining why —
    /// either way there is nothing left to edit.
    /// <para>
    /// Refuses a row whose editor was never opened: <see cref="StepRowViewModel.EditTitle"/> defaults to
    /// <c>""</c>, so a Save reachable with no prior <see cref="EditStep"/> would submit a blank title as a
    /// genuine (rejected) mutation instead of doing nothing. Unreachable from the shipped markup — the Save
    /// button lives only inside the <c>IsEditing</c>-gated editor — but stated as a guard rather than left to
    /// that alone, since a command is a wider surface than the one button that happens to bind it today.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMutatePlan))]
    private async Task SaveStepEdit(StepRowViewModel row)
    {
        if (!row.IsEditing) return;

        var edits = Steps.Where(r => r.Status == AgentStepStatus.Pending)
            .Select(r => r.StepId == row.StepId
                ? new PlanStepEdit(r.StepId, row.EditTitle, row.EditIntent, r.ExpectedArtifact)
                : new PlanStepEdit(r.StepId, r.Title, r.Intent, r.ExpectedArtifact))
            .ToList();
        row.IsEditing = false;
        await ApplyStepEditsAsync(edits);
    }

    /// <summary>Inserts a new Pending step immediately after <paramref name="row"/> in the submitted order —
    /// there is no "insert above" verb (D3's stated verb set). <paramref name="row"/> must itself be Pending
    /// (<see cref="StepRowViewModel.IsMutable"/> gates its button), since only Pending steps are ever part of
    /// the submitted tail at all.</summary>
    [RelayCommand(CanExecute = nameof(CanMutatePlan))]
    private async Task InsertStepBelow(StepRowViewModel row)
    {
        var edits = new List<PlanStepEdit>();
        foreach (var r in Steps.Where(r => r.Status == AgentStepStatus.Pending))
        {
            edits.Add(new PlanStepEdit(r.StepId, r.Title, r.Intent, r.ExpectedArtifact));
            if (r.StepId == row.StepId)
                edits.Add(new PlanStepEdit(null, _localization["Run_Plan_NewStep_Title"], null, null));
        }
        await ApplyStepEditsAsync(edits);
    }

    /// <summary>Swaps <paramref name="row"/> with the PRECEDING Pending row. A no-op (no service call at all)
    /// when <paramref name="row"/> is already first among the Pending rows — reordering can never place a
    /// Pending step ahead of the settled prefix (that boundary is structurally impossible on the service side,
    /// D3), and within the Pending tail itself there is nothing above the first row to swap with.</summary>
    [RelayCommand(CanExecute = nameof(CanMutatePlan))]
    private async Task MoveStepUp(StepRowViewModel row)
    {
        var pending = Steps.Where(r => r.Status == AgentStepStatus.Pending).ToList();
        var index = pending.FindIndex(r => r.StepId == row.StepId);
        if (index <= 0) return;

        (pending[index - 1], pending[index]) = (pending[index], pending[index - 1]);
        await ApplyStepEditsAsync(ToEdits(pending));
    }

    /// <summary>Swaps <paramref name="row"/> with the FOLLOWING Pending row — the mirror of
    /// <see cref="MoveStepUp"/>, a no-op when already last.</summary>
    [RelayCommand(CanExecute = nameof(CanMutatePlan))]
    private async Task MoveStepDown(StepRowViewModel row)
    {
        var pending = Steps.Where(r => r.Status == AgentStepStatus.Pending).ToList();
        var index = pending.FindIndex(r => r.StepId == row.StepId);
        if (index < 0 || index >= pending.Count - 1) return;

        (pending[index], pending[index + 1]) = (pending[index + 1], pending[index]);
        await ApplyStepEditsAsync(ToEdits(pending));
    }

    /// <summary>Marks <paramref name="row"/> Skipped in the submitted tail — every other Pending row rides
    /// along unchanged. ONE-WAY: a skipped step joins the immutable prefix the moment this lands, so a later
    /// mutation cannot un-skip it (D3).</summary>
    [RelayCommand(CanExecute = nameof(CanMutatePlan))]
    private async Task SkipStep(StepRowViewModel row)
    {
        var edits = Steps.Where(r => r.Status == AgentStepStatus.Pending)
            .Select(r => new PlanStepEdit(r.StepId, r.Title, r.Intent, r.ExpectedArtifact, Skip: r.StepId == row.StepId))
            .ToList();
        await ApplyStepEditsAsync(edits);
    }

    private static List<PlanStepEdit> ToEdits(IEnumerable<StepRowViewModel> rows) =>
        rows.Select(r => new PlanStepEdit(r.StepId, r.Title, r.Intent, r.ExpectedArtifact)).ToList();

    /// <summary>
    /// The one call site every mutating verb shares (D3). ALWAYS re-projects — win or lose — so a fact can
    /// await the mutation's full UI-visible effect instead of racing the fire-and-forget
    /// <see cref="RefreshAsync"/> that <see cref="OnRunChanged"/> would otherwise kick off on its own, and so
    /// the panel never shows a mutation that did not land. Privacy: titles never appear in the warning line,
    /// only the run id.
    /// <para>
    /// <b>Batch 08 F13: the note is set AFTER the refresh, never before it.</b> <see cref="Project"/> clears
    /// <see cref="PlanMutationNote"/> whenever the projected state is not <c>Paused</c>, so a note set first
    /// and refreshed second is wiped by its own refresh in exactly one case — and it is the case that most
    /// needs the note. <see cref="PlanMutationOutcome.NotPaused"/> means the row has already left
    /// <c>Paused</c> (the Flow card's "Continue run", or a second window, resumed it between the click and
    /// the write), so the refresh that was meant to surface the rejection erased it: the user's Skip vanished,
    /// the whole row-button group vanished with <c>CanMutatePlan</c>, and nothing said why. The other five
    /// outcomes are returned only after the service's own <c>Paused</c> gate, so they were never affected —
    /// which is why this is a one-line reorder rather than a freshness flag.
    /// </para>
    /// </summary>
    private async Task ApplyStepEditsAsync(IReadOnlyList<PlanStepEdit> edits)
    {
        PlanMutationResult result;
        try
        {
            result = await _runService.ApplyPlanMutationAsync(_runId, edits);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} plan mutation failed from panel", _runId);
            await RefreshAsync();
            PlanMutationNote = _localization["Run_Plan_Error_WriteFailed"];
            return;
        }

        await RefreshAsync();
        PlanMutationNote = result.Outcome == PlanMutationOutcome.Applied
            ? null
            : _localization[MutationErrorKey(result.Outcome)];
    }

    /// <summary>
    /// The rejection line for one <see cref="PlanMutationOutcome"/>. <c>internal</c> for Batch 08 F14:
    /// <c>LocalizationTests.AllCodeLocalizationKeys_MustExistInResources</c> scans for LITERAL keys
    /// (<c>_localization["…"]</c>), so five of the six keys below — every one except
    /// <c>Run_Plan_Error_WriteFailed</c>, which also appears as a literal in the <c>catch</c> above — were
    /// invisible to it: renaming or dropping one in the resx left the suite green and put a raw
    /// <c>[Run_Plan_Error_TooLong]</c> in the panel. That is the exact shape <c>T-CONV-3</c> already exists to
    /// guard for <c>RunStateToLabelConverter.LabelKey</c>, and it now guards this helper the same way, by
    /// enumerating the enum rather than re-listing the keys.
    /// </summary>
    internal static string MutationErrorKey(PlanMutationOutcome outcome) => outcome switch
    {
        PlanMutationOutcome.NotPaused => "Run_Plan_Error_NotPaused",
        PlanMutationOutcome.UnknownStep => "Run_Plan_Error_UnknownStep",
        PlanMutationOutcome.TitleRequired => "Run_Plan_Error_TitleRequired",
        PlanMutationOutcome.EmptyPlan => "Run_Plan_Error_EmptyPlan",
        PlanMutationOutcome.TooLong => "Run_Plan_Error_TooLong",
        _ => "Run_Plan_Error_WriteFailed", // WriteFailed, and any future member — never a silent success read
    };

    /// <summary>
    /// Read the run's tool-decision trace and project it. <c>internal</c> so the facts can await it directly
    /// rather than racing the <c>_uiContext.Post</c> the collection fill is marshaled through (G3, by the same
    /// mechanism <see cref="RefreshAsync"/> already uses — not a new one).
    /// </summary>
    internal async Task LoadTimelineAsync()
    {
        if (_timelineService is null)
        {
            await ApplyTimelineAsync(rows: null, readFailed: false);
            return;
        }

        IReadOnlyList<AgentTimelineEvent> rows;
        try
        {
            // OFF the caller's thread. GetForRunAsync's own first await does not suspend when the writer tail
            // is already complete — the normal case for a finished run — so without this hop the store's
            // connection lock and the mapping of up to 501 rows would run on the dispatcher.
            rows = await Task.Run(() => _timelineService.GetForRunAsync(_runId));
        }
        catch (Exception ex)
        {
            // A trace that cannot be read says so; it never claims the run recorded nothing, and it never
            // breaks the panel.
            _logger.LogWarning(ex, "Run {RunId} timeline could not be read", _runId);
            await ApplyTimelineAsync(rows: null, readFailed: true);
            return;
        }

        await ApplyTimelineAsync(rows, readFailed: false);
    }

    /// <summary>
    /// The ONE place this VM mutates the trace's bound state, and it always runs on the UI thread (G3) —
    /// including the null-service and read-failure arms, which used to assign straight from whatever thread
    /// the load happened to be on.
    /// </summary>
    private Task ApplyTimelineAsync(IReadOnlyList<AgentTimelineEvent>? rows, bool readFailed)
    {
        var done = new TaskCompletionSource();
        _uiContext.Post(_ =>
        {
            try
            {
                Timeline.Clear();
                DecisionPills.Clear();
                IsTimelineTruncated = false;
                TimelineNote = null;
                HasTimelineReadError = readFailed;

                // A statement about the TRACE, not a tool call — a note, never a row, and read out before the
                // ordering below so it cannot land in the middle of the table.
                if ((rows ?? []).Any(r => r.Kind == AgentTimelineEventKind.TraceTruncated))
                {
                    IsTimelineTruncated = true;
                    TimelineNote = _localization.Format("Run_Timeline_Truncated", AgentTimelineService.MaxEventsPerRun);
                }

                // EXCEPTIONS FIRST, then everything else — each half newest-first. The store hands rows back in
                // (RunId, Seq) order, i.e. oldest first, so one Reverse gives both halves their order at once.
                // This is what turns the trace from a log dump into an audit: the two rows that need a person
                // are at the top whether they happened first or five hundred calls ago.
                var events = (rows ?? [])
                    .Where(r => r.Kind != AgentTimelineEventKind.TraceTruncated)
                    .Reverse()
                    .ToList();
                var exceptions = events.Where(e => Severity(e.Decision) != RunDecisionSeverity.Routine).ToList();
                var routine = events.Where(e => Severity(e.Decision) == RunDecisionSeverity.Routine).ToList();

                for (var i = 0; i < exceptions.Count; i++)
                    Timeline.Add(Project(exceptions[i], showGroupSeparator: false));
                for (var i = 0; i < routine.Count; i++)
                    // The rule under the exception block, drawn by the FIRST row below it so the table needs no
                    // separate separator item (which would be a row the reflection guard has to allow for).
                    Timeline.Add(Project(routine[i], showGroupSeparator: i == 0 && exceptions.Count > 0));

                ApplyDecisionSummary(events);
                HasNoTimeline = !readFailed && Timeline.Count == 0;
            }
            finally
            {
                done.TrySetResult();
            }
        }, null);

        return done.Task;
    }

    /// <summary>
    /// The collapsed header's call count and exception badge, plus one pill per non-empty decision category.
    /// Zero-count categories are omitted rather than shown as "0 denied": a category that did not happen is not
    /// a fact about this run.
    /// </summary>
    private void ApplyDecisionSummary(IReadOnlyList<AgentTimelineEvent> events)
    {
        // Exceptions first, in the order a reader has to act on them. Written as an explicit list, not driven off
        // the decision enum, because the ORDER is the point and enum order is not it.
        var categories = new (string LabelKey, string PillKey, RunDecisionSeverity Severity)[]
        {
            ("Run_Timeline_Decision_AwaitingApproval", "Run_Timeline_Pill_AwaitingApproval", RunDecisionSeverity.Awaiting),
            ("Run_Timeline_Decision_Denied", "Run_Timeline_Pill_Denied", RunDecisionSeverity.Refused),
            ("Run_Timeline_Decision_Blocked", "Run_Timeline_Pill_Blocked", RunDecisionSeverity.Refused),
            ("Run_Timeline_Decision_Approved", "Run_Timeline_Pill_Approved", RunDecisionSeverity.Routine),
            ("Run_Timeline_Decision_AutoApproved", "Run_Timeline_Pill_AutoApproved", RunDecisionSeverity.Routine),
            ("Run_Timeline_Decision_Unknown", "Run_Timeline_Pill_Unknown", RunDecisionSeverity.Routine),
        };

        TimelineExceptionBadge = null;
        TimelineExceptionSeverity = RunDecisionSeverity.Routine;

        foreach (var (labelKey, pillKey, severity) in categories)
        {
            var count = events.Count(e => DecisionLabelKey(e.Decision) == labelKey);
            if (count == 0) continue;

            var text = _localization.Format(pillKey, count);
            DecisionPills.Add(new DecisionPillViewModel { Text = text, Severity = severity });

            // The badge is the FIRST exception category with a count — awaiting outranks refused because it is
            // the one the reader can still do something about.
            if (severity != RunDecisionSeverity.Routine && TimelineExceptionBadge is null)
            {
                TimelineExceptionBadge = text;
                TimelineExceptionSeverity = severity;
            }
        }
    }

    /// <summary>
    /// The render severity of one decision, derived from <see cref="DecisionLabelKey"/> rather than from a second
    /// switch over <see cref="ToolGateDecision"/>: two mappings over the same eleven ordinals is how a decision
    /// ends up labelled "Denied" and painted in the routine grey.
    /// </summary>
    internal static RunDecisionSeverity Severity(ToolGateDecision decision) => DecisionLabelKey(decision) switch
    {
        "Run_Timeline_Decision_AwaitingApproval" => RunDecisionSeverity.Awaiting,
        "Run_Timeline_Decision_Denied" or "Run_Timeline_Decision_Blocked" => RunDecisionSeverity.Refused,
        _ => RunDecisionSeverity.Routine,
    };

    private TimelineRowViewModel Project(AgentTimelineEvent row, bool showGroupSeparator) => new()
    {
        ToolName = row.ToolName,
        DecisionLabel = _localization[DecisionLabelKey(row.Decision)],
        OutcomeSuffix = row.Outcome == AgentTimelineOutcome.Error
            ? _localization["Run_Timeline_Outcome_Failed"]
            : null,
        StepLabel = row.StepId is { } stepId && Steps.Any(s => s.StepId == stepId)
            ? _localization.Format("Run_Timeline_Step", Steps.IndexOf(Steps.First(s => s.StepId == stepId)) + 1)
            : null,
        TimeLabel = row.CreatedAt.ToLocalTime().ToString("t"),
        Severity = Severity(row.Decision),
        ShowGroupSeparator = showGroupSeparator,
    };

    /// <summary>
    /// Eleven persisted decision ordinals collapse to five user-facing categories — the DB stays precise, the
    /// panel stays readable. Written as a switch with an explicit default arm, never an array index, so an
    /// ordinal from a future build renders as "unknown" instead of throwing (the append-only rule's other
    /// half).
    /// </summary>
    internal static string DecisionLabelKey(ToolGateDecision decision) => decision switch
    {
        // hermes #15's AutoApprovedSessionGrant folds in with the other standing authorities: from the
        // panel's point of view the call ran without anyone being asked, which is what this category says.
        ToolGateDecision.AutoApprovedStandingGrant or ToolGateDecision.AutoApprovedPolicy
            or ToolGateDecision.GrantedByName or ToolGateDecision.AutoApprovedAllowlist
            or ToolGateDecision.AutoApprovedSessionGrant
            => "Run_Timeline_Decision_AutoApproved",
        // ...and ApprovedForSession with the other card answers: a person said yes to this row.
        ToolGateDecision.ApprovedOnce or ToolGateDecision.ApprovedAlways
            or ToolGateDecision.ApprovedForSession
            => "Run_Timeline_Decision_Approved",
        // DeniedForRun is the deny beside a tool-approval park: a person said no to this row.
        ToolGateDecision.DeclinedByUser or ToolGateDecision.CardCancelled
            or ToolGateDecision.DeniedNotGranted or ToolGateDecision.UnknownTool
            or ToolGateDecision.DeniedForRun
            => "Run_Timeline_Decision_Denied",
        ToolGateDecision.DeniedDestructiveFloor => "Run_Timeline_Decision_Blocked",
        // hermes #16. Its own category, not folded into Denied: the call was not denied, it is WAITING — and
        // a timeline that said "denied" for the one row the user is expected to answer would misreport the
        // reason their run stopped. Without this arm it lands on "unknown", which is no better.
        ToolGateDecision.ParkedForApproval => "Run_Timeline_Decision_AwaitingApproval",
        _ => "Run_Timeline_Decision_Unknown",
    };

    // Truncated-Completed marker lives in ExtraJson as {truncated:true,reason} (IAgentRunService.CompleteAsync).
    // Both halves are read in one parse: the flag drives the state, the reason drives the chip copy.
    // A malformed/absent envelope degrades to "not truncated" (the panel stays quiet rather than guessing).
    private static (bool Truncated, string? Reason) ReadTruncation(AgentRun run)
    {
        if (string.IsNullOrEmpty(run.ExtraJson)) return (false, null);
        try
        {
            using var doc = JsonDocument.Parse(run.ExtraJson);
            if (!doc.RootElement.TryGetProperty("truncated", out var t) || t.ValueKind != JsonValueKind.True)
                return (false, null);
            var reason = doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
            return (true, reason);
        }
        catch
        {
            return (false, null);
        }
    }

    // Diff by step Id so the Running highlight moves without rebuilding the whole list.
    private void SyncSteps(IReadOnlyList<AgentStep> plan)
    {
        // Drop rows no longer in the plan.
        for (var i = Steps.Count - 1; i >= 0; i--)
        {
            if (!plan.Any(s => s.Id == Steps[i].StepId))
                Steps.RemoveAt(i);
        }

        for (var ordinal = 0; ordinal < plan.Count; ordinal++)
        {
            var step = plan[ordinal];
            var existing = Steps.FirstOrDefault(r => r.StepId == step.Id);
            if (existing is null)
            {
                var row = StepRowViewModel.From(step);
                ApplyPersonaAttribution(row);
                if (ordinal <= Steps.Count)
                    Steps.Insert(ordinal, row);
                else
                    Steps.Add(row);
            }
            else
            {
                existing.Status = step.Status; // move the highlight / update the glyph
                // Batch 08 W12: an EDIT preserves the step Id, so this is the only branch that ever sees a
                // rewritten Title/Intent/ExpectedArtifact — the row is never re-minted for those alone (R23).
                existing.Title = step.Title;
                existing.Intent = step.Intent;
                existing.ExpectedArtifact = step.ExpectedArtifact;
                ApplyPersonaAttribution(existing); // re-applied every pass — see the field's own doc comment
            }
        }

        // Batch 08 W12: reconcile ORDER as a SEPARATE pass, after the drop/insert/update pass above has
        // settled every row's presence and content. The insert pass only ever INSERTS a brand-new row at its
        // plan index; it never MOVES an existing one, so a reorder that preserves every step's Id (which is
        // the whole point of a reorder — the settled prefix's ledger/timeline rows stay attached) would
        // otherwise repaint content in place but leave the collection in its old visual order forever. Kept
        // separate from the loop above on purpose: an ObservableCollection.Move interleaved with that loop's
        // own index-based Insert calls would invalidate the indices it is still iterating.
        for (var ordinal = 0; ordinal < plan.Count; ordinal++)
        {
            var stepId = plan[ordinal].Id;
            var currentIndex = -1;
            for (var i = 0; i < Steps.Count; i++)
            {
                if (Steps[i].StepId == stepId) { currentIndex = i; break; }
            }
            if (currentIndex >= 0 && currentIndex != ordinal)
                Steps.Move(currentIndex, ordinal);
        }
    }

    /// <summary>
    /// Resolves a step row's avatar fields from <see cref="_personas"/> (Batch 07 §0.7/§4.3). SETTABLE on
    /// <see cref="StepRowViewModel"/>, not init-only: <see cref="RefreshAsync"/> is invoked from the
    /// CONSTRUCTOR (R21/R22), so the first projection can land before the persona map has loaded — an
    /// init-only row minted on that pass would never be corrected, because rows are replaced only when
    /// step IDS change (R23), never re-minted for a data change alone.
    /// </summary>
    private void ApplyPersonaAttribution(StepRowViewModel row)
    {
        if (row.AssignedPersonaId is { } id && _personas is not null && _personas.TryGetValue(id, out var persona))
        {
            row.PersonaId = persona.Id;
            row.PersonaEmoji = persona.Emoji;
            row.PersonaAccent = persona.AccentColor;
            row.PersonaName = persona.Name;
        }
        else
        {
            row.PersonaId = Guid.Empty;
            row.PersonaEmoji = null;
            row.PersonaAccent = null;
            row.PersonaName = null;
        }
    }

    /// <summary>
    /// Diff the child rows by run id, exactly as <see cref="SyncSteps"/> diffs steps and for the same reason: a
    /// rebuild on every <c>RunChanged</c> would collapse an expanded row and throw away its loaded trace under
    /// the user's cursor. Always on the UI thread (called from <see cref="Project"/>).
    /// </summary>
    private void SyncChildren(IReadOnlyList<AgentRun> children)
    {
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            if (!children.Any(c => c.Id == Children[i].RunId))
                Children.RemoveAt(i);
        }

        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            var existing = Children.FirstOrDefault(r => r.RunId == child.Id);
            if (existing is null)
            {
                var row = new ChildRunRowViewModel(child.Id, child.Goal ?? string.Empty, RequestChildTimeline);
                Apply(row, child);
                if (index <= Children.Count)
                    Children.Insert(index, row);
                else
                    Children.Add(row);
            }
            else
            {
                Apply(existing, child);
            }
        }

        // Written from the ROWS the projection just built, not from the argument, so the snapshot and what the
        // panel shows can never disagree. One assignment — never a mutation (see _childRunIds).
        _childRunIds = Children.Select(r => r.RunId).ToImmutableHashSet();
        HasChildren = Children.Count > 0;
        ChildrenNote = HasChildren
            ? _localization.Format("Run_Children_Count", Children.Count(r => r.IsFinished), Children.Count)
            : null;

        // Not static: the token figure is localized, so the row's label needs the instance.
        void Apply(ChildRunRowViewModel row, AgentRun child)
        {
            row.State = MapState(child, ReadTruncation(child).Truncated).Item1;
            var ledger = TryParseLedger(child.LedgerJson);
            row.InputTokens = ledger?.InputTokens ?? 0;
            row.OutputTokens = ledger?.OutputTokens ?? 0;
            row.TokensLabel = row.InputTokens + row.OutputTokens > 0
                ? TokensFigure(row.InputTokens + row.OutputTokens)
                : null;
        }
    }

    /// <summary>Start a child row's trace load, owning the fire-and-forget (and the logger) the way the parent's
    /// own expander does.</summary>
    private void RequestChildTimeline(ChildRunRowViewModel row)
    {
        var load = LoadChildTimelineAsync(row);
        row.TimelineLoadTask = load;
        load.SafeFireAndForget(_logger);
    }

    /// <summary>
    /// Load ONE child run's own trace, through the same store call and the same off-thread hop the parent's
    /// timeline uses. Two per-run views side by side — never one interleaved list (see <see cref="Children"/>).
    /// Failure-isolated: a fault leaves that row's trace empty and says so, and never breaks the panel.
    /// </summary>
    private async Task LoadChildTimelineAsync(ChildRunRowViewModel row)
    {
        if (_timelineService is null)
        {
            await ApplyChildTimelineAsync(row, rows: null, readFailed: false);
            return;
        }

        IReadOnlyList<AgentTimelineEvent> rows;
        try
        {
            rows = await Task.Run(() => _timelineService.GetForRunAsync(row.RunId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Child run {RunId} timeline could not be read", row.RunId);
            await ApplyChildTimelineAsync(row, rows: null, readFailed: true);
            return;
        }

        await ApplyChildTimelineAsync(row, rows, readFailed: false);
    }

    /// <summary>The ONE place a child row's trace state is mutated, always on the UI thread (G3).</summary>
    private Task ApplyChildTimelineAsync(ChildRunRowViewModel row, IReadOnlyList<AgentTimelineEvent>? rows, bool readFailed)
    {
        var done = new TaskCompletionSource();
        _uiContext.Post(_ =>
        {
            try
            {
                row.Timeline.Clear();
                row.HasTimelineReadError = readFailed;

                // the truncation marker's note belongs to the parent's own expander; a child row stays one list
                var events = (rows ?? [])
                    .Where(e => e.Kind != AgentTimelineEventKind.TraceTruncated)
                    .Reverse()
                    .ToList();
                var exceptions = events.Where(e => Severity(e.Decision) != RunDecisionSeverity.Routine).ToList();
                var routine = events.Where(e => Severity(e.Decision) == RunDecisionSeverity.Routine).ToList();

                // Same exception-first ordering as the parent's trace: a child that parked for approval is
                // exactly as easy to miss at the bottom of a child's list as at the bottom of the parent's.
                foreach (var e in exceptions)
                    row.Timeline.Add(Project(e, showGroupSeparator: false));
                for (var i = 0; i < routine.Count; i++)
                    row.Timeline.Add(Project(routine[i], showGroupSeparator: i == 0 && exceptions.Count > 0));

                row.HasNoTimeline = !readFailed && row.Timeline.Count == 0;
            }
            finally
            {
                done.TrySetResult();
            }
        }, null);

        return done.Task;
    }

    private void ApplyPerStepLedger(Ledger ledger)
    {
        foreach (var entry in ledger.PerStep)
        {
            if (!Guid.TryParse(entry.StepId, out var id)) continue;
            var row = Steps.FirstOrDefault(r => r.StepId == id);
            if (row is null) continue;
            row.InputTokens = entry.InputTokens;
            row.OutputTokens = entry.OutputTokens;
            row.TokensLabel = entry.InputTokens + entry.OutputTokens > 0
                ? TokensFigure(entry.InputTokens + entry.OutputTokens)
                : null;
        }
    }

    /// <summary>The localized "N tokens" figure — the bare number alone read as an id, not a cost.</summary>
    private string TokensFigure(long total) => _localization.Format("Run_Sub_Tokens", total.ToString("N0"));

    private static Ledger? TryParseLedger(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<Ledger>(json, LedgerJsonOptions); }
        catch { return null; }
    }

    private string FormatLedger()
    {
        var parts = new List<string> { TokensFigure(TotalInputTokens + TotalOutputTokens) };
        if (WallClockMs > 0)
            parts.Add(FormatDuration(WallClockMs));
        return string.Join(" · ", parts);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runService.RunChanged -= OnRunChanged;
        if (_timelineWatcher is not null)
            _timelineWatcher.TimelineAppended -= OnTimelineAppended;
        // The theme service is a singleton and outlives this VM, so a leaked handler would keep a whole projected
        // run, and every row in it, alive for the process's life.
        if (_themeService is not null)
            _themeService.ThemeChanged -= OnThemeChanged;
    }

    // Mirrors AgentRunService's private Ledger/StepLedger DTOs (camelCase JSON).
    private sealed class Ledger
    {
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long WallClockMs { get; set; }
        public List<StepLedgerEntry> PerStep { get; set; } = [];
    }

    private sealed class StepLedgerEntry
    {
        public string StepId { get; set; } = string.Empty;
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
    }
}
