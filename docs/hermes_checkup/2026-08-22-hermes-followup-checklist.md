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
| ~~**A1**~~ | A2–A4, A6, A7 | Is `ExpectedArtifact` already file-shaped often enough that the probe is fine? **Answered 2026-08-23: no — 56%, gate does not close.** Everything it gated stays open. |
| **B4** | B6–B10 | Does current compaction lose anything worth acting on? |
| **D-Q1** | D3–D8 | Is the goal onboarding (a canned tour, no LLM) or arbitrary "where do I…" questions? |

---

## A — Artifact evidence

- [x] **A1 · Read the artifact-outcome split — `found` / `NOT FOUND` / not-a-file — off real-run logs.**
  Not `probed / declared`: the release-visible line in `AgentVerifier.TryBuildArtifactFactsAsync` carries
  counts only, and `Probed` counts candidate *paths* (0–3 per declaration), so it carries no outcome. The
  split itself lives in the Debug-only facts block one line below — and G1 of
  [2026-08-22-group-a-brief.md](2026-08-22-group-a-brief.md) puts a counts-only tally of it onto the
  release-visible line, which is what makes it readable without a Debug build.
  **First read, 2026-08-22** — 23 declarations over 7 **probe lines** on one client: 57% `found`, 43%
  `not a file reference`, **0 `NOT FOUND`**. That refutes "already high", so A2–A4/A6/A7 stay open — but
  it is one machine over three days on code-shaped tasks, too small to tune on, and the 23 are **not
  independent samples**: probe lines within one run overlap, because a verify-fail replan re-verifies over
  an accumulating `CompletedSteps`. Widen it per
  [2026-08-22-a1-log-collection-runbook.md](2026-08-22-a1-log-collection-runbook.md), then re-read. The
  row to watch is `NOT FOUND`: if it stays at zero, the planner channel cannot produce a negative at all.
  **Do not average across the tally.** The counts-only tally changed what an `Artifact probe:` line carries,
  so this hand-counted first read is not comparable to anything measured after it — of the new fields only
  `notFileShaped / declared` is even loosely comparable to the 43%.
  **Gate read, 2026-08-23 — the gate does not close.** Four live runs on a throwaway profile, one per
  runbook prompt category: 15 declarations over three probed runs, which collapse to **9 distinct
  intended artifacts, 5 of them file-shaped — 56%**, against the ≥85% the gate needed. (The raw
  counters say 33%; a replan re-declares the same artifact against a new step row, so both the vague
  original and its concretized twin survive into the final facts block. Collapse the pairs.) The `n` is
  four runs, one machine, one provider, one afternoon — an existence proof, not a rate. **A2–A4, A6 and
  A7 all stay open.** The full reading, its three collection traps and its limits are in
  [2026-08-23-a1-pilot-reading.md](2026-08-23-a1-pilot-reading.md).
  `NOT FOUND` did **not** stay at zero — it read 4 — but every one came from a declaration naming
  *alternatives*, so the negative was unreadable rather than real. That is what re-sequenced A4 below.
  *Deps:* none · *Effort:* **XS** · *Value:* **High** (decision gate)

- [x] **A4 · Tighten how the plan describes `expectedArtifact` — three surfaces, not two.**
  The prose at `AgentPlanner.cs:782` reaches a **plan** turn only, and `:827` does **not** carry that
  sentence: it is the verbatim twin of `:784`, and apart from it `BuildReplanMessages` (`:817-830`) says
  nothing about the field. The other two surfaces are the tool-schema descriptions `AIFunctionFactory` ships every
  turn — `emit_plan`'s steps parameter (`:139` plan, `:148` replan) and `PlanStepArg.ExpectedArtifact`
  (`:159`) — so on a replan the schema is the *only* description of the field. Say what checkable means
  (something the app can look up, not "a summary of the Q3 numbers"), and say to omit the field otherwise.
  Weigh `:784`/`:827` in the same edit: their "ONE step listing every file in expectedArtifact" pulls the
  other way, and it is what produces the composite declarations that defeat a bare `found` / `NOT FOUND`
  harvest.
  **Re-sequenced ahead of A2 and landed 2026-08-23 as P1.** The pilot showed the two sentences do not
  in fact pull against each other: `:784`/`:827` govern **step granularity**, the defect is **candidate
  names inside one declaration**, and one clause settles both — *every name listed must exist when the
  step finishes*. It went in at `:159` and `:782`; `:784`/`:827` are untouched. Its `Deps: A2` was
  wrong: A2's numbers cannot say anything about the planner's wording, and the pilot's unreadable
  negative made this the highest value-per-effort row in the group. **Two of the three schema surfaces
  named above were left alone deliberately** — `:139` and `:148` summarise what an array element holds,
  and `:159` nests inside both; the reasoning is §1 of
  [2026-08-23-a4-replay-reading.md](2026-08-23-a4-replay-reading.md), which also carries the landed
  wording and the before/after it produced.
  *Deps:* none (was A2) · *Effort:* **XS** · *Value:* **High** (was Med)

