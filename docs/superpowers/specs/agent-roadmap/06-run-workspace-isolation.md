# Batch 06 — Run workspace isolation (run-aware file-tool base root)

**Phase 3 · Size M · Branch from the latest branch**

Milestone B built the per-run scratch dir (`%LOCALAPPDATA%\Pia\runs\<runId>\`), but headless runs currently write
**real deliverables straight to the assistant files folder** (`HeadlessRunLauncher.Initialize(workspaceRoot: null, …)`
— the "reserved opt-in-sandbox seam"). True isolation needs the file tool's **base root** to be run-aware, not just
the subpath (plan §9 line 358-362, §17.2, Q5 line 22).

## Goal

Make `FilesToolHandler`'s **base root** run-aware (ambient via `TaskContext`/`TaskAmbient`, established in 1.2) so a
run writes into its isolated workspace and results are **promoted** into the assistant folder on success — instead
of writing to the assistant folder directly. Escapes still rejected.

## Key seams

- `FilesToolHandler` — the base-root resolution (today the assistant folder); make it read the ambient run root.
- The per-run `TaskAmbient` / `TaskContext` (1.2 hook) — carries the active run's workspace root.
- `HeadlessRunLauncher` — currently passes `workspaceRoot: null` (writes to assistant folder); switch to the
  isolated run root + a promotion step.
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
