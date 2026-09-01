# Approval-park defects — checklist

**Status:** complete — all 20 steps landed 2026-09-01 · **Owner:** Marco Altmann · **Written:** 2026-08-31
**Origin:** [2026-08-31-approval-park-defects.md](2026-08-31-approval-park-defects.md), which root-causes
the four problems reported in `artifacts/Agent_Run/agent_run_issues.md`.

Tick each box in the commit that lands it.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little
standalone value, unblocks a High.

## Groups

| Group | Covers | Reported issue |
|---|---|---|
| A | Stop the tool loop when a run parks | 1 |
| B | Make a park hold the actual call | 2, 3a, 1.1 (data half) |
| C | Carry tool exchanges across park/resume | 2 |
| D | Vault vs. sandbox targeting | 3 |
| E | Cross-step artifact de-duplication | 3 (duplicate files) |
| F | Approval counter | 4 |
| G | Show the full approval text | 1.1 |

## Decision gates — ALL SETTLED 2026-08-31 (owner)

Lettered `Q` so they never collide with the step groups above. Every gate below is closed; the answer is
binding on the steps in its Blocks column.

| # | Question | Answer | Blocks |
|---|---|---|---|
| Q1 | May untruncated parked-call arguments and tool payloads (user content, incl. file bodies to 512 K chars) be persisted? | **Yes** — verbatim, in a new **local-only** store, FK-cascaded off `AgentRuns` so it is purged with the run. Never `AgentRuns.ExtraJson`, never `SyncAssistantChatMessage`, never logged outside `SensitiveDebug`. | B1, B2, G1, G2, C1 |
| Q2 | On grant: replay the exact call, or seed the step with it? | **Replay it once**, then re-run the step. The persisted row is marked replayed before execution, so at-most-once holds by construction rather than by advice. | B2 |
| Q3 | Where do pre-park tool exchanges live? | **One new local table for both** B1 and C1: a parked call is a call with no result. Workspace `.scratch` is out (model-writable, and absent at `RunWorkspaceMode.None`); a widened chat schema is out (cloud-synced, breaches the `_messages`/`_persisted` guardrail). | C1 |
| Q4 | Does `write_file` under a run workspace hard-refuse a `Vault/` path, or route it to `create_source`? | **Hard-refuse, and only that** — a path resolving into a `Vault` subtree under a run workspace. Every other workspace write is unchanged, and the interactive path is untouched. The refusal names `create_source` / `update_source`. | D1 |

Two facts found while settling them, which override the wording of the steps below:

- **`AgentRuns.ExtraJson` cannot carry B1.** Both resume claims `SET ExtraJson=NULL` — the documented
  reason `ClarificationsJson` and `FailureJson` each got their own column. Q1's store is a table, not a
  widened envelope.
- **D2's memory half is already written.** `BuiltInPluginDefaults.cs:45` already says *"Do not use
  `write_file` for a vault source, new or existing"*. Only the files half is missing, and built-ins load
  from code rather than from the DB, so the edit reaches existing installs with no migration.

---

## A — Stop the tool loop when a run parks

- [x] **A1 — Add a stop signal to the tool-dispatch contract.** Give `ToolDispatchContext` a settable
  stop flag and have `AiClientService`'s round loop finish the exchange instead of `continue`-ing when
  a handler sets it; the context is built inside the per-call `foreach` today, so hoist it out or
  return the stop decision from `DispatchToolCallsAsync`. *Deps:* — · *Effort:* S · *Value:* Enabler
- [x] **A2 — Set the flag on every park and withhold arm.** `BackgroundAssistantTurnRunner` raises it in
  `Park`, withheld-because-parked and withheld-because-asking; the advisory strings stay.
  *Deps:* A1 · *Effort:* XS · *Value:* High
- [x] **A3 — Pin the short-circuit.** A fake handler that parks in round 1 must produce exactly one
  provider round-trip, and the run must reach `WaitingForInput` with no further rounds; the interactive
  path must be unchanged. *Deps:* A2 · *Effort:* XS · *Value:* High