- [ ] **A2 · Route `ArtifactRef` through the existing artifact probe.**
  `produced: X` becomes `produced: X → found (2.1 KB)` / `→ NOT FOUND`.
  `Deps: A1` is **satisfied** — the gate read and did not close. But its priority dropped on a number
  A1 also produced: `artifactReported=` was **True on 2 of 17** step outcomes. P6 re-measured it at
  2 of 7 and 2 of 8, so the *share* is better than that first read suggested while the *count* is 2
  every time. P7's call on those numbers is **defer**.
  *Deps:* A1 (satisfied) · *Effort:* **S** · *Value:* **High**

- [ ] **A3 · Tests for the self-reported-but-missing case; keep the failure-isolation tests green.**
  *Deps:* A2 · *Effort:* **S** · *Value:* **High**

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

### A · the disjunction batch

Rows added 2026-08-23 from [2026-08-23-a4-disjunction-batch-brief.md](2026-08-23-a4-disjunction-batch-brief.md).
The reading they produced is [2026-08-23-a4-replay-reading.md](2026-08-23-a4-replay-reading.md).

- [x] **P1 · Forbid the disjunction in `expectedArtifact`, keep the conjunction.** Lands A4. The schema
  description at `AgentPlanner.cs:159` — the one surface that reaches plan *and* replan — and the
  plan-turn prose at `:782`. `:784`/`:827` untouched, no fourth surface on replan.
  *Deps:* none · *Effort:* **XS** · *Value:* **High**

- [x] **P2 · Replay the four prompts and read the delta.** Two arms on one provider rather than a
  replay against the pilot, because the pilot's provider now 401s. Declarations offering alternatives
  **3 of 8 → 0 of 6**; `notFileShaped` **6 of 8 → 0 of 6**; `probed` **2 → 6**; collapsed
  file-shapedness **20% → 100%**; and the first `NOT FOUND` in this corpus that is true on disk.
  *Deps:* P1 · *Effort:* **S** · *Value:* **High**

- [x] **P3 · Per-declaration not-found counter — decided, not built.** The condition for building it did
  not fire: no disjunction survives P1, and with every listed name required to exist a candidate miss
  *is* a declaration miss. Building it would install the wrong rule for a genuine conjunction. Reason in
  §2 of the reading.
  *Deps:* P2 · *Effort:* **XS** · *Value:* **Med**

- [x] **P4 · Record the gate reading.** A1 ticked at 56% with its `n`, A4 re-sequenced ahead of A2, the
  runbook's "near-zero `NOT FOUND` proves the channel produces no negative" row struck.
  *Deps:* none · *Effort:* **XS** · *Value:* **High**

- [x] **P5 · Fold the three collection traps into the runbook.** Accumulating `declared` with replan
  twins, concurrent runs defeating a count-based poll, and the structurally invisible answer-only
  category — plus §3's profile-isolation error.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**

- [x] **P6 · Re-measure report-channel supply.** `artifactReported=True` on **2 of 7** post-P1 step
  outcomes (29%), against 2 of 8 pre-P1 and the pilot's 2 of 17. The share moved, the count is 2 in all
  three.
  *Deps:* P2 · *Effort:* **XS** · *Value:* **High**

- [x] **P7 · The A2 recommendation.** **Defer** — the drop trigger (supply staying near 12%) did not
  fire, and the build case rests on 2 events. Re-read supply over ≥12 runs on a post-P1 build; build A2
  above ~40%, drop it below ~12%.
  *Deps:* P6 · *Effort:* **XS** · *Value:* **High**

- [ ] **P8 · Say that `expectedArtifact` is relative to the working folder.** Neither surface says which
  root a name is relative to, and on the post-P1 replay that cost a run its artifact: a replan declared
  a **rooted** path, the executor called `write_file` with that same path, and the sandbox refused it —
  *"Path is outside the assistant files folder"* — so nothing was written. Worth doing whatever caused
  the rootedness (the goal named the project, so a project subfolder is an ordinary response; `n = 1`
  per arm either way). Deliberately **not** folded into P1: changing the wording that produced a reading
  without re-measuring makes the reading unfalsifiable. Re-measure with it.
  *Deps:* P1 · *Effort:* **XS** · *Value:* **Med**

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
A1 → A4 → P8 → B5 → A5 → A2     # gate, then the cheap wins; A2/A3 wait on a wider supply re-read
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
