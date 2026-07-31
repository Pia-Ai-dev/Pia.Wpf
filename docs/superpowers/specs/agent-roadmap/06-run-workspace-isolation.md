# Batch 06 — Run workspace isolation (run-aware file-tool base root)

**Phase 3 · Size M · Work on `feature/agent-run-spine`** (see the chronicle in [`00-OVERVIEW.md`](00-OVERVIEW.md))

> **✅ SHIPPED 2026-07-31 as work groups G1–G5, `70400aa` → `695e123`.** Its polish is deliberately **outside**
> that range and lands later: the simplify commit `676f629`, the review fix `914730d`, and its share of the joint
> fix pass (`3b66603`, which closes six of this batch's review findings as three defects). The **executable** spec
> is [`06-run-workspace-isolation.impl.md`](06-run-workspace-isolation.impl.md), whose B8/B10/B15 sections carry
> in-place annotations where the fix pass falsified them; the measured seam map and the nine places **this** file
> was wrong are in [`phase3-workflow-plan.md`](phase3-workflow-plan.md) §2–§3. **Read those two before this one** —
> in particular §3.2, which is why the batch is five groups rather than the two this file describes. The prose
> below is kept as the original scoping and is *not* the as-built record.

Milestone B built the per-run scratch dir (`%LOCALAPPDATA%\Pia\runs\<runId>\`), but headless runs currently write
**real deliverables straight to the assistant files folder**: the method is
`HeadlessTurnExecutor.Initialize(string? workspaceRoot, …)` (`HeadlessTurnExecutor.cs:91`) — the "reserved
opt-in-sandbox seam" — and `HeadlessRunLauncher` merely *calls* it with `workspaceRoot: null`, on **both** the
launch (`HeadlessRunLauncher.cs:181`) and the resume (`:289`) path. So the scratch dir is created and cleaned but
nothing writes into it. True isolation needs the file tool's **base root** to be run-aware, not just the subpath
(plan §9 line 358-362, §17.2, Q5 line 22).

## Goal

Make `FilesToolHandler`'s **base root** run-aware (ambient via `TaskContext`/`TaskAmbient`, established in 1.2) so a
run writes into its isolated workspace and results are **promoted** into the assistant folder on success — instead
of writing to the assistant folder directly. Escapes still rejected.

## Key seams

- `FilesToolHandler` — the base-root resolution (today the assistant folder); make it read the ambient run root.
- The per-run `TaskAmbient` / `TaskContext` (1.2 hook) — carries the active run's workspace root.
- `HeadlessRunLauncher` — currently passes `workspaceRoot: null` at both call sites (writes to assistant
  folder); switch to the isolated run root + a promotion step. Note the *grant* side already narrowed:
  `HeadlessRunRequest.DefaultGrantedWrites` is `{write_file}` (no `delete_file`), and a resume restores the
  launch's persisted grant envelope — so isolation is now the only missing half, not both.
- `SafeFolderPath` / escape-rejection — must still hold against the new base root.

## Decisions to resolve

- **Promotion policy:** what/when is promoted from run workspace → assistant folder (on `Completed` only? on the
  owner's decision, per the existing "owner decision" commit?). Reconcile with the current "write real deliverables
  to the assistant folder" behavior — this batch changes _where the work happens_, then promotes.
- **Interactive runs:** do they also isolate, or keep writing to the assistant folder (they're attended)? Recommend
  isolate both for uniformity, promote on success.
- Backward-compat with the Milestone-B ephemeral-scratch behavior.

## Guardrails

- No escapes: the run base root is a hard boundary; path traversal rejected as today.
- Executor parity + crash safety: a crashed/cancelled run's un-promoted workspace is cleaned by the startup sweep.
- Privacy: paths/filenames are user content → `SensitiveDebug`/`SafeUrl` as applicable.

## Acceptance

Runs do their file work in an isolated per-run root; deliverables are promoted to the assistant folder on success;
escapes rejected; build green.
