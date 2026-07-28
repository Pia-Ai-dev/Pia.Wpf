# Batch 10 — Durability & lifecycle correctness — ✅ SHIPPED

**Phase 2 · Size M · `feature/agent-run-spine` · `e4ad6bf` → `770fad3` and `d1c746d` → `630c2c2`**
(plus the joint fix pass `aab9a06` → `601090e`, shared with Batch 11 — see the chronicle in
[`00-OVERVIEW.md`](00-OVERVIEW.md))

This file now describes **the code as built**, not as planned. Where the original spec's recommendation was
overturned at design time, the recommendation and the reason are kept — deleting them would invite someone to
re-litigate the same choice.

> **Build:** `dotnet build -p:EnableWindowsTargeting=true` → 0 errors, 194 pre-existing warnings, none in a file
> this batch touched. **Tests: written, never executed.** net10.0-windows cannot run on macOS; `dotnet test`
> was not invoked at any point. Execution is deferred to Windows/CI.

---

## What shipped

| Commit | What |
|---|---|
| `e4ad6bf` | `SqliteContext.GetConnection()`'s first-open path sets `PRAGMA journal_mode=WAL` then `PRAGMA busy_timeout=3000`, **before** `EnsureSchema` |
| `78e16dd` | `AssistantChatService` moves onto its own `SqliteConnection` + a `SemaphoreSlim(1,1)` gate covering **every** public method |
| `de8d391` | `IAssistantChatService.SetTitleAsync` — the auto-title rename stops being a full-replace writer |
| `cecd979` | The headless run's chat write rebases on the persisted rows (superseded by `011b989`) |
| `5767da5`, `770fad3` | `ChatSession.ForeignRunActive` + `ChatSessionManager`'s `RunChanged` subscription; `_ownRunIds` checked on the UI thread |
| `f5859d2` | `AssistantChatConcurrencyTests` — real `AssistantChatService` + real `AgentRunService` on one temp SQLite file |
| `d1c746d` | `ScheduledJobStatus.Completed` appended as ordinal 3 |
| `deff7d9` | All three settle methods branch on `Recurrence == Once` |
| `630c2c2` | Re-anchored a line ref the W3b edit shifted |
| `aab9a06` | `_ownRunIds` entries are **retired** when the run stops executing (fix pass) |
| `011b989` | `IAssistantChatService.SaveMergedAsync` — read + merge + write under **one** gate hold (fix pass) |
| `c067673` | `ForeignRunActive` also gates `RegenerateCore` and `SwitchToAgent` (fix pass) |
| `876992c` | An `UpdateAsync` that re-schedules forward re-arms a settled `Once` job (fix pass) |

## W1 — the shared connection had no write gate

**As built.** `AssistantChatService` owns `_connectionString` (from `SqliteContext.ConnectionString`), a lazily
opened private `SqliteConnection` with its own `busy_timeout=3000`, and a `SemaphoreSlim _gate = new(1, 1)`. The
constructor calls `context.GetConnection()` explicitly — copying `AgentRunService.cs:51-54`'s rationale
comment — so `EnsureSchema`/`MigrateSchema` has created `AssistantChats`/`AssistantChatMessages`/
`AssistantChatsFts` and the PRAGMA-detected `WorkingDirectory` column before the dedicated handle opens. The
ctor kept its **two** parameters (14 test call sites pin the shape). `ChatsChanged` is raised strictly **after**
`_gate.Release()`.

**Three things the original spec got wrong, corrected here:**

1. **The anchor list was incomplete.** The spec named only the four transactional writers. The **six
   untransacted reads** — `GetAsync`, `SearchAsync`, `TouchLastAccessedAsync`, `EvictOlderThanAsync`'s
   pre-select, `GetMaxUpdatedAtAsync`, `GetAllIdsAsync` — are the majority of the collision surface and the
   source of the only *user-visible* symptom that existed: `AssistantHistoryViewModel` posts `SearchAsync` from
   a pool-thread `ChatsChanged`, it threw `Execute requires the command to have a transaction object …`, and it
   was swallowed, leaving a silently stale history list. There is also a fifth `BeginTransaction` on the shared
   connection at `SqliteContext.cs:693` (the startup FTS backfill) that the spec never listed.
