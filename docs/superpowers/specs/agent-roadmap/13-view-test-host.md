# Batch 13 — The shared WPF test host, and the View coverage it has been blocking

**Phase 3 cleanup · Size S–M · Work on `feature/agent-run-spine`** (see the chronicle in
[`00-OVERVIEW.md`](00-OVERVIEW.md))

This batch exists because three other batches each hit the same wall and each wrote it down instead of
fixing it: Batch 03 **withdrew** a finished, passing test rather than ship a red gate
([`03-audit-timeline.impl.md`](03-audit-timeline.impl.md) §9.1), Batch 12 was named as the owner of the fix
and never did it, and Phase 3 booked its entire UI surface as manual-smoke debt under **R11**, calling it
"the single largest hole in Phase 3's coverage". It is also the first work on this branch that would
**shorten** the Rank-1 manual smoke round rather than lengthen it.

## The defect is live at HEAD, and it is not a flake — measured 2026-08-01

The prior record (`0f5c53bf`) described it as amplifying a *shared-host flake*: full-gate failure rate
0/3 → 2/3 with the fact present, 1/3 with it skipped. Re-measured at `fcfa7d5`, it is **worse and better
than that** — worse because it is now **3/3**, better because 3/3 is *deterministic* and therefore easy to
verify a fix against:

| Tree | Runs | Result |
|---|---|---|
| `fcfa7d5` clean | 1 | 2704 total / **0 failed** / 1 skipped (24s) |
| `fcfa7d5` + the withdrawn fact restored | 3 | 2705 total / **1 failed** / 1 skipped — **all three runs** |

The failure is identical every time: `UiDispatcherServiceTests.PostAsync_OnTheUiThread_QueuesAndCompletesWithTheMutationApplied`,
at **1m 00s 004ms / 009ms / 007ms** — i.e. `WpfStaHost.InvokeTimeout`, the "queue never reached
`SystemIdle`" signature the withdrawal commit describes. Note the victim is **not** the one that commit
named (`PostAsync_WhenTheActionThrows_FaultsTheReturnedTask`), which corroborates its own diagnosis that
the victim is *whichever test pumps next* rather than a specific test's defect.

The withdrawn fact is **recoverable verbatim**: `eb0fb369` added it, `0f5c53bf` deleted it, and
`AssistantViewParseTests.cs` has had **no edits since**, so `git show 0f5c53bf -- tests/ | git apply -R`
restores it and the test project builds at **0 warnings** unchanged. So the red half of red-before/green-after
is free, and no test needs re-authoring.

## Goal

`WpfStaHost` tolerates an Nth frame-pushing fact. Then spend that headroom: re-land the withdrawn fact, and
add the View-parse tests three other batches booked as debt.

## Key seams

- `tests/Pia.Wpf.Tests/Views/WpfStaHost.cs` — `Pump()` is `Dispatcher.PushFrame` + a `SystemIdle` exit
  callback, and every caller invokes it from **inside** a `WpfStaHost.Run` body, i.e. from within a
  `DispatcherOperation` on a thread that is *already* running `Dispatcher.Run()`. That nesting is the
  suspect: the host thread pumps continuously, so a test does not need a nested frame — it needs to *wait*
  until the queue has drained.
- `Views/AssistantViewParseTests.cs` (2 facts) and `Services/UiDispatcherServiceTests.cs` (5 facts) — the
  seven existing frame-pushing facts, all in the `WpfApplicationStatic` collection.
- `Dispatcher.Run()`'s re-entry loop in `WpfStaHost.Start` — the host's own comment flags it, and the
  withdrawal commit names it as the mechanism.

## Plan

1. **Diagnose, don't guess.** A deterministic repro exists (above). Discriminate between the candidate
   mechanisms — a leaked continuation that keeps the queue non-idle, a nested frame abandoned by an earlier
   throwing fact, or a `Dispatcher.Run()` re-entry that leaves a frame that no longer services `SystemIdle`
   — by instrumenting rather than by reasoning. Record which one it was.
2. **Fix at the root, preferring the design that deletes the hazard class.** Candidate: drop `PushFrame`
   entirely. Because the host thread runs `Dispatcher.Run()`, `Pump()` can be
   `dispatcher.InvokeAsync(() => { }, DispatcherPriority.SystemIdle).Task`, waited from the **test** thread
   under the existing bounded timeout. That requires the seven existing facts to be restructured from one
   `Run(() => { …; Pump(); … })` body into `Run(step1); Pump(); Run(step2)` — mechanical, and each step
   still executes on the STA thread. Constraint to respect: WPF objects are thread-affine and `Run<T>`'s
   contract is that only primitives cross back, so state that must survive between steps is held on the
   host thread, never marshaled.
3. **Prove it both directions.** Full gate 3× at `failed: 0` with the fact restored (expect 2705), and the
   fix demonstrated load-bearing by neutralising `Pump()` alone and watching the same test go red again.
4. **Spend the headroom.** Each of these is named debt in another batch's file, closable now and not before:
   - `RunProgressPanel_RendersATimelineRow_WithItsStepOutcomeAndDecision`, restored verbatim — closes
     Batch 03 §9.1 item 1 (five row binding paths + `HasNoTimeline`).
   - A parse test over `Pia.Views.SettingsViews.AssistantView` — the ~20-line file Batch 12's callout
     promised. **It SHORTENS three manual-smoke items; it closes none of them, and the distinction matters
     enough to write down.** Batch 04's item 1 is *"toggle it, restart, confirm it stuck"*, Batch 05's is the
     same defect, and 07's item 6 asks that the roster surface *"persists across a restart"* — a parse test
     proves the `Binding` path resolves and the string renders, which is the half that fails **silently**;
     it says nothing about whether the value reaches disk. So the round keeps the persistence half of each
     and loses the render half. What it does cover outright is the **07 roster surface's XAML** (R11), since
     that `ItemsControl` lives in this same view.
   - The per-step **persona avatar** row in `RunProgressPanel` (07 smoke item 7) via the same
     `ItemTemplate.LoadContent()` technique, if it comes cheap — the defect it guards (`Guid?`/`Guid` DP
     mismatch + unbound `Emoji` drawing an empty 20×20 box for every step row) is exactly the class of
     silent-binding failure a parse test catches.

## Guardrails

- **The tree must be green at every commit**, which is the whole reason the original fact was withdrawn.
  A partial fix that leaves the gate at 1/3 red is worse than the current state and must not be committed.
- Zero-warning policy in **both** configurations, `-t:Rebuild`, count read off MSBuild's summary line.
- Do not weaken an assertion to make a test pass; do not add a per-test timeout that converts a hang into a
  silent skip. A bounded failure that names its stage is the existing contract — keep it.
- No `src/` behaviour change is expected. If the diagnosis lands on product code (e.g. a ViewModel posting
  work that never settles), that is a finding to record, not to fix silently inside a test batch.

## Acceptance

The `WpfApplicationStatic` collection carries **more** frame-pushing facts than it does today with the full
gate at `failed: 0` on three consecutive runs; the withdrawn fact is back in the tree; the settings
`AssistantView` is parsed by a test; and the Rank-1 manual smoke round is **shorter** than it was — with
`00-OVERVIEW.md` and the affected "Opened by" sections amended to say which items came off it and why.
