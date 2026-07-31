export const meta = {
  name: 'phase3',
  description: 'Implement Phase 3 of the agent-run spine: Batch 06 workspace isolation + Batch 07 sub-agents',
  whenToUse: 'Run 1: args {stopAfterGroup:"G5"} builds all of Batch 06. Run 2: args {startAtGroup:"G6", skipPlanning:true} builds Batch 07. Grounding and the nine owner decisions live in docs/superpowers/specs/agent-roadmap/phase3-workflow-plan.md.',
  phases: [
    { title: 'Detail planning', detail: '2 opus spec authors + 1 opus reconciler; writes both .impl.md files' },
    { title: 'Implement', detail: 'sequential builders, commit per group' },
    { title: 'Simplify', detail: 'one sonnet pass per batch built' },
    { title: 'Review', detail: '3 opus lenses, then a refute-by-default verify per finding' },
    { title: 'Fix', detail: 'opus over CONFIRMED findings, then the roadmap update' },
  ],
}

const REPO = 'C:/projects/Pia.Wpf'
const PLAN = 'docs/superpowers/specs/agent-roadmap/phase3-workflow-plan.md'
const SPEC06 = 'docs/superpowers/specs/agent-roadmap/06-run-workspace-isolation.impl.md'
const SPEC07 = 'docs/superpowers/specs/agent-roadmap/07-subagents-multipersona.impl.md'

const GATE = `
BUILD + TEST GATE (this repo's commit-ready bar — a group is not done until this passes):

  1. dotnet build -t:Rebuild -v:n
  2. dotnet build -t:Rebuild -v:n -c Release
  3. dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"

The bar is 0 errors AND 0 warnings in BOTH configurations, and failed: 0 on the suite.

  * Read the warning count off MSBuild's "N Warning(s)" summary line. At -v:n every warning prints TWICE
    (inline + summary), so grepping the log double-counts. Do not grep for the count.
  * -t:Rebuild is mandatory. An INCREMENTAL build skips CoreCompile and therefore does not re-emit analyzer
    warnings, so an incremental "0 warnings" is meaningless. Sanity-check that the rebuild was genuine by counting
    CoreCompile/Csc invocations in the log: expect 4 (Pia.Shared, Pia.Wpf, the Pia.Wpf_<hash>_wpftmp XAML markup
    pass, Pia.Wpf.Tests).
  * WPF re-reports src/ warnings a second time under the generated wpftmp project. Fixing the source clears both.
  * This work is test-heavy, which is exactly the trap: 186 of this repo's historical 194 warnings were xUnit
    analyzer warnings in the test project. YOUR NEW TESTS MUST ADD ZERO. xUnit1051 (an untokened Task.Delay) is
    the one that has actually fired here before.
  * If a warning is genuinely wrong for the code, suppress it NARROWLY: a scoped #pragma warning disable <ID> /
    restore around the offending lines plus a comment saying why. Never a project-wide <NoWarn>.

TWO KNOWN INTERMITTENTS — re-run the class in isolation before calling either a regression:
  * TaskExtensionsTests.SafeFireAndForget_SlowTask_DoesNotBlock — wall-clock assumptions; low single-digit %,
    bursty and load-dependent; 4/4 green isolated.
  * AssistantChatConcurrencyTests.DeleteAllAsync_WithAnotherConnectionCommittingThroughout_Completes — its own
    comment calls the detection window PROBABILISTIC; never measured at base.
Neither failing ALONE fails the gate. Any OTHER red test does.
`

const CONSTRAINTS = `
STANDING CONSTRAINTS (${PLAN} §7 has the full list — these are the ones that bite):

  * CRLF. Every source and doc file in this repo is CRLF and the Write tool emits LF. If you CREATE a file,
    convert it to CRLF before committing — LF has already broken byte-identical raw-string tests here.
  * DO NOT push, merge or rebase. The branch is unpushed by owner decision and ~49 commits ahead of origin.
    Commit locally, nothing else.
  * Append-only persisted enums and ordinals. Never insert, never renumber. Paused(4) is RESERVED for Batch 08
    live-steering — do not take it.
  * The grant envelope's version check is an EXACT equality (envelope.V != 1). A bump makes every persisted
    envelope unreadable at once, so envelope changes stay ADDITIVE members of v:1.
  * Privacy-first logging. File PATHS and FILENAMES are user content. There is NO SensitiveError helper — the
    highest DEBUG-erased severity is SensitiveWarning. Information-and-above lines carry counts, booleans and ids
    only: "promoted {Count} files", never "{Path}". SafeUrl does not apply to paths.
  * A new user-visible string lands in ViewStrings.resx AND ViewStrings.de.resx AND ViewStrings.fr.resx —
    en/de/fr parity is test-enforced. Never hand-edit Designer.cs.
  * MVVM: logic in ViewModels, not Views. [ObservableProperty] / [RelayCommand]. Namespaces use "Pia", not
    "Pia.Wpf". Fields _camelCase. 4-space C#, 2-space XAML.
  * ViewModels must not reference System.Windows — an architecture rule enforces it and exempts only
    AssistantViewModel. RunProgressViewModel is NOT exempt.
  * Failure-isolated bookkeeping (Safe* wrappers), no interactive regression, executor parity (Live AND
    Headless), off-thread RunChanged stays marshaled.
`

const COMMIT = `
COMMITTING: one commit per group, at the end, only once the gate above is green.
Match this repo's commit style exactly — run \`git log -5 --format=%B\` first and copy the subject shape and any
trailer convention you find there verbatim. Subjects here look like "Chats: pin the non-deferred BeginTransaction
the delete-all relies on" — an area prefix, then a lowercase statement of what changed, no ticket numbers.
Stage deliberately (\`git add <paths>\`); never \`git add -A\` — the tree may carry unrelated local state.
`

const METHOD = `
METHOD RULES for this repo, learned the hard way and non-negotiable:

  * THE CODE WINS over any spec prose. This repo has a recorded history of logged hazards turning out to be
    PREMISE ERRORS (a SQLITE_BUSY_SNAPSHOT hazard that did not exist; a transitive-package exclusion that would
    have made CVE reporting worse). If a spec sentence and the code disagree, the code is right — annotate the
    spec in place, do not "fix" correct code to match stale prose.
  * A test over an EMPTY type set passes. Any name-scoped architecture assertion needs a non-vacuity guard
    (Assert.Single / a positive control) or a rename silently turns it green.
  * Demonstrate red-before-green for every BEHAVIOURAL fix: neutralise the fix, watch the test fail, restore it.
    If a test is a guard rather than a regression, say so in its own comment.
  * Do not claim a result you did not measure. Quote the actual gate output.
`

