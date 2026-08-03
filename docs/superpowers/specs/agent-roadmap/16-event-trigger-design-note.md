# 19 — `AgentRunTrigger.Event` — design note (no producer authorized)

**Design note only.** Answers T2-G3 in
[`../agent-roadmap-finish/01-code-checklist.md`](../agent-roadmap-finish/01-code-checklist.md). It changes no
code, authorizes no producer, and does not make `Event` a supported trigger. Its job is to stop the next reader
re-deriving the seams, and to settle one question that has been asked from two ends.

## 1. The state of the world

`AgentRunTrigger` is `User = 0, Schedule = 1, Event = 2` (`AgentEnums.cs:19-24`). **Nothing in `src/` writes
`Event`.** Every construction site is a literal `.User` or `.Schedule`: `IBackgroundAssistantTurnRunner.cs:27`,
`ScheduledJobBackgroundService.cs:362` (`ExecuteAgentTaskAsync`) and `:511` (`RunResearchTurnAsync`),
`ChatSessionManager.cs:818`, `:826`, `:1150`. The remaining `grep -n AgentRunTrigger` hits are the property
(`AgentRun.cs:21`), record members (`IAgentRunService.cs:30`, `IHeadlessRunLauncher.cs:23`), parameters
(`HeadlessRunLauncher.cs:1089` `TrySerializeGrantEnvelope`, `:1110` `SerializeGrantEnvelope`, `:1178`
`TrySerializeChildEnvelope`), doc comments (`AgentRun.cs:23`, `HeadlessRunLauncher.cs:120`,
`IBackgroundAssistantTurnRunner.cs:26`) — and **one read**: `AgentRunService.cs:1156`, in `MapRun` (`:1149`),
`TriggerKind = (AgentRunTrigger)r.GetInt32(5)` — an unvalidated
cast of a raw SQLite int straight into the enum. That is the line the append-only argument below turns on, and
the one a future producer's reviewer should open first.

*(Anchors into `AgentRunService`, `IAgentRunService`, `HeadlessRunLauncher` and `ScheduledJobBackgroundService`
carry their enclosing member name as well as a line, because those files were under active edit while this note
was written and their line numbers moved twice. Grep the member, not the number.)*

**Nothing branches on trigger kind either** — a repo-wide search for a `switch` on the trigger or a
`case AgentRunTrigger` returns zero matches. The enum's own comment says why that is by design: provenance
metadata, "NOT the persist discriminator (that is the execution path — §16 R14)" (`AgentEnums.cs:17-18`).

The ordinal is spent regardless. It reaches SQLite as `TriggerKind INTEGER NOT NULL`
(`SqliteContext.cs:320`), so the enum is append-only for the ordinary local reason: existing rows hold ints,
and a build that reordered them would misread its own history. **It is *not* append-only for a wire reason** —
`IAgentTimelineService.cs:16` states it outright: *"There is no `SyncAgentRun` DTO and runs never cross the
sync wire."* Confirmed: no `AgentRun` type in `src/Pia.Shared/`, no `AgentRun` in `SyncMapper.cs`. Anyone
carrying over §13.1's "an older peer may store an unknown ordinal unvalidated" should know that hazard is
`ScheduledJobStatus`' (`ScheduledJob.cs:7-12`), not this enum's.

One place already tolerates a new name for free: the grant envelope serializes the trigger as a *string*
(`HeadlessRunLauncher.cs:1116`, in `SerializeGrantEnvelope`, `Trigger = trigger.ToString()`) and the reader
treats it as "diagnostics only; never consulted to widen a grant" (`:1315-1316`, the envelope DTO member).
`"trigger":"Event"` would round-trip harmlessly.

## 2. What an "event" would be here — the producers that actually exist

Signals that fire without a user asking. Three sweeps are needed, and the third is the one that is easy to
miss: `public event` across `src/Pia.Wpf/Services/`; the watcher loops that raise no event; and the
`BackgroundService` **pollers**, which raise no event either and are where the best-shaped candidates turn out
to live.

