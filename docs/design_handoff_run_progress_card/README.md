# Handoff: Agent-Run Planning Card — "Signal band" redesign

## Overview

Redesign of the **run-progress / planning card** in the Pia WPF assistant — the compact panel
pinned above the chat transcript whenever the open chat has a live or selected agent run.

The redesign (option **1b, "Signal band"**) solves the two problems named in the brief:

1. **"Too quiet — hard to tell what's happening at a glance."**
   State stops being a coloured 12px word among four greys and gets a *structural home*: a
   full-width tinted **band** at the top of the card. The band carries a state icon, the
   current-activity sentence at 15px as the lead line, a metadata sub-line, and the one
   conditional action button. Below it, a 4-segment progress strip shows plan position.

2. **"Tool activity feels like a log dump, not an audit trail."**
   The expander opens with a **decision summary** (pill counts: awaiting / denied / blocked /
   auto-approved), and the row list is **sorted by exception first**: Awaiting approval, Denied
   and Blocked rows sit above a rule in full-strength colour; auto-approved rows follow in
   reverse-time order in a recessive grey. The collapsed expander header itself shows the
   exception count, so a parked call is visible without expanding.

Secondary decisions carried in the design:

- **Ledger demoted.** `{tokens} Tokens · {s}s` no longer sits next to the actions. Elapsed time
  lives in the band's sub-line; the full token figure is a tooltip on that sub-line (and is shown
  inline in terminal states, where nothing else competes).
- **Long plans windowed.** Running step ±1, with muted fold rows above and below
  ("6 earlier steps — all done" / "4 later steps — pending"). Card height stays bounded; no
  inner scrollbar inside a chat.
- **Personas** stay as a 20px ringed avatar, placed between the status glyph and the title so it
  never pushes the title's ellipsis point around; the persona name is appended to the title in
  muted grey and is the first thing to be ellipsised.

## About the design files

The files in this bundle are **design references created in HTML** — prototypes showing intended
look and behaviour, **not production code to copy**. The task is to recreate them in the existing
WPF codebase using its established patterns:

- `src/Pia.Wpf/Controls/Assistant/RunProgressPanel.xaml` (+ `.cs`)
- `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs`
- `src/Pia.Wpf/Converters/RunProgressConverters.cs`
- `Run_*` keys in `ViewStrings.resx` (EN/DE/FR)

Use existing theme resources / dynamic resources for colour rather than hard-coding the hex
values below; the hex values are given so you can verify the mapping and add the two or three
tokens that don't exist yet (warning tint, band tints).

## Fidelity

**High fidelity.** Colours, type sizes, weights, spacing and row heights below are final and
should be reproduced closely. Layout is the design; exact sub-pixel matching is not required, but
type sizes and row heights are (they were chosen against the 11–12px baseline of the existing
card).

## Screens / views

There is one control with **eight states** plus three orthogonal overlays. All are drawn in
`1b State Matrix.dc.html`.

### Shared frame

- Card: background `#FFFFFF`, 1px border `rgba(214,211,205,.9)`, radius **10px**,
  shadow `0 1px 3px rgba(28,25,23,.06)`, `overflow: hidden` (band corners clip to the radius).
- Card fills the width of the transcript column; reference renders at 640px.
- Font: **Segoe UI** everywhere; monospace (Consolas) only for times, token counts and tool names.

### Region A — the state band (always present)

Grid: `[icon 15px] [gap 11] [text column *] [gap 11] [button auto]`, padding `12px 14px`,
1px bottom border.