const GROUPS = [
  {
    id: 'G1',
    batch: '06',
    model: 'sonnet',
    title: 'Guard carve-out + verifier root',
    behaviourChange: false,
    content: `Two prep changes, NO behaviour change (the workspace root is still null everywhere after this group):

(a) GUARD CARVE-OUT. SensitivePathGuard.BuildBlockedRoots() blocks %LOCALAPPDATA%\\Pia wholesale via
    AddEnv("LOCALAPPDATA","Pia") (SensitivePathGuard.cs:74) and BuildAllowedExceptions() carves out exactly one
    island, AssistantWorkspace.LegacyWorkdir (:103). The runs base dir
    (Path.Combine(LocalApplicationData,"Pia","runs"), HeadlessRunLauncher.cs:107) is inside the blocked root and
    is NOT an exception — so every file op inside a run workspace would pass containment and then be REJECTED by
    the guard at one of six IsBlocked sites (FilesToolHandler.cs:689, 803, 984, 1050, 1230, 1253).
    Promote the runs base dir to a SHARED CONSTANT next to AssistantWorkspace.LegacyWorkdir/DefaultRoot, have
    HeadlessRunLauncher use it instead of composing the path inline, and add it to BuildAllowedExceptions so the
    guard and the launcher cannot drift apart.

(b) VERIFIER ROOT. The ambient is set PER STEP and restored in the step's finally
    (HeadlessTurnExecutor.cs:289-291 set, :314-315 restore). Verify runs from the orchestrator OUTSIDE any step
    flow (AgentRunOrchestrator.cs:211-212 -> SafeVerify -> AgentVerifier.VerifyAsync), so TaskAmbient.Current is
    null there and AgentVerifier.cs:210's "ambientRoot ?? settings.AssistantFilesFolder" silently falls back to
    the settings folder. Once steps write into the workspace, that makes EVERY declared ExpectedArtifact probe the
    wrong root -> verdict fails -> the shared replan budget burns -> every run ends Completed+"unverified".
    Add RunContext.WorkspaceRoot as a settable property, exactly symmetric with the existing
    RunContext.WorkingSubpath (RunContext.cs:58); assign it in HeadlessTurnExecutor.BeginRunAsync next to the
    existing "ctx.WorkingSubpath = null" line (:130); and have AgentVerifier.TryBuildArtifactFactsAsync prefer
    ctx.WorkspaceRoot over the (null-during-verify) ambient.
    Correct AgentVerifier's doc comment at ~:262, which currently ASSERTS "WorkspaceRoot is null in production and
    the settings folder IS the root the step writes landed in" — Phase 3 falsifies it, and these ownership
    comments are load-bearing here.

TESTS — the part that has to be right: FilesToolHandlerWorkspaceEscapeTests roots its _runRoot under
Path.GetTempPath() (:137-141), which is OUTSIDE every blocked root, so the existing suite STRUCTURALLY CANNOT SEE
the guard collision. Your new tests must root at the REAL shape (LocalApplicationData\\Pia\\runs\\<guid>) or drive
the launcher through its runsBaseDirOverride ctor parameter (HeadlessRunLauncher.cs:97), and must assert a
SUCCESSFUL write inside the workspace — not only that escapes are still rejected. Also assert that an artifact
written into the workspace root is FOUND by the verifier's probe.`,
  },
  {
    id: 'G2',
    batch: '06',
    model: 'opus',
    title: 'Flip both Initialize call sites to the run root',
    behaviourChange: true,
    content: `THE FIRST COMMIT WHERE BEHAVIOUR CHANGES. Pass the run root instead of null at both
HeadlessTurnExecutor.Initialize call sites in HeadlessRunLauncher: the launch path (~:209) and the resume path
(~:339). NOTE the older spec's anchors DRIFTED — Initialize is at HeadlessTurnExecutor.cs:104-116, not :91, and
:181/:289 are a CTS construction and a grant-envelope restore respectively. Verify by reading, not by trusting
either number.

Rewrite the two doc comments that currently describe null as the INTENDED production value
(HeadlessTurnExecutor.cs:88-99 and the inline comment above the launch call). They are load-bearing and they
become actively misleading after this commit.

Re-root the workspace-escape tests so they exercise the real runs-dir shape rather than GetTempPath, and add
coverage that a run's write actually lands inside %LOCALAPPDATA%\\Pia\\runs\\<runId> and that traversal out of it
is still rejected. Escapes must still be rejected — the run base root is a hard boundary, unchanged.

Do NOT touch the promotion story here; nothing is promoted yet. After this group a headless run writes into its
own workspace and the deliverable does not reach the assistant folder. That is expected and temporary — G4 closes
it. Say so in the commit message so the intermediate state is not mistaken for a bug.`,
  },
  {
    id: 'G3',
    batch: '06',
    model: 'opus',
    title: 'Workspace provisioning: worktree when repo, else copy',
    behaviourChange: true,
    content: `OWNER DECISION D5: "worktree when the root is a repo, else copy". Build the provisioner that owns
BOTH modes and its symmetric teardown.

WORKTREE MODE. When the effective source root resolves to a git repo toplevel and git is installed, provision the
run workspace as "git worktree add" on a fresh branch (sketch: pia/run/<runId>) instead of an empty directory.
Reuse what exists rather than inventing it: GitToolHandler injects IGitProcessRunner and gates on IsGitInstalled
(GitToolHandler.cs:46, :74), already runs "git rev-parse --show-toplevel" on EVERY call as its is-repo check
(:532), and already passes a ceiling directory for containment (:673). There is deliberately NO "git worktree" in
the agent tool surface (:150-160) — provisioning is APP-SIDE and must stay that way. This adds no new agent
capability.

COPY MODE. Everything else: the plain directory of today, unchanged.

DEGRADE, DO NOT FAIL. Any fault in the worktree path (git absent, rev-parse non-zero, worktree add fails, a
detached/bare repo, a path git rejects) degrades to COPY mode. A run must never fail because provisioning got
clever. Log the mode at Information with ids/booleans only — never the path.

TEARDOWN IS SYMMETRIC AND IT IS THE PART THAT LEAKS. In worktree mode the workspace is not just a directory:
"git worktree remove" (and prune) is required, or the user's repo keeps a stale .git/worktrees/<id> registration
forever. Route the existing cleanup paths through the provisioner — the launch-time sweep
(HeadlessRunLauncher.cs:451, whose only predicates are "run is null" and a 30-day age) and OnChatsChanged
(:480-498). Worktree mode MUTATES the user's repo (a worktrees entry + a branch ref) even though the working tree
is untouched: accepted, but teardown must be exact.

GIT TOOL PARITY. GitToolHandler carries its own independent copy of the root-resolution pattern
(baseRoot = _currentFolder at :138, its own ResolveEffectiveRoot at :675) and NEVER reads
TaskAmbient.Current?.WorkspaceRoot — so without this change the agent writes into the workspace and commits the
interactive folder's stale tree. Make it read the ambient WorkspaceRoot the same way FilesToolHandler does
(FilesToolHandler.cs:170-171), so files and git agree in BOTH modes.

KNOWN AND ACCEPTED, worth a comment: a worktree starts from a COMMIT, so uncommitted and untracked files in the
user's tree are invisible to the run. That is a release-note item, not a bug to fix.`,
  },
  {
    id: 'G4',
    batch: '06',
    model: 'opus',
    title: 'Promotion + publish affordance',
    behaviourChange: true,
    content: `OWNER DECISIONS D3 ("Completed auto, else offer to publish") and D5b ("the branch is the
deliverable").

ORDERING IS FORCED AND IT MATTERS: drain steps -> verify (against the run root, which G1 made possible) ->
promote -> CompleteAsync. Promoting inside the terminal-settle path BEFORE CompleteAsync is what dissolves the
"Completed but not yet promoted" crash window without needing a promotion-aware sweep.

COPY MODE PROMOTION: copy the run workspace into the AssistantFilesFolder ROOT. That preserves today's
destination byte-for-byte — a headless run creates its own stub chat with no WorkingDirectory
(HeadlessRunLauncher.cs:128-138) and BeginRunAsync deliberately sets ctx.WorkingSubpath = null (:130), so relative
paths inside the run are unchanged.

WORKTREE MODE PROMOTION: the branch IS the deliverable. NO automatic merge — that keeps conflict handling out of
an unattended path entirely. The run panel must state plainly that the output is on branch X, or the user will ask
"where is my file?" and find nothing. New user-visible string -> all three resx files.

FAILED / CANCELLED RUNS: do not promote automatically; surface an offer to publish what the run produced, reusing
the existing notification/action surface rather than inventing one. Needs loc keys in all three resx files AND a
retention rule so an unanswered offer cannot pin a workspace forever. Decide and DOCUMENT the interaction with the
30-day sweep.

NAMING: NamingConventionTests' allowedSuffixes list (:32-36) does NOT contain "Promoter" — name the type
...Service / ...Handler / ...Store. Do not grow the allowlist for one type. Register the new interface in
Bootstrapper or DiRegistrationTests fails.

LOGGING: counts, ids and booleans at Information ("promoted {Count} files"). Paths ONLY via SensitiveWarning or a
scoped #if DEBUG — there is no SensitiveError.

ALSO CLOSE R4: OnChatsChanged (:480) deletes a run's workspace synchronously on chat deletion. Today that
directory is empty; after G2 it is the only copy of un-promoted work. For a still-non-terminal run, cancel it
first rather than deleting under a live writer.`,
  },
  {
    id: 'G5',
    batch: '06',
    model: 'opus',
    title: 'Interactive isolation + chip resolution',
    behaviourChange: true,
    content: `OWNER DECISIONS D4 ("isolate both") and D8 ("resolve chips on open").

(a) INTERACTIVE ISOLATION — net-new, not a flag flip. The interactive Planned run is a bare
    _agentRunService.CreateAsync(...) (ChatSessionManager.cs:772); NO directory is created anywhere on that path
    today. Give interactive Planned runs the same workspace lifecycle as headless ones, through the G3
    provisioner, and promote through the G4 service. Keep the chat's working subpath behaviour intact —
    ResolveEffectiveRoot takes baseRoot as a parameter and does not care where it came from
    (FilesToolHandler.cs:182), so the narrowing layer needs no change.
    Watch the crash/cleanup paths: an interactive run's workspace needs the same teardown as a headless one, and
    the in-memory _runsByChat map is never reloaded from the DB.

(b) CHIP RESOLUTION. The interactive per-step TaskContext carries a file-touch sink that builds
    FileRef(touch.AbsolutePath, ...) chips into the assistant message (ChatSession.cs:663). With (a), a chip
    points into runs\\<guid> and dies the instant promotion moves the file. Implement resolve-on-open: if the
    recorded path is missing AND sits under the runs base dir, resolve the SAME relative path under the assistant
    folder. Deliberately do NOT rewrite persisted chat content — that would land in Batch 10's write-arbitration
    territory (AssistantChatService's gate) for no benefit.
    Test BOTH phases: a chip opened DURING the run (file present in the workspace) and the same chip opened AFTER
    promotion (file present only at the promoted location). Both must open the right file.

This is the last group of Batch 06. After it the tree is shippable and this run stops here by owner decision —
Batch 07 (G6-G10) is a SECOND invocation, deliberately, so 06 can be proven first.`,
  },
  {
    id: 'G6',
    batch: '07',
    model: 'opus',
    title: 'Per-step persona + provider resolution',
    behaviourChange: true,
    content: `OWNER DECISION D6: "the planner picks from a roster". This group is the producer + the resolution;
G7 is the settings surface and the UI.

WHAT IS FIXED TODAY, precisely: AgentRunOrchestrator.RunAsync(AgentRun, IAgentTurnExecutor, Persona, AiProvider,
RunProfile, CancellationToken, bool resume) (:35) fixes ONE (Persona, AiProvider) pair for the whole run and
threads the same objects into PlanAsync/ReplanAsync/VerifyAsync. HeadlessTurnExecutor resolves _provider ONCE in
BeginRunAsync (:139-154) and reuses that field at every step (:299). Those are the only two fixed points —
IAiClientService is ALREADY provider-per-call and needs no change at all.

DO:
  * AgentPlanner.BuildSteps currently hardcodes AssignedPersonaId = null (:295) at the only step-construction
    site. Have the planner emit a real AssignedPersonaId per step, chosen from the roster it is told about.
  * The orchestrator resolves (Persona, AiProvider) PER STEP from that id instead of closing over one run-level
    pair.
  * HeadlessTurnExecutor stops caching _provider in BeginRunAsync and resolves per step. REUSE the existing
    clone-the-provider-to-apply-ReasoningEffort logic verbatim (:151-153).
  * Fallback: when a step's AssignedPersonaId is null, unresolvable, or names a persona OUTSIDE the roster, fall
    back to the run persona. A model naming a persona that does not exist must never fail a run.
  * EXECUTOR PARITY IS A STANDING GUARDRAIL: the Live path needs the same treatment, not just Headless.

NOTE: IPersonaService.ResolveActiveAsync(WindowMode, UserOperatingMode) takes no run/step/chat id and the in-chat
picker writes the same global per-mode setting (AssistantViewModel.cs:547). Extend rather than repurpose it — the
interactive picker's behaviour must not change.`,
  },
  {
    id: 'G7',
    batch: '07',
    model: 'sonnet',
    title: 'Roster settings + panel attribution',
    behaviourChange: true,
    content: `(a) THE ROSTER SURFACE. A per-mode roster of eligible personas in AppSettings plus its settings UI,
which is what G6's planner reads. New strings -> all three resx files. Add a camelCase JSON round-trip test for the
new setting, matching how AppSettingsAgentPlanningTests covers the Batch 05 flag.

(b) PANEL ATTRIBUTION — TWO OF THESE ARE PRE-EXISTING DEFECTS, not new work.
    StepRowViewModel.AssignedPersonaId already exists ({ get; init; }, RunProgressViewModel.cs:495), is populated
    in From(AgentStep) (:514), and RunProgressPanel.xaml:66-68 already binds it to PiaPersonaAvatar. But
    AssignedPersonaId is Guid? while PersonaIdProperty is typeof(Guid) (defaulting to Guid.Empty), and Emoji is
    never bound — so EVERY step row already draws an empty 20x20 shadowed box today. Fix both.
    PiaPersonaAvatar/PersonaGlyph have ONLY PersonaId + Emoji DPs; there is no AccentColor path anywhere, so
    accent differentiation is genuinely net-new — keep it minimal or defer it and say which you did.

(c) THE VM DEPENDENCY. RunProgressViewModel is hand-constructed POSITIONALLY, outside DI
    (AssistantViewModel.cs:397), and its own ctor comment flags this as a break-everything-silently-until-compile
    hazard. Add IPersonaService as a TAIL parameter with a null default — as IAgentTimelineService was added — and
    update that single production call site. Do NOT introduce a System.Windows reference: the ViewModel ratchet
    exempts only AssistantViewModel. Leave the raw SynchronizationContext at :176 alone.

(d) VIEWMODEL-LEVEL COVERAGE ONLY. Do NOT add a frame-pushing View test. WpfStaHost holds exactly 7 frame-pushing
    facts and the 8th previously took the gate from 0/3 to 2/3 failing; the fix belongs to Batch 12. The XAML is
    booked as manual-smoke debt.`,
  },
  {
    id: 'G8',
    batch: '07',
    model: 'opus',
    title: 'A run state for a parent awaiting children',
    behaviourChange: true,
    content: `THE HIGHEST-RISK GROUP IN PHASE 3. Owner decision D7 chose a separate child slot pool, which means a
parent AWAITS its children — and no existing run state can represent that.

WHY NOTHING EXISTING WORKS — verify each yourself before designing:
  * Planning/Running/Verifying are all swept to Cancelled at every startup: FailInterruptedRunsAsync is a single
    bulk "UPDATE AgentRuns SET State=Cancelled WHERE State < WaitingForInput" (AgentRunService.cs:357-360).
  * WaitingForInput cannot carry a "waiting on N children" marker across a resume: TryBeginResumeAsync is the
    service's ONLY CAS (:309-333) and it unconditionally sets ExtraJson=NULL on the claim (:321).
  * Paused(4) is RESERVED for Batch 08 live-steering (08-live-steering.md:12, RunProgressViewModel.cs:225).

SO: append a new persisted state ordinal and make it work end to end — the sweep must not cancel a legitimately
waiting parent, a resume must claim it without destroying what it needs to remember, and the legal transitions must
be updated. SetStateAsync is an unconditional blind UPDATE (:146-163); decide deliberately whether this state needs
a CAS and justify it.

Also settle what happens to a waiting parent whose children were all swept away by a restart. A parent that waits
forever is worse than one that fails.

Tests: pin the ordinal with a golden name->ordinal map, assert the sweep leaves the new state alone, assert a
resume round-trips whatever the parent must remember.`,
  },
  {
    id: 'G9',
    batch: '07',
    model: 'opus',
    title: 'ParentRunId producer + child grant envelope',
    behaviourChange: true,
    content: `THE COLUMNS ALREADY EXIST — the PRODUCER does not. ParentRunId and AssignedPersonaId are real
columns, fully round-tripped (AgentRunService.cs:108-123/:454-467 insert, :599/:622 read), so NO migration is
needed. But AgentRunCreateRequest (IAgentRunService.cs:16-23) has NO ParentRunId parameter, so no code path can
create a child at all.

DO:
  * Add ParentRunId to AgentRunCreateRequest as an OPTIONAL TRAILING parameter.
  * This changes IAgentRunService, which breaks TWO hand-written full-surface fakes enumerating all 16 members — a
    COMPILE failure, not a soft skip. Migrate both IN THIS COMMIT: AgentRunOrchestratorTests.cs:142 and
    ThrowingAgentRunService at BackgroundAssistantTurnRunnerRunSpineTests.cs:290.
  * Add the missing IX_AgentRuns_ParentRunId index.
  * THE CHILD GRANT ENVELOPE, a security seam: AgentRunCreateRequest takes an OPAQUE PolicyJson the service never
    parses, and the envelope helpers are internal to HeadlessRunLauncher (~:682). A naive child-spawn creates a run
    with a NULL policy — and the resume floor then WIDENS that to the {write_file} default. Expose a
    narrow-for-child helper re-serializing a SUBSET of the parent's grants, plus a test asserting a child's
    envelope is NEVER wider than its parent's.
  * Keep the envelope at v:1 with additive members — envelope.V != 1 is an exact-equality check.`,
  },
  {
    id: 'G10',
    batch: '07',
    model: 'opus',
    title: 'Child slot pool + roll-up',
    behaviourChange: true,
    content: `OWNER DECISION D7: a SEPARATE child slot pool so siblings run in parallel while the parent awaits.

WHY A SEPARATE POOL IS MANDATORY: _slots = new SemaphoreSlim(2, 2) on the singleton launcher
(HeadlessRunLauncher.cs:26) is waited inside the dispatch Task.Run BEFORE the orchestrator is built (:199 launch,
:333 resume) and released only in the finally after RunAsync returns. A nested acquire on the SAME pool deadlocks:
two parents each hold 1 of 2 slots while blocked on a child needing a slot from that pool. Never reuse _slots for
awaited children.

DO:
  * A separate pool, sized deliberately, with a comment stating that deadlock argument so nobody "simplifies" it
    back into _slots.
  * Cancellation cascades via the existing linked CTS (AgentRunOrchestrator.cs:46). Verify, do not re-invent.
  * NO ORPHANED CHILDREN — a parent's terminal settle must account for every child it spawned.
  * ExecutingRunStore is a reverse map runId -> chatId (:6-11), so concurrent runs on ONE chat already work — a
    child sharing the parent's chatId needs no store change. Confirm rather than assume.
  * If you add a new executor TYPE, AgentRunBracketTests (:38) requires it to implement one of the two executor
    contracts and inject IExecutingRunStore. Prefer the EXISTING launcher/executor and avoid the new type.
  * Route child tool calls through the EXISTING unattended gate. ToolAutonomyRuleTests (:34) pins the EXACT count
    of ToolAutonomy.Resolve / IsMcpTool / IsAutoApproveEligible calls per gate file (1 each). If a new
    ToolGateSurface value is needed, APPEND it and update the golden name->ordinal map plus
    AgentTimelineVocabularyTests in the same commit.

LEDGER ROLL-UP — say WHICH budget nests, because two coexist: an EPHEMERAL per-dispatch RunContext (reset on every
resume, RunContext.cs:89-92) that gates pausing, and a PERSISTED ledger that accrues forever (WriteLedger,
AgentRunService.cs:778-786). AddUsageAsync only touches the run named by runId — there is no cross-run method.
Either push per child write or aggregate on read (WHERE ParentRunId=@p, indexed by G9). Pick one, state why, pin it.

TIMELINE — DO NOT PROMISE A MERGED VIEW. Seq is monotonic only within a RunId, each child gets its own 500-row cap
(AgentTimelineService.cs:60), and CreatedAt is EXPLICITLY rejected as an ordering source (SqliteContext.cs:342-343).
Ship per-run views with a parent->child drill-down. A merged timeline needs a new cross-run key and is not this
group's work.

BUDGETS AND THE SCHEDULED-JOB LOCK: ScheduledJobBackgroundService holds _runLock (SemaphoreSlim(1,1), shared with
ExecuteResearchAsync) across "await handle.Completion" (:166 -> :202), so no scheduled job of either kind can
dispatch for the parent's wall clock PLUS every descendant's. A delegating run's budget defaults must fit that
envelope. Record the interaction even if you do not change the lock.`,
  },
]

