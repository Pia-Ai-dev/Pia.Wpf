# Pia.Wpf — Forward Plans

Open implementation plans only. When a plan here is fully implemented, delete
it and update this list. (The chat-surface plans below came out of the visual
refresh, see `docs/handoff/`: the receiving UI controls already exist under
`src/Pia.Wpf/Controls/Chat/`, the data producers are the remaining work.)

| Plan | Status |
| --- | --- |
| [callouts.md](callouts.md) | not started — `PiaCallout` control exists; Markdig parser + producer wiring missing |
| [source-chips.md](source-chips.md) | not started — `PiaSourceChip` control exists; tool-result plumbing missing (web citations already feed `Sources` via a separate path) |
| [2026-06-07-memory-vault-migration-open-questions.md](2026-06-07-memory-vault-migration-open-questions.md) | open — legacy `Memories` table retirement, live sync cut-over to vault files, lint scheduling, parser frontmatter fidelity |
| [2026-06-25-meeting-browser-feature-deepdive.md](2026-06-25-meeting-browser-feature-deepdive.md) | deferred |
| [2026-06-25-meeting-browser-implementation-plan.md](2026-06-25-meeting-browser-implementation-plan.md) | deferred |
| [2026-07-04-sync-transfer-optimization.md](2026-07-04-sync-transfer-optimization.md) | phases 1–4 done; phase 5 ship sequencing (submodule bump + shim cleanup) open |
| [2026-07-04-sync-transfer-optimization-implementation-plan.md](2026-07-04-sync-transfer-optimization-implementation-plan.md) | same |
| [2026-07-06-ingest-plugin-server-sync-followup.md](2026-07-06-ingest-plugin-server-sync-followup.md) | open — server seeding for ingest plugin GUID …007 |

## Conventions assumed by these plans

- The receiving UI control already exists in `Controls/Chat/` and binds
  to properties on `Pia.Models.AssistantMessage`.
- Add **only** additive properties to `AssistantMessage` — never break
  existing serialization or rendering paths.
- Honor the privacy logging rules (`CLAUDE.md` "Privacy-First Logging"):
  payloads + user-named items must use `SensitiveDebug` / `SafeUrl`.
- Use `{DynamicResource}` against the Pia tokens for any new visual
  elements (`Resources/Theme/PiaTokens.*.xaml`).