| State | Band bg | Band border | Icon | Lead line (15px) | Sub-line (11.5px) | Action |
|---|---|---|---|---|---|---|
| Planning | `rgba(28,25,23,.04)` | `rgba(214,211,205,.9)` | spinner, ink | "Building a plan…" (`#1C1917`) | "Planning · {s}s elapsed" (`#78716C`) | — |
| Running | `rgba(0,120,212,.08)` | `rgba(0,120,212,.18)` | spinner, accent | running step title (`#1C1917`) | "Running · step {n} of {m} · {s}s elapsed" (`#005A9E`) | Pause |
| Delegating | same as Running | same | spinner, accent | "Waiting for the sub-agents to finish…" | "Delegating · {k} of {n} finished · {s}s elapsed" | Pause |
| Waiting for you | `rgba(0,120,212,.12)` | `rgba(0,120,212,.28)` | filled accent dot with `!` | reason, semibold `#005A9E` | "Waiting for you · step {n} of {m} · {s}s elapsed" | **Continue** (primary) |
| Paused | `rgba(0,120,212,.12)` | `rgba(0,120,212,.28)` | pause glyph, accent | "You paused this run" | "Paused · step {n} of {m} · you can change the plan now" | **Continue** (primary) |
| Completed | `rgba(22,163,74,.08)` | `rgba(22,163,74,.2)` | filled green dot with ✓ | "Completed" semibold `#15803D` | "{n} of {m} steps · {s}s · {tokens} tokens" | Publish files (only if unpublished) |
| Completed, truncated | `rgba(28,25,23,.04)` | `rgba(214,211,205,.9)` | hollow grey ring | "Completed" semibold `#78716C` | "{n} of {m} steps · {s}s" | Publish files (if applicable) |
| Failed | `rgba(220,38,38,.07)` | `rgba(220,38,38,.22)` | filled red dot with `!` | "Failed" semibold `#DC2626` | "Stopped at step {n} of {m} · {s}s" | Publish files (if applicable) |

- **Truncation reason chip** sits inline to the right of the text column in Completed-truncated:
  11px, `#78716C`, bg `#F5F5F0`, 1px `rgba(214,211,205,.9)`, radius 4, padding `4px 7px`.
  Copy: "Result not verified" / "Stopped at budget" / "Ended early". **Never red.**
- **Card border** switches to `rgba(0,120,212,.35)` for Waiting/Paused and `rgba(220,38,38,.3)`
  for Failed — the whole card, not just the band, so it reads in scrollback.
- The lead line is **single-line, ellipsised**; it replaces the old separate italic activity line.
  When there is nothing to say (terminal states) it is the state name instead.
- Spinner: 15px, 2px ring, `rgba(0,120,212,.25)` track / `#0078D4` head, 1.1s linear rotation.
  Shown only in Planning / Running / Delegating.

**Buttons.** Secondary (Pause, Publish files): bg `#FFFFFF`, 1px border
`rgba(0,120,212,.4)` (accent contexts) or `rgba(214,211,205,.9)`, radius 5, padding `6px 13px`,
600 / 11.5px, text `#005A9E` / `#44403C`; hover fills with a 6% tint of the same hue.
Primary (Continue): bg `#0078D4`, text `#FFFFFF`, hover `#106EBE`, padding `7px 15px`.
Each button disables (opacity .5, no hover) while its async action is in flight; Pause's label
becomes "Pausing…".

### Region B — progress segments (live states only)

Row of `n` equal segments, `height 3px`, `gap 3px`, radius 2, padding `10px 14px 0`.
Done `#16A34A`; running `#0078D4` pulsing (opacity 1→.35, 1.6s ease-in-out); pending
`rgba(214,211,205,.9)`; failed `#DC2626`; skipped `rgba(214,211,205,.9)` at 50% opacity.
Hidden in terminal states. For plans over ~12 steps keep the segments (they compress) — this is
the only element that shows the *whole* plan when the list is windowed.

### Region C — step list

Column, `padding 9px 14px 12px`, `gap 1px`. Row grid:
`[glyph 16px] [avatar 20px, optional] [title *] [trailing auto]`, `gap 9px`.

| Step state | Glyph | Title colour / weight | Trailing |
|---|---|---|---|
| Pending | 7px hollow circle, 1.5px `#D6D3D1` | `#A8A29E`, 400 | — |
| Running | 8px filled `#0078D4`, pulsing | `#1C1917`, 600 | "now" pill: 34×16, radius 8, bg `rgba(0,120,212,.15)`, 600/9.5px `#005A9E` |
| Done | ✓ 12px `#16A34A` | `#44403C` (`#78716C` while the run is live), 400 | token count, mono 11px `#D6D3D1` |
| Failed | ✕ 12px `#DC2626` | `#1C1917`, 600 | token count |
| Skipped | ⊘ 12px `#A8A29E` | `#A8A29E`, 400 | "skipped", 11px `#A8A29E` |

Row height 28px (30px for the running row). The running row gets a 2px left accent bar
(`#0078D4`, `margin-left:-9px; padding-left:7px`) and bg `rgba(0,120,212,.05)`.
Titles are single-line ellipsised; token counts never wrap.

**Fold rows** (windowed long plans): 24px tall, `⋮` in the glyph column (`#A8A29E`), text 11.5px
`#78716C`, clickable to expand the whole list in place. Copy: "6 earlier steps — all done" /
"4 later steps — pending". Window = running step ±1 when the plan exceeds 7 steps.