// args may arrive as a real object OR as a JSON-ENCODED STRING depending on how the tool call was formed.
// This is not defensive noise: on 2026-07-31 a run received the stringified form, every option silently read as
// undefined, and the workflow re-ran the detail-planning phase, re-entered three already-committed groups and
// built G6 — a whole batch outside the scope it was given. Parse both shapes, then ASSERT what was applied.
const A = (() => {
  if (typeof args === 'string') {
    try {
      return JSON.parse(args)
    } catch (e) {
      throw new Error(`args was a string but not valid JSON, so no option could be applied: ${args.slice(0, 200)}`)
    }
  }
  return args || {}
})()

const startAt = A.startAtGroup ? String(A.startAtGroup) : null
const stopAfter = A.stopAfterGroup ? String(A.stopAfterGroup) : null
const skipPlanning = !!A.skipPlanning
// Groups already committed by an EARLIER invocation of this workflow (e.g. after a crash). They are not rebuilt,
// but they ARE in scope for the simplify / review / roadmap phases — otherwise a continuation run reviews only
// its own tail and silently treats the rest of the batch as out of scope.
const alreadyBuilt = Array.isArray(A.alreadyBuilt) ? A.alreadyBuilt.map(String) : []
// Build nothing; run only the simplify/review/fix/roadmap tail over `alreadyBuilt`. For the case where the
// implement phase already happened (possibly across several interrupted invocations) but was never reviewed.
const skipImplement = !!A.skipImplement
if (skipImplement && alreadyBuilt.length === 0) throw new Error('skipImplement with an empty alreadyBuilt would review nothing')