| Candidate signal | Where | Wants a run? | Natural identity |
|---|---|---|---|
| **Reminder came due** — 30 s poll, fires once per due reminder | `ReminderBackgroundService.cs:34-53`, `CheckAndFireRemindersAsync` (`:56-76`) | **The textbook case.** A discrete due moment, unattended, with nothing user-initiated about the firing. | **`Reminder.Id`, a `Guid`** (`Models/Reminder.cs:8`) — stable across firings of one reminder, exactly as `ScheduledJob.Id` is. Fits `TriggerRef` unchanged. |
| Todo deadline within 24 h | `Flow/TodoDeadlineBackgroundService.cs:41-52`, `ReconcileAsync` (`:79`, `GetDueWithinAsync(Window)` at `:87`) | Not as it stands. This is a **level, not an edge**: the same set re-surfaces every 15-minute tick (`:20`) for up to 24 h (`:19`) and is retracted when no longer due. A run keyed on "still inside the window" fires on every tick with nothing advancing. | `TodoItem.Id`, a `Guid` (`Models/TodoItem.cs:8`) |
| **Meeting ended** — `WaitForEndAsync` returns, service self-stops | `MeetingAttendeeService.cs:606-631`, via `IMeetingSession.WaitForEndAsync` (`IMeetingSession.cs:45`) | Attractive: "summarize the meeting that just ended" is unattended work with a natural completion trigger. Note the asymmetry — joining is a user action (`StartAsync`), *ending* is not. | **None exists.** No `MeetingId`/`SessionId` field anywhere in `Services/MeetingAttendee/`. The transcript folder is settings-wide and shared by every meeting the app ever attends (`MeetingTranscriptPaths.cs:17-20` takes no meeting argument), and the only per-meeting string anywhere is a timestamp in a save-dialog default the user can edit (`TranscriptOverlayViewModel.cs:377`). That leaves the meeting URL, which is sensitive (CLAUDE.md → `SafeUrl`) and cannot be stored raw. |
| Meeting state transitions (`InLobby`, `Error`, …) | `MeetingAttendeeService.cs:109` `StateChanged`, raised at `:787-796` | No. Mid-lifecycle UI states; a run keyed on `InLobby` would race the join it is watching. | as above |
| **Vault file changed** — debounced `FileSystemWatcher` over `*.md` | `VaultWatcher.cs:22-46` | Plausible ("a note changed, re-derive something"), but it is the *loudest* signal in the app: Pia's own writes flow through it with no special-casing (`VaultWatcher.cs:16-18`), so a run that writes the vault re-triggers itself. | vault-relative path — a **string** |
| **Ingest completed** for one source | `AutoIngestService.cs:45-46` / `IIngestScheduler.cs:27,30`; raised at e.g. `AutoIngestService.cs:179`, `:300`, `:355` | Plausible and better-shaped than the raw watcher: already hash-gated and serialized, so it fires on real content change only. `IngestCompleted` carries no argument, so only `IngestStarted(sourceRef)` identifies anything. | `sourceRef` — a **string** |
| Sync arrival | `SyncClientService.cs:101` `SyncCompleted` | **Not a producer today, and here is what is missing:** `SyncCompletedEventArgs` carries counts and two bools only (`SyncCompletedEventArgs.cs:5-10`). It cannot say *what* arrived, so nothing downstream could scope a run. | none available |
| Plugin / MCP notification | — | **No surface at all.** A repo-wide grep for `NotificationHandler` / `OnNotification` returns nothing; `McpPluginToolHandler` reads `tool.Name` and calls `InvokeAsync` and nothing else. A producer here needs a subscription built first. | n/a |
| Hotkey, window open/close | `NativeHotkeyService.cs:16`, `WindowManagerService.cs:38-40` | These are user actions wearing an event's clothing. `User` is the honest trigger for them. | n/a |

The shape of the last column is what §3 and §5 turn on, and it splits: the *event-shaped* candidates — watcher,
ingest, meeting end — have a **string** identity or none, while the two **polled due-date** producers already
carry a stable `Guid` that fits `TriggerRef` as it is. The candidate that is hardest to key is not the one that
is easiest to want.

