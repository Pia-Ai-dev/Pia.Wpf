# Brief — Next Implementation Batch (Hermes Follow-Up)

**Status:** ready to execute. Self-contained: paste §0 into a fresh session and it has everything.
**Owner:** unassigned. **Written:** 2026-08-22.
**Origin:** the rows left over after batch 1 of
[`2026-08-22-hermes-followup-checklist.md`](2026-08-22-hermes-followup-checklist.md).

---

## 0. The prompt

> Read `docs/hermes_checkup/2026-08-22-next-batch-brief.md` and implement the batch it scopes, in one
> workflow following our schema: plan → implement → build gate → simplify (sonnet) → review → fix →
> finalize. The A track is on hold; do not touch it.

Everything below is what that brief says.

---

## 1. Where the repo is

Branch `feature/speaker-attribution`. Batch 1 landed as five commits, newest first:

| Commit | Rows |
|---|---|
| `b7aa30bb` | D1 — tour target collector + `Ctrl+Shift+F12` debug dump |
| `e6b145df` | C1–C3 — `RoutineBlueprint`, catalog with `topic-digest`, card list in `RoutinesView` |
| `8b912124` | B1, B2, B5 — synthetic transcripts, corpus extraction script, compaction pinning tests |
| `0e825f43` | A5 — `ArtifactRef` persisted into `AgentSteps.ExtraJson`, seeded on resume |
| `821adcfc` | The A1 log-collection runbook |

**Nothing in batch 1 has been executed as a test.** 63 test methods compiled on macOS and never ran —
`net10.0-windows` cannot execute there. If a Windows `dotnet test` run has happened since, its result
outranks anything written here.

### The A track is on hold — do not touch it

A1 is a decision gate that closes A2–A4, A6 and A7. Its first reading came off one client's logs:
23 declarations over 7 verifier runs, 57% `found`, 43% `not a file reference`, **zero `NOT FOUND`**.
That refutes *"already high"* but it is one machine over three days on code-shaped tasks. More logs
are being collected per
[`2026-08-22-a1-log-collection-runbook.md`](2026-08-22-a1-log-collection-runbook.md).

**Until that sample lands, A2, A3, A4, A6 and A7 are out of scope.** So is review recommendation #13
(planner discipline), which is A4 wearing a different hat. A5 already shipped and is done.

---

## 2. Scope

Three rows. Two are `S`; the third is `M` only because it is the same `S`-shaped task seven times,
which is exactly what a workflow fan-out is for.

### C4 — the remaining seven blueprints *(High)*

Deps C3, satisfied. Source: `2026-08-22-routine-blueprints-plan.md` §7, §8, §9.

`topic-digest` shipped alone to prove the shape. Seven remain:

| Key | Kind | Drives |
|---|---|---|
| `morning-brief` | AgentTask | Todos due today + active reminders |
| `evening-winddown` | AgentTask | Tomorrow's todos + reminders |
| `weekly-review` | AgentTask | Completed vs open todos, stalled Kanban cards, the week's vault notes |
| `competitor-watch` | Research | Named companies, material news only |
| `meeting-followup` | AgentTask | New vault transcripts → action items |
| `bills-renewals` | AgentTask | Recurring-payment heads-up |
| `habit-checkin` | AgentTask | Recurring nudge + reflection |

Follow `topic-digest` exactly: `src/Pia.Wpf/Models/RoutineBlueprint.cs` holds both the record and the
catalog, keys are `Routines_Blueprint_{Key}_Title` / `_Description` in `ViewStrings{,.de,.fr}.resx`,
and the card AutomationId is `Routines_Blueprint_{Key}`.

Binding constraints:

- **Every blueprint declares the narrowest `GrantedTools` set that makes it work, and the PR says why.**
  `topic-digest` ships `GrantedTools: []` because reads run ungranted and web search is a provider
  capability. A blueprint that only reads gets nothing. This is the security half of the feature
  (plan §8) and the reason it exists at all — over-granting by checklist is what it replaces.
- **No second job engine.** Every path ends at `IScheduledJobService.CreateAsync`. If a blueprint needs
  a field that method does not take, add the parameter there.
- **`ScheduledJobKind` is append-only** — use the existing two.
- **Keys are persisted-adjacent**: add one, never rename one.
- **de/fr must be real translations** matching the register of the existing entries, not English with
  an umlaut.

