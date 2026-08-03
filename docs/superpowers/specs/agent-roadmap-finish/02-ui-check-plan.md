# The manual round — what to check in the UI now

This is **Rank 1** in [`../agent-roadmap/00-OVERVIEW.md`](../agent-roadmap/00-OVERVIEW.md), and since Batch 08
shipped it is the *only* ranked item: nothing is queued behind it. Every check here is one a green suite cannot
substitute for. Code still owed is in [`01-code-checklist.md`](01-code-checklist.md).

**Sequenced by equipment, not by batch**, because equipment is what gates the round. Eight sessions; the first
three need nothing you do not already have. IDs trace back to the overview's "Opened by" sections
(`B04-` Batch 04 · `B03-` Batch 03 · `B05-` Batch 05 · `P3-` Phase 3 · `B09-` Batch 09 · `B08-` Batch 08 ·
`H7-` the hermes §7 sweep) — file a failure by ID and the reasoning is one grep away.

| Session | Equipment |
|---|---|
| S1 Plain app | nothing new |
| S2 Locales | the app at its smallest width, in EN/DE/FR |
| S3 Restart round trips | nothing new |
| S4 Interactive run | one live provider, a real model streaming |
| S5 Workspace & publish | a real git repo **and** a plain non-repo folder |
| S6 MCP | one real MCP server (stdio) |
| S7 Unattended | a real 30-second tick, a real clock, a process kill |
| S8 Two devices | a second machine on a different build — **separately deferrable** |

---

## S1 — The plain app, no new equipment

- [ ] **H7-4 — all eight `AutoWireViewModel` views still show their data.** They went from **inert to live** in
  the 2026-08-03 sweep (the attached property resolved `null` for all eight before it). Enumerated by grep rather
  than copied — `nav:ViewModelLocator.AutoWireViewModel="True"` in `src/Pia.Wpf/Views/`: **AssistantView**,
  **AssistantHistoryView**, **HistoryView**, **MemoryView**, **OptimizeView**, **RemindersView**,
  **SettingsView**, **TodoView**. Note the overview's own list names seven and **omits `AssistantHistoryView`** —
  open that one too. This is a real gap, not paranoia: the STA host cannot raise a real `Loaded`, so the one step
  that fires the new guard in production is the step the suite synthesises.
- [ ] **P3-fix-i — message avatars fill their 28×28** in an ordinary chat *and* in the history inspector. The
  accent ring was re-implemented as an overlaid sibling `Border`; nothing automated can see it.
- [ ] **P3-cons-v(b) — an ordinary run shows NONE of the three note lines.** Quiet-on-an-ordinary-run is the
  property that makes the notes worth reading at all.
- [ ] **B03-3 — the run panel does not stutter** during a run with many tool calls. The store's lock split is
  reasoned, never measured, and no test can go red for it.
- [ ] **B14/B15 residue — click through the first-run wizard once.** `FirstRunWizardWindow` and everything under
  `Views/WizardSteps/` cannot be parsed under the test host, so a misspelled binding path there is silent at
  0 warnings.

## S2 — Three locales at the app's smallest width

The largest single cluster on the round, and it needs only pixels. `LocalizationTests` proves **key parity**;
parity is not rendering, and clipping is a layout property no test here measures.

- [ ] **B08-7 — the 25 strings live steering added**, in all three files. Worst cases named rather than guessed:
  the DE button header (`Wird pausiert…` + `Fortsetzen` can co-occur); `Run_Nudge_Scope_Note`, the longest new
  string in any locale (FR 200 chars, DE 187); `Run_Pause_Error_Refused` (DE 138) as a muted note under the
  header; the two inline-editor placeholders; and the five row verb tooltips inside a step row that already
  carries a glyph, an avatar, a trimmed title and a token count. Read the count off the resx diff, not off this
  line.
- [ ] **H7-1 — the tool-approval card's decision bar now has FOUR buttons** (`Decline` · `Allow once` ·
  `This session` · `Always allow`) where it had three. Confirm the bar does not wrap or clip at the smallest
  width, in every language. First time this bar has had to lay out four.