// Reject an args object whose keys we do not recognise — a typo'd option must not read as "no option given".
const KNOWN_ARGS = ['startAtGroup', 'stopAfterGroup', 'skipPlanning', 'alreadyBuilt', 'skipImplement']
const unknownArgs = Object.keys(A).filter(k => KNOWN_ARGS.indexOf(k) === -1)
if (unknownArgs.length > 0) throw new Error(`unrecognised args key(s): ${unknownArgs.join(', ')}`)
if (startAt && GROUPS.findIndex(g => g.id === startAt) < 0) throw new Error(`startAtGroup "${startAt}" is not a group id`)
if (stopAfter && GROUPS.findIndex(g => g.id === stopAfter) < 0) throw new Error(`stopAfterGroup "${stopAfter}" is not a group id`)

log(`ARGS APPLIED — startAt=${startAt || '(none, starts at G1)'} stopAfter=${stopAfter || '(none, runs to G10)'} skipPlanning=${skipPlanning} alreadyBuilt=[${alreadyBuilt.join(',')}]`)

const startIdxRaw = startAt ? GROUPS.findIndex(x => x.id === startAt) : 0
const stopIdxRaw = stopAfter ? GROUPS.findIndex(x => x.id === stopAfter) : GROUPS.length - 1
const startIdx = startIdxRaw < 0 ? 0 : startIdxRaw
const stopIdx = stopIdxRaw < 0 ? GROUPS.length - 1 : stopIdxRaw

log(`THIS INVOCATION will attempt: ${GROUPS.slice(startIdx, stopIdx + 1).map(g => g.id).join(', ')}${skipPlanning ? ' (reusing the on-disk impl specs)' : ''}`)

phase('Detail planning')

let reconciled = null