## B — Make a park hold the actual call

- [x] **B1 — Persist the parked call verbatim.** Name, arguments and call id, alongside today's display
  string, in whichever store gate Q1 settles on. *Deps:* Q1 · *Effort:* M · *Value:* Enabler
- [x] **B2 — Honour it on grant.** Either replay the granted call before the step re-runs, or seed the
  resumed step with it so the model reissues it verbatim — per gate Q2.
  *Deps:* B1, Q2 · *Effort:* M · *Value:* High
- [x] **B3 — Extend both to withheld calls.** The second and later calls in a parked exchange (the
  `create_source` in the reported run) must survive the same way, not just the one that parked.
  *Deps:* B2 · *Effort:* S · *Value:* High

## C — Carry tool exchanges across park/resume

- [x] **C1 — Persist pre-park tool exchanges to a resume-readable store.** Local-only and run-scoped;
  must not reach `SyncAssistantChatMessage`, which is cloud-synced and guardrail-separated from
  `_messages`. *Deps:* Q3 · *Effort:* M · *Value:* Enabler
- [x] **C2 — Re-seed them in `BeginRunAsync`.** They flow through the existing
  `AgentToolCarryover.ClearOldResults` + `AgentContextCompactor` seam unchanged, so the context budget
  still holds. *Deps:* C1 · *Effort:* S · *Value:* High
- [x] **C3 — Pin the resume.** Park mid-step, resume, and assert the rebuilt step request contains the
  pre-park tool exchange rather than prose alone. *Deps:* C2 · *Effort:* S · *Value:* High

## D — Vault vs. sandbox targeting

- [x] **D1 — Refuse a `Vault/` write inside a run workspace.** `PrepareWriteFile` rejects a path
  resolving into the vault and names `create_source` / `update_source`, because the workspace copy-in
  deliberately excludes the vault. The method is shared with the interactive path, so scope the guard
  to a non-null `TaskAmbient.Current?.WorkspaceRoot`. *Deps:* Q4 · *Effort:* S · *Value:* High
- [x] **D2 — Disambiguate the two stores in the plugin prompts.** The files addition states the sandbox
  root is not the vault; the memory addition states `create_source` is how a document reaches it.
  *Deps:* — · *Effort:* XS · *Value:* Med
- [x] **D3 — Require an explicit vault subfolder or an ask.** A goal naming the vault without a target
  folder must resolve to `sources/<subfolder>` or a `request_user_input` question, not a guess.
  *Deps:* D2 · *Effort:* S · *Value:* Med

## E — Cross-step artifact de-duplication

- [x] **E1 — Seed each step with the artifacts already declared.** The step instruction forbids
  re-creating an existing deliverable under a new name. *Deps:* C3 · *Effort:* S · *Value:* Med
- [x] **E2 — Probe every declared artifact in the verifier.** Resolve the `declared=1`-against-three-Done-steps
  open question and flag near-duplicate deliverables in the verdict. *Deps:* E1 · *Effort:* S ·
  *Value:* Med

## F — Approval counter

- [x] **F1 — Derive the Awaiting pill from run state, not history.** It shows only while the run is
  parked on a tool approval and never on a terminal run; no schema or store change, the timeline stays
  INSERT-only. *Deps:* — · *Effort:* S · *Value:* High
- [x] **F2 — Relabel superseded park rows.** A `ParkedForApproval` row the run has moved past renders as
  "nicht ausgeführt" instead of "Wartet auf Freigabe". *Deps:* F1 · *Effort:* XS · *Value:* Med
- [x] **F3 — Pin the two invariants.** Awaiting ≤ 1 while parked (the store is first-call-wins), and 0
  once the run is Completed, Failed or Cancelled. *Deps:* F2 · *Effort:* XS · *Value:* High

## G — Show the full approval text

- [x] **G1 — Carry an untruncated approval description.** Per gate Q1; if the answer is no, raise the
  120/400 caps to a stated, justified figure instead. *Deps:* Q1, B1 · *Effort:* S · *Value:* Med