2. **A dedicated connection ALONE does not fix W1.** Both exceptions are intra-ADO.NET properties of *one
   `SqliteConnection` object used by two threads*, and the chat service is called from **three** thread classes
   (UI: `ChatSessionManager`; run pool: `HeadlessTurnExecutor` ×2 concurrent runs at the slot cap,
   `BackgroundAssistantTurnRunner`; `BackgroundService` pool: `AssistantChatSyncService`,
   `AssistantChatRetentionService`). Move it to a private handle and those five callers still collide on the new
   handle with exactly the same two exceptions. Both in-tree precedents say “dedicated connection **guarded by
   a lock**” (`FlowPersistenceStore.cs:7`/`:18`, `AgentRunService.cs:14`/`:44`). The spec's literal wording
   (“dedicated connection first, a gate is the broader later fix”) would have shipped a batch whose own
   acceptance criterion was unmet.
3. **The shared connection needed WAL + a busy timeout, in its own earlier commit.** Moving chat writes to a
   second connection *converts* an intra-connection `InvalidOperationException` into a cross-connection
   `SQLITE_BUSY`, and the shared side had zero protection. In default rollback-journal mode the chat
   transaction holds `RESERVED` from its first write through `COMMIT`, so any write by `TodoService`/
   `MemoryService`/`ReminderService`/`ScheduledJobService`/`VaultIndexer` fails *immediately* with “database is
   locked”. Without `e4ad6bf` this batch would have been a net regression for ten services.

**Deviations from the design, recorded:**

- **`SemaphoreSlim` + `await WaitAsync(ct)`, not the two in-tree `lock` precedents.** You cannot `await` inside
  a `lock`, the class is async throughout (ten `await Execute*Async` sites), and a `lock` would make the WPF UI
  thread **block its message pump** for the duration of another thread's full replace — a direct violation of
  “the user's Send never blocks on a headless step's persistence”. Documented in the class comment. Cost:
  `WaitAsync(ct)` can now throw `OperationCanceledException` where a caller previously always completed, reachable
  only from `AssistantChatSyncService`'s stopping token during shutdown.
- **The gate covers reads as well as writes.** This is the single detail a builder would have got wrong; see
  point 1. Consequence: a history `SearchAsync` now *awaits* behind an in-flight full replace. It awaits rather
  than blocks, so the UI stays responsive, but on a very long transcript the history refresh is visibly late.
  If that shows up, the answer is a shorter write (the incremental follow-up), not a narrower gate.
- **`EvictOlderThanAsync` is three phases, not “pre-select outside the gate”.** Taken literally, the design's
  wording re-opened the exact bug W1 exists to fix — the pre-select is an untransacted read on the dedicated
  connection. As built: pre-select under a short gate hold → release → the cross-service
  `_runService.ChatHasPlannedRunAsync` filter with **no** gate held (the actual intent: never hold two service
  gates at once) → re-take the gate for the delete-loop transaction.
- **`SetTitleAsync` returns `Task<bool>`, not `Task`.** The design also wanted the existing “chat disappeared
  before rename” warning kept; the two are incompatible because `AssistantChatService` owns no `ILogger` and
  adding one changes the ctor that 14 test call sites pin. The zero-row signal comes back as a return value and
  `ChatSessionManager` logs it.

## W2 — two writers on one chat row

**Option 2 (“one effective writer, chosen by run state”) was chosen** over the spec's option 1 (route the resume
into the live session) and option 3 (incremental write). Three mechanisms, not one:

- **(a) `ForeignRunActive` closes the concurrency at the source.** `ChatSessionManager` subscribes to
  `IAgentRunService.RunChanged`, marshals to the UI thread (G3), and sets the flag for
  `Planning`/`Running`/`Verifying`, clearing it on parked/terminal. `RestoreActiveRunAsync` seeds it — a
  hydrated session never executes its own run, so a re-attached executing run is *by definition* foreign.
  `AssistantViewModel` surfaces it and it gates `SendMessageCommand`, `RegenerateCore` **and**
  `SwitchToAgent` (the last two were doors the first pass left open — `c067673`; that commit also gave
  `SwitchToAgent` an `IsStreaming` guard it never had). `CanExecuteRunInBackground` is deliberately **not**
  gated: it launches into a *new* chat id and never writes this chat.