## 3. The seams a producer collides with

**3a. `TriggerRef` is `Guid?` and means one thing.** `AgentRun.cs:23-24` — *"e.g. `ScheduledJob.Id` when
`AgentRunTrigger.Schedule`"*; written at `AgentRunService.cs:118`/`:158`, indexed at `SqliteContext.cs:346`,
bound as lowercase-`D` Guid text (`AgentRunService.cs:776-777`). A child run deliberately carries null
(`AgentRunOrchestrator.cs:829-832`).

An event has no `ScheduledJob` row, but the column's *type* is only a problem for the string-keyed candidates.
A reminder- or todo-keyed producer writes its own entity's `Guid` and the column needs no change — the comment
says "e.g.", and `TriggerRef` already means "the thing that caused this run" rather than "a job row". A
string-keyed producer (vault path, `sourceRef`) has the real dilemma: mint a synthetic Guid or leave the column
null. **Minting a fresh Guid per firing is worse than null** — see 3b.

**3b. Which layer of duplicate protection survives depends entirely on which producer.**
`AnyExecutingRunForTriggerAsync` (`AgentRunService.cs:765-781`, SQL at `:761-763`) counts rows with the same
`TriggerRef` in a non-terminal state. It is explicitly *"Defence in DEPTH, never the primary bound"*
(`IAgentRunService.cs:319-320`, on `AnyExecutingRunForTriggerAsync`) — the primary bound is **state advancing at
dispatch time**, which is what keeps a tick off an occurrence it already fired.

The reminder producer has both. The depth guard matches, because the key is stable. And it has a genuine primary
bound of the same *kind* as the scheduler's: `GetDueRemindersAsync` selects on `NextFireAt <= now`
(`ReminderService.cs:100-113`), and firing calls `DismissAsync` (`:181-212`), which either marks a one-shot
`Completed` (`:189-195`) or advances `NextFireAt` through `ComputeNextFireAt` for a recurrence (`:196-206`). The
due row stops being due the moment it fires. (The advance happens *after* the fire side effects — toast and Flow
item at `ReminderBackgroundService.cs:67-68`, dismissal at `:69` — so a crash in between re-fires on the next
30 s tick. That is the same window the scheduler has, not a new one.)

The string-keyed producers have neither. No schedule advances, so there is no primary bound; and with a null or
per-firing-unique `TriggerRef` the depth guard never matches. `VaultWatcher`'s 300 ms debounce
(`VaultWatcher.cs:24`) coalesces bursts but not a slow editor. For those candidates a *stable derived* key in a
Guid-typed column is the only shape that keeps even the depth guard — and per §2 the obvious derivation inputs
are sensitive. The todo-deadline producer is the third case: the key is stable, so the depth guard holds, but
nothing advances, so a completed run is immediately eligible to fire again on the next tick.

**3c. The hazard is not a broken `switch` — it is the absence of one.** Since nothing branches on trigger kind
(§1), adding a producer breaks no existing dispatch. The real risk runs the other way: **new** code that tests
`!= User` and calls the remainder "Schedule" — and every "Schedule" path assumes a job row it can read (grants
and provider at `ScheduledJobBackgroundService.cs:362-369`, in `ExecuteAgentTaskAsync`; strike accounting via
`MarkRunFailedAsync`, `ScheduledJob.cs:21-26`). An `Event` run has none. Any first producer should land *with*
the first trigger-kind branch, so the two-valued assumption is broken deliberately rather than discovered.

**3d. Autonomy: the surface is settled, the grant *provenance* is not.** The unattended gate does **not** come
from the trigger — it comes from the execution path. `BackgroundAssistantTurnRunner` hardcodes
`ToolGateSurface.Unattended` (`:420`, `:468`), the interactive path hardcodes `ToolGateSurface.Interactive`
(`ChatSession.cs:1049-1050`), and `ToolAutonomy.Resolve` keys on `Surface`, never on trigger
(`ToolAutonomy.cs:139-149`, `:196`, `:261`). So an `Event` run routed through the headless executor
(`HeadlessRunLauncher.cs:431` → `HeadlessTurnExecutor.cs:420` → the runner above) inherits the unattended gate
for free. What is open is where its **grants** come from: a `Schedule` run's come from its job row, an
interactive run holds no standing grant at all (`ChatSessionManager.cs:818`, envelope `[]`), and an `Event` run —
having no job row and no user in the loop — could only take them from settings. That is a standing write grant
for a run nobody requested. **This is the unanswered question, not a detail** (see §5).