**Persona avatar:** 20px circle, bg `#F5F5F0`, 1.5px `#0078D4` ring, emoji 11px. Persona name
appended to the title as `· Experienced Coder` in `#78716C`.

**Plan mutation (Paused only):** trailing group of five 22×20 buttons — Edit ✎ / Insert below + /
Move up ▲ / Move down ▼ / Skip ⊘ — 1px `rgba(214,211,205,.9)`, radius 4, bg `#FFFFFF`, glyph
`#44403C`. Settled rows keep the group at `color:#D6D3D1` and disabled, and the whole row goes to
opacity .55 — layout must not shift. While the run is live the group is **not rendered at all**
and the band's sub-line ("you can change the plan now") is replaced by nothing; the old
"Pause the run to change its plan." note is shown as a muted 11.5px line under the list.

**Inline editor** (replaces the row, never a dialog): container 1px `rgba(0,120,212,.35)`,
radius 7, bg `#FAFAF7`, padding `8px 9px`. Eyebrow "EDITING STEP {n}" 600/10.5px uppercase
`#78716C`, letter-spacing .05em. Title input: bg white, 1px `#0078D4` when focused, radius 5,
12px text. Body textarea: same, 11.5px, min-height 44px, label semantics from
`Run_StepInstruction_*`. Footer right-aligned: Cancel (text, `#78716C`) then Save (primary).

**Note lines** (conditional, stacked, 11.5px/1.5, `#78716C`; warning ones `#B45309`) go directly
under the list: workspace/publish notes, branch note, plan-mutation result/error, refused-pause
note.

### Region D — steering note (Waiting-for-you / Paused, above the list)

Box: 1px `rgba(0,120,212,.28)`, bg `rgba(0,120,212,.05)`, radius 7, padding `9px 10px`,
margin `12px 14px 4px`. Label "Note for the rest of this run" 600/11px `#005A9E`.
Textarea: white, 1px `rgba(214,211,205,.9)`, radius 5, min-height 30px, 12px, placeholder
"Optional — for example, keep the summary under 200 words" (`#A8A29E`).
Footnote 10.5px `#78716C`: "Sent with every remaining step of this continuation. Not saved; does
not survive a restart."

### Region E — Tool activity expander

**Collapsed header** (always present once the run has any tool calls): bg `#FAFAF7`, 1px top
border `rgba(214,211,205,.7)`, padding `9px 14px`. "Tool activity" 600/12px `#44403C`;
"{n} calls" 11px `#78716C`; then an **exception badge** if any: 600/10.5px `#B45309` on
`rgba(180,83,9,.1)`, radius 4, padding `3px 6px` ("2 awaiting"); red equivalent for denied/blocked.
Chevron ▾/▴ right-aligned.

**Expanded body**, padding `11px 14px 13px`, bg `#FAFAF7`:

1. **Decision pills** row, `gap 6px`, wrap, radius 12, padding `4px 9px`, 11px:
   - awaiting → 600 `#B45309`, bg `rgba(180,83,9,.1)`, 1px `rgba(180,83,9,.25)`
   - denied / blocked → 600 `#DC2626`, bg `rgba(220,38,38,.08)`, 1px `rgba(220,38,38,.22)`
   - auto-approved / approved → 400 `#78716C`, bg `#FFFFFF`, 1px `rgba(214,211,205,.9)`
   Zero-count categories are omitted.
2. **Rows**: 4-column grid `56px | 74px | * | auto`, `gap 0 10px`, line-height 24px, 11.5px.
   Columns: time (mono, `#A8A29E`) · "Step N" (`#78716C`, omitted if the step no longer exists) ·
   tool name (mono; `#1C1917` for exceptions, `#44403C` otherwise, with a `failed` suffix in
   `#DC2626`) · decision (right-aligned; 600 in the exception colour, or `#A8A29E` for
   auto-approved).
   **Ordering: all exception rows first, then a 1px `rgba(214,211,205,.7)` rule, then the rest in
   reverse chronological order.** A rule also caps the top of the table.
   Still metadata only — no paths, args or results.
3. **Sub-states** (mutually exclusive with the table): empty → "No tool decisions were recorded
   for this run." (`#78716C`); read failed → "The tool activity for this run could not be read."
   (`#B45309`); truncated → the table plus a dashed-top-border note "Trace shortened — only the
   first {n} decisions were kept." (`#78716C`).