- **(b) The auto-title rename is no longer a full-replace writer.** `RenameChatAsync` was
  `GetAsync` → mutate `Title` → `SaveAsync`, fire-and-forget, so its DB snapshot was routinely stale by the time
  it wrote and it could revert messages a headless step had written in between. It now calls `SetTitleAsync`
  (single `UPDATE` + the FTS row refresh, since the FTS row indexes the title).
- **(c) `SaveMergedAsync` makes the headless write non-destructive.** The store reads the persisted rows,
  absorbs by `Id` any row it did not author, and writes — **all inside one `_gate` hold.** Keying on `Id` is
  mandatory: `Ordinal` is the writer's loop index, while `Id`s round-trip through `AssistantMessageMapper` and
  are what `AgentStep.First/LastMessageId` name.

**Two fix-pass corrections that mattered:**

- The first implementation put the rebase in the **executor** (`HeadlessTurnExecutor.RebaseOnPersistedRowsAsync`),
  reading through `GetAsync` — which takes the gate, reads, and **releases** it before `SaveAsync` re-takes it.
  Any writer committing in that gap still had its rows deleted. The merge moved into the **store** as
  `SaveMergedAsync` and the executor method was deleted. Ordering is a stable `OrderBy(Timestamp)` inside
  `SaveMergedAsync`, because absorbed rows appended to the tail were being renumbered into the wrong
  chronological order.
- `_ownRunIds` was added at launch and **never removed**, and the `RunChanged` handler skipped own runs
  unconditionally — so an interactively-launched run that parked and was then resumed *headlessly* stayed
  classified as “own” forever, and the Send lever never fired on the single most likely two-writer path in the
  product. `aab9a06` retires the entry when the run stops executing.

**Why option 3 was rejected (keep this — it is the most re-litigable decision in the batch).** The spec says
incremental writing removes the bug *class*, and it does. It is not affordable as one item in a Size-M batch:
`AssistantViewModel.RegenerateCore` (`:854-855`) removes the selected answer **and every message after it** from
`session.Messages` and relies on the next full replace to delete those already-persisted rows. An
append/upsert-by-`Id` writer resurrects them. So option 3 is not “add `AppendMessagesAsync`” — it must model
deletion *intent*, and regenerate's intent (drop this suffix) is mechanically indistinguishable from a headless
run's append (add this suffix): both are suffix edits at the same position. It needs a truncate-or-tombstone
API, an `Ordinal`-renumbering rule, an exemption for `SaveFromRemoteAsync` (remote snapshots **are**
authoritative and must still delete), and a rewrite of the regenerate flow. It is recorded in 00-OVERVIEW as the
follow-up that would retire the class.

## W3 — a `Once` job re-launched forever

**As built.** A new terminal `ScheduledJobStatus.Completed` (**ordinal 3, append-only**) plus one
`if (existing.Recurrence == RecurrenceType.Once)` branch inside each of `MarkRunCompleteAsync`,
`MarkRunFailedAsync` and `AdvanceMissedRunAsync`. Each branch only chooses the `CommandText` of the **single
existing** UPDATE (shared parameters bound after the branch, one round-trip), so bookkeeping cannot newly throw —
`MarkRunComplete`/`MarkRunFailed` are *not* try/catch-wrapped in the background service, and a second statement
there could abort the tick's remaining due jobs. `RecurrenceCalculator`, `ScheduledJobBackgroundService` and
`IScheduledJobService` were **not** touched, which is what keeps the two hand-written fakes and the NSubstitute
mock unchanged.

- **The predicate is `Recurrence`, never `Kind`, never “does `NextFireAt` still look past”.** `Kind` would kill
  every `AgentTask` job after its first run. “Looks past” is wrong in *both* directions: it catches a
  Daily/Weekly row whose stored config was synced or hand-edited into the past, and it **misses the quiet second
  face of W3 entirely** — `Once` with `SpecificDate == null` falls through to the Daily expression, which *does*
  clamp forward, so that job never looks past and silently repeats every day forever.
- **`NextFireAt` is left as it is** in every terminal branch (the `ReminderService.DismissAsync` precedent). The
  row keeps an honest record of when it was supposed to fire; `Status` is what removes it from
  `WHERE NextFireAt <= @Now AND Status = 'Active'`.