if (skipPlanning) {
  log('detail planning SKIPPED by args.skipPlanning — the impl specs on disk are authoritative, including their BUILDER NOTE blocks')
} else {
  const SPEC_AUTHOR_COMMON = `
You are the Design step for Phase 3 of the Pia agent-run spine. Repo root: ${REPO}. Branch:
feature/agent-run-spine.

READ ${PLAN} FIRST, IN FULL. It is the approved plan: it carries the measured seam map (§2), the nine places the
older batch specs are WRONG (§3), the risk register (§4), the ten work groups (§5) and the standing constraints
(§7). The owner's nine decisions are settled in §1 — do not re-open them, do not offer alternatives, do not
"improve" one. Your job is to turn the plan into an implementation spec precise enough that a builder does not
have to re-derive anything.

You MAY read any source file and you SHOULD read every seam you are about to specify — the plan's anchors were
measured but the tree moves. You may run read-only git commands. Do NOT edit any source file, do NOT build, do NOT
test, do NOT commit. You write exactly ONE file: your own spec.

Match the house style of this repo's existing impl specs — read
docs/superpowers/specs/agent-roadmap/04-autonomy-policy.impl.md for the shape (numbered sections, explicit
decisions with their reasoning, a §9 acceptance/test list, and "CODE RIGHT, SPEC WRONG" notes wherever prose and
tree disagree). Be concrete: name files, members and line anchors; state what each commit contains; write the test
list as facts someone can implement without inventing the assertion.

IMPORTANT SCHEDULING FACT: the owner is running this as TWO invocations. Batch 06 (G1-G5) runs now; Batch 07
(G6-G10) is a separate later invocation that will SKIP this design phase and read your spec off disk. So your file
must stand alone months from now with no conversational context.

${METHOD}
${CONSTRAINTS}

Write the file with CRLF line endings (every file in this repo is CRLF; the Write tool emits LF, so convert).
Your final text is a short report, not the spec — the spec goes to disk.
`

  const specs = await parallel([
    () =>
      agent(
        `${SPEC_AUTHOR_COMMON}

YOUR FILE: ${SPEC06} — Batch 06, run workspace isolation. THIS IS THE BATCH BEING BUILT IN THIS INVOCATION, so
your spec is read by builders within the hour.

Cover work groups G1 through G5 from the plan's §5, in that order, one section each. The load-bearing content:

  * G1: the SensitivePathGuard carve-out (the guard blocks %LOCALAPPDATA%\\Pia wholesale and carves out only
    LegacyWorkdir) and RunContext.WorkspaceRoot carried to AgentVerifier (the ambient is null during verify).
    These are the two SHIP-BLOCKERS the older batch spec omits entirely — plan §3 corrections 2 and 5. Specify
    them as prerequisites, not as polish.
  * G2: flipping both Initialize call sites. Re-anchor the drifted line numbers yourself (plan §3 correction 1).
  * G3: the two provisioning modes from decision D5 — git worktree when the root is a repo, plain copy otherwise
    — plus symmetric teardown (worktree remove/prune, not rmdir) and GitToolHandler ambient parity. Specify the
    degrade-to-copy fault list explicitly.
  * G4: promotion ordering (verify -> promote -> CompleteAsync), copy-mode destination, worktree-mode "the branch
    is the deliverable" (D5b, no auto-merge), and D3's publish offer for failed/cancelled runs with its retention
    rule.
  * G5: interactive isolation (D4) and chip resolve-on-open (D8).

Be explicit about what each group does NOT do, especially the intermediate state after G2 where a run writes into
its workspace and nothing promotes yet.

For every test you specify, say whether it is a REGRESSION (demonstrable red before the fix) or a GUARD (pins a
premise, cannot go red on a revert) — this repo requires that distinction in the test's own comment. And note the
trap in plan §4 R1: the existing escape suite roots under GetTempPath() and therefore cannot see the guard
collision, so your test list must specify the REAL runs-dir shape and a SUCCESSFUL write.`,
        { label: 'plan:06-impl-spec', phase: 'Detail planning', model: 'opus', effort: 'high' }
      ),
    () =>
      agent(
        `${SPEC_AUTHOR_COMMON}

YOUR FILE: ${SPEC07} — Batch 07, sub-agents / multi-persona. NOTE: this batch is NOT built in this invocation —
it is the second run. Your spec has to survive on disk and be picked up cold, so favour completeness over brevity
and do not rely on anything a reader would only know from today.

Cover work groups G6 through G10 from the plan's §5, in that order, one section each. The load-bearing content:

  * G6: per-step persona and provider resolution. The only two fixed points are AgentRunOrchestrator.RunAsync's
    signature and HeadlessTurnExecutor's cached _provider field — IAiClientService is already provider-per-call.
    Decision D6 is "the planner picks from a roster", so specify the planner's emission, the roster it is told
    about, and the fallback for an out-of-roster or unresolvable persona. Executor parity: Live AND Headless.
  * G7: the roster settings surface (+ three resx files) and the panel attribution fix. Note that TWO defects
    there are PRE-EXISTING (a Guid?/Guid DP mismatch and an unbound Emoji, so every step row already renders an
    empty avatar) and that accent colour is genuinely net-new. No View test is available — specify ViewModel-level
    coverage and book the XAML as manual smoke.
  * G8: a NEW APPENDED persisted run state for a parent awaiting children. The highest-risk piece in Phase 3.
    Specify why no existing state works — the startup sweep cancels everything below WaitingForInput, the resume
    CAS unconditionally nulls ExtraJson, and Paused(4) belongs to Batch 08 — and specify sweep, resume and
    transition behaviour, plus what happens to a waiting parent whose children were swept away.
  * G9: ParentRunId on AgentRunCreateRequest (optional trailing param), the migration of BOTH hand-written
    16-member fakes in the same commit, the missing index, and the narrow-for-child grant envelope. Specify the
    "a child is never wider than its parent" test.
  * G10: the separate child slot pool (reusing _slots deadlocks — state the argument), cascade cancellation via
    the existing linked CTS, the no-orphans guarantee, ledger roll-up naming WHICH of the two coexisting budgets
    nests, and per-run timeline views with a drill-down — NOT a merged ordering, which is impossible without a new
    cross-run key.

CRITICAL: Batch 06 is being built RIGHT NOW, in parallel with you, from the sibling spec. So the tree your batch
will meet is NOT the tree you are reading — it will already carry a run-aware file root, a workspace provisioner
with two modes, a promotion step inside the terminal settle, and isolated interactive runs. Write G6-G10 against
that FUTURE tree and say so explicitly wherever it matters (especially G6, which edits the same
HeadlessTurnExecutor/AgentRunOrchestrator that 06 is touching).

Batch 07's own spec says the seams should be re-scoped at design time and calls itself the largest remaining
batch. Do that re-scoping explicitly: where a seam in the plan turns out to be wrong, say "CODE RIGHT, SPEC WRONG"
and specify what the tree actually needs.

For every test you specify, say whether it is a REGRESSION or a GUARD.`,
        { label: 'plan:07-impl-spec', phase: 'Detail planning', model: 'opus', effort: 'high' }
      ),
  ])

  log(`detail planning: ${specs.filter(Boolean).length}/2 specs authored`)

  const RECONCILE_SCHEMA = {
    type: 'object',
    additionalProperties: false,
    required: ['collisions', 'groupNotes', 'blockers'],
    properties: {
      collisions: {
        type: 'array',
        items: {
          type: 'object',
          additionalProperties: false,
          required: ['file', 'bothWant', 'resolution', 'ownedByGroup'],
          properties: {
            file: { type: 'string' },
            bothWant: { type: 'string' },
            resolution: { type: 'string' },
            ownedByGroup: { type: 'string' },
          },
        },
      },
      groupNotes: {
        type: 'array',
        items: {
          type: 'object',
          additionalProperties: false,
          required: ['groupId', 'note'],
          properties: {
            groupId: { type: 'string', description: 'G1..G10' },
            note: { type: 'string' },
          },
        },
      },
      blockers: {
        type: 'array',
        items: {
          type: 'object',
          additionalProperties: false,
          required: ['groupId', 'problem', 'suggestion'],
          properties: {
            groupId: { type: 'string' },
            problem: { type: 'string' },
            suggestion: { type: 'string' },
          },
        },
      },
    },
  }

  reconciled = await agent(
    `You are the reconciler for Phase 3's design step. Repo root: ${REPO}.

Two opus agents just wrote ${SPEC06} and ${SPEC07} independently, from the same approved plan (${PLAN}). Read all
three files. Your job is the collision they could not see, because they wrote in parallel:

  * Batch 06 and Batch 07 BOTH touch HeadlessTurnExecutor and AgentRunOrchestrator. 06 adds a workspace root and
    reorders the terminal settle (verify -> promote -> CompleteAsync); 07 changes the orchestrator's per-run
    (Persona, AiProvider) pair into a per-step resolution and stops the executor caching _provider in
    BeginRunAsync. Decide who owns which edit and in what order, so the second builder to arrive is not surprised.
  * Anything else both specs specify, contradict each other on, or assume about the other's output.

SCHEDULING FACT THAT CHANGES YOUR JOB: the owner runs this as TWO invocations. Batch 06 (G1-G5) builds in this
run; Batch 07 (G6-G10) is a separate later invocation that SKIPS the design phase entirely and reads the specs
cold off disk. Therefore:

  ** YOU MUST WRITE EVERY PER-GROUP NOTE INTO THE SPEC FILE ITSELF, as a clearly delimited
     "BUILDER NOTE (Gx) — from the reconciler" block inside that group's section — in ADDITION to returning it in
     the structured result. The structured notes reach only THIS run's builders; the ones you write into the file
     are the only thing G6-G10's builders will ever see. A note that exists only in the structured result is LOST
     for Batch 07. **

You MAY EDIT both spec files to make them consistent — that is the point of this step, not a side effect. Keep
both files CRLF. Do NOT edit any source file, do not build, do not test, do not commit.

Use blockers ONLY for something that genuinely cannot be built as specified — a wrong premise, a missing
prerequisite, an ordering that cannot work. An empty blockers array is the expected outcome.

Group ids are G1..G10 exactly as in ${PLAN} §5.`,
    { label: 'plan:reconcile', phase: 'Detail planning', model: 'opus', effort: 'high', schema: RECONCILE_SCHEMA }
  )

  if (reconciled && Array.isArray(reconciled.blockers) && reconciled.blockers.length > 0) {
    log(`RECONCILER FLAGGED ${reconciled.blockers.length} BLOCKER(S): ${reconciled.blockers.map(b => `${b.groupId}: ${b.problem}`).join(' | ')}`)
  }
  if (reconciled && Array.isArray(reconciled.collisions)) {
    log(`reconciled ${reconciled.collisions.length} cross-batch collision(s)`)
  }
}

