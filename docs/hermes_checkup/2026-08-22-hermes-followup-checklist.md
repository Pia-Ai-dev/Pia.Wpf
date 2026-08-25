# Implementation Checklist — Hermes Follow-Up Plans

Tracking file for the plans spawned by
[`2026-08-22-hermes-update-review.md`](2026-08-22-hermes-update-review.md). One row per implementation
step. Tick as they land. **A row is a pointer, not the record** — the reasoning, the measurements and the
traps behind every tick live in the group's doc below, and are deliberately not repeated here.

> **Before you shorten this file.** The rows are one line by design — already collapsed once, with each
> row's substance moved into the doc it links, so the link is the only pointer left to it. Stripping links
> to tidy up destroys that detail. The **Phase 2** table below is a deletion licence, not a to-do list:
> re-check each file's inbound references before acting on it.

| Group | Plan |
|---|---|
| **A** | [Artifact evidence](artifact-evidence.md) |
| **B** | [Compaction recall — closeout](2026-08-24-compaction-recall-closeout.md) |
| **C** | [Routine blueprints](2026-08-22-routine-blueprints-plan.md) · ordering decision: [C4 before C5](2026-08-22-c4-before-c5-decision.md) |
| **D** | [Guided tour — parked](../guided_tour/2026-08-24-d-track-parked.md) |
| **E** | [Routine pin sync — decision](2026-08-24-routine-pin-sync-decision.md) |
| **F** | [Gate profile hygiene](../test_hygiene/2026-08-24-gate-profile-hygiene.md) |
| **G** | [Failure legibility — the export and the failure layer](../failure_legibility/2026-08-24-failure-legibility.md) |