- **The terminal branch bumps `UpdatedAt`; the recurring branch keeps today's deliberate non-bump.** Not
  cosmetic: `SyncClientService`'s pull merge keeps the remote row when `remote.UpdatedAt >= local.UpdatedAt`
  (`:1477`) and `UpsertFromSyncAsync` writes `Status` back to `'Active'` while leaving `NextFireAt` (still the
  past instant) alone — so a settle that did not bump would be **reverted by the first pull** while testing
  green locally. That is the worst kind of fix.
- **The park settle IS the job's settle.** Nothing new marks the job complete when a parked run is later resumed
  and finishes. The spec's “the resume path has no job context” is a **wiring** statement, not a data one —
  `run.TriggerKind`/`TriggerRef` are loaded at `HeadlessRunLauncher.cs:220` and `TriggerRef` *is* `job.Id`. The
  wiring was **declined**, not blocked, for three reasons: double-advance (the park path already advanced, and
  there is no firing id to make a resume-time settle idempotent), an owner-device violation (only the owner
  fires and advances, but a resume is user-initiated from whichever device shows the Flow card), and asymmetric
  plumbing (`ResumeAsync` returns bare `true` and keeps its completion `Task` only in `_inflight`). The park
  settle now being *terminal* for `Once` is what makes that decline safe.
- **`RecurrenceCalculator` was not clamped, and the no-clamp contract is now pinned by a test.** The spec
  forbade the clamp and was right, for a reason worth keeping: a clamped `Once` job leaves the due window and
  then **fires again** at the clamped instant — same goal, same tokens, a fresh chat, a success toast, no failure
  counter, nothing in the log. Today's bug was loud and therefore diagnosable. Worse, the calculator is **one
  singleton shared with reminders**, and `ReminderService.CreateAsync`/`EnableAsync` route `Once` through it:
  “remind me today at 15:00” typed at 15:05 currently stores 15:00-today and fires on the next tick — with the
  `Once` arm clamped it silently jumps to tomorrow. Worst of all a clamp lands **green** on the whole suite,
  because the only pre-existing `Once` calculator test used a *future* date. Two pins were added: past date, and
  same-day-earlier-hour.

## Still open (see 00-OVERVIEW “Opened by Batch 10”)

`ActivateAsync`'s composer race (needs an owner decision — both fixes are visible interactive regressions) ·
W2's residual window (a live turn already streaming when Continue is clicked) · the incremental write that would
retire the class · chat-deletion resurrection (`HeadlessRunLauncher.cs:419`) · no composer hint string ·
`SQLITE_BUSY_SNAPSHOT` on read-first deferred transactions · no real re-arm surface for a settled one-off
(Batch 09) · no backfill, so every existing past-dated `Once` job fires **once more** (release notes) ·
`MarkRunFailedAsync` retiring a one-off on its first failure.

## Tests written (never executed)

`AssistantChatConcurrencyTests` (11) · `AssistantChatServiceTests` (+) · `SqliteContextTests` (3) ·
`ScheduledJobServiceTests` (19) · `RecurrenceCalculatorTests` (+2) · `SyncMapperNewEntitiesTests` (+2) ·
`ChatSessionManagerTests` (+) · `AssistantViewModelLeverTests` (+) · `HeadlessTurnExecutorTests` (+).

**Two honest limits on that coverage:**

1. The W1 concurrency tests are asserted to **fail on the pre-W1 tree by reasoning, not by demonstration.** If
   CI shows `AssistantChatConcurrencyTests` green on a revert of `78e16dd`, the tests are not exercising the
   collision and must be tightened.
2. The design asked for a `ManualResetEventSlim` barrier in `CountingChatService` to hold a writer *mid*-write.
   That decorator wraps only the `SaveAsync` boundary — the gate is not yet taken and no transaction is open
   there — so a barrier in it cannot hold the mid-transaction window, and would have produced a test that
   passes on the unfixed tree. The read-gating test instead widens the window through the public API (one
   transaction, 400 inserts). Its post-fix assertion is unconditional; its **pre-fix failure is highly likely,
   not guaranteed.** This is written into the test body, not hidden.

## Acceptance — met, with one qualification

No swallowed persistence failures under the E2 per-step write cadence ✅ · one chat row has exactly one effective
writer at a time ✅ **except** the residual window above · a `Once` job fires once ✅ (existing rows fire once
more first, by design) · build green ✅ · tests written, execution deferred to Windows/CI ✅.