const notesByGroup = {}
if (reconciled && Array.isArray(reconciled.groupNotes)) {
  for (const n of reconciled.groupNotes) {
    if (!n || !n.groupId) continue
    notesByGroup[n.groupId] = notesByGroup[n.groupId] ? `${notesByGroup[n.groupId]}\n${n.note}` : n.note
  }
}

phase('Implement')

let started = startAt === null
const built = []
let lastFailure = null

for (let i = 0; skipImplement ? false : i < GROUPS.length; i++) {
  const g = GROUPS[i]
  if (!started) {
    if (g.id === startAt) started = true
    else continue
  }

  const note = notesByGroup[g.id]
  const spec = g.batch === '06' ? SPEC06 : SPEC07

  const result = await agent(
    `You are the builder for Phase 3 work group ${g.id} — "${g.title}" (Batch ${g.batch}).
Repo root: ${REPO}. Branch: feature/agent-run-spine. You implement ONE group and commit ONCE.

READ FIRST, IN THIS ORDER:
  1. ${spec} — your group's implementation spec. It is authoritative on HOW, and it carries
     "BUILDER NOTE (${g.id})" blocks written by the reconciler. Find yours and follow it.
  2. ${PLAN} — §1 (the owner's settled decisions), §2 (the measured seam map), §3 (nine places the older batch
     specs are WRONG), §4 (the risk register — find the rows that touch YOUR files) and §7 (constraints).
  3. CLAUDE.md.

YOUR GROUP:
${g.content}
${note ? `\nRECONCILER NOTE FOR ${g.id} (authoritative over the spec text):\n${note}\n` : ''}
${lastFailure ? `\nTREE STATE WARNING — the previous group did not finish cleanly:\n${lastFailure}\nEstablish the actual state with git status / git log before you start. Do not build on an assumption about it.\n` : ''}
${g.behaviourChange ? 'THIS GROUP CHANGES OBSERVABLE BEHAVIOUR. Say so in the commit message, and say what the tree does between this commit and the group that completes the story.\n' : 'THIS GROUP IS PREP: it must NOT change observable behaviour. If you find yourself changing what the app does, stop and report instead.\n'}
${METHOD}
${CONSTRAINTS}
${GATE}
${COMMIT}

WHAT TO REPORT BACK (a handoff to the next builder, not a summary for a human):
  * The exact gate numbers you measured: warnings in Debug, warnings in Release, and total/failed/skipped from the
    suite. Quote them. Never report a number you did not run.
  * The commit sha and subject.
  * Every file you touched.
  * Anything the spec got wrong, and what you did instead.
  * Anything you deliberately did NOT do, and why.

** END YOUR REPORT WITH A LITERAL STATUS TOKEN ON ITS OWN LINE — exactly one of:
     GATE: GREEN   (both configurations at 0 warnings, suite failed: 0, and you committed)
     GATE: RED     (anything else — including a green build you chose not to commit)
   The orchestrating script matches on that token to decide what to tell the next builder. Prose is not enough. **

IF YOU CANNOT GET THE GATE GREEN: do NOT commit red. Leave the tree in the cleanest state you can, report exactly
what fails and what you tried, and end with GATE: RED. A stopped group is recoverable; a red commit on this branch
is not cheap.`,
    { label: `build:${g.id}`, phase: 'Implement', model: g.model, effort: 'high' }
  )

  built.push({ id: g.id, batch: g.batch, report: result })

  if (!result) {
    lastFailure = `${g.id} ("${g.title}") returned NO report — it may have died mid-edit. Verify the tree with git status and git log before touching anything.`
    log(`${g.id}: NO REPORT — tree treated as suspect`)
  } else if (/GATE:\s*GREEN/i.test(result)) {
    lastFailure = null
    log(`${g.id} done, GATE: GREEN (${built.length} group(s) attempted)`)
  } else {
    lastFailure = `${g.id} ("${g.title}") did NOT report GATE: GREEN. Its own report:\n${result.slice(0, 1500)}`
    log(`${g.id}: no GATE: GREEN token — treating as unfinished`)
  }

  if (stopAfter && g.id === stopAfter) {
    log(`stopping after ${g.id} by args.stopAfterGroup; ${GROUPS.length - (i + 1)} group(s) deliberately NOT attempted in this invocation`)
    break
  }
}

const builtIds = built.map(b => b.id)
// Everything the reviewers must judge: this run's groups plus any committed by an earlier invocation.
const inScopeIds = GROUPS.map(g => g.id).filter(id => alreadyBuilt.indexOf(id) !== -1 || builtIds.indexOf(id) !== -1)
log(skipImplement
  ? `implement phase SKIPPED by args.skipImplement — reviewing the already-committed groups: ${alreadyBuilt.join(', ')}`
  : `implement phase complete — attempted: ${builtIds.join(', ')}`)

const SCOPE_NOTE = `
SCOPE — read this before reporting anything as missing.

IN SCOPE, and you must judge ALL of it: ${inScopeIds.join(', ') || '(none)'}.
${alreadyBuilt.length > 0 ? `Of those, ${alreadyBuilt.join(', ')} were committed by an EARLIER invocation of this workflow (it stopped on a\nconnection error and was restarted). They are just as in-scope as the rest — read their commits and judge them\nexactly as if this run had built them. Do NOT skip them and do NOT assume they were reviewed already; they were\nnot.\nBuilt in THIS invocation: ${builtIds.join(', ') || '(none)'}.\n` : ''}
OUT OF SCOPE: every other group. Phase 3 is being delivered as TWO invocations — Batch 06 (G1-G5) first and
Batch 07 (G6-G10) second — so that 06 can be proven before 07 starts. A decision or spec section belonging to a
group NOT in the in-scope list is OUT OF SCOPE and must NOT be reported as unimplemented, missing, or a defect.
`

phase('Simplify')

const batchOf = {}
for (const g of GROUPS) batchOf[g.id] = g.batch
const batchesBuilt = ['06', '07'].filter(b => inScopeIds.some(id => batchOf[id] === b))

for (const b of batchesBuilt) {
  const ids = inScopeIds.filter(id => batchOf[id] === b).join(', ')
  await agent(
    `You are a simplification pass over Phase 3's committed work. Repo root: ${REPO}.

YOUR SCOPE: Batch ${b} only — the in-scope groups are: ${ids} (some may have been committed by an earlier
invocation of this workflow that stopped on a connection error; treat them identically). Read
${b === '06' ? SPEC06 : SPEC07} so you know what the code was meant to be, then find the commits with
\`git log --oneline\` and read the diffs.

QUALITY ONLY. You are NOT hunting for bugs and NOT adding features — a later review phase does that. Look for:
reuse of something that already exists, naming that fights the surrounding code, wrong altitude (a helper that
should be inline or vice versa), dead or unreachable code introduced by the batch, duplicated logic ACROSS the
groups (each was built by a different agent and they could not see each other), and comments that no longer match
the code they sit on.

PRESERVE ALL FUNCTIONALITY AND EVERY TEST'S INTENT. If a simplification would change behaviour, do not make it —
report it instead. Do not delete a test to make a suite tidier. Do not "simplify" away a comment that records a
decision or a premise: in this repo those are load-bearing, and several exist specifically to stop a future reader
making a wrong change.

Match the surrounding code's idiom, comment density and naming rather than an external standard.

${METHOD}
${CONSTRAINTS}
${GATE}
${COMMIT}

If you find nothing worth changing, that is a legitimate outcome — say so and commit nothing.
Report the gate numbers you measured and the commit sha (or that there was none). End with GATE: GREEN or
GATE: RED on its own line.`,
    { label: `simplify:batch-${b}`, phase: 'Simplify', model: 'sonnet', effort: 'high' }
  )
  log(`simplify pass for Batch ${b} complete`)
}