Fold in **review recommendation #15** (*"meeting → action-items prompt, evidence-first"*, `S`) as
`meeting-followup`'s `QueryTemplate` rather than doing it twice: state transcript completeness and
low-confidence spans **before** extracting anything. That framing comes from the speaker-attribution
work and it is the whole point of the row.

### D7 — AutomationId gap-fill *(Med, and it unblocks the A track's data collection)*

Deps D1, satisfied. Source: `2026-08-22-guided-tour-tool-plan.md` §8 step 7.

The checklist calls this `S`. It is scoped as `S` per view, and **ten views have zero AutomationIds**:

```
0  AssistantView.xaml          0  RemindersView.xaml
0  DirectTranscriptionOverlay  0  TodoPanelControl.xaml
0  FirstRunWizardWindow.xaml   0  TodoView.xaml
0  HistoryView.xaml            0  VaultView.xaml
0  OptimizeView.xaml           0  VoiceModeOverlay.xaml
```

Against `26 RoutinesView` · `12 NavigationSidebarView` · `7 SettingsView` · `7 MeetingAttendeeOverlay`
· `4 AssistantHistoryView` · `1 AssignmentsView`.

**Start with `AssistantView.xaml`.** It is the app's main surface and it is currently unaddressable:
the Chat/Agent lever and the Run-in-background button can only be reached by *localized name*
(`type=RadioButton[name='Agent']`), which forces the UI to English and which `winwright heal` cannot
repair. That is what blocks the A1 collection runbook, so this row pays into the thing everything else
is waiting on.

Then `TodoView`, `VaultView`, `RemindersView`, `HistoryView` — the surfaces a "where do I…" question
actually targets.

Rules:

- Follow `docs/ui_automation/ui-automation-playbook.md`'s existing naming convention. Read it; do not
  invent a scheme. Update it in the same change with the ids you add — it is the registry of record.
- Interactive and identity-bearing elements only. Do not spray ids across every `Border`.
- An id is a compatibility surface once a UI script or a tour references it. Name it for what the
  control *is*, not where it sits.
- **This is not D2/D3.** No adorner, no `ITourToolHandler`, no tool registration, nothing the model can
  reach. D-Q1 (canned tour vs. generic tool) is still an open gate.

### #11 — per-routine persona and reasoning effort *(Med)*

Source: review recommendation #11, `S`. No plan doc — the planning phase writes what it needs.

`ScheduledJob` carries `ProviderId` but no persona and no reasoning effort, so every routine runs on
the default assistant at the default effort. `src/Pia.Wpf/Services/StepPersonaResolver.cs` already
exists and `Pia.Models.ReasoningEffort` already has the enum; the routine simply cannot reach either.

It lands on the surface C1–C3 just extended, and it composes with C4: a blueprint is the natural place
to carry a sensible default persona and effort per automation type.

Watch: `ScheduledJob` crosses the sync wire. Decide deliberately whether each new field syncs, and
follow the precedent already set by `QuietOnSuccess` (local-only) versus `GrantedTools` (synced).

---

## 3. Decide before implementing

**C4 before C5, or C5 first?** The plan's §10 orders C4 then C5, and the checklist's suggested order
agrees. But four of the seven — `competitor-watch` (which companies?), `bills-renewals` (what?),
`habit-checkin` (which habit?), and arguably `weekly-review` — have a genuine free-text parameter, and
Tier 0 has nothing to fill it with. `topic-digest` resolved this by naming its topic outright
("artificial intelligence") rather than leaking a literal `{topic}` into a job Query. Shipping C4 as
planned means **four more opinionated hardcodes** that C5 then has to unpick.

The planning phase must answer this explicitly and record why. Either is defensible:

- **C4 as planned** — seven cards land now, users edit the query in the editor that opens prefilled
  anyway, and C5 upgrades them later. Faster to visible value, matches the plan.
- **C5 first, then C4** — the seven land with real slots and no hardcodes to unpick. `M` more work up
  front, and it delays the user-visible half.

Do not silently pick one. Say which, and why, in the plan.

---

## 4. Constraints

Read `CLAUDE.md` in full first. The ones that bite hardest here:

- **Comment discipline.** Default to no comment. A surviving comment or `<summary>` gets one short
  line, never a `<para>`, never a restatement of what the code does. **Never cite a task, batch, spec,
  plan or ticket ID in source or XAML** ("C4", "§7", "per the plan"). That belongs in the commit
  message. Existing files violate this; do not imitate them.
