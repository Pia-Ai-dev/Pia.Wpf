# Pia.Wpf — Forward Plans

These docs are implementation plans for follow-on work that came out of the
visual refresh (see `docs/handoff/`). Phase 1 + Phase 2 of the visual refresh
shipped; the UI controls for the surfaces below exist (under
`src/Pia.Wpf/Controls/Chat/`) but their **data producers** are not wired yet.

| Plan | Status | Rough effort |
| --- | --- | --- |
| [stats-summary.md](stats-summary.md) | not started | ~2h |
| [suggestion-chips.md](suggestion-chips.md) | not started | 0.5–1d |
| [source-chips.md](source-chips.md) | not started | ~1d |
| [callouts.md](callouts.md) | not started | ~1d |

Each plan is self-contained: it states what the surface is, lists the
producer changes by file, names the symbols to add, and ends with an
acceptance test you can run from chat. Pick whichever one you're closest
to needing — they don't depend on each other.

## Conventions assumed by these plans

- The receiving UI control already exists in `Controls/Chat/` and binds
  to properties on `Pia.Models.AssistantMessage` (Phase 2 commit).
- Add **only** additive properties to `AssistantMessage` — never break
  existing serialization or rendering paths.
- Honor the privacy logging rules (`CLAUDE.md` "Privacy-First Logging"):
  payloads + user-named items must use `SensitiveDebug` / `SafeUrl`.
- Use `{DynamicResource}` against the Pia tokens for any new visual
  elements (`Resources/Theme/PiaTokens.*.xaml`).
