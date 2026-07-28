# Batch 01 — Budget-pause polish

**Phase 2 · Size XS · Work on `feature/agent-run-spine`** (there is no `feature/agent-budget-pause` branch —
see the chronicle in [`00-OVERVIEW.md`](00-OVERVIEW.md))

The budget-pause → `WaitingForInput` + resume batch shipped, and the hardening batch (`19c7a03` → `HEAD`) closed
all of its follow-ups, including the two nits this file used to list. What is left is the assumptions below.

## Closed already — do not re-do

- **Interactive pause wedging the live session** (must-fix) — fixed in `1b6d162` (non-terminal `OnPaused`).
- **R3 resume-slice + stale pause marker** — fixed in `093fe18`.
- **Item 1, parked-run reachability after restart** — closed by the hardening batch via option (b):
  `ChatSessionManager.ActivateAsync`'s hydrate branch now re-attaches the chat's newest non-terminal
  `Planned` run (failure-isolated, off the UI thread, never replacing a live run and never resurrecting a
  terminal one), so the panel + Continue reappear. The `WaitingForInput` card stays suppressed for the
  foreground chat, as option (a) would have changed.
- **Item 2, D2 transcript-preservation test** — covered by the hardening batch: each completed step is now
  persisted by both executors, and `HeadlessTurnExecutorTests` asserts that a parked run's replies survive,
  that a resume appends without erasing them, and that the interim and terminal writes agree on message Ids.
- **Post-resume wall-clock inflation** — the ledger now accumulates ACTIVE time (`activeMs` +
  `segmentStartedAt` inside `LedgerJson`, no schema change), so a parked gap is never billed.

- **`HeadlessRunLauncher.ResumeAsync` indentation** — the `Task.Run` lambda body is indented with its lambda
  now; the resume path no longer reads as if it were at method scope.
- **`_inflight` micro-race** — both completion `finally` blocks go through `RemoveInflight(runId, ownCts)`,
  which removes the entry only when it is still *this* dispatch's (identified by its per-dispatch CTS), so an
  original launch's teardown can no longer delete a resume's entry and make `StopAsync` miss it (G-4).

## Work items

None — this batch is empty. Keep the file for the assumptions below until they are either closed or promoted
into their own batch.

## Open assumptions to revisit (may become their own work)

- Resume always re-launches via the **headless** path, so a resumed interactive run runs unattended and uses
  the **scheduled** budget envelope + the **current default** persona/provider (not necessarily its origin's).
  Since the interactive create persists an *empty* grant envelope (D1 producer), an interactive-origin resume
  also holds **no write grants** — honest (the launch had none either, only per-click cards) but it means a
  resumed segment cannot write files; deriving the real per-tool grants a session held is a Batch 04 job.
  A true interactive-streaming resume (re-bind a live `ChatSession`) is deferred — see Batch 08. The restored
  panel makes this easier to reach: a resumed run's headless chat writes and a still-open live `ChatSession`
  are two writers on one chat row (see 00-OVERVIEW “Deliberately open”).
- A parked **recurring** scheduled run now advances its schedule (so it stops re-launching every tick). A
  `RecurrenceType.Once` job cannot: `ComputeNextFireAt` returns the same past instant for it, so it stays due
  and still re-launches — see 00-OVERVIEW “Deliberately open”. Nothing marks the **job** complete when a
  parked run is later resumed and finishes either — the resume path has no job context.
- No resume-count cap (each Continue is an explicit user/Flow action — acceptable).

## Acceptance

Build green; nothing left to clear. Guardrails from the budget-pause batch (1–9 in its synthesis) stay intact
— especially G5 (pause never raises `TurnCompleted`/settles `Completed`) and G3 (crash sweep parks
`WaitingForInput`/`Paused`).
