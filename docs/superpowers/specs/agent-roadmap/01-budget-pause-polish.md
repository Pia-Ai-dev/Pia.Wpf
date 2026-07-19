# Batch 01 — Budget-pause polish

**Phase 2 · Size S · Branch from `feature/agent-budget-pause`**

The budget-pause → `WaitingForInput` + resume batch shipped, but its verify pass left a few should-fix items
and open assumptions. This batch closes them. (The must-fix — an interactive pause wedging the live session —
was already fixed in `1b6d162`; the R3 resume-slice + stale-marker fixes landed in `093fe18`.)

## Work items

1. **Interactive parked-run reachability after app restart** _(should-fix, the important one)._
   A run paused at budget in the **foreground** chat has its `WaitingForInput` Flow card suppressed
   (`AgentRunNotificationSurface.cs:99-101`, to avoid redundancy with the panel Continue button), and
   `ActiveRunId` is runtime-only (`ChatSession.cs`, set at launch in `ChatSessionManager.cs:512`, never
   rehydrated). So after a restart the parked interactive run survives in the DB (good) but has **no resume
   affordance** — neither card nor panel. Headless parked runs are unaffected (their durable card survives).
   **Decision to resolve (pick one):**
   - (a, recommended, smallest) Publish the `WaitingForInput` card **even for the foreground chat** — it's
     `Persistent`+`RequestDurable`, so it survives restart and is retracted on resume/terminal. Cost: a
     redundant card while the user is watching the panel.
   - (b) Rehydrate `ActiveRunId` for a chat's non-terminal run on session open (`ChatSessionManager.ActivateAsync`)
     so the panel + Continue reappear. Larger, but keeps the card suppressed.
   - (c) Re-publish parked-run cards at startup.

2. **D2 transcript-preservation test** _(should-fix, test gap)._
   `HeadlessTurnExecutor.BeginRunAsync` was changed on resume to **load existing chat rows instead of clearing**
   (else the terminal `EndRunAsync` full-replace would erase pre-pause history) — the plan called this CRITICAL,
   but there is no test. Add a `HeadlessTurnExecutorTests` case: save a chat with prior user/assistant rows →
   `BeginRunAsync`(resume) → step → `EndRunAsync` → assert prior rows survive and no duplicate goal message; plus
   the fresh-empty-chat case still seeds `[goal]`.

3. **Nits.**
   - `HeadlessRunLauncher.ResumeAsync` `Task.Run` lambda body is under-indented one level (`~:246-293`).
   - `_inflight` micro-race: the original headless completion's `finally` does an unconditional
     `TryRemove(run.Id)` that could delete a resume's entry (practically unreachable — user-reaction-time vs a
     `finally`); a keyed/`TryUpdate` guard closes it.

## Open assumptions to revisit (may become their own work)

- Resume always re-launches via the **headless** path, so a resumed interactive run runs unattended and uses
  the **scheduled** budget envelope + the **current default** persona/provider (not necessarily its origin's).
  A true interactive-streaming resume (re-bind a live `ChatSession`) is deferred — see Batch 08.
- No resume-count cap (each Continue is an explicit user/Flow action — acceptable).

## Acceptance

Build green; a parked interactive run is resumable after restart (item 1); D2 seeding is tested; nits cleared.
Guardrails from the budget-pause batch (1–9 in its synthesis) stay intact — especially G5 (pause never raises
`TurnCompleted`/settles `Completed`) and G3 (crash sweep parks `WaitingForInput`/`Paused`).