- [ ] **B04-6 — the agent-settings section**, whose longest string got *longer* in the fix pass; the German
  label in particular.
- [ ] **B03-5 — the "Tool activity" decision column**, now five columns instead of three, with the German
  decision label the long one.
- [ ] **P3-§8-9 — the publish offer, the persona-roster surface, and the "output is on branch X" line.**
- [ ] **P3-fix-ii — four more pause-reason keys**, plus the German `WaitingForChildren` chip, changed from
  "Delegiert" to "Verteilt Arbeit" so a participle does not sit next to a lit spinner reading as *finished*.
- [ ] **B09-b — the scheduled-jobs section**, whose German strings are long. Batch 14 renders this section in a
  test and proves nothing whatever about clipping.
- [ ] **P3-cons-iii/iv (locale halves) — the conflict result note beside the standing offer line, and the
  branch/publish line**, both in DE and FR.

## S3 — Restart round trips

Each of these is a *persistence* check. Batch 13 automated the silent half of the first two (the binding paths);
what is left is disk.

- [ ] **B04-1 / B05-toggle — toggle it, restart, confirm it stuck.** Both agent CheckBoxes (the autonomy toggle
  and `AgentPlanReasoningTurnEnabled`). There is no `AssistantSettingsViewModel` test at all, so this is the
  only coverage the wiring has.
- [ ] **P3-§8-6 — the persona roster persists across a restart**, and a plan really does assign different
  personas to different steps with the right provider on each.
- [ ] **B03-4 — the retention prune actually runs.** Set retention to 1 day, hand-age a row, confirm the
  `Information` line reports a **non-zero** delete.

## S4 — One interactive run against a live provider

Everything here needs a real model mid-stream; every automated fact drives a fake client.

- [ ] **B04-2 — with the autonomy setting ON**, a covered write shows a **pre-resolved accepted** card — never
  *nothing*; silence would mean the card-before-execute ordering was lost — while `delete_file` still shows a
  live Decline/Allow-once pair with **no** Always-allow.
- [ ] **B04-3 — the `scheduled-research` card**: titled "Create Scheduled job", **two** buttons not three,
  detail rows as label/value pairs.
- [ ] **B08-1 — pause mid-step and confirm the run RESUMES and completes** rather than settling cancelled. The
  step must be genuinely mid-provider-call: that is the only condition under which the executors' cancel arms
  behave as they do in production. This batch shipped exactly this defect on three paths.
- [ ] **B08-2 — pause a run SITTING ON AN ACTION CARD** (a `write_file` awaiting a click) and confirm it pauses
  rather than declining-and-continuing. **The highest-value item on the whole round, and its obstacle is that it
  needs a human deliberately NOT to click.** The release path is a `Decline` that *continues* the exchange.
- [ ] **B08-5 — edit, insert, reorder and skip a pending step of a PAUSED run**, and watch the run honour each
  on its next step. Judge the ergonomics: the drag-free reorder, and the cosmetic hoist of a `Skipped` row above
  the still-pending tail (deliberately not chased — see "not bugs" below).
- [ ] **B08-6 — a nudge visibly changes the next step's behaviour, and is visibly gone after a second resume.**
  The sequence is pause → type the note → Continue → the next step differs.
- [ ] **B05-live — a two-call plan with a reasoning effort configured still validates** (the run goes `Planned`,
  not SingleTurn-degraded) and logs its doubled-cost `Information` line **exactly once** with the toggle ON and
  **not at all** with it OFF.
- [ ] **H7-5 — a run panel showing a FAILED step mid-plan still reads as a run in progress**, not as a crashed
  one, and the verifier digest's `[ok, declared]` / `[ok, unconfirmed]` / `[failed, observed]` tag legend is
  comprehensible to someone who has never read this folder.
- [ ] **P3-§8-5 — an interactive run's file chips**, clicked *during* the run and again *after* promotion —
  both phases of the resolve-on-open fallback.
- [ ] **B08 negative control — pause, then do nothing for a minute.** The run should be genuinely idle and the
  button honest. The review asked for this explicitly and no fact reaches it.

## S5 — Workspace, promotion and publish