phase('Review')

const FINDINGS_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  required: ['findings'],
  properties: {
    findings: {
      type: 'array',
      items: {
        type: 'object',
        additionalProperties: false,
        required: ['title', 'file', 'line', 'severity', 'claim', 'failureScenario', 'suggestedFix'],
        properties: {
          title: { type: 'string' },
          file: { type: 'string' },
          line: { type: 'number' },
          severity: { type: 'string', description: 'must-fix | should-fix | nit' },
          claim: { type: 'string' },
          failureScenario: { type: 'string' },
          suggestedFix: { type: 'string' },
        },
      },
    },
  },
}

const REVIEW_COMMON = `
You are reviewing Phase 3 of the Pia agent-run spine, as committed. Repo root: ${REPO}.

Read ${PLAN} (§1 decisions, §4 risk register, §7 constraints), the relevant impl spec, then the actual diffs
(\`git log --oneline\` to find this run's commits, then read them).
${SCOPE_NOTE}
You may read anything, run read-only commands, and run the build and the tests. Do NOT edit, do NOT fix, do NOT
commit — a later phase applies fixes.

REPORT ONLY WHAT YOU CAN GROUND. Every finding needs a file, a line, and a concrete failure scenario: inputs or
state, then the wrong outcome. "This could be clearer" is not a finding. A finding you cannot express as a failure
scenario is a nit at best — mark it so or drop it.

Your findings go to an ADVERSARIAL verification pass whose job is to REFUTE them. A finding built on a wrong
premise will be caught and thrown out, so state your premise explicitly and make sure it is one you actually
checked. That is not a reason to hold back a real finding — it is a reason not to guess.
`

const REVIEW_LENSES = [
  {
    key: 'guardrails',
    prompt: `${REVIEW_COMMON}

YOUR LENS: GUARDRAILS AND CORRECTNESS — "what breaks, and what silently does the wrong thing?"

Work the plan's §4 risk register row by row and check whether each risk that touches a BUILT group was actually
closed or merely mentioned. In particular:
  * R1: can a broken workspace root still ship green? Are the new tests rooted at the REAL runs-dir shape, and do
    they assert a SUCCESSFUL write rather than only rejected escapes?
  * R2/R3: does the verifier probe the run root? Is the falsified doc comment fixed?
  * R4/R5/R16: is workspace teardown SYMMETRIC with provisioning — does worktree mode actually prune its
    registration, and does chat deletion no longer destroy live un-promoted work?
  * R7: any path or filename reaching a log line at Information or above? (There is no SensitiveError, so look for
    LogError/LogWarning carrying user content.)
  * The architecture rules: the exact-count gate theory, the bracket-ownership rule, the naming allowlist, the
    ViewModel System.Windows ratchet, DI registration.
Also check the standing guardrails independently of the register: failure-isolated bookkeeping, executor parity
(does the Live path really get what Headless got?), off-thread RunChanged marshaling, append-only ordinals, and
whether any new path can strand a run forever or lose a user's work.`,
  },
  {
    key: 'conventions',
    prompt: `${REVIEW_COMMON}

YOUR LENS: CONVENTIONS AND TEST QUALITY — "would this pass this repo's own bar, and do the tests prove what they
claim?"

  * CLAUDE.md compliance: privacy-first logging (paths and filenames count as user content), MVVM placement,
    naming, indent, the "Pia" namespace rule.
  * Every new user-visible string present in ALL THREE resx files, with real German and French rather than English
    placeholders. Designer.cs not hand-edited.
  * CRLF on every file this run created.
  * TEST QUALITY, the half most likely to be weak:
      - Non-vacuity: does any name-scoped or type-scoped assertion pass over an EMPTY set? Anything without a
        positive control or an Assert.Single-style guard is suspect.
      - Does each test's comment distinguish a REGRESSION (demonstrably red before the fix) from a GUARD (pins a
        premise)? Claiming the former without the demonstration is the specific failure mode here.
      - Are the assertions the ones the spec asked for, or weaker ones that happen to pass?
      - Coverage gaps against the spec's §9 acceptance list — name what is NOT covered.
      - Any new xUnit analyzer warning risk (an untokened Task.Delay is xUnit1051 and has fired in this repo).
  * Verify the gate yourself: rebuild both configurations and run the suite. Report the real numbers. If a
    builder's reported numbers do not match yours, say so plainly — that is a finding.`,
  },
  {
    key: 'conformance',
    prompt: `${REVIEW_COMMON}

YOUR LENS: SPEC AND DECISION CONFORMANCE — "does the tree do what was actually decided, and where did it quietly
do something else?"

Check the tree against the owner's decisions in ${PLAN} §1 that belong to BUILT groups, one at a time:
  * D2: is the runs dir the location, and is the guard carve-out behind a SHARED constant so guard and launcher
    cannot drift?
  * D3: does a Completed run promote automatically, and does a failed/cancelled run get a publish OFFER with a
    retention rule — not silent loss and not silent promotion?
  * D4: do interactive runs really isolate, and did that leave any interactive regression behind?
  * D5/D5b: worktree when the root is a repo and copy otherwise, degrading to copy on fault; is the branch
    genuinely the deliverable with NO automatic merge, and does the UI SAY the output is on a branch?
  * D8: do chips resolve in BOTH phases — during the run and after promotion?
Then: what did the spec promise for a BUILT group that is missing, and what did the code add that no spec or
decision asked for? Scope creep is a finding. So is a spec section quietly not implemented.
Finally: is anything in the plan's §8 smoke list now actually automatable and left untested?`,
  },
]

const reviewed = await parallel(
  REVIEW_LENSES.map(l => () =>
    agent(l.prompt, { label: `review:${l.key}`, phase: 'Review', model: 'opus', effort: 'high', schema: FINDINGS_SCHEMA })
  )
)

const allFindings = []
for (let i = 0; i < reviewed.length; i++) {
  const r = reviewed[i]
  if (!r || !Array.isArray(r.findings)) continue
  for (const f of r.findings) allFindings.push({ ...f, lens: REVIEW_LENSES[i].key })
}

const seenKeys = new Set()
const deduped = []
for (const f of allFindings) {
  const key = `${f.file}::${String(f.title).toLowerCase().slice(0, 60)}`
  if (seenKeys.has(key)) continue
  seenKeys.add(key)
  deduped.push(f)
}

const VERIFY_CAP = 12
const rankOf = s => (s === 'must-fix' ? 0 : s === 'should-fix' ? 1 : s === 'nit' ? 2 : 3)
deduped.sort((a, b) => rankOf(a.severity) - rankOf(b.severity))
const toVerify = deduped.slice(0, VERIFY_CAP)

log(
  `review: ${allFindings.length} raw -> ${deduped.length} deduped -> verifying ${toVerify.length}` +
    (deduped.length > VERIFY_CAP
      ? ` (CAPPED: ${deduped.length - VERIFY_CAP} lowest-severity finding(s) NOT verified and NOT fixed)`
      : '')
)

const VERDICT_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  required: ['refuted', 'reasoning', 'confidence'],
  properties: {
    refuted: { type: 'boolean', description: 'true if the finding does not hold' },
    reasoning: { type: 'string' },
    confidence: { type: 'string', description: 'high | medium | low' },
  },
}

