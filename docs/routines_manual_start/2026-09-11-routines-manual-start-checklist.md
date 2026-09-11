# Manual routines + run-from-chat — checklist

**Status:** A and B landed 2026-09-11, gate green (7159 tests, Failed: 0), 0 warnings Debug + Release; C2 smoke and C3 release notes open · **Owner:** Marco Altmann · **Written:** 2026-09-11
**Origin:** [2026-09-11-manual-routines-and-run-from-chat.md](2026-09-11-manual-routines-and-run-from-chat.md),
which holds the grounding, the seven settled decisions and the file-by-file edits. This file is the tracking
surface only — tick each box in the commit that lands it. Step ids mirror that plan’s edit-table rows; the
test and close-out steps (A13, B6, C1–C3) have no edit row of their own.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little
standalone value, unblocks a High.

## Groups

| Group | Covers |
|---|---|
| A | `RecurrenceType.Manual` — a routine that never fires on its own |
| B | `run_routine` — the at-command door that starts one properly |
| C | Close-out: gate, smoke, release notes |

## Decision gates

| # | Question | Answer | Blocks |
|---|---|---|---|
| Q1 | Does a routine started from chat run **detached** (its own chat, run panel, toast) or inside the current chat? | **CLOSED 2026-09-11 (owner): detached.** It is the only shape that carries the routine's working directory, agent shape and grants; in-chat would mean re-hosting the live chat's working dir, persona and mode. Confirm before B1 lands — reversing it afterwards rewrites all of B. | B1–B6 |
| Q2 | May `run_routine` take an extra instruction ("start it, but for Q3")? | **No in v1** — the stored `Query` runs verbatim. Parameterised templates need their own design. | B1, B2 |
| Q3 | Does a manual routine retire after five consecutive failures, like a recurring one? | **CLOSED 2026-09-11 (owner): no — exempt it** (plan D7). Retirement drops it out of `GetActiveAsync`, so the template vanishes from the `@Routine` picker and from `run_routine`'s name resolution. Accepting retirement instead is viable — **Enable** restores it — but must be a deliberate answer. | A12 |

## A — manual recurrence

- [x] **A1 — Append `Manual` to `RecurrenceType`.** Add the member after `Yearly` in
  `src/Pia.Wpf/Models/Reminder.cs:3` with the one-line append-only note the sync wire requires.
  *Deps:* — · *Effort:* `XS` · *Value:* `Enabler`
- [x] **A2 — Teach the calculator that `Manual` never fires.** Add
  `RecurrenceCalculator.Never = new DateTime(9999, 1, 1)` and an explicit `Manual` arm before the default
  arm, which otherwise falls through to daily.
  *Deps:* A1 · *Effort:* `XS` · *Value:* `Enabler`
- [x] **A3 — Guard the due query.** Add `AND Recurrence <> 'Manual'` to `GetDueJobsAsync`
  (`ScheduledJobService.cs:128`) so a hand-edited row cannot arm a template.
  *Deps:* A1 · *Effort:* `XS` · *Value:* `High`
- [x] **A4 — Refuse `Manual` for reminders.** `ReminderService.CreateAsync`/`UpdateAsync` reject it; the
  enum is shared and a reminder has no manual door.
  *Deps:* A1 · *Effort:* `XS` · *Value:* `Med`
- [x] **A5 — Filter `Manual` out of reminder tool input.** Both `Enum.TryParse` sites in
  `ReminderToolHandler.cs:135,186` treat it as unrecognised, so a model asking for a "manual" reminder
  cannot silently get a daily one.
  *Deps:* A1 · *Effort:* `XS` · *Value:* `Med`
- [x] **A6 — Hide the schedule fields for a manual routine.** Add `EditorWantsTimeOfDay`, raise it from
  `OnEditRecurrenceChanged`, gate the time clause of `CanSave` on it, and skip `SaveAsync`'s time parse so
  an empty hidden box cannot refuse the save.
  *Deps:* A1 · *Effort:* `S` · *Value:* `High`
- [x] **A6b — Stop the editor seeding days from the year 9999.** Fall back to `DateTime.Now` at
  `RoutinesViewModel.cs:934-936` when the row's `NextFireAt` is the never-sentinel.
  *Deps:* A6 · *Effort:* `XS` · *Value:* `Med`
- [x] **A7 — Render "never" as an em dash.** Extend `NextFireAtToShortStringConverter` and point the detail
  pane's Next-run text (`RoutinesView.xaml:453`) at it instead of `StringFormat=g`.
  *Deps:* A2 · *Effort:* `XS` · *Value:* `High`
- [x] **A8 — Localize the new recurrence label.** `Settings_ScheduledJobs_Recurrence_Manual` in the en, de
  and fr resx files; never `Designer.cs`.
  *Deps:* A1 · *Effort:* `XS` · *Value:* `High`