Needs a real git repo *and* a plain folder. Batch 06 is the first change on this branch a user could notice
without opening a settings page: it relocates where every unattended run's files land.

- [ ] **P3-§8-1 — a real headless run writes into an isolated workspace and promotes on success.** The suite
  proves promotion copies/retains/tears down against a fixture root, not that a user's deliverable appears where
  they expect it.
- [ ] **P3-§8-2 — worktree mode against a real repo**: the run branch exists, the agent's work is **on it**, the
  working tree is untouched, and `git worktree list` afterwards shows no stale registration. Check the commit's
  **author**, and that `--no-verify` did not surprise a repo with hooks — promotion commits app-side, because the
  model cannot commit for itself.
- [ ] **P3-§8-3 — copy mode against a non-repo folder** (the ordinary case), plus the degrade path with git
  absent.
- [ ] **P3-§8-4 — a failed run's publish offer**: decline it and confirm the workspace is retained and later
  swept; accept it and confirm the files land. Then a **copy-mode conflict on a *successful* run**, where the
  workspace is now retained and Publish re-counts the conflict to render it.
- [ ] **P3-cons-iii — a copy-mode conflict, published manually.** Read the panel as a whole: the result note
  ("N published, M left alone…") and the still-standing offer line render **together, deliberately**. The
  question is whether they read as one coherent statement or as a contradiction. Confirm the workspace really is
  still on disk afterwards.
- [ ] **P3-cons-iv — a worktree run whose run-branch commit FAILED.** The panel must name **no** branch and show
  the Publish button; clicking it **retries the commit**, and on success the branch line appears where the offer
  was. Also see a FAILED worktree run offer Publish where it used to name an empty branch. *This is not covered
  by the branch-line test* — that drives a flag on a constructed ViewModel and touches neither promotion nor the
  retry.
- [ ] **P3-cons-v(a) — the automatic conflict note.** A background copy-mode run that hits a conflict shows the
  conflict count on completion with **no click**.

## S6 — One real MCP server

`ToolClass.External` is **only ever faked** in the suite — nothing routes through a live
`McpPluginToolHandler`. Two batches list this for the same reason.

- [ ] **B04-7 — an external tool prompts with the full triad, and Always-allow persists.**
- [ ] **B04-7(b) — the most valuable single check on the round:** with a server exposing a tool named exactly
  `create_todo`, confirm voice mode **refuses** it. That was a real must-fix and its whole chain is faked.
- [ ] **B04-5 — voice mode with the agent-write setting OFF declines and names the chat window.** This is the
  batch's one user-facing *removal*: voice mode used to execute every write with no gate at all. A user who
  relied on it will read it as a regression — it belongs in the release notes.
- [ ] **B03-2 — the same server, seen from the trace side:** an external call produces a "Tool activity" row
  with the right class and decision.
- [ ] **H7-1(b) — the "this session" tier behaves:** it survives the rest of the session, and it is **never
  persisted** — restart and the grant is gone.

## S7 — Unattended: a real clock, a real tick, a real process death

- [ ] **H7-2 — an unattended run that hits an un-granted promptable capability PARKS and shows a Continue card**
  where it used to silently deny. **The sharpest item on the round:** a park a user cannot interpret is worse
  than the deny it replaced, because the run is now stuck instead of finished. Judge whether the card explains
  *which* capability is being asked for and *why the run stopped*, on a run you did not watch start.
- [ ] **B08-3 — pause a scheduled run and confirm another due job dispatches while it sits paused**, with a real
  Flow card and the schedule visibly advanced in Settings.
- [ ] **H7-3 — two due jobs, the first long-running, both proceed**, over a real 30-second tick — and the
  success/failure toasts are still attributable to the right job now that two can be in flight.