const verdicts = await parallel(
  toVerify.map(f => () =>
    agent(
      `You are an adversarial verifier. Your job is to REFUTE the finding below, not to confirm it.
Repo root: ${REPO}.

FINDING (from a ${f.lens} review of Phase 3)
  title:    ${f.title}
  file:     ${f.file}:${f.line}
  severity: ${f.severity}
  claim:    ${f.claim}
  scenario: ${f.failureScenario}

Go to the code and try to break the claim. Read the file and everything around it. Check whether:
  * the premise is simply false — the symbol, member, line or behaviour is not what the finding says;
  * a guard, an earlier branch, a caller or an existing test already prevents the scenario;
  * the scenario is unreachable in practice (no call path constructs that state);
  * the "wrong" behaviour is a deliberate, documented decision — check ${PLAN} §1 and the impl spec before calling
    something a defect, because several look wrong until you read the reason;
  * the finding is about work DELIBERATELY out of scope for this invocation (Phase 3 runs as two passes; groups
    G6-G10 are not built yet by design — if that is what the finding is about, it is refuted);
  * an existing test would already be red if the claim were true (run it if that settles it).

You may read anything and run read-only commands, and you may run a specific test. Do NOT edit or commit.

WHY THIS MATTERS HERE: false-premise findings are a RECORDED failure mode on this branch — a SQLITE_BUSY_SNAPSHOT
hazard that turned out not to exist, and a transitive-package exclusion that would have made CVE reporting worse.
Both were caught by investigating before implementing. A confirmed-but-wrong finding costs a real fix pass and can
make correct code worse.

DEFAULT TO refuted: true IF YOU ARE UNCERTAIN. Set refuted: false only when you have positively established, from
the code, that the finding holds AND the scenario is reachable. State the specific evidence either way.`,
      { label: `verify:${String(f.title).slice(0, 40)}`, phase: 'Review', model: 'sonnet', effort: 'high', schema: VERDICT_SCHEMA }
    ).then(v => ({ finding: f, verdict: v }))
  )
)

const confirmed = verdicts
  .filter(Boolean)
  .filter(v => v.verdict && v.verdict.refuted === false)
  .map(v => v.finding)

// A verifier that DIED (limit, connection) returns null. Counting those as "refuted" silently converts
// "never checked" into "checked and dismissed" — which happened on 2026-07-31, where 11 verifiers died on a
// weekly limit and the run reported "11 refuted". Separate the three outcomes and never merge them.
const refutedCount = verdicts.filter(v => v && v.verdict && v.verdict.refuted === true).length
const noVerdict = verdicts.filter(v => !v || !v.verdict).length
const unverifiedByCap = Math.max(0, deduped.length - VERIFY_CAP)
log(`adversarial verify: ${confirmed.length} CONFIRMED, ${refutedCount} refuted, ${noVerdict} NO VERDICT (verifier died), ${unverifiedByCap} never sent (cap)`)
if (noVerdict + unverifiedByCap > 0) {
  log(`WARNING: ${noVerdict + unverifiedByCap} of ${deduped.length} finding(s) have NO verdict and were NOT fixed. They are UNKNOWN, not dismissed — re-run the verify phase before treating this review as complete.`)
}

phase('Fix')

let fixReport = 'No confirmed findings — no fix pass was needed.'

if (confirmed.length > 0) {
  const list = confirmed
    .map(
      (f, i) =>
        `${i + 1}. [${f.severity}] ${f.title}\n   ${f.file}:${f.line}\n   claim:    ${f.claim}\n   scenario: ${f.failureScenario}\n   suggested: ${f.suggestedFix}`
    )
    .join('\n\n')

  fixReport =
    (await agent(
      `You are the fix pass for Phase 3. Repo root: ${REPO}. Branch: feature/agent-run-spine.

The findings below survived an adversarial verification pass whose default was to refute — so each has been
positively established against the code. Fix them.

${list}
${SCOPE_NOTE}
HOW TO WORK:
  * Read ${PLAN} (§1 decisions, §4 risks, §7 constraints) and the relevant impl spec before changing anything.
  * Fix the DEFECT, not the symptom, and stay inside the finding's scope. A fix that grows into a redesign is a
    finding for the next batch, not work for this pass — say so instead.
  * You are permitted to disagree. If a finding is wrong despite surviving verification, or if the right fix is
    worse than the defect, DO NOT implement it: say which finding, why, and what you did instead. This repo's
    history records several review findings correctly DECLINED with the premise accepted — a respectable outcome,
    and better than a wrong change. Do not perform agreement.
  * Where a fix is behavioural, demonstrate red-before-green: neutralise the fix, watch the test fail, restore.
  * Group the fixes into logical commits rather than one blob.

${METHOD}
${CONSTRAINTS}
${GATE}
${COMMIT}

Report what you fixed and its commit, what you DECLINED and why, and the final gate numbers you measured. End with
GATE: GREEN or GATE: RED on its own line.`,
      { label: 'fix:confirmed-findings', phase: 'Fix', model: 'opus', effort: 'high' }
    )) || 'The fix pass returned no report — verify the tree state with git status and git log.'
}

const roadmap = await agent(
  `You are the roadmap pass that closes this invocation of Phase 3. Repo root: ${REPO}. Branch:
feature/agent-run-spine. This is a DOCS-ONLY commit — do not touch source or tests.
${SCOPE_NOTE}
Read docs/superpowers/specs/agent-roadmap/00-OVERVIEW.md, ${PLAN}, the relevant impl spec, and this run's actual
commits (\`git log --oneline\`). Then update the roadmap to match the tree.

** DISCIPLINE FIRST: 00-OVERVIEW.md is ~1220 lines of dense accumulated reasoning and its value IS that
   accumulation. APPEND AND ANNOTATE ONLY. Do not shorten, restructure, tidy, or "clean up" any existing section,
   and do not delete superseded reasoning that still applies — this repo deliberately keeps it with a note. A
   tidying pass would destroy the most valuable thing in the file. **

WHAT TO WRITE:
  1. The batch chronicle: a row for each batch COMPLETED in this invocation, with its real first->last commit
     range read from git. Note any commit inside a range that belongs to no batch, the way existing rows do. If a
     batch was only partly built, say so precisely rather than claiming it shipped.
  2. The rank table: mark only what actually shipped. The manual Windows smoke round STAYS at Rank 1 — Phase 3
     lengthens that list and does not touch it.
  3. The capability view: what a run can now do, in the voice of the existing bullets.
  4. A NEW "Opened by Phase 3" section in the house style — known, reasoned, not closed. It must include every
     item in ${PLAN} §8 that this invocation made real (what it adds to the manual smoke list), every risk from §4
     that was ACCEPTED rather than closed, everything the fix pass DECLINED with its reason, and anything a
     builder flagged as deliberately not done. The value of these sections here is that they record the REASON,
     not just the gap — write them that way.
  5. State plainly that Phase 3 is being delivered in TWO invocations and which groups remain, so a reader does
     not mistake a partial phase for a finished one.
  6. Corrections in place wherever this run proved a document wrong — annotate with a "CODE RIGHT, SPEC WRONG"
     note rather than silently editing.

MEASURE, DO NOT ASSERT:
  * Run the full gate yourself and quote the real numbers: rebuild in Debug AND Release, plus the suite. Do NOT
    copy a number from another agent's report — every stale figure in that file got there that way.
  * Read the git position from git: \`git rev-list --count origin/feature/agent-run-spine..HEAD\` and
    \`git log --oneline origin/feature/agent-run-spine..HEAD\`. DESCRIBE the local-only tail rather than
    hardcoding a count — that file's own warning is that every hardcoded count went stale.
  * Do NOT push, merge or rebase.

ALSO: ${PLAN} was the input to this run. Mark its status as EXECUTED-IN-PART with the outcome and which groups
remain, rather than leaving it reading as a plan that has not happened.

${GATE}
${COMMIT}

Report the gate numbers, the commit, and anything you could not reconcile. End with GATE: GREEN or GATE: RED.`,
  { label: 'fix:roadmap-update', phase: 'Fix', model: 'opus', effort: 'high' }
)

return {
  builtInThisInvocation: builtIds,
  inheritedFromEarlierInvocation: alreadyBuilt,
  reviewedScope: inScopeIds,
  groupsRemaining: GROUPS.map(g => g.id).filter(id => inScopeIds.indexOf(id) === -1),
  reconcilerBlockers: reconciled && reconciled.blockers ? reconciled.blockers : [],
  reviewFindingsRaw: allFindings.length,
  reviewFindingsDeduped: deduped.length,
  reviewFindingsVerified: toVerify.length,
  reviewFindingsNotVerifiedDueToCap: Math.max(0, deduped.length - VERIFY_CAP),
  confirmedFindings: confirmed.map(f => `[${f.severity}] ${f.file}:${f.line} — ${f.title}`),
  refuted: refutedCount,
  noVerdictVerifierDied: verdicts.filter(v => !v || !v.verdict).map(v => (v && v.finding ? `${v.finding.file}:${v.finding.line} — ${v.finding.title}` : '(unknown finding)')),
  neverSentDueToCap: deduped.slice(VERIFY_CAP).map(f => `${f.file}:${f.line} — ${f.title}`),
  fixReport,
  roadmap,
}