- **Privacy logging.** A rendered `QueryTemplate` and an element `Name` from the visual tree are user
  content. `SensitiveDebug` / `SafeUrl.Format`, never a bare `LogInformation`.
- **Localization.** Every new key exists in en, de and fr. A key missing from de/fr silently ships
  English. Check whether `ViewStrings.Designer.cs` is a checked-in artifact that needs the key too.
- **Namespaces are `Pia`, not `Pia.Wpf`.** MVVM: `[ObservableProperty]` / `[RelayCommand]`, no logic in
  code-behind.
- **Architecture.** `tests/Pia.Wpf.Tests/Architecture/` holds NetArchTest layering and naming rules. A
  new type in the wrong layer compiles and fails there.

### Build

`Directory.Build.props` sets `TreatWarningsAsErrors=true`, so a warning is a build failure. MSBuild
output on the dev Mac is German-localized (`Warnung(en)` / `Fehler`).

```bash
dotnet build -t:Rebuild -p:EnableWindowsTargeting=true -v:m 2>&1 | tail -40
dotnet build -t:Rebuild -c Release -p:EnableWindowsTargeting=true -v:m 2>&1 | tail -40
```

The bar is `0 Warnung(en)` / `0 Fehler` in **both**. **Never run `dotnet test` on macOS** — compiling
the test project is the check available there. Never run two builds concurrently.

Do not commit unless asked. Batch 1 was committed one group per commit, with each group's checklist
ticks riding in its own commit.

---

## 5. Workflow shape

Same schema as batch 1, which produced 13 confirmed findings from 25 raised:

1. **Plan** — one agent per row, grounded by actually reading the source, returning a structured plan.
2. **Implement** — parallel over **disjoint file ownership**. C4 fans out one agent per blueprint; D7
   one agent per view. Give every agent an explicit list of files it may touch and a handoff channel
   for anything outside it.
3. **Build gate** — serialized, single agent, Debug then Release, drive to zero.
4. **Simplify** — sonnet, one per area, quality only. Comment discipline is the most-violated rule;
   assume there are violations.
5. **Review** — five dimensions (correctness · CLAUDE.md conformance · tests · integration and
   architecture · scope and dead code), each finding then killed or confirmed by **two independent
   refuters** with different lenses.
6. **Fix** — apply what survived, rebuild.
7. **Finalize** — tick the checklist rows actually delivered, verified against `git diff` rather than
   trusted; final Debug and Release rebuild.

Two things batch 1's review caught that are worth pointing the reviewers at again: a new test that
NRE'd on an unstubbed substitute and would have broken the `dotnet test` gate on Windows, and a
`Focus()` call inside `IsVisibleChanged` that silently does nothing.

---

## 6. Done means

- C4: seven blueprints, each with en/de/fr strings, a justified `GrantedTools` set, and a card that
  opens the existing editor prefilled.
- D7: `AssistantView` addressable, plus at least the four surfaces named above, and
  `docs/ui_automation/ui-automation-playbook.md` updated in the same change.
- #11: a routine can carry a persona and a reasoning effort, the sync decision is recorded, and the
  editor exposes both.
- Both configurations rebuild clean, checklist ticked, nothing committed without being asked.
- A report naming what still needs a human on Windows — the `dotnet test` gate above all.

---

## 7. Swaps, if this batch is too big or too small

**Drop first:** #11. It is the least coupled of the three.

**Add, all `S` with deps satisfied:** review #7 (global pause — tray toggle plus a flag checked by the
scheduler tick and the headless launcher, never kills in-flight work), #8 (repetition guard before the
truncated-response continuation nudge, ~95 lines, no dependencies), #10 (mark iteration-truncated
child results so a parent can tell "finished" from "ran out of budget").

**The bigger prize, deliberately not in this batch:** review #2 (error layer + recovery actions on the
failure card, `M`) and #3 (Send Diagnostics — consented, redacted log bundle, logs only, never
transcripts, `S–M`). The checklist says take them together, and it is right: #2 names which layer
broke, #3 is the action the same card offers when naming it is not enough. Neither has a plan doc, so
that batch should open with one. #3 has a second argument behind it now — the A1 measurement needed
logs hand-copied off a Windows box, which is precisely the workflow #3 productizes.