## 4. Is `Event` the ordinal §13.1 wants? No.

§13.1 ([`04-autonomy-policy.impl.md`](04-autonomy-policy.impl.md) §13, item 1; summarized in
[`00-OVERVIEW.md`](00-OVERVIEW.md) "Opened by Batch 04") wants a new append-only ordinal because the resume
grant floor is origin-blind: the interactive Planned create (`ChatSessionManager.cs:826`, envelope `[]`) and the
"Run in background" detach (`:1150`, defaulting to `DefaultGrantedWrites` = `{write_file}`,
`IHeadlessRunLauncher.cs:38`) both persist `TriggerKind = User` + `RunShape.Planned`, so on envelope loss
nothing on the row says which floor is right (`HeadlessRunLauncher.cs:556-565`, the resume envelope restore).

The test: **does relabeling either of those two rows `Event` state something true about its provenance?** No —
both are user-initiated. They differ on the **attended / detached** axis *within* `User`. `Event` is a value on
the **who caused it** axis. They are orthogonal, so an `Event` producer contributes **zero bits** to the
distinction §13.1 needs.

**Therefore: whatever discriminator §13.1 ends up with must live on the attended/detached axis *within* `User`,
and `Event` is not on that axis.** Naming it is a schema decision this note does not make — §13.1 declined it
for the same reason. Spending `Event` on the detach case would be a permanent mislabel of a persisted column,
for a benefit (one fallback floor) that any correctly-scoped discriminator delivers just as well.

**And the bit §13.1 wants already exists — it is simply not recorded on the row.** The detach at `:1150` goes
through the headless launcher and is gated `Unattended`; the interactive create at `:826` runs on the live
session and is gated `Interactive` (3d has both citations). The two paths *are* distinguishable at execution
time; the row just does not remember which one made it. That is a narrower and more accurate statement of the
gap than "an ordinal is missing," and it is the pointer the next reader wants. The two items remain independent;
neither blocks the other. (§13.1's own line refs have since rotted as the file grew: it cites
`ChatSessionManager.cs:753`/`:1010`; the current lines are `:826`/`:1150`.)

## 5. What would have to be true before anyone writes `.Event`

Conditions to be *answered*, not steps to take. Which of them are open depends on the producer — the first two
are already met by one candidate.

1. **A named producer with a stable identity** — a correlation key that is the same across two firings of the
   same cause, is a `Guid` (or `TriggerRef`'s type changes), and does not embed a meeting URL or other sensitive
   value. Met by the reminder producer as it stands (§2); open for every string-keyed candidate.
2. **A duplicate-suppression story that does not rely on a schedule.** Also met by the reminder producer, whose
   due state advances at fire time (3b). Open for the rest — and for the string-keyed candidates *both* bounds
   are gone, so something must replace the primary one, not just the depth guard.
3. **A ruled-on grant provenance for an unattended run nobody requested** (3d). Ruling "none, ever" is a
   perfectly good answer and is the narrow one.
4. **A trigger-kind branch, shipped with the producer** (3c), so no later reader can read "not `User`" as
   "`Schedule`".
5. **A statement of what a user sees and can switch off.** An event-driven run appears in a chat with no
   user act behind it; the roadmap has already settled that a capability a user relied on being removed belongs
   in release notes, and the inverse — one appearing unasked — is at least as visible.

## 6. What this note does not do

It does not authorize a producer, does not reserve `Event` for any of the candidates in §2, and does not make
`Event` a supported trigger. `Event = 2` remains what it is today: an ordinal that is spent, documented, and
written by nothing. A future batch that wants it must answer §5 first, and §4 is settled — that batch is **not**
§13.1's.
