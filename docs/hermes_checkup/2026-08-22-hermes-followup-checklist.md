# Implementation Checklist — Hermes Follow-Up Plans

Tracking file for the five plans spawned by
[`2026-08-22-hermes-update-review.md`](2026-08-22-hermes-update-review.md). One row per
implementation step. Tick as they land.

| Group | Plan |
|---|---|
| **A** | [Artifact evidence](2026-08-22-artifact-evidence-plan.md) |
| **B** | [Compaction recall](2026-08-22-compaction-recall-test-plan.md) |
| **C** | [Routine blueprints](2026-08-22-routine-blueprints-plan.md) · ordering decision: [C4 before C5](2026-08-22-c4-before-c5-decision.md) |
| **D** | [Guided tour](2026-08-22-guided-tour-tool-plan.md) |
| **E** | [Per-routine persona + reasoning effort](2026-08-22-routine-persona-effort-plan.md) |

**Effort** — `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface
· `L` a week or more, a new subsystem.

**Value** — `High` user-visible improvement or a real risk closed · `Med` worthwhile, not headline ·
`Enabler` little standalone value, unblocks a High.

**The groups are independent bar one step** — `E8` needs C4's blueprints. Every other dependency
below is within-group unless marked otherwise.

---

## Decision gates

Three steps can close work below them. Do not tick their dependants without revisiting.

| Gate | Closes | Question it answers |
|---|---|---|
| **A1** | A2–A4, A6, A7 | Is `ExpectedArtifact` already file-shaped often enough that the probe is fine? |
| **B4** | B6–B10 | Does current compaction lose anything worth acting on? |
| **D-Q1** | D3–D8 | Is the goal onboarding (a canned tour, no LLM) or arbitrary "where do I…" questions? |

---

## A — Artifact evidence

- [ ] **A1 · Read the `probed / declared` ratio off real-run logs.**
  The line already exists in `AgentVerifier.TryBuildArtifactFactsAsync`. No code.
  **First read, 2026-08-22** — 23 declarations over 7 verifier runs on one client: 57% `found`, 43%
  `not a file reference`, **0 `NOT FOUND`**. That refutes "already high", so A2–A4/A6/A7 stay open — but
  it is one machine over three days on code-shaped tasks, too small to tune on. Widen it per
  [2026-08-22-a1-log-collection-runbook.md](2026-08-22-a1-log-collection-runbook.md), then re-read. The
  row to watch is `NOT FOUND`: if it stays at zero, the planner channel cannot produce a negative at all.
  *Deps:* none · *Effort:* **XS** · *Value:* **High** (decision gate)

- [ ] **A2 · Route `ArtifactRef` through the existing artifact probe.**
  `produced: X` becomes `produced: X → found (2.1 KB)` / `→ NOT FOUND`.
  *Deps:* A1 · *Effort:* **S** · *Value:* **High**

- [ ] **A3 · Tests for the self-reported-but-missing case; keep the failure-isolation tests green.**
  *Deps:* A2 · *Effort:* **S** · *Value:* **High**

- [ ] **A4 · Tighten the planner and replan prompt wording** (`AgentPlanner.cs:782`, `:827`) — say what
  checkable means, say to omit the field otherwise.
  *Deps:* A2 (decide after A2's numbers — it may be unnecessary) · *Effort:* **XS** · *Value:* **Med**

- [x] **A5 · Persist `ArtifactRef` into `AgentSteps.ExtraJson` and seed it in `SafeSeedResumeContext`.**
  Fixes the resume asymmetry; also lets the timeline show what each step produced.
  Landed ahead of the A1 gate on purpose: persisting the field is worth doing whichever way A1 reads.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**

- [ ] **A6 · Extract `IArtifactProbe` with a file implementation** — behaviour-preserving refactor of
  today's probe.
  *Deps:* A2 · *Effort:* **S** · *Value:* **Enabler**

- [ ] **A7 · Todo / reminder / vault probes + typed `kind:ref` prefix in the tool description.**
  Widens the evidence surface past the filesystem.
  *Deps:* A6 · *Effort:* **M** · *Value:* **High**

---

## B — Compaction recall

- [x] **B1 · Synthetic transcript generator with planted facts.** Committed; no real user data.
  *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

- [x] **B2 · Corpus extraction script** (`AssistantChatMessages` → JSON fixture, gitignored).
  *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

- [ ] **B3 · Question-bank generator, per-transcript cache, and the verbatim-leak filter.**
  The leak filter is what stops restatement luck from inflating every arm.
  *Deps:* B1 · *Effort:* **M** · *Value:* **Enabler**

- [ ] **B4 · Arms A (uncompacted) + B (current), judge, scorecard writer.**
  First real number. If A scores < 90%, the instrument is broken — fix that before reading anything.
  *Deps:* B2, B3 · *Effort:* **M** · *Value:* **High** (decision gate)

- [x] **B5 · Pin the "user messages are never compacted" invariant with a test** against
  `Microsoft.Agents.AI.Compaction`. Pia pins the head goal and newest instruction; middle user
  messages are not pinned. *(From review rec #5, not the test plan.)*
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**

- [ ] **B6 · Arm C — mechanical anchor index.** Biggest single win in hermes's scorecard.
  *Deps:* B4 · *Effort:* **M** · *Value:* **High**

- [ ] **B7 · Message-level search granularity.** `AssistantChatsFts` is per *chat*, not per *message* —
  either add a message-level index or scope recovery to a direct `AssistantChatMessages` query.
  *Deps:* none · *Effort:* **M** · *Value:* **Enabler** (hard prerequisite for B8)

- [ ] **B8 · Arm D — recovery pointer.** Worth +20 to +43 pts standalone in hermes's run.
  *Deps:* B4, B7 · *Effort:* **M** · *Value:* **High**

- [ ] **B9 · Arm E — pin all user messages.**
  *Deps:* B4 · *Effort:* **S** · *Value:* **Med**

- [ ] **B10 · Full sweep, scorecard, findings** — including what was luck.
  *Deps:* B6, B8, B9 · *Effort:* **S** · *Value:* **High**

---

## C — Routine blueprints

- [x] **C1 · `RoutineBlueprint` record + `RoutineBlueprintCatalog` with `topic-digest` only.**
  *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

- [x] **C2 · `.resx` entries (en/de/fr) for that one blueprint** — proves the localization shape before ×8.
  *Deps:* C1 · *Effort:* **XS** · *Value:* **Enabler**

- [x] **C3 · Card list in `RoutinesView`; click opens the existing editor prefilled.** AutomationIds per
  the playbook. **The vertical slice — this is where the blank-box fix becomes visible.**
  *Deps:* C1, C2 · *Effort:* **M** · *Value:* **High**

- [x] **C4 · Remaining seven blueprints + their strings.** Each declares its narrowest `GrantedTools` set.
  *Deps:* C3 · *Effort:* **M** · *Value:* **High**
  - All eight ship `Kind: Research`, not the `AgentTask` the plan's §7 table named: the `AgentTask` leg
    maps an empty grant list to null and the launcher turns null into its `write_file` default, so a card
    advertising no writes would have run able to write. Seven grant nothing at all; `meeting-followup`
    grants `create_todo` alone, and a test recomputes the effective set the way the dispatcher does.
  - **The batch went wider than this row.** It also corrected the plan's §7 table — Kind on all eight
    rows, and the time/day/text slots the read surface already covers, which is what shrinks C5 to two
    text slots — and retired topic-digest's three "change the topic in the goal box" description tails,
    because a description now says what you get rather than what to fill in.

- [ ] **C5 · `RoutineSlot` + `RoutineBlueprintFill.ToCreateArgs`** with the four validation rules
  (reject unknown slot names is the load-bearing one).
  *Deps:* C1 · *Effort:* **M** · *Value:* **Med**

- [ ] **C6 · Slot-prompt step before the editor opens**, for blueprints with text slots.
  *Deps:* C3, C5 · *Effort:* **M** · *Value:* **Med**

- [ ] **C7 · Expose the catalog + slot schema via `ScheduledJobToolHandler`** so the assistant creates
  routines from a blueprint and asks for blank slots.
  *Deps:* C5 · *Effort:* **M** · *Value:* **Med**

---

## D — Guided tour

- [x] **D1 · Visual-tree target collector + a debug command that dumps `targets`.**
  Verifiable with no LLM in the loop.
  *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

- [ ] **D2 · Spotlight adorner + popover, driven by a hardcoded AutomationId.**
  First visible result. Pia has no `Adorner` usage today — this is new ground.
  *Deps:* none · *Effort:* **M** · *Value:* **Enabler**

- [ ] **D3 · `ITourToolHandler` with `targets` / `show` / `stop`, `isAvailable` gating, Esc handler.**
  Interactive sessions only — a headless run must never hijack the screen.
  *Deps:* D1, D2, **D-Q1** · *Effort:* **M** · *Value:* **High**

- [ ] **D4 · `start` / `next` / `prev` with the paging chrome.**
  *Deps:* D3 · *Effort:* **S** · *Value:* **Med**

- [ ] **D5 · Cross-view navigation** — resolve → navigate → await load → re-resolve → fail cleanly.
  **Where the real value is:** "where is X" is the question people actually ask.
  *Deps:* D3 · *Effort:* **M** · *Value:* **High**

- [ ] **D6 · Virtualized-list scroll-into-view; overlay / adorner-layer handling.**
  *Deps:* D5 · *Effort:* **M** · *Value:* **Med**

- [ ] **D7 · AutomationId gap-fill** for surfaces a tour needs but cannot address; feeds
  `docs/ui_automation/ui-automation-playbook.md`. Also improves UI-test coverage.
  *Deps:* D1 · *Effort:* **S** · *Value:* **Med**

- [ ] **D8 · Recorded UI script in `tests/ui-scripts/`** running a two-step tour end to end.
  *Deps:* D4, D5 · *Effort:* **S** · *Value:* **Med**

---

## E — Per-routine persona + reasoning effort

Promoted out of "Not yet planned" (review #11), where it was rated **S**. **As built it is an `M`** —
two schema columns with both migration halves, seven `ScheduledJobService` sites, a shared resolver, both
dispatch legs, an editor with two new controls and thirteen new strings in each of three locales, and
three new test files. The `S` estimate priced the model change and missed the fan-out; the step ratings
below are the real ones.

- [x] **E1 · `PersonaId` + `ReasoningEffort` on `ScheduledJob`, both migration halves, the clear
  sentinels.** `Guid.Empty` clears the persona, a `clearReasoningEffort` flag clears the effort, and both
  columns are appended to `MapJob`'s positional SELECT.
  *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

- [x] **E1b · Neither pin crosses the sync wire, pinned by tests.** `SyncScheduledJob` and `SyncMapper`
  are unchanged on purpose; a field the server does not know about would come back null and erase the
  owner's pin after one push-pull cycle.
  *Deps:* E1 · *Effort:* **XS** · *Value:* **High**

- [x] **E2 · `RunPinResolver` — one persona ladder, one effort ladder.** Static, so neither leg gains a
  constructor dependency; it replaces the three hand-rolled clone-and-stamp blocks.
  *Deps:* E1 · *Effort:* **S** · *Value:* **Enabler**

- [x] **E3 · The AgentTask leg, and the provider-clear bug in the same seam.** `HeadlessRunRequest`
  carries both pins, and `Guid.Empty` now clears the provider too — which fixes the editor's "Default
  provider" row having been a silent no-op.
  *Deps:* E2 · *Effort:* **S** · *Value:* **High**

- [x] **E4 · The Research leg gets both pins.** `BackgroundTurnRequest` carries them, so the pinned
  persona's system prompt is what `PrepareTurn` composes — the substance of the pin on this leg.
  *Deps:* E2 · *Effort:* **S** · *Value:* **High**

- [x] **E5 · The editor: a persona picker, an effort picker, and the "no longer available" row.**
  AutomationIds `Routines_Field_Persona` and `Routines_Field_Effort`. **The vertical slice.** The picker
  is deliberately not gated on the agent roster, which is empty by default.
  *Deps:* E1 · *Effort:* **M** · *Value:* **High**

- [x] **E6 · Tests — written, never executed.** Three new files (`ScheduledJobPersonaPinTests`,
  `RunPinResolverTests`, `ScheduledJobsPinMigrationTests`) and six extended, including the three
  `FakeJobService` signature fixes and both `RoutinesViewModel` ctor sites that would otherwise fail the
  test project's compile. A tick here means the suite exists, not that it is green — E7 is the run.
  *Deps:* E3, E4, E5 · *Effort:* **M** · *Value:* **High**

- [ ] **E7 · Verification handoff — the only thing that can turn this group green.** `dotnet build
  -t:Rebuild` in both configurations, `dotnet test` with no filter, an eyeball pass in the real app, and
  **one open against a pre-change profile** — migration half (b) is the `ALTER TABLE` path, and every test
  and every fresh profile takes the `CREATE TABLE` path instead.
  *Deps:* E6 · *Effort:* **XS** · *Value:* **High**

- [x] **E8 · Blueprint effort defaults; no persona default.** `RoutineBlueprint.DefaultEffort` is set on
  all eight cards and `StartFromBlueprint` carries it into the editor. A persona default is deliberately
  absent: a built-in id can be hidden by `BlockedBuiltInPersonas`, and a catalog cannot know the user's
  own personas.
  *Deps:* E1, C4 · *Effort:* **XS** · *Value:* **Med**

- [ ] **E9 · Persist the resolved run persona and effort on the `AgentRuns` row.** Closes the resume gap
  the review surfaced: both pins are resolved per dispatch and never stored, so a scheduled run that parks
  at its budget resumes on the current mode persona at the mode default effort. One seam, and it closes
  both pins without giving the launcher a dependency on the job store.
  *Deps:* E3 · *Effort:* **S** · *Value:* **Med**

---

## Suggested order

Cheapest decisive work first, then the vertical slices.

```
A1 → B5 → A5 → A2 → A3          # gate, then the cheap wins and the strongest evidence signal
C1 → C2 → C3                    # blueprint vertical slice — first user-visible change
B1 → B2 → B3 → B4               # gate: is compaction actually losing anything?
C4 · A6 → A7 · B6 · B7 → B8     # widen, once the gates have answered
E1 → E2 → E3 · E4 → E5 → E6     # per-routine pins; E8 rides with C4, E7 is the Windows run
D1 → D2 → D3 → D5               # tour, after the cheaper items land
```

`A1` and `B5` together are under two days and can both close a branch of work.

---

## Not yet planned

From the review's recommendation table, no plan doc written. Listed so they are not lost.

**If you pick anything up from here next, take #2 and #3 together.** They are the only two `High`s
in this table and they are one feature area — failure legibility. #2 names which layer broke; #3 is the
action the same card offers when naming it isn't enough. Shipped separately, #3 lands on a card that
still can't say what went wrong.

| Item | Review # | Effort | Value |
|---|---|---|---|
| Error layer + recovery actions on the failure card | 2 | M | High |
| Send Diagnostics — consented, redacted log bundle (logs only, never transcripts) | 3 | S–M | High |
| Global pause (ESTOP) — tray toggle, never kills in-flight work | 7 | S | Med |
| Repetition guard before the truncated-response continuation nudge | 8 | S | Med |
| Empty-response guard with a cost-aware retry budget | 9 | S–M | Med |
| Mark iteration-truncated child results for the parent | 10 | S | Med |
| Citation ledger inversion in `WebCitationExtractor` | 14 | M | Med |
| Meeting → action items: the *decisions* half, citations, and an on-demand path | 15 | XS | Low |
| Outbound webhooks on the existing timeline observer drain | 16 | M | Low |
| Timeout inventory, then one resolver if the count justifies it | 17 | S | Low |
| Adversarial UX test as a recorded WinWright flow + prompt | 18 | S | Low |

**#15 is half closed, not closed.** C4's `meeting-followup` blueprint ships the evidence-first framing the
review asked for: before it extracts a single action item the template states the meeting's title and
date, who the front matter lists as attendees, whether the transcript reads as complete or breaks off
mid-sentence, and whether the speaker labels are real names, generic placeholders or absent — and it names
every passage it is not confident about. It also queries existing todos first, so a re-run is not a
duplicate factory, and it attributes an owner only where the transcript supports one. Three things the
review named are still open: it extracts **action items only**, not the *decisions* the source prompt also
produces; the todos it creates carry the meeting title and date in their notes but **no citation** back to
the transcript passage; and it exists only as a daily routine over meetings dated *today*, so nothing
points it at one named past meeting. The row above is the remainder, re-rated down accordingly.
