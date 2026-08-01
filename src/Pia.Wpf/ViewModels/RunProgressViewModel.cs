using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
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
    [NotifyPropertyChangedFor(nameof(ShowPauseFirstNote))]
    [NotifyPropertyChangedFor(nameof(CanMutatePlan))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
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

    /// <summary>True while a resume is being launched — gates the Continue button against a double-click
    /// (the CAS in the resume service is the hard guard; this is the UI-visible affordance).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    private bool _isResuming;

    /// <summary>The budget-pause Continue affordance, widened by Batch 08 D1 item 8 to the user-pause state
    /// too: <see cref="RunProgressState.Paused"/> is the CAS's own target, and both states offer the identical
    /// Continue command (<see cref="IAgentRunResumeService.ResumeAsync"/> claims either via the row's own
    /// state). Trips nothing per Ground E, and the <c>!IsTerminal</c> form this could also be written as would
    /// red <c>RunProgressViewModelChildrenTests.cs</c>'s WaitingForChildren fact — this two-member set does not.</summary>
    public bool CanContinue => (State is RunProgressState.WaitingForInput or RunProgressState.Paused) && !IsResuming;

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
    public bool CanPause =>
        _steering is not null
        && (State is RunProgressState.Running or RunProgressState.WaitingForChildren)
        && !IsPausing;

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
    /// Batch 06 G4 / plan D3: a settled run whose isolated workspace still holds files nobody promoted —
    /// usually a FAILED or CANCELLED run, because a clean one promotes automatically before it is marked
    /// Completed. <b>Not only those</b>: a clean copy-mode run whose promotion hit a CONFLICT keeps its
    /// workspace too (the run's version of that file was deliberately not written and exists nowhere else), so
    /// a Completed run can legitimately raise this offer. Drives the offer line and the Publish button.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPublish))]
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
        // refresh command. One indexed read per user click is the cheaper mistake.
        var load = LoadTimelineAsync();
        TimelineLoadTask = load;
        load.SafeFireAndForget(_logger);
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
        IAgentRunSteeringService? steering = null)
    {
        _timelineService = timelineService;
        _workspaces = workspaces;
        _personaService = personaService;
        _steering = steering;
        _runService = runService;
        _runId = runId;
        _localization = localization;
        _resumeService = resumeService;
        _logger = logger;
        // Captured on the construction (UI) thread; may be null in a headless test → run inline.
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _runService.RunChanged += OnRunChanged;
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
    // Batch 08 G2: the "user" arm is not reachable TODAY — ComputeActivity returns null for Paused (the state
    // chip already carries it), so a user-paused run renders no activity line at all. It is added anyway
    // because the alternative is a latent "Stopped at budget" the day someone makes the line render for
    // Paused, and it is deliberately NOT mapped to the existing Run_State_Paused chip label: a mapping arm
    // that borrows a string written for another control reads fine and breaks on the next copy edit.
    private string DescribePause(string? reason) => reason switch
    {
        AgentRunOrchestrator.ChildrenParkedReason => _localization["Run_Activity_ChildrenParked"],
        AgentRunService.ChildrenInterruptedReason => _localization["Run_Activity_ChildrenInterrupted"],
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
        AgentRunState.WaitingForInput => DescribePause(RunPauseEnvelope.ReadReason(run)),
        AgentRunState.WaitingForChildren => _localization["Run_Activity_WaitingForChildren"], // 07 G8
        _ => null, // Paused / terminal — the state chip already carries it
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
                IsTimelineTruncated = false;
                TimelineNote = null;
                HasTimelineReadError = readFailed;

                foreach (var row in rows ?? [])
                {
                    if (row.Kind == AgentTimelineEventKind.TraceTruncated)
                    {
                        // A statement about the TRACE, not a tool call — surfaced as a note, never as a row.
                        IsTimelineTruncated = true;
                        TimelineNote = _localization.Format("Run_Timeline_Truncated", AgentTimelineService.MaxEventsPerRun);
                        continue;
                    }

                    Timeline.Add(Project(row));
                }

                HasNoTimeline = !readFailed && Timeline.Count == 0;
            }
            finally
            {
                done.TrySetResult();
            }
        }, null);

        return done.Task;
    }

    private TimelineRowViewModel Project(AgentTimelineEvent row) => new()
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
    };

    /// <summary>
    /// Eleven persisted decision ordinals collapse to five user-facing categories — the DB stays precise, the
    /// panel stays readable. Written as a switch with an explicit default arm, never an array index, so an
    /// ordinal from a future build renders as "unknown" instead of throwing (the append-only rule's other
    /// half).
    /// </summary>
    internal static string DecisionLabelKey(ToolGateDecision decision) => decision switch
    {
        ToolGateDecision.AutoApprovedStandingGrant or ToolGateDecision.AutoApprovedPolicy
            or ToolGateDecision.GrantedByName or ToolGateDecision.AutoApprovedAllowlist
            => "Run_Timeline_Decision_AutoApproved",
        ToolGateDecision.ApprovedOnce or ToolGateDecision.ApprovedAlways
            => "Run_Timeline_Decision_Approved",
        ToolGateDecision.DeclinedByUser or ToolGateDecision.CardCancelled
            or ToolGateDecision.DeniedNotGranted or ToolGateDecision.UnknownTool
            => "Run_Timeline_Decision_Denied",
        ToolGateDecision.DeniedDestructiveFloor => "Run_Timeline_Decision_Blocked",
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
        }
        else
        {
            row.PersonaId = Guid.Empty;
            row.PersonaEmoji = null;
            row.PersonaAccent = null;
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

        static void Apply(ChildRunRowViewModel row, AgentRun child)
        {
            row.State = MapState(child, ReadTruncation(child).Truncated).Item1;
            var ledger = TryParseLedger(child.LedgerJson);
            row.InputTokens = ledger?.InputTokens ?? 0;
            row.OutputTokens = ledger?.OutputTokens ?? 0;
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

                foreach (var e in rows ?? [])
                {
                    if (e.Kind == AgentTimelineEventKind.TraceTruncated)
                        continue; // the note belongs to the parent's own expander; a child row stays one list

                    row.Timeline.Add(Project(e));
                }

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
        }
    }

    private static Ledger? TryParseLedger(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<Ledger>(json, LedgerJsonOptions); }
        catch { return null; }
    }

    private string FormatLedger()
    {
        var parts = new List<string> { $"{TotalInputTokens + TotalOutputTokens:N0} Tokens" };
        if (WallClockMs > 0)
            parts.Add($"{WallClockMs / 1000.0:0.#}s");
        return string.Join(" · ", parts);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runService.RunChanged -= OnRunChanged;
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

/// <summary>
/// Read-only row for one delegated CHILD run (Batch 07 D17) — the drill-down target. <see cref="Title"/> is the
/// child's goal and is SENSITIVE: bound to UI only, never logged, exactly like <c>StepRowViewModel.Title</c>.
/// <para>
/// Expanding a row loads THAT run's own trace, per run and never merged into the parent's — see
/// <c>RunProgressViewModel.Children</c> for why interleaving is not implementable.
/// </para>
/// </summary>
public sealed partial class ChildRunRowViewModel : ObservableObject
{
    private readonly Action<ChildRunRowViewModel> _requestTimeline;

    /// <param name="requestTimeline">Starts this row's trace load. An <c>Action</c> and not a
    /// <c>Func&lt;Task&gt;</c> on purpose: the fire-and-forget belongs to the owner, which has the logger — a row
    /// that swallowed its own faults would need a logger of its own for no other reason.</param>
    public ChildRunRowViewModel(Guid runId, string title, Action<ChildRunRowViewModel> requestTimeline)
    {
        RunId = runId;
        Title = title;
        _requestTimeline = requestTimeline;
    }

    public Guid RunId { get; }

    /// <summary>The child's goal. SENSITIVE user/model content — bound, never logged.</summary>
    public string Title { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    private RunProgressState _state;

    [ObservableProperty]
    private long _inputTokens;

    [ObservableProperty]
    private long _outputTokens;

    /// <summary>Whether this child will still change. Drives the parent's "N of M finished" count only.</summary>
    public bool IsFinished => State is RunProgressState.Completed or RunProgressState.TruncatedCompleted
        or RunProgressState.Failed;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>This child run's own tool-decision trace. Loaded on each expand, like the parent's.</summary>
    public ObservableCollection<TimelineRowViewModel> Timeline { get; } = [];

    [ObservableProperty]
    private bool _hasNoTimeline = true;

    [ObservableProperty]
    private bool _hasTimelineReadError;

    /// <summary>The in-flight (or last) trace load, exposed so a fact can await the fire-and-forget the expand
    /// starts rather than racing it — the same affordance the parent's <c>TimelineLoadTask</c> is.</summary>
    internal Task? TimelineLoadTask { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value) return;

        // Re-read on EVERY expand, for the reason the parent's own expander records: a trace read while the
        // child was still working would otherwise keep claiming "nothing recorded" for the rest of the session.
        _requestTimeline(this);
    }
}

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
    private long _inputTokens;

    [ObservableProperty]
    private long _outputTokens;

    public bool IsRunning => Status == AgentStepStatus.Running;

    /// <summary>
    /// Batch 08 D3: gates the row's five plan-mutation buttons' <c>IsEnabled</c> — a settled step (Done,
    /// Skipped or Failed) never offers to be edited, inserted after, reordered or skipped again (a skip is
    /// ONE-WAY). This is independent of <see cref="RunProgressViewModel.CanMutatePlan"/>, which gates the
    /// SAME buttons' visibility and each command's own <c>CanExecute</c> at the run level — a live run hides
    /// the whole row-button group; a paused run still greys out a settled row's group via this property.
    /// </summary>
    public bool IsMutable => Status == AgentStepStatus.Pending;

    partial void OnStatusChanged(AgentStepStatus value) => OnPropertyChanged(nameof(IsRunning));

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

/// <summary>
/// Read-only row for one recorded tool decision (Batch 03). Everything here is metadata — the store holds no
/// tool arguments, no results and no paths, so there is nothing else to project and nothing here to reveal.
/// A property that named a file or carried a payload would fail the reflection assert in
/// <c>RunProgressViewModelTimelineTests</c>.
/// </summary>
public sealed class TimelineRowViewModel : ObservableObject
{
    /// <summary>Schema, not user content: a built-in constant or an MCP server's declared tool name.</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>One of five localized categories over the eleven persisted decision ordinals.</summary>
    public string DecisionLabel { get; init; } = string.Empty;

    /// <summary>Localized "failed" when the authorized call threw; null otherwise.</summary>
    public string? OutcomeSuffix { get; init; }

    /// <summary>"Step N" when the row's step is still in the projected plan; null when it is not (a replan
    /// deletes step rows, and the trail deliberately outlives them).</summary>
    public string? StepLabel { get; init; }

    public string TimeLabel { get; init; } = string.Empty;
}