- [x] **A9 + A10 — Tell the model manual routines exist.** Extend the `create_scheduled_research` description and
  the scheduled-research plugin's `systemPromptAddition` (`BuiltInPluginDefaults.cs:82`).
  *Deps:* A1 · *Effort:* `XS` · *Value:* `Med`
- [x] **A11 — Let the AI draft propose a template.** Add `manual` to the recurrence vocabulary the draft
  prompt enumerates (`TextOptimizationService.cs:212`), which `:278` already parses back by name.
  *Deps:* A1 · *Effort:* `XS` · *Value:* `Med`
- [x] **A12 — Exempt a template from failure retirement.** Per gate Q3, skip the `Status = 'Failed'` flip in
  `MarkRunFailedAsync` for `Manual`; the failure counter still climbs.
  *Deps:* Q3, A1 · *Effort:* `XS` · *Value:* `High`
- [x] **A13 — Pin the manual-routine invariants.** Tests 1–8 and 11 of the plan's test plan, of which test 3
  (a completed manual run leaves the row `Active` at `Never`) protects the template property.
  *Deps:* A2, A3, A6, A12 · *Effort:* `S` · *Value:* `High`

## B — run a routine from chat

- [x] **B1 — Add the `run_routine` tool.** Inject `IScheduledJobRunner` into `ScheduledJobToolHandler`,
  declare the tool, and return a pending `ScheduledJobToolCall` whose `Execute` calls `RunNowAsync`.
  *Deps:* Q1 · *Effort:* `S` · *Value:* `High`
- [x] **B2 — Resolve by name, refuse honestly.** Name-first resolution with id fallback, a distinct sentence
  per `ScheduledJobRunNowResult`, and an outright refusal for a `MeetingAttendance` routine.
  *Deps:* B1 · *Effort:* `S` · *Value:* `High`
- [x] **B3 — Wire the tool into the gate.** Add it to the `Routine` at-command mapping
  (`AssistantPromptComposer.cs:225`) and to `AuthorityAuthoringTools` (`ToolPermissionService.cs:107`).
  *Deps:* B1 · *Effort:* `XS` · *Value:* `High`
- [x] **B4 — Say where the answer lands.** One sentence in the plugin's `systemPromptAddition`: the routine
  runs with its own working directory, persona and grants, and replies in a new chat.
  *Deps:* B1 · *Effort:* `XS` · *Value:* `Med`
- [x] **B5 — Give the action card a verb.** Optional `ActionCardBuilder.cs:174` row plus three resx keys;
  without it the card falls through to the generic processing label.
  *Deps:* B1 · *Effort:* `XS` · *Value:* `Med`
- [x] **B6 — Pin the tool's behaviour.** Tests 9–10: resolution, each refusal, membership in the `Routine`
  tool set, and absence from an unattended turn.
  *Deps:* B2, B3 · *Effort:* `S` · *Value:* `High`

## C — close-out

- [x] **C1 — Clear the gate and the warning bar.** Built exe with no filter at `Failed: 0`, plus
  `dotnet build -t:Rebuild -v:n` at `0 Warning(s)` in Debug and Release.
  *Deps:* A13, B6 · *Effort:* `XS` · *Value:* `High`
- [ ] **C2 — Smoke it in the real app.** A manual routine survives a restart without firing, and "start it"
  on an `@Routine` chip produces a detached run in the routine's own working directory.
  *Deps:* C1 · *Effort:* `XS` · *Value:* `High`
- [x] **C3 — Curate the release notes.** Rewrite `docs/release_notes/RELEASE.md` in place for both halves,
  per the format rules in `docs/release_notes/README.md`.
  *Deps:* C2 · *Effort:* `XS` · *Value:* `Med`

## Not yet planned

- Parameterised templates — an extra instruction merged into the stored `Query` at start time (Q2).
- A deterministic "Start" affordance on the `@Routine` picker row, bypassing the model entirely.
- Promoting an existing chat or agent run into a routine template.
- User-facing documentation for manual routines in the Pia.Docs guides (separate repo).

## Suggested order

1. **A1 → A2 → A3.** The invariant, cheapest and decisive: after these a manual routine cannot fire on any
   path, and everything else is presentation.
2. **A4, A5.** Close the shared-enum hole in reminders while the enum change is fresh.
3. **A12 → A13 (tests 1–5, 11).** Pin the service-level invariants before any UI leans on them.
4. **B1 → B2 → B3 → B6.** The user-visible half, independent of A; it can ship on its own if A slips.
5. **A6 → A6b → A7 → A8 → A13 (tests 6–8).** The editor slice, which is where a half-finished A is most visible.
6. **A9 + A10, A11, B4, B5.** Prompt and card polish, once the behaviour underneath is settled.
7. **C1 → C2 → C3.**