- [x] **G2 — Expand the run panel's approval line.** An expander over `ApprovalTargetLine`, with an
  `AutomationProperties.AutomationId` and its `[InlineData]` row in `ViewAutomationIdTests.cs` in the
  same change. *Deps:* G1 · *Effort:* S · *Value:* Med
- [x] **G3 — Let the Flow card body be readable.** A bounded multi-line body plus a tooltip in place of
  the single-line `TextTrimming="CharacterEllipsis"`; if it gains an interactive control, the
  AutomationId and its test row land with it. *Deps:* G1 · *Effort:* S · *Value:* Med

## Landed beyond the checklist

- [x] **Interactive ask parity.** `ChatSession`'s `request_user_input` pre-route and its withheld-write arm
  raise the loop stop signal too, so the interactive step path stops on an ask exactly as the unattended
  one does. Owner decision, 2026-08-31; not a checklist step because the checklist scoped issue 1 to the
  run panel. *Deps:* A2 · *Effort:* XS · *Value:* Med

## Outcome

All 20 steps landed on `feature/agent_issues` between 2026-08-31 and 2026-09-01, in six batches.
Full unfiltered `dotnet test`: **5855 total, 0 failed, 5797 passed, 58 skipped** (the `Explicit`
live-provider tests). `dotnet build -t:Rebuild` reports `0 Warnung(en) / 0 Fehler` in Debug and Release.

Known open, carried rather than closed:

- **Risk 3 is closed on both channels** by [2026-09-01-vault-probe-plan.md](2026-09-01-vault-probe-plan.md):
  the probe now stats the vault on whichever channel names a vault-shaped reference, so a declared
  `sources/hr/urlaub-2026.md` reaches the vault too. The declared half keeps its true working-folder
  `NOT FOUND` on purpose; the pair is reconciled by the standing guidance in the artifact block.
- **Withheld rows can outlive several park/resume cycles**, holding up to 512 K chars that was never
  executed. Bounded by the per-park and per-run caps and the terminal purge. The cheap tightening
  (supersede every unreplayed row on each park) is rejected on purpose — it would drop the surviving
  `create_source` that group B exists to keep.
- **The end-to-end re-run of the original goal has not been done.** It needs a throwaway profile that
  also patches `assistantFilesFolder`; `PIA_DATA_DIR` alone does not redirect the vault.
- **A stale-latch window in the approval detail.** If a park clears while a store read is in flight, the
  late read can set the latch against the new state. Non-blocking; the next `RunChanged` corrects it.

---

## Verification

**Per step:** `dotnet build -t:Rebuild -v:n` in Debug **and** Release, `0 Warning(s)` / `0 Error(s)`
read off MSBuild's summary line.

**Per group:** full unfiltered `dotnet test` at `failed: 0` — once per group, not per step. The suite
runs ~11 min and class filters do not narrow it, so a per-step run would cost about three hours across
this checklist for no extra signal.

---

## Suggested order

Cheapest decisive work first, then the vertical slices.

1. **A1 → A2 → A3.** The dialog latency is the loudest defect and the fix touches one loop plus three
   arms. Nothing else depends on it, so it can ship alone.
2. **F1 → F2 → F3.** Self-contained in `RunProgressViewModel`, no store or schema change, closes a
   visibly wrong number.
3. **D2.** Two prompt strings; it narrows issue 3 while the expensive halves are still being decided.
4. **Settle Q1, Q2, Q3, Q4.** Four questions, one sitting. Everything below is blocked on them.
5. **C1 → C2 → C3.** The amnesia fix. The largest single win in the run, and E depends on it.
6. **B1 → B2 → B3.** The replayable park. Restores the discarded `create_source` and the discarded
   extract in one slice.
7. **D1 → D3.** Vault targeting, now that a withheld vault call actually survives.
8. **G1 → G2 → G3.** The full-text surfaces, once there is a full text to show.
9. **E1 → E2.** De-duplication last, so it de-duplicates a real case rather than a symptom.