**Effort** — `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface
· `L` a week or more, a new subsystem.

**Value** — `High` user-visible improvement or a real risk closed · `Med` worthwhile, not headline ·
`Enabler` little standalone value, unblocks a High.

**The groups are independent bar one step** — `E8` needs C4's blueprints. Every other dependency below is
within-group unless marked otherwise.

**Gate at the last row to land (`52328597`):** `dotnet test` with no filter → **total 4987, failed 0,
skipped 59**; Debug and Release both rebuild to 0 Warning(s).

---

## Decision gates

Four gates. Three are answered; do not tick a dependant of the open one without revisiting it.

| Gate | Closes | Question it answers |
|---|---|---|
| ~~**A1**~~ | A2–A4, A6, A7 | Is `ExpectedArtifact` already file-shaped often enough that the probe is fine? **Answered 2026-08-23: no — 56% against the ≥85% it needed.** Everything it gated stays open. |
| ~~**B4**~~ | B6–B10 | Does current compaction lose anything worth acting on? **Answered 2026-08-23: yes — arm B 0.0% against arm A's 98.3%, on 4 of 4 transcripts.** B6–B10 then closed 2026-08-24 and promoted nothing. |
| ~~**G-Q1**~~ | G5 | Does Retry re-dispatch the whole run from its goal, or resume from the failed step? **Answered 2026-08-25: neither, and `G5` is withdrawn.** Only 2 of the 15 descriptors `FailureMapper` builds carry `SafeToReRun` and neither can reach a failure card; resume-from-step is closed separately, because a throwing step leaves its row `Running` while the next-pending query selects `Pending` only. §15 of [../failure_legibility/2026-08-24-failure-legibility.md](../failure_legibility/2026-08-24-failure-legibility.md). |
| **D-Q1** | D3–D6, D8 | Is the goal onboarding (a canned tour, no LLM) or arbitrary "where do I…" questions? **Unanswered, and its dependants are parked** — [../guided_tour/2026-08-24-d-track-parked.md](../guided_tour/2026-08-24-d-track-parked.md). It does not block `D7`. |

---

## A — Artifact evidence

The readings, the pre-registered band, the eleven collection traps and the collection protocol:
[artifact-evidence.md](artifact-evidence.md).

- [x] **A1 · Read the artifact-outcome split — `found` / `NOT FOUND` / not-a-file — off real-run logs.**
  Gate read 2026-08-23 over four live runs: 9 distinct intended artifacts, 5 of them file-shaped — **56%**, so the gate did not close.
  *Deps:* none · *Effort:* **XS** · *Value:* **High** (decision gate)

- [x] **A4 · Tighten how the plan describes `expectedArtifact` — three surfaces, not two.**
  Landed 2026-08-23 as `P1`, re-sequenced ahead of A2 because A2's numbers cannot say anything about the planner's wording.
  *Deps:* none (was A2) · *Effort:* **XS** · *Value:* **High**

- [ ] **A2 · Route `ArtifactRef` through the existing artifact probe.**
  Deferred inside its pre-registered band — report-channel supply last read **22.0% over 13 runs**, against build ≥40% / drop ≤12%.
  *Deps:* A1 (satisfied) · *Effort:* **S** · *Value:* **High**

- [ ] **A3 · Tests for the self-reported-but-missing case; keep the failure-isolation tests green.**
  *Deps:* A2 · *Effort:* **S** · *Value:* **High**

- [x] **A5 · Persist `ArtifactRef` into `AgentSteps.ExtraJson` and seed it in `SafeSeedResumeContext`.**
  Fixes the resume asymmetry; landed ahead of the A1 gate because it is worth doing whichever way the gate reads.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**

- [ ] **A6 · Extract `IArtifactProbe` with a file implementation.**
  Behaviour-preserving refactor of today's probe — but land it with A7 or not at all, because A7 needs an async seam.
  *Deps:* A2 · *Effort:* **S** · *Value:* **Enabler**

- [ ] **A7 · Todo / reminder / vault probes + typed `kind:ref` prefix in the tool description.**
  Widens the evidence surface past the filesystem; its prefix dispatch must run before `FileCandidates`, which is what makes A6 a prerequisite.
  *Deps:* A6 · *Effort:* **M** · *Value:* **High**

### A · the disjunction batch

- [x] **P1 · Forbid the disjunction in `expectedArtifact`, keep the conjunction.**
  Lands A4, on the two surfaces that reach plan *and* replan — `AgentPlanner.cs:159` and `:782`.
  *Deps:* none · *Effort:* **XS** · *Value:* **High**

- [x] **P2 · Replay the four prompts and read the delta.**
  Two arms on one provider: declarations offering alternatives 3 of 8 → 0 of 6, collapsed file-shapedness 20% → 100%.
  *Deps:* P1 · *Effort:* **S** · *Value:* **High**

- [x] **P3 · Per-declaration not-found counter — decided, not built.**
  Its condition never fired: with every listed name required to exist, a candidate miss *is* a declaration miss.
  *Deps:* P2 · *Effort:* **XS** · *Value:* **Med**

- [x] **P4 · Record the gate reading.**
  A1 ticked at 56% with its `n`, and the runbook's "near-zero `NOT FOUND` proves the channel produces no negative" row struck.
  *Deps:* none · *Effort:* **XS** · *Value:* **High**

- [x] **P5 · Fold the collection traps into the runbook.**
  Accumulating `declared` with replan twins, concurrent runs defeating a count-based poll, and the structurally invisible answer-only category.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**

- [x] **P6 · Re-measure report-channel supply.**
  `artifactReported=True` on 2 of 7 post-P1 step outcomes, against 2 of 8 pre-P1 and 2 of 17 in the pilot — the share moved, the count is 2 every time.
  *Deps:* P2 · *Effort:* **XS** · *Value:* **High**

- [x] **P7 · The A2 recommendation.**
  **Defer** — the drop trigger did not fire and the build case rested on 2 events.
  *Deps:* P6 · *Effort:* **XS** · *Value:* **High**

- [x] **P8 · Say that `expectedArtifact` is relative to the working folder.**
  Landed 2026-08-23 on `:159` and `:782`, and re-measured over 13 runs: not one declaration carries a rooted path.
  *Deps:* P1 · *Effort:* **XS** · *Value:* **Med**

- [x] **P9 · Investigate the step that reported `succeeded=True` on a refused tool call.**
  Answered 2026-08-24, **no production change** — the call was never refused, and the real gap is that `AgentTimelineOutcome.Ok` means "`Execute()` returned", so an executed-but-failed tool call renders exactly like a successful one.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**

---

## B — Compaction recall

**Closed 2026-08-24, promoted nothing.** The result, the four instrument defects and the nine fixes a
re-open needs first: [2026-08-24-compaction-recall-closeout.md](2026-08-24-compaction-recall-closeout.md).

- [x] **B1 · Synthetic transcript generator with planted facts.**
  Committed; no real user data. Run recipe and fixture conventions: `tests/Pia.Wpf.Tests/Integration/Compaction/README.md`.
  *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

- [x] **B2 · Corpus extraction script** (`AssistantChatMessages` → JSON fixture, gitignored).
  *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

- [x] **B3 · Question-bank generator, per-transcript cache, and the verbatim-leak filter.**
  The cache key is **(transcript fingerprint, window, max output)**, because the removed set belongs to the pair and a transcript-only key would answer one budget's questions from another's.
  *Deps:* B1 · *Effort:* **M** · *Value:* **Enabler**

- [x] **B4 · Arms A (uncompacted) + B (current), judge, scorecard writer.**
  Arm A **98.3%**, arm B **0.0%** on all four shapes — exactly equal to a no-context control, so an evicted message is gone rather than summarised.
  *Deps:* B2, B3 · *Effort:* **M** · *Value:* **High** (decision gate)

- [x] **B5 · Pin the "user messages are never compacted" invariant with a test** against `Microsoft.Agents.AI.Compaction`.
  Pia pins the head goal and the newest instruction; middle user messages are not pinned.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**

- [x] **B6 · Arm C — mechanical anchor index.**
  +48.3 and wins 4 of 4, **not promoted**: the block is a 100%-precision answer key produced by the generator's own filler, and it is appended after the compactor has already fit the window.
  *Deps:* B4 · *Effort:* **M** · *Value:* **High**

- [x] **B7 · Message-level search granularity.**
  Shipped as a scoped `SearchMessagesAsync` query rather than a second FTS index, then **deleted 2026-08-25 (`52328597`)** — the track closed promoting nothing and the API had zero production callers.
  *Deps:* none · *Effort:* **M** · *Value:* **Enabler**

- [x] **B8 · Arm D — recovery pointer.**
  +24.2 and wins 4 of 4, **not promoted**: the harness searched an in-memory oracle, and no shippable store holds the tool content eviction reaches first.
  *Deps:* B4, B7 · *Effort:* **M** · *Value:* **High**

- [x] **B9 · Arm E — pin all user messages.**
  Reads as a refusal and is not one — on three of four shapes every planted fact sits on an assistant message, so its 0.0% there is forced. **Untested, not refused.**
  *Deps:* B4 · *Effort:* **S** · *Value:* **Med**

- [x] **B10 · Full sweep, scorecard, findings.**
  Six columns × four transcripts on DeepSeek V4 Flash; the useful output is four instrument defects, first among them that compaction only ever runs on agent-run **step** turns, so half the corpus modelled a list the product never compacts.
  *Deps:* B6, B8, B9 · *Effort:* **S** · *Value:* **High**

- [x] **B11 · Per-provider context-window defaults — 128k for an unknown model, a table for known ones.**
  Stamped in `ProviderService.LoadProvidersAsync`, where providers are constituted, and **not** in `AgentContextBudget.From`, where the first attempt failed 80 tests.
  *Deps:* none · *Effort:* **S** · *Value:* **High**

- [x] **B12 · OpenRouter reports its own window — read it live, and take the field off the form.**
  `top_provider.context_length` and never the advertised figure, exact id first with the base only as a fallback; the catalogue is now cross-vendor rather than gated on provider type.
  *Deps:* B11 · *Effort:* **S** · *Value:* **High**

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

- [x] **C5 · `RoutineSlot` + `RoutineBlueprintFill.ToCreateArgs`** with **three** of the four validation
  rules (reject unknown slot names is the load-bearing one).
  **Done 2026-08-24.** `RoutineSlot` + `RoutineSlotKind` beside `RoutineBlueprint`, the fill engine in
  `Pia.Services`, and its result types in `Pia.Models` — `NamingConventionTests` bans a record from the
  `Pia.Services` root namespace, and it is what caught that rather than a reviewer. Two slots ship:
  `topic-digest`'s `{topic}` (default *artificial intelligence*, so the rendered prompt is what shipped
  before) and `competitor-watch`'s `{companies}`. Full reading:
  [2026-08-24-c5-c7-batch-report.md](2026-08-24-c5-c7-batch-report.md).
  - **Rule 3 is not shipped**, per the brief's "ship `RoutineSlotKind.Text` only": with one kind it has
    nothing to check, and a rule that cannot fire is worse than an absent one. Rules 1, 2 and 4 each have a
    test that fires them. Rule 1 is deliberately about the **name**, not the value, so a typo cannot pass by
    carrying an empty string.
  - **`Optional` does not ship either, and trap 4.2 dissolves with it.** Nothing substitutes empty:
    `companies` defaults to `(none given)` and the next sentence branches on exactly that, so both renders
    are grammatical where an empty substitution would leave a dangling `: .`. Resolution is one ladder,
    `value → Default → error`, which is what an optional slot would have said. `Options` and `Strict` go
    with rule 3.
  - **Trap 4.3 closed in the same change** — `StartFromBlueprint` renders through the fill engine, and the
    two `RoutinesViewModelTests` assertions that read `EditQuery == blueprint.QueryTemplate` now read
    rendered-with-defaults. The brace ban is inverted and also refuses an unbalanced brace.
  - **Plan §11 Q1 answered yes**: `ScheduledJobs.BlueprintKey`, additive, both migration halves, appended
    to the END of the positional SELECT because `MapJob` reads by ordinal, and **off the sync wire** per
    E1b. Written but not yet read by anything.
  - Effort corrected to **S** per the C4 decision §6.5, which re-rated it the moment the slot count fell
    from five to two.
  *Deps:* C1 · *Effort:* **S** (was M) · *Value:* **Med**

- [x] **C6 · Labelled slot fields for blueprints with text slots**, inline in the editor.
  **Done 2026-08-24**, ahead of its 2026-08-24 deferral. **The slot count is what moved it.** The deferral
  priced C6 as an `M` of `Med` that replaces "edit the prose in the goal box" with a labelled field *for two
  of eight cards*; the twenty-blueprint expansion in the row below makes it **fourteen slots across twenty
  blueprints**, and those defaults are personal facts — a watchlist, a language, a city's worth of clients.
  Clicking *Your watchlist* and saving scheduled someone else's holdings every evening unless the user found
  and hand-edited a phrase buried mid-paragraph.
  - **Plan §11 Q4 answered: neither the clarification pipeline nor a dialog.** An `ItemsControl` between
    `Routines_Field_Name` and the goal label, one labelled field plus help text per slot, prefilled with the
    slot's `Default` so the value is visible rather than hidden behind a watermark. The editor is already an
    inline panel, so this adds no new surface and no new `UserControl` — and therefore no
    `expectedNestedViews` change.
  - **The one rule.** The goal re-renders on card click and on every slot change, and stops the moment the
    user edits the goal by hand. A keystroke and the renderer's own write are the same `PropertyChanged`
    event, so the renderer announces itself with a `_renderingGoal` flag and `OnEditQueryChanged` only sets
    the hand-edit latch when that flag is clear. Both reset wherever `_editBlueprintKey` resets, so switching
    cards re-arms the render.
  - **Scope call: the block is hidden on `StartEdit`.** A stored query is the user's own text, and rendering
    over it from slot defaults is exactly what C6 exists to prevent.
  - **`competitor-watch` keeps `(none given)`.** With a labelled field the sentinel is now self-explanatory
    rather than buried, and the template's next sentence branches on that exact phrase into a vault lookup —
    an empty default would both break the branch and leave "Watch these companies: .".
  - Nothing new was needed for localization: all 28 slot `LabelKey`/`HelpKey` strings already shipped with C5
    in all three locales and were read by nothing. `EveryBlueprintKeyResolvesInAllThreeLocales` already
    covered them, so no test needed extending.
  - Reuses `RoutineBlueprintFill.ToCreateArgs(blueprint, values)` unchanged — blank still counts as
    unsupplied, so clearing a field falls back to that slot's default on its own.
  - Eight ViewModel tests, plus a desktop pass that confirmed the four things no ViewModel test can see: the
    block appears for a slotted card and is absent for `morning-brief`, a slot keystroke visibly moves the
    goal box, a hand-edited goal survives a later slot keystroke, and save/reopen round-trips the text.
  *Deps:* C3, C5 · *Effort:* **S** (was M) · *Value:* **High** (was Med — 14 of 20 cards, not 2 of 8)

- [x] **C7 · Expose the catalog + slot schema via `ScheduledJobToolHandler`** so the assistant creates
  routines from a blueprint and asks for blank slots.
  **Done 2026-08-24.** Two tools: `list_routine_blueprints` (a read, no card — a separate tool rather than
  eight titles baked into a description that ships on every turn) and `create_routine_from_blueprint`.
  - **Both of trap 4.4's asymmetries are closed.** The create tool takes no `query`, no `kind` and **no
    `grantedTools`** — the absence of the parameter is the mechanism that stops the model widening the
    grants, and a test pins it against the shipped JSON schema. The blueprint's `DefaultEffort` now reaches
    `CreateAsync`, so the tool path no longer silently drops the pin the card path honours.
  - Every refusal — unknown key, unknown slot name, unparseable `slots` — comes back as a **tool result,
    not an approval card**, so the user is never shown a card offering to create the wrong routine. `slots`
    is a JSON object rather than a CSV because a slot value routinely contains commas, which *which
    companies* is exactly.
  - The card shows the **rendered** query plus the blueprint it came from and the effort it will run at.
  - **Two things the brief did not name.** `create_routine_from_blueprint` was added to
    `AuthorityAuthoringTools` although it takes no grant list — approving it once lets Pia stand up a
    routine that writes unattended, which is what the caution says — and to the `@`-command `Research` row,
    since `AssistantPromptComposer` loads *only* the tools a tagged domain lists.
  *Deps:* C5 · *Effort:* **M** · *Value:* **Med**

- [x] **C8 · Twenty blueprints, grouped and searchable, with the catalog as the primary action.**
  **Done 2026-08-24**, owner-requested. Seven of the shipped eight read the user's own todos, reminders,
  kanban and vault, so a fresh profile met a menu of things that only pay off after weeks of use — and the
  menu was unreachable anyway once a routine existed, because it lived in the placeholder pane a selection
  replaces.
  - Twelve new world-fed blueprints, each with a default that produces a real answer on its first run. Nine
    need web search; `word-of-the-day`, `meal-ideas` and `learn-one-thing` deliberately do not, so a
    local-model provider still has working cards. `Category` stops being dead cadence scaffolding and
    becomes the two rendered groups: fourteen "works right away", six "uses your Pia data".
  - `Routines_NewJob` opens the catalog instead of a blank editor, with a start-from-blank escape hatch, a
    search box over title and description, collapsible groups, and an auto-open when no routines exist.
  - **The risk that shaped it:** web search is a provider capability, off by default outside Pia Cloud, and
    `BuildSystemPrompt` says *nothing* when it is inactive — so a markets routine on such a provider would
    print fabricated prices rather than fail. Every web-dependent template ends with a shared guard refusing
    to answer from memory, and a test pins the guard to `RequiresWebSearch` **in both directions**; the
    second direction, that any template mentioning a web search must carry the flag, is the one the bug
    actually travels in.
  - Deliberate deviation from the plan, tested: expansion is forced on the step *into* a search, not on
    every keystroke, so a group collapsed mid-search stays collapsed.
  - Desktop pass done. **Open:** the no-web-search hint reads only the default assistant provider, but
    `ScheduledResearchProviderResolver` prefers a job's pinned `providerId`, so pinning a non-searching
    provider on a web-requiring routine warns about nothing. Firing a web routine for real is `G1`.
  *Deps:* C4 · *Effort:* **M** · *Value:* **High**

---

## D — Guided tour

**`D2`–`D6` and `D8` are PARKED by the owner, 2026-08-24** — not cancelled. The resume point, what will
have rotted, the ~3–4 weeks plus a desktop session it costs, and the design carried verbatim as Part II:
[../guided_tour/2026-08-24-d-track-parked.md](../guided_tour/2026-08-24-d-track-parked.md).

- [x] **D1 · Visual-tree target collector + a debug command that dumps `targets`.**
  Survives the parking as `D7`'s instrument; the dump method itself became DEBUG-only in `52328597`, since it serialises every UIA `Name` — chat and todo titles — onto the clipboard and only its keybinding had been gated.
  *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

- [ ] **D7 · AutomationId gap-fill** for surfaces a tour needs but cannot address.
  Open as a tag-along on the next UI change, not as scheduled work; regenerate the gap list from `ViewAutomationIdTests`' `IdKind.Missing` and the playbook, never from a Ctrl+Shift+F12 dump, which by construction only shows ids that already exist.
  *Deps:* D1 (satisfied) · *Effort:* **S** · *Value:* **Med**

---

## E — Per-routine persona + reasoning effort

**Shipped and verified on Windows.** Rated `S` in the review and built as an `M`. The decision the code
cannot state — why neither pin crosses the sync wire, what would reverse that, and the two owner
questions still open — is [2026-08-24-routine-pin-sync-decision.md](2026-08-24-routine-pin-sync-decision.md).

- [x] **E1 · `PersonaId` + `ReasoningEffort` on `ScheduledJob`, both migration halves, the clear sentinels.**
  `Guid.Empty` clears the persona, a `clearReasoningEffort` flag clears the effort.
  *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

- [x] **E1b · Neither pin crosses the sync wire, pinned by tests.**
  A field the server does not know about comes back null and erases the owner's pin after one push-pull cycle; the three triggers that would reverse this are in [2026-08-24-routine-pin-sync-decision.md](2026-08-24-routine-pin-sync-decision.md).
  *Deps:* E1 · *Effort:* **XS** · *Value:* **High**

- [x] **E2 · `RunPinResolver` — one persona ladder, one effort ladder.**
  Static, so neither dispatch leg gains a constructor dependency; it replaces three hand-rolled clone-and-stamp blocks.
  *Deps:* E1 · *Effort:* **S** · *Value:* **Enabler**

- [x] **E3 · The AgentTask leg, and the provider-clear bug in the same seam.**
  `Guid.Empty` now clears the provider too, which fixes the editor's "Default provider" row having been a silent no-op.
  *Deps:* E2 · *Effort:* **S** · *Value:* **High**

- [x] **E4 · The Research leg gets both pins.**
  `BackgroundTurnRequest` carries them, so the pinned persona's system prompt is what `PrepareTurn` composes.
  *Deps:* E2 · *Effort:* **S** · *Value:* **High**

- [x] **E5 · The editor: a persona picker, an effort picker, and the "no longer available" row.**
  `Routines_Field_Persona` and `Routines_Field_Effort`; the picker is deliberately not gated on the agent roster, which is empty by default.
  *Deps:* E1 · *Effort:* **M** · *Value:* **High**

- [x] **E6 · Tests — written, never executed.**
  Three new files and six extended; a tick here means the suite exists, not that it is green.
  *Deps:* E3, E4, E5 · *Effort:* **M** · *Value:* **High**

- [x] **E7 · Verification handoff — the only thing that could turn this group green.**
  Done 2026-08-23, all four halves, including migration half (b) against a copy of the real `history.db` with both pin columns dropped.
  *Deps:* E6 · *Effort:* **XS** · *Value:* **High**

- [x] **E8 · Blueprint effort defaults; no persona default.**
  `RoutineBlueprint.DefaultEffort` on all eight cards; a persona default is absent because a built-in id can be hidden and a catalog cannot know the user's own personas.
  *Deps:* E1, C4 · *Effort:* **XS** · *Value:* **Med**

- [x] **E9 · Persist the resolved run persona and effort on the `AgentRuns` row.**
  Closes the resume gap: the launcher writes what the dispatch *resolved*, and both a budget park and a user pause read it back through one `ResumeAsync`.
  *Deps:* E3 · *Effort:* **S** · *Value:* **Med**

- [x] **E10 · Carry the launch's provider across a resume, not just the persona.**
  Pre-existing rather than introduced by E9 — `ResumeAsync` passed `explicitProviderId: null`; `IAssistantChatService.GetProviderIdAsync` is the one-scalar accessor that fixes it.
  *Deps:* E9 · *Effort:* **XS** · *Value:* **Med**

- [x] **E11 · Decide what a null persisted effort should mean on resume.**
  Owner call 2026-08-24: freeze both directions, carried by an `AgentRuns.EffortPinRecorded` column rather than derived from `PersonaId is not null`, which would mis-answer the live-session run.
  *Deps:* E9 · *Effort:* **XS** · *Value:* **Med**

---

## G — Failure legibility

`G1` is review **#3**, scoped to *Export* rather than *Send*. `G2`–`G5` are review **#2 slice 2**; slice 1
shipped as `3c90aa74`. The whole track — the export, the failure layer, the `G-Q1` answer and the redaction design — is one doc:
[../failure_legibility/2026-08-24-failure-legibility.md](../failure_legibility/2026-08-24-failure-legibility.md).

- [x] **G1 · Export Diagnostics — a consented, redacted zip written locally, plus reveal-in-Explorer.**
  The app had no route to its own logs at all; redaction is applied on the way **out** rather than at the log site (12 ordered rules in two tiers), measured at **0 residual hits** over the real 39-file corpus and driven through the running app 2026-08-24.
  *Deps:* none · *Effort:* **S** · *Value:* **High**

- [x] **G2 · `PiaFailure` descriptor + type-keyed mapper + `AgentRuns.FailureJson`.**
  Additive beside slice 1's free-text reason and keyed on exception **type**, with the mapper walking the inner chain (`AggregateException` → `ClientResultException` → `HttpRequestException` → `SocketException`) because matching the outermost type alone classified every real transport failure as `Unclassified`; the descriptor carries its own JSON codec after a camelCase writer and a Pascal reader read back as "no layer" rather than as an error.
  *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

- [x] **G3 · Widen `IsPreModelFailure` to read `SafeToReRun`.**
  Widening, never loosening, and the vouching mechanism is a typed `PreModelLaunchException` thrown at the one launcher site that precedes the stub-chat save. **Covered by tests only; never exercised live.**
  *Deps:* G2 · *Effort:* **XS** · *Value:* **Med**

- [x] **G4 · Layer name + recovery action on the failure card.**
  Both actions already existed — Export diagnostics for `App`/`Unclassified`, Settings → Providers for `Provider`/`Endpoint` — and it was driven through the running app against a dead port.
  *Deps:* G2 · *Effort:* **S** · *Value:* **High**

- [ ] ~~**G5 · Retry on the failure card, honouring `SafeToReRun`.**~~ **WITHDRAWN 2026-08-25 — a negative result, not deferred work.**
  Of the 15 descriptors `FailureMapper` builds only 2 carry `SafeToReRun`, and neither can reach a failure card, so the button would be enabled never; resume-from-step is closed too. Any future attempt inherits one trap: a retry claim must not `SET ExtraJson = NULL` the way both existing resume claims do.
  *Deps:* G2, G4, ~~G-Q1~~ (answered) · *Effort:* **M** · *Value:* **Med**

- [x] **G6 · Log retention that actually retains.**
  `MaxRollingFiles` bounds one base name while `FormatLogFileName` mints a new one every day, so nothing was ever pruned (39 files / 40 MB on the dev profile); `LogFileRetention` keeps 30 days, takes age from the **name** because the export perturbs mtime, and runs at the top of `InitializeAsync` rather than inside the `AddLogging` lambda, which three architecture tests reflect-invoke against the un-redirected real profile.
  *Deps:* G1 · *Effort:* **S** · *Value:* **Med**

- [x] **G7 · Ship the rolled log files, and let the consent dialog name what it leaves out.**
  NReco appends the roll index with no separator, so every rolled file was excluded as `UnrecognisedName`; both callers now share `LogFileRetention.SliceOf`, ordering inside a day is by **write time** because `Ascending` wraps, and the dialog reports the excluded count by kind instead of claiming 7 files while dropping 32.
  *Deps:* G1 · *Effort:* **S** · *Value:* **High**

---

## F — Test hygiene

Not from the review — found 2026-08-23 while seeding a throwaway profile for the wide A read. The
evidence, the instrumentation that named the offenders, and the one instance of this defect class that is
**still live**: [../test_hygiene/2026-08-24-gate-profile-hygiene.md](../test_hygiene/2026-08-24-gate-profile-hygiene.md).

- [x] **F1 · `dotnet test` writes to the user's REAL profile.**
  Two named tests rather than ambient `PiaPaths` unsafety — `ScheduledJobToolIntegrationTests` opened the default-path `SqliteContext`, and `WpfStaHost` booted the whole application; afterwards `history.db`, `-wal` and `-shm` are byte-identical across a gate run.
  *Deps:* none · *Effort:* **S** · *Value:* **High** (the gate must not mutate the machine it runs on)

- [x] **F3 · Two directory mtimes are the gate's remaining footprint on the real profile.**
  Closed by a `RedirectedProfileFixture` inside the already-serialized `PiaPathsStatic` collection plus rebuilding `SensitivePathGuard`'s two `static readonly` root arrays behind a lock; measured 0 of 9 paths changed.
  *Deps:* none · *Effort:* **S** · *Value:* **Low**

- [x] **F2 · A chat-history row can be DELETED by AutomationId but not opened by one.**
  Fixed with a real `AssistantChat_Open_{ChatId}` button plus container ids for chat and routine rows, which had been reporting a `ToString()` as their UIA name. **Landed unverified in the app.**
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**

---

## Open points with no row yet

Consequences of shipped code that a later session should **decide** rather than inherit.

- **The model-window catalogue is a dated snapshot that nothing refreshes on its own.** `OpenRouterContextWindows.SnapshotDate` is `2026-08-24` and the table is generated from [`../openrouter_models/2026-08-24-openrouter-context-lengths.md`](../openrouter_models/2026-08-24-openrouter-context-lengths.md) — regenerate, never hand-edit. Live re-reads happen only when an OpenRouter provider is saved. Decide between a refresh path, a regenerated snapshot, and nothing.
- **Ollama models resolve to nothing and take the 128k floor.** A no-op today rather than a regression, since a 4k local model never reaches 128k; worth fixing only if local models gain a window source.
- **`RoutineSlotKind` ships with one member and no reader.** Delete it until a second kind exists, or keep it as the seam `Time`/`Enum` land on — `RoutineSlot` is not persisted, so either direction is code-only. §2.3 of [2026-08-24-c5-c7-batch-report.md](2026-08-24-c5-c7-batch-report.md).
- **`BlueprintKey` stays data-only** (owner, 2026-08-24): no UI reads it, the question it answers needs months of real use, and SQL against `history.db` answers it meanwhile.

---

## Phase 2 — three deletions still owed

Their content is already absorbed; the files stay on disk because each has an inbound link inside a
**frozen** C-track doc. Delete them, and repair those links, once `C6` has landed and had its desktop pass.

| File | Absorbed by | Frozen doc that links it |
|---|---|---|
| `2026-08-23-compaction-arm-ab-reading.md` | [2026-08-24-compaction-recall-closeout.md](2026-08-24-compaction-recall-closeout.md) | [2026-08-24-c5-c7-batch-report.md](2026-08-24-c5-c7-batch-report.md) |
| `2026-08-22-routine-persona-effort-plan.md` | [2026-08-24-routine-pin-sync-decision.md](2026-08-24-routine-pin-sync-decision.md) | [2026-08-22-c4-before-c5-decision.md](2026-08-22-c4-before-c5-decision.md) |
| `2026-08-22-next-batch-brief.md` | its one unabsorbed line — a `Focus()` inside `IsVisibleChanged` silently doing nothing — now in [../ui_automation/ui-automation-playbook.md](../ui_automation/ui-automation-playbook.md); the rest is superseded, and wrong about the branch, the OS and the gate | [2026-08-22-c4-before-c5-decision.md](2026-08-22-c4-before-c5-decision.md) |

---

## Not yet planned

From the review's recommendation table, no plan doc written. Listed so they are not lost. **Both
failure-legibility items were promoted out** — #3 shipped as `G1`, and #2 slice 2 is `G2`–`G5`, whose plan
renames the descriptor's third member from the review's `Retryable` to `SafeToReRun` because they ask
different questions: "provably nothing spent and nothing written", not "the call might work if repeated".

| Item | Review # | Effort | Value |
|---|---|---|---|
| Global pause (ESTOP) — tray toggle, never kills in-flight work | 7 | S | Med |
| Repetition guard before the truncated-response continuation nudge | 8 | S | Med |
| Empty-response guard with a cost-aware retry budget | 9 | S–M | Med |
| Mark iteration-truncated child results for the parent | 10 | S | Med |
| Citation ledger inversion in `WebCitationExtractor` | 14 | M | Med |
| Meeting → action items: the *decisions* half, citations, and an on-demand path | 15 | XS | Low |
| Outbound webhooks on the existing timeline observer drain | 16 | M | Low |
| Timeout inventory, then one resolver if the count justifies it | 17 | S | Low |
| Adversarial UX test as a recorded WinWright flow + prompt | 18 | S | Low |

**#15 is half closed.** C4's `meeting-followup` blueprint ships the evidence-first framing the review
asked for; still open are the *decisions* half, a citation back to the transcript passage, and a path that
points at one named past meeting rather than at today's.

---

## Suggested order

Cheapest decisive work first, then the vertical slices. Everything below has landed except where marked.

```
A1 → A4 → P8 → B5 → A5             # gates and the cheap wins
B1 → B2 → B3 → B4 → B6…B10         # compaction: gate, then the sweep — closed, promoted nothing
B11 → B12                          # context-window defaults; what made the B track matter at all
C1 → C2 → C3 → C4 → C5 → C7        # blueprint vertical slice — C6 is the remainder
E1 → E2 → E3 · E4 → E5 → E6 → E7   # per-routine pins; E8 rides with C4, E7 is the Windows run
E9 → E10 · E11                     # resume semantics
G1 · G2 → G3 · G4 → G6 · G7        # failure legibility — G5 withdrawn, not deferred
D1 → D2 → D3 → D5                  # tour — D1 landed, the rest PARKED 2026-08-24
```

**Six rows are open, and none of them is the obvious next move.** `A2`, `A3`, `A6` and `A7` wait on a
supply re-read that costs a desktop session; `C6` waits on plan §11 Q4 plus a desktop pass; `D7` is a
tag-along. The deferred remainder, and what to pick up instead, is
[2026-08-25-bucket-3-handoff.md](2026-08-25-bucket-3-handoff.md).
