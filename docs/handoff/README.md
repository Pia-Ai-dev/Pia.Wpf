# Pia.Wpf — Visual Refresh Handoff

This folder contains everything Claude Code needs to implement the visual refresh of the Pia WPF
desktop application.

## What's in here

| File | Purpose |
| --- | --- |
| `PROMPT.md` | **Start here.** Ready-to-paste prompt for Claude Code. |
| `01-migration-guide.md` | Base refresh: tokens, sidebar, bubbles, action card, input bar, dark-mode wiring. **Ship this first.** |
| `02-modern-pro-controls.md` | Modern Pro — new reusable controls for long-form Pia responses (Markdig + ColorCode). Build after step 1 is shipped. |
| `03-memory-refresh.md` | Memory view refresh — dense category tree + full inspector (tags, JSON, lifecycle, access sparkline, related, embedding meta). |
| `tokens/PiaTokens.Light.xaml` | Drop-in `ResourceDictionary` — Light theme tokens. |
| `tokens/PiaTokens.Dark.xaml` | Drop-in `ResourceDictionary` — Dark theme tokens. |
| `tokens/PiaStyles.xaml` | Reusable `Style` definitions referenced from the guides. |
| `reference/` | Screenshots from the design exploration for visual reference. |

### `reference/` contents

- `01-modern-light.png` — Phase 1 target look, light theme.
- `02-modern-dark.png` — Phase 1 target look, dark theme.
- `03a-modern-pro-top.png` / `03b-modern-pro-mid.png` / `03c-modern-pro-bot.png` — Phase 2 (Modern
  Pro) long-form Pia response, captured top → middle → bottom of one scroll position because the
  layout is intentionally tall.
- `04-memory-before.png` / `04-memory-modern.png` — Phase 3 (Memory) before ↔ after.

## Stack assumptions

- .NET 10
- [WPF UI](https://github.com/lepoco/wpfui) (lepoco) — `ui:` namespace
- [Markdig](https://github.com/xoofx/markdig) + `Markdig.Wpf`
- [ColorCode.Core](https://github.com/CommunityToolkit/ColorCode-Universal) + WPF formatter

## Working order

1. **Read `PROMPT.md`** and pass it to Claude Code as the initial instruction.
2. Claude Code reads `01-migration-guide.md`, applies tokens + styles app-wide.
3. Run, screenshot, review. Ship.
4. Claude Code reads `02-modern-pro-controls.md`, adds the new chat controls.
5. Run, screenshot, review. Ship.
6. Claude Code reads `03-memory-refresh.md`, rebuilds the Memory view from the new controls.
7. Run, screenshot, review. Ship.

## Design principles (binding)

- **Zero structural changes.** No new views, no view-model rewrites. Only `ResourceDictionary`,
  `Style`, `ControlTemplate` and new `UserControl`s.
- **Tokens are the only source of truth.** Every color/spacing value goes through
  `{DynamicResource}` against `PiaTokens.*.xaml`.
- **WPF-UI stays in charge of the chrome.** We override its system brushes (`SystemAccent*`,
  `AccentFillColor*`, `ApplicationBackground*`) so the whole library re-tints automatically.
- **New controls mirror existing ones.** The new chat controls are analogous to your existing
  `ActionCard` and `CodeBlock` — same naming convention, same DP API style.
