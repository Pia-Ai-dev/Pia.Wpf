# Handoff: Agent-Run Planning Card (redesign brief for Claude Design)

**Product context.** Windows desktop app (WPF), an AI assistant with an "interactive agent mode": the user gives a goal, the assistant builds a step plan, executes it with tools (git, file writes, …), asks for approvals where required, and reports progress. The element to redesign is the **run-progress / planning card** — a compact panel pinned above the chat transcript whenever the open chat has a live or selected agent run. The attached screenshot shows its simplest settled form: state **Completed**, the plan's step list, and the expanded **Tool activity** audit trail.

**Current visual language (baseline to react to).** One quiet card; 11–12 px type; muted greys for everything except a single state color; dense rows; tiny text buttons; collapsible sections. The dense step list + token ledger is the one "signature" element; everything else is deliberately quiet. All copy is localized (EN/DE/FR) and numbers/dates render per locale (screenshot is German: "70.137 Tokens · 651,7s"). Light + dark theme via design tokens (accent blue, success green, danger red, muted/text greys).

Source of truth: `src/Pia.Wpf/Controls/Assistant/RunProgressPanel.xaml`, `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs`, `src/Pia.Wpf/Converters/RunProgressConverters.cs`, `Run_*` keys in `ViewStrings.resx`.

---

## 1. Header row — state chip + ledger + actions

Left side:
- **Indeterminate spinner**, shown only while work is happening (Planning / Running / Delegating). Never shown for paused or terminal states.
- **State chip** (semibold label, colored per state):
  - *Planning*, *Running*, *Delegating* (parked while sub-agents work) — default text color
  - *Waiting for you* (paused at its budget) and *Paused* — accent blue (action needed)
  - *Completed* — success green
  - *Completed but truncated* — reads "Completed" but muted, plus a separate muted reason chip: "Result not verified" / "Stopped at budget" / "Ended early" (never red)
  - *Failed* — danger red
- Optional **muted truncation/reason chip** next to the label (see above).

Right side:
- **Live ledger strip**: `{total tokens} Tokens · {seconds}s` (input+output tokens, thousands-separated; elapsed wall-clock seconds). Updates live during the run.
- **Conditional action buttons** (compact; at most one of Pause/Continue visible at a time; each self-disables while its async action is in flight to prevent double-clicks):
  - **Pause** (label becomes "Pausing…" while the request is in flight) — only while Running/Delegating.
  - **Continue** (primary style) — only while Waiting-for-you / Paused.
  - **Publish files** — only on a settled run whose sandboxed workspace still holds unpublished files (typically Failed/Cancelled runs).

## 2. Current-activity line

One quiet italic line under the header, hidden when there is nothing to say, single-line ellipsised. Content by situation: "Building a plan…", the running step's title, "Checking the work…" (verify pass), "Waiting for your approval to use {tool}", "Stopped at budget — continue?", "Waiting for the sub-agents to finish…", "A sub-agent stopped at its own budget — continue?", "You paused this run", "Waiting for you to clarify the goal in the chat", "Waiting for your answer in the chat".

## 3. Muted note lines (conditional, stacked, small)

- "This run's files are still in its workspace." (publish offer)
- Publish outcome: "Published N file(s)." / "N file(s) were left alone because they changed while the run was working." / publish-failed note.
- "Output is on branch {branch}" (worktree mode only).
- "Pause the run to change its plan." — shown exactly while the Pause button is shown.
- Result/error of the last plan mutation (e.g. "A step needs a title.", "The plan can only be changed while the run is paused.").
- Refused-pause note ("This run could not be paused just now… try again.").

## 4. Steering note (only while Continue is offered)

Label "Note for the rest of this run" + multiline text input (placeholder: "Optional — for example, keep the summary under 200 words") + a scope footnote ("sent with every remaining step of this continuation… not saved, does not survive a restart"). The note travels with the resume when Continue is pressed.

## 5. Plan / step list — the signature element (middle of the screenshot)

One row per plan step, in order; rows update in place live (the running highlight moves, reorders move rows, edits rewrite titles without rebuilding the list).

Per row:
- **Status icon + color**: Pending = muted hollow circle; Running = accent sync icon **and a subtle row background highlight**; Done = green check circle; Failed = red error circle; Skipped = muted dismiss circle. Skipped rows stay in the list.
- **Optional persona avatar** (small emoji avatar with an accent ring) when the step was delegated to a named persona; absent otherwise.
- **Step title**, single-line ellipsised.
- **Per-step token count**, right-aligned, subtle: "10,230 in".

**Plan mutation (only while the run is Paused):**
- While the run is live the per-row action group is hidden entirely (the "Pause the run to change its plan." note explains why).
- While paused, each not-yet-settled row shows five compact buttons: **Edit step / Insert step below / Move step up / Move step down / Skip step**. Settled rows (done/failed/skipped) keep the button row but disabled, so layout doesn't shift.
- **Edit is inline, never a dialog** (the panel lives inside a chat): the row's title swaps to two inputs — short title, and multi-line "what the assistant should do" (this is what the model actually receives) — plus Save/Cancel.

## 6. "Tool activity" expander — audit trail (bottom of the screenshot)

- Collapsed by default; re-read from the store on **every** expand (so it's fresh even mid-run).
- Rows are **metadata only** (deliberately no file paths, args, or results): `time · "Step N" (attribution, only if that step still exists in the plan) · tool name (git_init, write_file, …) · optional "failed" suffix when the call errored · right-aligned decision`.
- **Decision labels** (five user-facing categories): Auto-approved / Approved / Denied / Blocked / Awaiting approval (+ Unknown fallback). Note the screenshot's mix: a Completed run can still show "Awaiting approval" rows for calls that parked the run.
- Distinct sub-states, never conflated: **empty** ("No tool decisions were recorded for this run.") vs **read failed** ("The tool activity for this run could not be read.") vs **truncated** note when the trace hit its 500-event cap ("Trace shortened — only the first N decisions…").

## 7. "Sub-agents" expander (hidden on ordinary runs)

- Only when the run delegated work to child runs; shows an "N of M finished" count line.
- One collapsible row per child: state chip (same palette as the header), the child's goal as title (ellipsised), token count. Expanding loads **that child's own** tool-activity trace (same row shape minus the step column), with its own empty/read-failed states. Traces are never merged across runs.

---

## State matrix (what a prototype should cover)

| State | Spinner | Chip color | Header actions | Extra regions |
|---|---|---|---|---|
| Planning | on | default | — | activity line "Building a plan…" |
| Running | on | default | Pause | activity line (step title / waiting-for-approval) |
| Delegating | on | default | Pause | activity line re: sub-agents |
| Waiting for you | off | accent | Continue | nudge box; budget activity line |
| Paused | off | accent | Continue | nudge box; per-row plan-mutation buttons; "you paused" line |
| Completed | off | green | (Publish if unpublished files) | — |
| Completed, truncated | off | muted + reason chip | same | reason chip |
| Failed | off | red | (Publish if unpublished files) | publish notes |

Plus orthogonal overlays: persona avatars on delegated steps; inline step editor open; tool-activity expander open (empty / populated / truncated / read-failed); sub-agents expander with one child expanded.

**Behavior notes for the prototype.** The card is persistent (remains after completion, reappears from chat history) and live-updates while attached. All editing is inline — no modal dialogs. Buttons disable while their action is in flight. Token/time figures tick live during a run. Everything user-content-shaped (step titles, goals, nudge text) is display-only and untrusted-length: plan for ellipsis/wrapping.