- [ ] **H7-6 — what the Scheduled-jobs row SHOWS while a one-off reads `Status == Completed` for the duration of
  its run.** Deliberate (dispatch is a one-off's settle), and this is the visible half of **T0-1**, whose fix is
  deferred. Observe the symptom; do not file the state itself.
- [ ] **B03-1 — a real headless run's trace across a restart**, the one that matters most: launch a background
  run, let it park at its budget, **quit and relaunch**, click Continue, expand — rows from both segments
  present, in order, with no duplicate `Seq`. The only live proof of the cross-process seeding.
- [ ] **B04-4 — park → flip the autonomy setting → Continue.** The resumed run must **still card every write**:
  the envelope is the run's authority of record, so flipping the setting cannot widen a parked run.
- [ ] **P3-§8-8 — a parent with parallel children**: cancellation cascades, no orphans, the ledger rolls up, and
  the parent survives an app restart in its waiting state.
- [ ] **B08-4 — pause a fan-out parent**, confirm every child *that was pausable* parks and none is orphaned,
  then **kill the app with children at `Paused`, relaunch**, and confirm the startup sweep leaves them and the
  parent's Continue **supersedes** them with a fresh generation. The restart is the half no in-process fact
  reaches.

## S8 — Two devices (separately deferrable)

Needs a second machine on a different build. Do not let it block the round.

- [ ] **B09-c — the owner-mismatch row.** `IsOwnedByThisDeviceAsync` returning false is faked everywhere it is
  tested; one test forces the flag but cannot produce a second device.
- [ ] **B09-unknown-status — a newer peer's `ScheduledJobStatus` ordinal survives a sync round trip** and renders
  inert. The automated fact injects the ordinal directly.
- [ ] **B03-device-local — the "Tool activity" trace is empty on the second device**, and the UI does not
  distinguish "this device recorded nothing" from "nothing happened". Confirm the symptom so it is a known
  boundary rather than a bug report.

---

## Two judgements, not checkboxes

Both need an opinion written down, and neither can ever be "passed". Record the opinion beside the item.

- **J1 — does reason-then-emit actually produce better plans?** (B05.) The suite proves the boosted round
  happens, degrades safely and is paid for. Nothing proves the plans improve — and the ladder collapses, so only
  `High`/`XHigh` is a real boost. The toggle's default is OFF and this judgement is what decides whether it
  changes.
- **J2 — does "this session" read as narrower than "always allow"** to someone who did not write it? (H7-1.)
  A four-way decision bar only helps if the middle tier is legible.

---

## Do NOT file these as bugs — they are the design

A tester who does not know these will report six defects that are settled decisions.

- **There are no steering controls on a *running* run.** All steering is pause-gated (Batch 08 D3/D4), which is
  what removes the mutation-versus-drain race by construction. Same for the nudge box: pause first.
- **"Move up" on the first pending row and "Move down" on the last are enabled and do nothing** — F17, declined
  by name, with three reasons.
- **A `Skipped` row hoists above the still-pending tail** after the next mutation — F15, cosmetic and documented
  at both ends; a skipped step never drains.
- **A child still `Planning` when its parent pauses is left RUNNING**, not cancelled and not paused. The
  achievable guarantee is: no child `Cancelled`, no child stranded non-terminal, and every child that *was*
  pausable reaches `Paused`. Quote that shape, not the tidy one.
- **Continue on a paused parent re-dispatches a FRESH generation** — it does not resume the old children.
- **"Publish files" on a worktree run commits to a branch.** Judge whether the wording reads acceptably in all
  three locales; the behaviour is intended.
- **A worktree run cannot see uncommitted or untracked work** — a worktree starts from a commit. A release note
  is owed (R16); it is not a bug.
- **`WaitingForInput` and `Paused` are different states** and say different things: the system parking a run at a
  budget edge versus the user parking it by hand. A panel saying "stopped at budget" about a hand-paused run
  *would* be a bug.
- **Voice mode declining a write** is the intended removal (B04-5), not a regression.

## How to record a result

Tick the box, or file the failure under its ID (`B08-2`, `P3-§8-4`, `H7-1`). Start from a tree whose gate you
measured yourself — Debug and Release `-t:Rebuild` at 0/0 and the suite at `failed: 0`. If
`AssistantChatConcurrencyTests.DeleteAllAsync_WithAnotherConnectionCommittingThroughout_Completes` fails
**alone**, that is the known intermittent: re-run it isolated to confirm, and it does not fail the gate.