Re-read the trace from the store on **every** expand.

### Region F — Sub-agents expander (only when the run delegated)

Same header treatment as Region E, with "{k} of {m} finished". One row per child:
20px persona avatar, child goal (ellipsised) + state word in the header palette
(`#16A34A` Completed / `#78716C` Ended early / `#DC2626` Failed), token count mono `#D6D3D1`.
Expanding a child loads **that child's own** trace, same row shape minus the "Step N" column,
with its own empty / read-failed states. Never merge traces.

## Interactions & behaviour

- Card is persistent: stays after completion, reappears from chat history, live-updates while
  attached.
- Elapsed seconds tick live in the band sub-line; token total updates live but is only *visible*
  on hover (tooltip) during live states.
- Progress segments and the step list update in place — the running highlight moves, reorders
  move rows, edits rewrite titles without rebuilding the list.
- Buttons self-disable while their async action is in flight (prevents double-click).
- All editing inline; no modal dialogs.
- Motion budget: spinner rotation (1.1s linear), running-dot & running-segment pulse
  (1.6s ease-in-out, opacity 1→.35). Band tint changes cross-fade over 150ms. Nothing else animates.
  No blur, no glass, no gradients.
- Everything user-shaped (step titles, child goals, note text) is display-only and
  untrusted-length: single-line ellipsis on titles, wrap on note lines.
- Localisation: EN/DE/FR via `Run_*` resource keys; German strings run ~30% longer — the band's
  lead line and every step title must ellipsise, never wrap, and the sub-line must be allowed to
  ellipsise too. Numbers and dates per current culture.

## State management

Existing `RunProgressViewModel` should expose (names indicative):

- `RunState` enum → drives band tint/icon/lead/actions via a converter set
- `LeadLine`, `SubLine`, `TruncationReason` (nullable)
- `IsSpinnerVisible`, `ShowProgressSegments`
- `Steps` (ObservableCollection: `Status`, `Title`, `Instruction`, `Tokens`, `Persona`,
  `CanMutate`, `IsEditing`), `WindowedSteps` + `EarlierFoldCount` / `LaterFoldCount`
- `CanPause`, `CanContinue`, `CanPublish`, `IsActionInFlight`
- `SteeringNote` (two-way, cleared on resume)
- `NoteLines` (ordered collection of `{Text, Severity}`)
- `ToolActivity`: `IsExpanded`, `LoadState` (loading/ok/empty/failed), `Rows`,
  `DecisionCounts`, `IsTruncated`, `TruncationCap`
- `SubAgents`: `Children`, `FinishedCount`, per-child `ToolActivity` block

Trace load is triggered by `IsExpanded → true`, always re-reading from the store.

## Design tokens

Pia tokens (existing): paper `#F5F5F0` / paper-light `#FAFAF7` / surface `#FFFFFF`;
ink `#1C1917` / ink-light `#44403C` / ink-muted `#78716C`; accent `#0078D4` /
accent-light `#106EBE` / accent-muted `#005A9E`; success `#16A34A`; error `#DC2626`;
warning `#B45309`; border `rgba(214,211,205,.6)`; radius 10.

Add if missing: `--pia-ink-faint #A8A29E`, `--pia-ink-ghost #D6D3D1`, success-dark `#15803D`,
and the six band tints listed in the Region A table.

Spacing scale used: 2 / 3 / 5 / 7 / 9 / 10 / 11 / 12 / 14 px. Radii: 4 (chips), 5 (buttons,
inputs), 7 (inset boxes), 10 (card), 12 (pills). Type: 15 / 12.5 / 12 / 11.5 / 11 / 10.5 px,
weights 400 and 600 only.

## Assets

No new image assets. Icons are simple glyphs (✓ ✕ ⊘ ⋮ ▲ ▼ ✎ + ! ▾ ▴) — replace with the
codebase's existing icon set (Fluent/Segoe MDL2) at matching optical sizes. Persona avatars are
the existing emoji avatars.

## Files in this bundle

- `1b State Matrix.dc.html` — **the implementation reference**: all eight states plus the
  expanded audit, the paused plan-mutation row set and the inline editor.
- `Agent Run Card.dc.html` — the original four-direction exploration; `1b` is the chosen one,
  the others are context for why.
- Both files load the Pia design-system stylesheet from `_ds/…` in the parent project. Opened
  standalone from this folder they will render unstyled at the shell level — the card markup
  itself is fully self-contained inline styles and renders correctly regardless.
