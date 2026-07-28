# Batch 01 — Budget-pause polish

**Phase 2 · Size XS · Work on `feature/agent-run-spine`** (there is no `feature/agent-budget-pause` branch —
see the chronicle in [`00-OVERVIEW.md`](00-OVERVIEW.md))

The budget-pause → `WaitingForInput` + resume batch shipped, and the hardening batch (`19c7a03` → `HEAD`) closed
almost all of its follow-ups. What is left is two nits in one file.

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

## Work items (all that remains)

1. **`HeadlessRunLauncher.ResumeAsync` indentation.** The `Task.Run` lambda body (`:277`–`:345`) is
   under-indented one level relative to the lambda, so the resume path reads as if it were still at method
   scope. Cosmetic only; touch nothing else in the method.
2. **`_inflight` micro-race.** Both completion `finally` blocks call `_inflight.TryRemove(run.Id, out _)`
   unconditionally, so the original launch's teardown (`:209`) can delete a *resume*'s entry (written at
   `:329`) — which would make `StopAsync` (`:348`) miss that task on shutdown. Practically unreachable (user
   reaction time vs. a `finally`), but a keyed guard — remove only if the stored task is still *this* task —
   closes it.

## Open assumptions to revisit (may become their own work)

- Resume always re-launches via the **headless** path, so a resumed interactive run runs unattended and uses
  the **scheduled** budget envelope + the **current default** persona/provider (not necessarily its origin's).
  A true interactive-streaming resume (re-bind a live `ChatSession`) is deferred — see Batch 08. The restored
  panel makes this easier to reach: a resumed run's headless chat writes and a still-open live `ChatSession`
  are two writers on one chat row.
- A parked *scheduled* run now advances its schedule (so it stops re-launching every tick), but nothing marks
  the **job** complete when that run is later resumed and finishes — the resume path has no job context.
- No resume-count cap (each Continue is an explicit user/Flow action — acceptable).

## Acceptance

Build green; the two nits cleared. Guardrails from the budget-pause batch (1–9 in its synthesis) stay intact
— especially G5 (pause never raises `TurnCompleted`/settles `Completed`) and G3 (crash sweep parks
`WaitingForInput`/`Paused`).
