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
| **F** | Test hygiene — no plan doc; found while executing group A |
| **G** | Failure legibility — [Export Diagnostics](../failure_legibility/2026-08-24-export-diagnostics.md) (review #3) · [Failure layer + recovery actions](../failure_legibility/2026-08-24-failure-layer-plan.md) (review #2 slice 2); both promoted out of *not yet planned* |

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
| ~~**B4**~~ | B6–B10 | Does current compaction lose anything worth acting on? **Answered 2026-08-23: yes, totally.** Arm B scored **0.0%** against arm A's **98.3%** at an 8000/2000 window, on 4 of 4 transcripts - indistinguishable from having no transcript at all. The gate does **not** close; B6-B10 stay open. Reading: [2026-08-23-compaction-arm-ab-reading.md](2026-08-23-compaction-arm-ab-reading.md). **B6-B10 all closed 2026-08-24 and none of them promoted an arm** — see [2026-08-24-compaction-arms-cde-reading.md](2026-08-24-compaction-arms-cde-reading.md), whose §0.1 also narrows this row: compaction only ever runs on agent-run STEP turns, so half the corpus this gate was answered on models a path the product never compacts. |
| **G-Q1** | G5 | Does Retry re-dispatch the whole run from its goal, or resume from the failed step? **Unanswered.** It cannot be settled by preference: re-dispatch duplicates every write the run already made, resume-from-step needs the step ledger to be trustworthy after a fault, and `SafeToReRun` only makes re-dispatch safe for the *pre-model* cases — a set narrow enough that a Retry gated on it may be worth very little. Answer with the per-layer duplicate-write analysis. §4 of [../failure_legibility/2026-08-24-failure-layer-plan.md](../failure_legibility/2026-08-24-failure-layer-plan.md). |
| **D-Q1** | D3–D6, D8 | Is the goal onboarding (a canned tour, no LLM) or arbitrary "where do I…" questions? **Still unanswered, and its dependants are PARKED as of 2026-08-24** — [../guided_tour/2026-08-24-d-track-parked.md](../guided_tour/2026-08-24-d-track-parked.md). No longer blocks `D7`, which was severed from the track and stays open. |

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
  **Re-read on 13 runs / 41 step outcomes, 2026-08-23: 9 True — 22.0%. Still deferred, and now on a
  measured number rather than 2 events.** The band was fixed before the app opened: ≥40% build, ≤12% drop,
  and 12–40% defers *while naming what would move it*. Two named triggers, in §8 of
  [2026-08-23-a2-wide-read.md](2026-08-23-a2-wide-read.md): **build** if a comparable corpus clears 40%,
  **or** if one run is found where the report channel names an artifact the probe never saw — that second
  one is the only thing A2 can do that the probe cannot, and this corpus produced its opposite. **Drop**,
  and reopen A7 separately, if the share falls back toward 12% on a mix *not* dominated by file-producing
  prompts; 9 of the 13 runs declared a file, which is a property of the runbook's categories and not of a
  real day.
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

- [x] **P8 · Say that `expectedArtifact` is relative to the working folder.** Neither surface said which
  root a name is relative to, and on the post-P1 replay that cost a run its artifact: a replan declared
  a **rooted** path, the executor called `write_file` with that same path, and the sandbox refused it —
  *"Path is outside the assistant files folder"* — so nothing was written. Worth doing whatever caused
  the rootedness (the goal named the project, so a project subfolder is an ordinary response; `n = 1`
  per arm either way). Deliberately **not** folded into P1: changing the wording that produced a reading
  without re-measuring makes the reading unfalsifiable. Re-measure with it.
  **Landed 2026-08-23** on the same two surfaces P1 used — `:159` and `:782` — and *not* on `:139`/`:148`.
  Two reasons, both in §10 of [2026-08-23-a4-replay-reading.md](2026-08-23-a4-replay-reading.md): §1's
  argument for leaving `:139`/`:148` alone does not reach `:782`, which is a second full statement of the
  field's contract rather than a summary of a container; and `:159` is load-bearing because a **replan turn
  gets no grounding fence at all**, so on the very turn that produced the rooted path the schema
  description is the only place the working folder is named. **A third comparability cut** — §5's 6/6 and
  100% were measured on the pre-P8 wording, so the wide read is not a delta against them.
  **Re-measured 2026-08-23 and it held.** Not one declaration in 13 runs carries a rooted path. The same
  Ledger README prompt that produced `/Ledger/README.md`, a refused `write_file` and `notFound=1` before
  P8 declared plain `README.md` twice, wrote it, and probed `found=2`. `n = 1` per arm, and the cheapest
  reading of the original rootedness — the goal named the project — is still not excluded.
  *Deps:* P1 · *Effort:* **XS** · *Value:* **Med**

- [x] **P9 · Investigate the step that reported `succeeded=True` on a refused tool call.** The one loose
  thread the replay left in prose. **Not A3, and not blocked by A2:** A3 tests the *artifact* channel
  (declared-but-absent) and waits behind A2, while this is the *success-determination* channel — what
  makes a step report success at all — and nothing gates it. Scoped **investigate, not fix**: the model
  did answer in prose after the sandbox refused its only `write_file` call, so whether `succeeded` is
  wrong depends on what a step outcome is meant to assert. Three readings and the reason this is worth a
  row — the run-level probe caught what the step outcome missed, while the report channel A2 would have
  widened had nothing to say — are in §11 of
  [2026-08-23-a4-replay-reading.md](2026-08-23-a4-replay-reading.md). Answer first whether anything in the
  step outcome should change, or whether the missing piece is only that a refusal is surfaced nowhere.
  **A second instance, 2026-08-23:** `2dcc6fd2` persisted five declarations and **failed**, yet its only
  verify pass saw three — so a run that fails after its last verify never reports what it declared
  afterwards. Same shape as the original: work the tally cannot see. §10 of
  [2026-08-23-a2-wide-read.md](2026-08-23-a2-wide-read.md), trap 2.
  **Answered 2026-08-24, no production change** —
  [2026-08-24-p9-refused-write-reading.md](2026-08-24-p9-refused-write-reading.md). The call was **never
  refused**: `write_file` was granted, took the gate's `AutoRun` arm, executed, and returned
  `WriteResult.Failed` from `FilesToolHandler` itself. So reading 1 holds and the step outcome needs nothing —
  the tool result is not an input to either `succeeded` branch, and the executor never sees it. The gap is that
  `AgentTimelineOutcome.Ok` means "`Execute()` returned", not "it worked", and the panel's `OutcomeSuffix`
  fires only on `Error` (a *throwing* tool), so an executed-but-failed call renders exactly like a successful
  one. No cheap fix: the timeline is metadata-only, so it cannot store the error, and there is no shared
  failure envelope to read — `write_file` alone returns a structured `success` field against ~118 bare
  `"Error: …"` string sites, neither convention enforced. §5 of the reading ranks what would be worth
  building; a `Failed` outcome populated only from the unambiguous `WriteResult` shape is the top candidate.
  The **second instance is a separate, measurement-only defect**: the drain loop's `if (cancelled || failed)
  break;` sits immediately before the verify pass, so a failed run never re-probes by design. Read
  `AgentSteps.ExpectedArtifact` rather than a probe line's `declared` (already the A2 wide read's traps 1–2);
  `TryBuildArtifactFactsAsync` is a pure filesystem probe with no provider call, so running it alone on the
  failure path would complete the tally free — but that is A6/A7's, not this row's.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**

---

## B — Compaction recall

- [x] **B1 · Synthetic transcript generator with planted facts.** Committed; no real user data.
  *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

- [x] **B2 · Corpus extraction script** (`AssistantChatMessages` → JSON fixture, gitignored).
  *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

- [x] **B3 · Question-bank generator, per-transcript cache, and the verbatim-leak filter.**
  The leak filter is what stops restatement luck from inflating every arm.
  **Done 2026-08-23** - `Integration/Compaction/CompactionRecallHarness.cs` and `CompactionRecallTests.cs`,
  beside B1, no new project.
  - The cache key is **(transcript fingerprint, window, max output)**, per the plan's §15 answer 2: the
    removed set belongs to the *pair*, so a transcript-only key would answer the second budget's questions
    from the first budget's removed set - a wrong number that looks right.
  - Three **ordinary gate tests** hold the parts that decide whether any number means anything. The bank asks
    only about removed content (each gold answer occurring exactly once in the removed trace and zero times
    in the retained one); the leak filter drops a fact the tail restates, leaving exactly one fewer question;
    and two budgets write two cache files, with a 64k window producing an **empty** bank - the clearest proof
    the key is not transcript-only.
  - The filter reads `SyntheticTranscript.Trace`, not `message.Text`: it flattens tool-call arguments and
    tool-result payloads too, so an answer hiding in a tool result cannot pass a text-only check.
  - Provider comes from configuration (`PIA_COMPACTION_PROVIDER`, resolved against `providers.json` with the
    real `DpapiHelper`), skips when absent, and never falls back to a local model. Temperature 0 goes through
    the production handler because the shipped `CreateChatOptions` sets none. The fixture path still spends
    one generation call per real transcript; it is implemented and unused by the synthetic corpus, which
    ships its own gold answers.
  - The three live entry points report **Not Run** in the gate, verified by arithmetic rather than by reading
    the attribute: `succeeded` went 4610 -> 4618 (+8, exactly the new gate tests) and `skipped` 54 -> 57
    (+3, the live ones), at `failed: 0`.
  - `Export-CompactionCorpus.ps1` (B2) **never parsed** - `"$resolved:"` in its containment throw is read as
    a drive-qualified variable. Fixed. It still cannot run here: `sqlite3` is on neither PATH nor in the
    winget store, so the DB reading for §2 of the reading doc was done from a copy through `node:sqlite`.
  *Deps:* B1 · *Effort:* **M** · *Value:* **Enabler**

- [x] **B4 · Arms A (uncompacted) + B (current), judge, scorecard writer.**
  First real number. If A scores < 90%, the instrument is broken — fix that before reading anything.
  **Done 2026-08-23. The first real number, and it is a floor rather than a shortfall:** arm A **98.3%**,
  arm B **0.0%**, on all four shapes at an 8000/2000 window, 240 calls in 5m27s with
  `mistral-medium-latest` answering and judging at temperature 0. Full reading, pre-registered before the
  first call: [2026-08-23-compaction-arm-ab-reading.md](2026-08-23-compaction-arm-ab-reading.md).
  - The instrument cleared its own floors three ways: arm A above both the 90% and the synthetic 95% bar; a
    **no-context control arm at 0.0%**, which the plan did not ask for and which rules out the subtler cousin
    of restatement luck (the planted answers are formulaic, so a model able to extrapolate `PIA-E` + index
    would have scored on arm B without recalling anything); and every bank landing at the full 15, meaning
    arm B's retained context held no planted fact at all.
  - **B ≥ 85% of A is refused on 4 of 4.** Arm B equals the no-context control exactly, which is the plan's
    §2 fact 1 with a number on it: nothing is summarised, so an evicted message is gone. Dropping 78% of the
    tokens took 100% of the removed facts.
  - Corpus is the **synthetic** generator, not the real profile, and that is itself a finding: 99 real
    messages exist in total, and `AssistantChatMessages` can carry neither a tool round nor an attachment, so
    two of §5's four shapes are unreachable through its SQL rather than merely absent. Synthetic arm B is a
    **lower bound**: the claim it supports is *a fact stated once and evicted is gone*, 60 of 60 questions.
  - Second budget still owed. No configured provider sets `MaxContextWindowTokens`, so there is no real
    window to measure and the harness skips rather than inventing one.
  - Learned the hard way, recorded so the next sweep does not: concurrency 3 earns a 429 from Mistral (the
    sweep now paces at 1.1 s with capped exponential backoff at concurrency 2), and one provider fault on
    the fourth transcript used to discard all three earlier transcripts' calls.
  *Deps:* B2, B3 · *Effort:* **M** · *Value:* **High** (decision gate)

- [x] **B5 · Pin the "user messages are never compacted" invariant with a test** against
  `Microsoft.Agents.AI.Compaction`. Pia pins the head goal and newest instruction; middle user
  messages are not pinned. *(From review rec #5, not the test plan.)*
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**

- [x] **B6 · Arm C — mechanical anchor index.** Biggest single win in hermes's scorecard.
  **Built and measured 2026-08-24; NOT promoted.** Reading: [2026-08-24-compaction-arms-cde-reading.md](2026-08-24-compaction-arms-cde-reading.md).
  `CompactionArms.AnchorIndex` appends one block naming, per source message and with its original transcript
  position, the identifiers found verbatim in what was dropped. Extractors are written against the plan's
  candidate list and tested against sentences the corpus never wrote, so the regex is not tuned to the
  generator. **Scored 48.3% against arm B's 0.0% — +48.3, wins 4 of 4, which fires §11's promotion rule.**
  Do not build from that number. Three findings, in the order that matters:
  - **The stimulus is a 100%-precision answer key.** On 3 of 4 shapes the block is exactly 13 lines, one anchor
    each, and all 13 are gold answers — zero distractors. Both that precision AND the 152-token size follow
    from ONE unrelated design choice: the generator's filler is lowercase-letters-spaces-periods, so nothing
    but the planted facts survives the extractor. Cost and benefit are one corpus property stated twice.
  - **Measured density off this corpus: 0.0 anchors/1K tokens in the filler against 5.2–22.6 in repo docs,
    diffs, logs and C# source** — projecting to ~800–1,700 tokens for the same dropped region. A plain
    `find src -type f` tool result runs 57.4/1K and yields a block **99.8% the size of the body it indexes**.
    There is no cap, and adding one introduces a selection policy nothing measured.
  - **The structural half is inert where it would ship.** Both round-0 seams build text-only messages
    (`AssistantMessage.ToChatMessage()` emits only `TextContent`/`DataContent`; `HeadlessTurnExecutor`
    rebuilds rows as plain text), so the `FunctionCallContent.Name`/`CallId` reads never execute there. They
    run only in the in-step tool loop, whose messages are never persisted — the same path arm D cannot reach.
  Also: the block is appended AFTER the compactor fit the window, so arm C never pays its own cost.
  *Deps:* B4 · *Effort:* **M** · *Value:* **High**

- [x] **B7 · Message-level search granularity.** `AssistantChatsFts` is per *chat*, not per *message* —
  either add a message-level index or scope recovery to a direct `AssistantChatMessages` query.
  **Done 2026-08-24 — the scoped query, deliberately NOT the index.** `IAssistantChatService
  .SearchMessagesAsync(chatId, term, limit)` returns `AssistantChatMessageHit(MessageId, Ordinal, Role,
  Snippet)` from a `LIKE` over one chat's rows. **Read this as "the reader exists", not "the index exists".**
  Plan §7 keeps arms C/D/E as harness prototypes that touch no shipped code until §11's bar is cleared, and a
  second FTS table needs backfill plus save/delete/evict maintenance — bought before the measurement that
  authorises it. B4 also measured the real corpus at **99 messages total**, so a substring scan over one chat
  is not a performance question yet. **B10 answered this and the index is still not owed** — arm D cleared the
  rule's bar, but a `LIKE` over `AssistantChatMessages` cannot see the tool content eviction reaches first, so
  the missing piece is a store that holds it, not an index over one that does not.
  - `Ordinal` is the whole point over `AssistantChatsFts`, which can only name the conversation: a hit has to
    be citable back to a position in the transcript.
  - A blank term returns nothing rather than every row — `'%%'` would turn recovery into a transcript dump.
  - The term is `LIKE`-escaped with an explicit `ESCAPE '\'`, because the things worth recovering are exactly
    the ones containing `%` or `_`: an error string, a file path, a quantity. Proved non-vacuous by removing
    the escaping and watching `SearchMessagesAsync_TreatsLikeWildcardsInTheTermAsLiterals` fail.
  - The snippet is a window **around the match**, not the head of the message: a hit whose snippet omits the
    term cannot be used to decide whether to read the row.
  *Deps:* none · *Effort:* **M** · *Value:* **Enabler** (hard prerequisite for B8)

- [x] **B8 · Arm D — recovery pointer.** Worth +20 to +43 pts standalone in hermes's run.
  **Built and measured 2026-08-24; the one clean empirical result, and NOT a compactor change.** Reading:
  [2026-08-24-compaction-arms-cde-reading.md](2026-08-24-compaction-arms-cde-reading.md).
  **+24.2, wins 4 of 4, with `gold=0` on all four** — arm D's own context held none of the answers, arm B was
  0.0% and the no-context control was 0.0%, so its points really are search-earned. That is where it stops.
  - **§11 promotes an arm "to a real implementation proposal against `AgentContextCompactor.cs`", and arm D
    cannot be one.** The compactor returns a `List<ChatMessage>`; the only half it could emit is the footer,
    and the footer alone earns nothing.
  - **The store the mechanism needs does not exist on either path.** `SearchMessagesAsync` has zero production
    callers (B7 wrote it for this row); `AssistantChatMessages` holds user/assistant text plus a
    `ToolCallCount` **count**, so a real search is blind to exactly the tier eviction reaches first
    (threshold 0.45); and at the in-step loop `AiClientService.cs:236-241` says the list holding tool content
    "is discarded when the loop ends". **The footer's promise that dropped messages are "still stored and
    searchable" is false in the product for that content.**
  - **The harness searched an oracle** — the in-memory `removed` set with tool arguments and results rendered
    in. And the tell: `chat-tool-heavy` is arm D's BEST transcript (33.3%) and is the one shape whose facts
    land in tool payloads (5 of 15). It looks strongest exactly where it would degrade most.
  - **The hermes +20…+43 comparison is dropped**, not cited: different codebase, real ~500K transcripts, a
    summarising rather than evicting compactor, and an actual retrieval path.
  - **The search-rate lever is real but not diagnosable from this run.** 51.8% conversion on 28 searches,
    searched on only 6–8 of 15 — but both levers are worth ~the same (all-15 at today's rate ≈ 51.8%; perfect
    conversion on today's 28 ≈ 46.7%), and `Recovered` increments when a term PARSES, before any hit exists,
    while `RenderHits` returns a non-null "no matches" string. No trace callback was passed, so no term, hit
    count or second-round answer was captured. A rerun must log all three.
  Cost is understated by construction: 88 answering calls against 60 for B and C, ~+51% input tokens, and a
  second sequential round-trip the user waits on.
  *Deps:* B4, B7 · *Effort:* **M** · *Value:* **High**

- [x] **B9 · Arm E — pin all user messages.**
  **Built and measured 2026-08-24; the rule was INAPPLICABLE, not refusing.** Reading: [2026-08-24-compaction-arms-cde-reading.md](2026-08-24-compaction-arms-cde-reading.md).
  Scored +6.7 averaged and won 1 of 4, which reads as a §11 refusal and is not one. **On three of four shapes
  every planted fact sits on an assistant message, so pinning user messages had nothing to pin: arm E's context
  held zero gold by construction and its 0.0% there is FORCED, not measured.** (Reproduced from the generator's
  index arithmetic and matching the reported gold 0/0/0/5 exactly.) Averaging three forced zeros is what pulls
  **+26.7 down to +6.7** — and on the one transcript where the treatment could act, arm E **cleared the bar**.
  Its 3.325-point shortfall against the 10-point line also sits inside the plan's own ±3–5 judge noise, so only
  the "3 of 4" clause refused it, decided by three no-op cells. §11's anti-luck row applies instead: suspected
  restatement luck until a fifth transcript reproduces it.
  **Withholding user messages from compaction is untested, not refused.** Predicting the forced zeros before
  any call spend was a statement about the corpus, not about pinning.
  *Deps:* B4 · *Effort:* **S** · *Value:* **Med**

- [x] **B10 · Full sweep, scorecard, findings** — including what was luck.
  **Done 2026-08-24: [2026-08-24-compaction-arms-cde-reading.md](2026-08-24-compaction-arms-cde-reading.md).** One sweep, six columns (A, B, C, C0, D, E) × 4 transcripts on
  `deepseek/deepseek-v4-flash` answering and judging at temperature 0 — all arms together, because the model
  is part of the measurement and B4's Mistral baseline cannot serve a DeepSeek arm. 37 minutes, ~750 calls,
  **no 429 and no lost transcript**: the pacing and per-transcript try/catch the arm-A/B reading added both held.
  **The sweep's most useful output is not a ranking of arms — it is four defects in the instrument**, and the
  first one is why nothing here may drive a build:
  - **Compaction runs on agent-run STEP turns only.** All four `CompactAsync` seams
    (`ChatSession.cs:953`, `HeadlessTurnExecutor.cs:469`, `AiClientService.cs:248` and `:660`) are gated
    on a non-null budget; interactive chat passes null *by design* (`ChatSession.cs:766`) and
    `BackgroundAssistantTurnRunner` passes nothing. **The two chat shapes — 30 of 60 questions — model a list
    the product never hands the compactor.**
  - **Effective N is one, not four.** `guidPrefix` is drawn before any shape branch and no caller overrides
    `Seed = 20260822`, so all four transcripts plant byte-identical gold answers and ask an identical bank;
    the two agent shapes are the same branch twice. §11's "wins on ≥3 of 4" clause cannot do its anti-luck job.
  - **The 8000/2000 window was an env override**, 0.76% of the answering provider's 1,048,576 catalogue window
    — at which every transcript here would pass through uncompacted, i.e. as arm A. Neither half of that may be
    printed without the other: the fallback is 128,000, `deepseek-r1` is 64,000, and the in-step tool loop can
    overflow any window from inside one step.
  - **One model answered and judged**, so leniency raises exactly `C−B`/`D−B`/`E−B` while arm B sits pinned
    at the floor. No per-arm correct/partial/wrong split was captured; it is owed.
  **Two §11 rows the reading applies by name that the plan left silent:** the "B ≥ 85% of A" row does **not**
  fire (B/A = 0.0% everywhere — the pre-registered "compaction is fine" outcome is refused); and **the control
  arm C0 satisfies the promotion rule as written** (+20.0, 4 of 4), because §11 has no control carve-out. That
  is a gap in the plan, recorded rather than quietly exempted.
  **Verified adversarially before it was written.** Four independent skeptics on distinct lenses (arithmetic,
  arm-C-as-artifact, arm-D-mechanism, external validity) refuted claims 3, 5, 6 and 8 of the intended reading —
  including a real arithmetic error ("3.6% on three transcripts" is 3.6%/3.9%/8.7%) — and the load-bearing
  structural claims were then re-checked in source rather than taken on report. §7 of the reading lists the
  nine instrument fixes the next sweep needs, seven of which cost no provider call.
  *Deps:* B6, B8, B9 · *Effort:* **S** · *Value:* **High**

- [x] **B11 · Per-provider context-window defaults — 128k for an unknown model, a table for known ones.**
  **Owner decision, 2026-08-24.** Found while reading B4's "no configured provider sets a window": that is
  not a gap in the profile, it is the shipped state for everyone. `MaxContextWindowTokens` is a **hand-typed
  field in the provider editor** (`ProviderEditModel.cs:148`/`:171`), never fetched from any API — no
  provider reports one — and it defaults to null, so `AgentContextBudget.From` returns null and
  `AgentContextCompactor.CompactAsync` returns the message list unmodified. **Compaction is off for every
  user today.**
  The decision is to default **128k for any unknown model** and author a table of known ones.
  - **This ships before B6/B8/B9, and that is deliberate.** With no budget the full list goes to the
    provider, so a chat past the real window fails *provider-side* — a hard error. The default converts that
    into graceful degradation, which is better even at B4's 0% recall of evicted content. An 8000-token
    default would have been the opposite call; 128k is generous enough that compaction fires only on
    genuinely long chats, which is what makes shipping first safe.
  - **It also promotes B6/B8/B9 from lab result to live defect.** They improve a dormant path today; once
    this lands they improve one that fires.
  - `MaxOutputTokens` needs no default — `From` accepts a null (→ 0) as long as it is below the window.
  - The field is deliberately off `SyncProvider` and off `ProviderFingerprint.Compute`; a default must not
    change either.

  **Done 2026-08-24, and where the default lives is the whole finding.** `ContextWindowDefaults.For(modelName)`
  plus a stamp in `ProviderService.LoadProvidersAsync`, which every one of the eight provider reads now goes
  through. The window is filled into the loaded object and **not written back**: the editor binds what it is
  given, so the user sees the assumed number and can change it, and a window nobody edited stays out of
  `providers.json`.
  - **The first attempt put the default in `AgentContextBudget.From` and it was wrong — 80 tests failed.**
    `From` is a pure reader shared by three call sites, one of them the in-step tool loop, so defaulting there
    gave every bare `AiProvider` in the process a budget, including stubs that never came from persistence.
    Turn tests across `ChatSession`, `LiveTurnExecutor`, `AgentRunOrchestrator` and `MidPlanAsk` started
    compacting and the suite went from 27s to 55s. Moving the policy to where providers are *constituted* fixed
    all 80 without touching one of them, and the runtime came back — which is the confirmation it was a design
    error rather than stale expectations. `From` stays null for an unconfigured provider, and its doc says why.
  - **The table is source-gated.** Only the Claude family ships, from the `claude-api` skill's model table
    (1M for Fable 5 / Opus 5 / 4.8 / 4.7 / 4.6 / Sonnet 5 / Sonnet 4.6, 200K for Haiku 4.5) — reachable here
    through OpenRouter or OpenAICompatible, since Pia has no Anthropic provider type. Everything else takes the
    128k fallback rather than a guess: a wrong row is worse than no row, because too high sends a request the
    provider rejects and too low silently evicts context. `NoRowShadowsAnother` guards the substring matching.
  - **Local models are the known soft spot and it is a no-op, not a regression.** An Ollama model with a 4k
    window gets 128k, so compaction never fires and Ollama keeps sliding its own window — exactly today's
    behaviour. Guessing low would have *added* eviction where there was none.
  - ~~**The real answer is live discovery, and it is one refactor away.**~~ **Done for OpenRouter,
    2026-08-24** — see `B12` below. Still open for the Anthropic Models API (`max_input_tokens`), which Pia
    has no provider type for.
  - Gate **4740 / failed: 0**; both configurations rebuild to 0 Warning(s). Covered by
    `ContextWindowDefaultsTests` (the table, the fallback, case-insensitivity, shadowing) and
    `ProviderContextWindowStampTests`, which writes a real `providers.json` to a temp directory, reads it back
    through the service and asserts the budget resolves.
  *Deps:* none · *Effort:* **S** · *Value:* **High**

- [x] **B12 · OpenRouter reports its own window — read it live, and take the field off the form.**
  **Owner decision, 2026-08-24.** B11's premise was that no provider API reports a context window.
  **OpenRouter does**: `GET /api/v1/models` is public, needs no key, and every one of its 422 models carries
  one. Snapshot and refresh recipe:
  [`../openrouter_models/2026-08-24-openrouter-context-lengths.md`](../openrouter_models/2026-08-24-openrouter-context-lengths.md).
  - **`top_provider.context_length`, never the advertised `context_length`.** They differ for **42 of 422**
    and the advertised figure is the larger, so using it would size requests the route refuses —
    `thedrummer/unslopnemo-12b` advertises 1024000 and serves **32768**, a 31x overshoot;
    `meta-llama/llama-4-scout` 1310720 against 327680.
  - **Read live on every save** (`ProviderService.ApplyOpenRouterContextWindowAsync`), because the value
    moves: alias ids float to whatever the author ships, and OpenRouter re-routes models between hosts. A
    failed lookup keeps the snapshot's value rather than failing the save.
  - **The field is hidden for OpenRouter** in `ProviderEditContentDialog`, through the existing
    `ProviderTypeToVisibilityConverter` rather than a new mechanism. Hidden, not disabled — a value the save
    overwrites is worse than no box at all.
  - **Normalisation was the trap, and the naive rule was measured wrong.** OpenRouter ids are namespaced,
    alias rows carry a leading `~`, and a `:variant` suffix selects a route. Collapsing `:variant` onto the
    base looked safe — 64 of 72 variants match their base — but **8 do not, mostly smaller**:
    `poolside/laguna-s-2.1:free` serves 262144 against the base's 1048576, `z-ai/glm-5.2:free` 256000
    against 1048576. Stripping would have overshot 4x for free-tier users. The rule is **exact id first,
    base only as a fallback**, shared by the snapshot and the live read through
    `OpenRouterContextWindows.LookupKeys` so they cannot drift. Three variants have no base row at all.
  - **A test caught a real crash path**: `JsonElement.TryGetInt32` *throws* on a JSON null rather than
    returning false, and a null `context_length` is a shape the payload can carry.
  - The seed table is generated from the doc, not transcribed — 422 rows, 42 overrides applied, verified for
    key collisions before it was written.

  **Reframed 2026-08-24 by the owner, and the table's justification changed with it.** Asked why a 422-row
  table exists when the value is fetched live, and what other providers get from it. Honest answers: the fetch
  runs **on save only**, so an OpenRouter provider nobody re-opens runs on the snapshot indefinitely; and
  other providers got **nothing**, the lookup being gated on provider type. The owner's call was *don't keep
  the table for the OpenRouter case alone* and *do try to resolve for everyone, fuzzily, with a generous
  default when in doubt*. So:
  - **The gate is gone.** `ContextWindowDefaults.For(modelName)` is one path for every provider type — the
    vendor-documented family table, then the catalogue, then the 128k floor. No refresh-on-load machinery was
    built: an existing provider simply carries the resolved value until its next save.
  - **The catalogue is now a cross-vendor registry**, reached by bare ids too. Measured before building:
    350 distinct basenames, and `gpt-4o` → 128000, `o3-mini` → 200000, `deepseek-chat` → 128000,
    `gemini-2.5-pro` → 1048576 all resolve. Ollama's short tags (`llama3`, `phi4`) do not, and take the floor.
  - **Separator folding closes the convention gap the owner flagged**: OpenRouter publishes
    `claude-haiku-4.5` where Anthropic's own id is `claude-haiku-4-5`. Folding `.` to `-` makes them meet and
    was measured to add no ambiguity. Longest-prefix-on-a-boundary then catches dated ids
    (`gpt-4o-2024-08-06`) and `-latest` aliases.
  - **Two bugs the new tests caught, both real.** `LookupKeys` normalised without the fold while the index
    applied it, so every id containing a dot missed — `z-ai/glm-5.2:free` fell through. And a *conflicted*
    basename then fell through to the prefix search and took `glm-5`'s window for `glm-5.2`, a different
    model. A known-but-ambiguous name now stops rather than falling through, which is what "generous default"
    has to mean.
  - **Caveat to keep in view:** these are `top_provider` values — what OpenRouter's route serves, which can
    sit below what a vendor serves directly (`anthropic/claude-sonnet-4` is 200000 here against 1000000
    advertised). For a direct provider that biases low, compacting early rather than sending a request the
    provider refuses.
  - Gate **4783 / failed: 0**; both configurations rebuild to 0 Warning(s).
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

- [x] **D1 · Visual-tree target collector + a debug command that dumps `targets`.**
  Verifiable with no LLM in the loop.
  **Survives the 2026-08-24 parking of the rest of the track, deliberately.** `TourTargetWalker` +
  `TourTargetCollector`, registered at `Bootstrapper.cs:437`, dumped by
  `MainWindowViewModel.DumpTourTargetsAsync` on **Ctrl+Shift+F12**, with 363 lines of tests. Kept because it
  is the instrument `D7` needs and costs one singleton to carry — not because the tour is imminent.
  *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

> **⏸ D2–D6 and D8 are PARKED by the owner, 2026-08-24.** Entry point for resuming, including what has
> already shipped, what will have rotted, and the ~3–4 weeks plus a desktop session it costs:
> [../guided_tour/2026-08-24-d-track-parked.md](../guided_tour/2026-08-24-d-track-parked.md). The design is unchanged and still executable —
> [2026-08-22-guided-tour-tool-plan.md](2026-08-22-guided-tour-tool-plan.md).
>
> Parking is a scheduling call, not a verdict: nothing measured says the tour is a bad idea. The plan's own
> risk table predicted it — *"the cheaper items in the review (blueprints, error surface, diagnostics) should
> land first"* — and blueprints landed while the error surface and diagnostics did not.
>
> **`D1` stays in the product** (`TourTargetWalker`, `TourTargetCollector`, **Ctrl+Shift+F12 in DEBUG
> builds only**, 363 lines of walker tests). Kept because it is the repo's only *runtime* AutomationId
> inventory and its blind spots are complementary to the static `ViewAutomationIdTests` sweep — it sees
> container-style ids, template ids whose `OnApplyTemplate` never fires, and realized virtualized rows, all
> named playbook gaps. **It cannot find MISSING ids** (the walker only offers elements that already have one),
> so its contribution to `D7` is the *confirmation* half, not the gap list. Removing it would touch 8 files
> including two non-tour-local edits, so carrying it is cheaper.
>
> **`D-Q1` is still the thing to answer first**, and it decides whether this is a tool or a control:
> onboarding ⇒ a canned tour with no LLM and no `D3`; arbitrary "where do I…" questions ⇒ the generic tool as
> planned. `D2` is not gated on it, but building `D2` first buys a demo rather than a feature.

- [ ] **D7 · AutomationId gap-fill** for surfaces a tour needs but cannot address; feeds
  `docs/ui_automation/ui-automation-playbook.md`. Also improves UI-test coverage.
  **Kept open as a TAG-ALONG, not as scheduled work (2026-08-24).** It never depended on `D-Q1` and `D1` is
  done, so it is technically startable — but **scheduling it as its own row is reopening a track the owner just
  parked.** Its substance (add the id, bump the `[InlineData]` count in `ViewAutomationIdTests`, update the
  playbook's "Known gaps") is what any UI change should be doing anyway, so fold it into the next UI work
  rather than picking it up alone. Regenerate the gap list from `ViewAutomationIdTests`' `IdKind.Missing`
  and the playbook — **not** from a Ctrl+Shift+F12 dump, which by construction only shows ids that already
  exist.
  *Deps:* D1 (satisfied) · *Effort:* **S** · *Value:* **Med**

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

- [x] **E7 · Verification handoff — the only thing that can turn this group green.** `dotnet build
  -t:Rebuild` in both configurations, `dotnet test` with no filter, an eyeball pass in the real app, and
  **one open against a pre-change profile** — migration half (b) is the `ALTER TABLE` path, and every test
  and every fresh profile takes the `CREATE TABLE` path instead.
  **Done 2026-08-23**, all four halves. Both configurations rebuild to 0 Warning(s); `dotnet test` with no
  filter is `failed: 0`. The pickers offer *"Use the active persona"* + all 12 personas (not roster-gated,
  as E5 intended) and *"Use the persona's setting"* + all six efforts with readable labels; a routine saved
  with **Experienced Coder / Extra high** persisted both pins and kept them across a Disable toggle. The
  **"No longer available"** row was machine-checked with `ww_assert_value` against a job whose `PersonaId`
  names no persona — and the effort pin beside it still read *"Extra high"*, so the two degrade
  independently. Migration half (b) ran against a copy of the real `history.db` with **both pin columns
  dropped**, so `CREATE TABLE IF NOT EXISTS` was a no-op and only the ALTER pass could restore them; it
  did, the app started clean, and both pins then round-tripped through the editor into columns that exist
  only because the migration added them. Detail in §12 of
  [2026-08-23-a2-wide-read.md](2026-08-23-a2-wide-read.md).
  *Deps:* E6 · *Effort:* **XS** · *Value:* **High**

- [x] **E8 · Blueprint effort defaults; no persona default.** `RoutineBlueprint.DefaultEffort` is set on
  all eight cards and `StartFromBlueprint` carries it into the editor. A persona default is deliberately
  absent: a built-in id can be hidden by `BlockedBuiltInPersonas`, and a catalog cannot know the user's
  own personas.
  *Deps:* E1, C4 · *Effort:* **XS** · *Value:* **Med**

- [x] **E9 · Persist the resolved run persona and effort on the `AgentRuns` row.** Closes the resume gap
  the review surfaced: both pins are resolved per dispatch and never stored, so a scheduled run that parks
  at its budget resumes on the current mode persona at the mode default effort. One seam, and it closes
  both pins without giving the launcher a dependency on the job store.
  **Landed 2026-08-23.** Two nullable TEXT columns with both migration halves, appended to the END of
  `RunColumns` because `MapRun` reads by ordinal; the launcher writes what the dispatch *resolved* (not what
  the request asked) and the resume reads it back off the row. Both claims — a budget park and a user pause —
  funnel through one `ResumeAsync`, so there is one read-back site, not two. `Guid.Empty` is not normalized
  to NULL on write the way `ScheduledJobService` does it; harmless, because `RunPinResolver` guards on it.
  The **interactive** Planned origin (`ChatSessionManager`) got the persona too — it resumes through the same
  launcher, so a user who moved the persona picker while reading a plan would otherwise have the remaining
  steps run as someone else. Its effort is deliberately left null: that path derives effort purely from the
  persona, so null re-derives the same value.
  Pinned by `AgentRunPinPersistenceTests` (round trip, both readers, the unknown-effort degrade, and the
  `ALTER TABLE` half via `DROP COLUMN`) and by
  `HeadlessRunLauncherTests.Resume_RunsThePersonaAndEffortTheLaunchResolved_NotTheCurrentModeDefault`, which
  **moves the per-mode default while the run is parked** — verified non-vacuous by reverting each half of the
  read-back separately and watching that half's assertion fail.
  *Deps:* E3 · *Effort:* **S** · *Value:* **Med**

- [x] **E10 · Carry the launch's provider across a resume, not just the persona.** Found while landing E9,
  and **pre-existing**: `ResumeAsync` passes `explicitProviderId: null`, so a scheduled job that pinned an
  explicit `ProviderId` (`ScheduledJobBackgroundService` does populate it) runs its remaining steps on
  whatever the persona/mode ladder answers instead. E9 did not introduce this and makes it *better* in the
  common case — the resume now walks the ladder from the run's own persona, which is the persona the launch
  used — but the explicitly-pinned case is untouched. No new column needed: the launch already writes the
  resolved provider onto the run's stub chat (`AssistantChats.ProviderId`). Deliberately out of E9's scope
  rather than smuggled in, and it wants the cheap accessor first — `IAssistantChatService.GetAsync` returns
  the chat *with its messages*, which is not something to pay for on every Continue.
  **Shipped 2026-08-24.** `IAssistantChatService.GetProviderIdAsync` is that accessor (one scalar SELECT, no
  message read), and `ResumeAsync` hands its answer to `ResolveProviderAsync` as the explicit rung. Two
  fallbacks kept the change from being able to make a park *worse* than before: a store fault is logged and
  answers null, and a provider deleted during the park already fell through `ResolveProviderAsync`'s ladder,
  so neither can turn a resumable run into an unresumable one. Pinned by
  `Resume_RunsTheProviderTheLaunchResolved_NotTheCurrentModeDefault`, which pins **both** an explicit provider
  and an effort — the shape a scheduled job actually launches with, and the one where the launch stamps the chat
  off `ApplyEffort`'s clone rather than the stored provider, so an Id dropped in cloning would have made the
  row a silent no-op with every other assertion green. Plus
  `Resume_WhenTheLaunchProviderIsGone_FallsBackToTheLadder` and a service-level round-trip. Verified
  non-vacuous by restoring `explicitProviderId: null` and watching the first assertion fail.
  **Not covered:** whether an *interim* chat write preserves `ProviderId`. `BuildChatSnapshot` sets it
  explicitly, but a park at `WallClock: TimeSpan.Zero` leaves the step Pending, so no interim persist fires in
  these facts and the assertion sees only the launch's stub.
  *Deps:* E9 · *Effort:* **XS** · *Value:* **Med**

- [x] **E11 · Decide what a null persisted effort should mean on resume.** The freeze E9 installs is
  asymmetric, and one direction contradicts the other. A non-null effort is frozen (the launch's value wins
  even if the persona is later edited); a null one is not, because null re-enters at the jobPin rung and
  falls through to the persona's **current** effort — so a job with no effort pin, on a persona whose effort
  is edited during the park, resumes at an effort the launch never used. Observed directly while proving the
  E9 test non-vacuous: with the effort read-back reverted the assertion read `Low`, the pinned persona's own
  value. Distinguishing "resolved to nothing" from "predates the columns" needs a sentinel or a separate
  recorded-pins marker; `ReasoningEffort.None` cannot be it, since the codebase treats None as a real
  pinnable value. Cheap to fix, but it is a semantics call, not a bug fix.
  **Answered by the owner 2026-08-24: freeze both directions.** Record that the launch resolved its pins, so
  a null means *resolved to nothing* rather than *predates the columns*, and both directions freeze
  identically — a persona edited during a park can no longer change what a resumed run costs. That needs the
  separate recorded-pins marker named above, since `None` is unavailable as a sentinel. The row is now a
  build, not a decision.
  **Shipped 2026-08-24.** The marker is a column, `AgentRuns.EffortPinRecorded` — **not** derived from
  `PersonaId is not null`, which was the cheap option and is wrong: `ChatSessionManager` creates a live-session
  run with a persona but **no** resolved effort, so deriving would have told that row's resume "the launch
  resolved nothing" and frozen away an effort it has always fallen through to. The freeze itself is one
  conditional — `ResolveProviderAsync` gains `freezeEffort`, which withholds the persona rung so
  `RunPinResolver.ApplyEffort` sees the recorded value alone. `ALTER TABLE … INTEGER NOT NULL DEFAULT 0` gives
  legacy rows the answer the semantics want for free. Pinned in `AgentRunPinPersistenceTests` (round trip of a
  recorded null through both readers, the `DROP COLUMN` migration half, and the unrecorded-null case) and by
  two launcher facts — `Resume_WhenTheLaunchResolvedNoEffort_KeepsTheProvidersOwn_NotThePersonasEditedValue`
  (asserting a value the *provider* carries, so "the freeze held" is distinguishable from "nothing was applied")
  and `Resume_OfARowThatRecordedNoPins_StillFallsThroughToThePersonasEffort`. Both halves verified non-vacuous
  by pinning `freezeEffort` to each constant in turn and watching the other's assertion fail.
  **Deliberately left:** the live path still records no effort at all, so a live-created run's resume keeps
  reading the persona's current one. Recording it there is a change to live behaviour, not to resume semantics.
  *Deps:* E9 · *Effort:* **XS** · *Value:* **Med**

---

## Suggested order

Cheapest decisive work first, then the vertical slices.

```
A1 → A4 → P8 → B5 → A5 → A2     # gate, then the cheap wins; A2/A3 wait on a wider supply re-read
C1 → C2 → C3                    # blueprint vertical slice — first user-visible change
B1 → B2 → B3 → B4               # gate: is compaction actually losing anything?
C4 · A6 → A7 · B6 · B7 → B8     # widen, once the gates have answered
E1 → E2 → E3 · E4 → E5 → E6     # per-routine pins; E8 rides with C4, E7 is the Windows run
D1 → D2 → D3 → D5               # tour — D1 landed, the rest PARKED 2026-08-24 (see the D section)
```

`A1` and `B5` together are under two days and can both close a branch of work.

### Queued 2026-08-24, after the C5/C7 batch

Owner-selected, in the order the dependencies allow. `B11` leads because it is what makes the B-track
matter at all, and because until it lands an over-window chat fails provider-side.

```
B11 → E10 · P9 · E11        # DONE 2026-08-24 (B11+B12, then all three XS rows)
B7 → B8                     # message-level search, then the recovery pointer
C6                          # needs plan §11 Q4 answered first, and a desktop pass
```

**The whole B-track closed 2026-08-24 and promoted nothing.** `B6`/`B8`/`B9` were built, `B10` swept all six
columns on DeepSeek V4 Flash, and the honest outcome is that **the instrument has to be fixed before any arm's
number can drive a change** — starting with the fact that compaction only runs on agent-run *step* turns, so
half the corpus models a message list the product never hands the compactor. §7 of
[2026-08-24-compaction-arms-cde-reading.md](2026-08-24-compaction-arms-cde-reading.md) is the ordered fix list;
**seven of its nine items cost no provider call.** Do that before spending on a second sweep.

Everything else still open is behind a gate: `C6` behind plan §11 Q4, and `A2 → A3 → A6 → A7` behind the supply
re-read (§8 of [2026-08-23-a2-wide-read.md](2026-08-23-a2-wide-read.md) fixed the band in advance — build above
40%, drop below 12%; the last read was 22% on 13 runs).

**The D-track was parked by the owner 2026-08-24** —
[../guided_tour/2026-08-24-d-track-parked.md](../guided_tour/2026-08-24-d-track-parked.md). `D7` was severed
from it and stays open, ungated, at `S`/`Med`.

**So six rows are open and NONE of them is the right next move.** `A2`–`A7` wait on a supply re-read that
costs a desktop session; `C6` waits on plan §11 Q4; `D7` is a tag-along rather than scheduled work. The next
work is therefore the two `High`s in the *not yet planned* table below — **the error layer on the failure card
and consented diagnostics export, which are one feature area (failure legibility)** — and the first slice of it
is already free: `AgentRunService.FailAsync` serialises `{ error }` into `AgentRuns.ExtraJson` on **every**
failure, and the column's only reader (`RunProgressViewModel.ReadTruncation`) short-circuits on `truncated`
and never looks at it. **Every failed run already knows why it failed and the UI says "Ended with an error".**

**Both of those landed 2026-08-24.** Slice 1 of #2 is `3c90aa74` (the reason on the card) and #3 is group
`G` below (Export Diagnostics). The next work in this area is **#2 slice 2**, which is the only part of the
*not yet planned* table's two `High`s still open.

### Queued 2026-08-24, after the `G1` UI run

`#2` slice 2 now has rows and a plan doc
([../failure_legibility/2026-08-24-failure-layer-plan.md](../failure_legibility/2026-08-24-failure-layer-plan.md)),
so the "none of the open rows is the right next move" paragraph above no longer holds — `G2` is.

```
G2 → G3          # the enabler, then the cheap gap-closure it unlocks
G2 → G4          # the user-visible half; can run alongside G3
G-Q1 → G5        # only after the gate
```

`G2 → G3` is under two days and closes a gap `IsPreModelFailure`’s own doc comment already records. `G4`
is where the user-visible value is, and both of its recovery actions are already built — `G1` is one of
them. `G5` is deliberately last and deliberately gated: `SafeToReRun` only makes a re-dispatch safe for the
*pre-model* cases, which may leave a Retry worth very little.

**`BlueprintKey` stays data-only** (owner, 2026-08-24): no UI reads it, the question it answers needs months
of real use, and it is answerable by SQL against `history.db` in the meantime.

---

## G — Failure legibility

Promoted out of the *not yet planned* table below. `G1` is review **#3**, scoped as **Export** rather than
*Send* by the owner on 2026-08-24 — plan and reading:
[`../failure_legibility/2026-08-24-export-diagnostics.md`](../failure_legibility/2026-08-24-export-diagnostics.md).
`G2`–`G5` are review **#2 slice 2**, planned 2026-08-24 in
[`../failure_legibility/2026-08-24-failure-layer-plan.md`](../failure_legibility/2026-08-24-failure-layer-plan.md);
slice 1 already shipped as `3c90aa74`. `G1` is a **dependency of `G5`**, not a sibling: it is the recovery
action the non-retryable layers offer.

- [x] **G1 · Export Diagnostics — a consented, redacted zip written locally, plus reveal-in-Explorer.**
  The app had **no route to its own logs at all**, while `CLAUDE.md`'s support story already told users to
  hand-attach `%LOCALAPPDATA%\Pia\Logs\pia-*.log`.
  **The design centre is redaction on the way OUT, not at the log site.** 523 call sites hand an exception to
  `LogError`/`LogWarning`/`LogCritical` (measured), so `ex.Message` and its stack trace are in the release log
  in hundreds of places — one of them the exact string slice 1 puts on the failure card
  (`BackgroundAssistantTurnRunner.cs:273`). The log stays as written; the export applies a documented,
  ordered, **two-tier** rule set (`LogRedactor`, 12 rules — 6 deterministic, 6 best-effort, the tier declared
  in code and asserted). Whole-URL collapse, stable `host-NNN` codes, `<profile-*>` tokens, and every
  `DBUG`/`TRCE` **message body dropped wholesale** with its continuation lines omitted.
  **Measured over the real 39-file corpus** (247,884 lines, 41.5 MB): 130,790 debug bodies replaced,
  2,129 continuation lines omitted, and a scan of every output line for the account name, machine name,
  `C:\Users` or an email shape found **0 residual hits**.
  Zip = `logs/` + `README.txt` + `manifest.json` + `environment.json`, asserted as an **exact entry set**
  against seeded decoys (`providers.json`, `history.db`, `history.db-wal`, `settings.json`, `Logs.zip`,
  `transcript.md`) rather than by a deny-list, which would go vacuous. Caps: newest 7 files under 10 MB,
  a **contiguous** newest-first run, every excluded file still named in the manifest with a closed-enum reason.
  Entry point: Settings → General → Application, one `ui:Button`, `ShowConfirmationDialogAsync`, then
  `ShellLauncher.RevealInExplorer` — reveal, never open.
  **Two things found while building, both outside the brief.** (a) `MaxRollingFiles = 7` **prunes nothing**
  because `FormatLogFileName` mints a new base name per day — 39 files / 40 MB on the dev profile;
  *retention is NOT fixed here*, which is why the cap exists. (b) `SafeLog.SensitiveInformation` and
  `SensitiveWarning` forwarded to `LogInformation`/`LogWarning`, putting 13 call sites' speaker names, consent
  names and workspace paths at INFO/WARN where a level gate cannot see them; both now emit at `LogDebug`,
  which is what makes the drop rule's guarantee true. Checked before changing: nothing keys on the level —
  the only consumer, `scripts/Measure-SpeakerAttribution.ps1:152`, matches message text and already parses a
  `SensitiveDebug` sibling identically. `SafeLogLevelTests` locks it by reflection, so it holds in Release too.
  **Deliberately left out:** log retention; any upload or *Send* path; a content preview in the consent dialog
  (the manifest and cap are shown, a scrolling redacted-text viewer is a bigger feature); a policy gate; #2
  slice 2; and the `WriteResult` green-snackbar seam. `OutputService.cs:110` interpolates a window title into
  an exception logged at WARN — redacted on export rather than fixed at source, because fixing it would
  contradict the design centre.
  **No `ViewAutomationIdTests` count bump was needed and none was made** — that number is a floor asserted
  with `>=`, "set well under the measured total so ordinary edits to the view never touch this file"; the
  load-bearing assertion is the missing-id one, which the new button satisfies.
  **Non-vacuity measured, not asserted:** 17 shipped mechanisms reverted one at a time, **17 of 17 caught** —
  and the first pass found a real hole, which is the point of running it. `FileMode.CreateNew` was shadowed by
  a redundant `File.Exists` pre-check, so the atomic collision guard and the same-second race it closes had no
  test at all; the pre-check is gone and `CreateNew` is now the single mechanism.
  Gate **4907 / failed: 0 / 4848 succeeded / 59 skipped** (from 4841 at `da95cc8b`, +66 tests); Debug and
  Release both `-t:Rebuild` to **0 Warning(s)**.
  **Human smoke test RUN, 2026-08-24 — all four questions pass, and it found four defects, all now fixed.**
  Reading: [../failure_legibility/2026-08-24-export-diagnostics-ui-test-reading.md](../failure_legibility/2026-08-24-export-diagnostics-ui-test-reading.md);
  the plan it executed is [../failure_legibility/2026-08-24-export-diagnostics-ui-test-plan.md](../failure_legibility/2026-08-24-export-diagnostics-ui-test-plan.md).
  Eight exports through the real UI over two arms — a throwaway profile seeded with 20 real log files, then the
  real profile (that artefact deleted immediately; `%LOCALAPPDATA%\Pia\Diagnostics` is gone). Button, dialog,
  Cancel-writes-nothing, the export against a **sink-held** log whose `File.OpenRead` was proved to throw
  first, and reveal-with-the-zip-selected all pass; the **residual scan came back 0 every time**, over the log
  entries *and* the three generated ones, for the account name, machine name, `C:\Users`, `AppData`, an email
  shape and all five configured provider names. All three exclusion reasons and the `CreateNew` collision
  guard were reached at runtime — the latter by planting 91 decoy archives rather than racing the clock: no
  archive written, all 91 decoys byte-identical afterwards.
  **The four defects, all legibility rather than privacy, all fixed in the same commit as the reading:**
  (a) `manifest.json` emitted `"ExclusionReason": 0` — an ordinal, unreadable from inside the archive and
  indistinguishable from the `null` on the included rows; now a `JsonStringEnumConverter`, verified in the
  running app. (b) The `OutputAlreadyExists` arm was the only failure arm that did not log, so a refused
  export left **nothing** in the log a support engineer would then ask for. (c) The six
  `DiagnosticsExportFailure` causes all collapsed into one message; the two a user can act on now say so
  (two new keys × 3 resx). (d) **Only the live run could have found this one:** a provider named `local`
  rewrote the inside of the token R04 had just emitted — `<profile-local>` came out as
  `<profile-<provider-3>>`, count **0** in a real archive, which also silently stopped R12’s tokenised-path
  pass. Key-driven rules now run outside the tokens earlier rules emit; the machine-suffix and
  tokenised-path passes stay unguarded because they read those tokens on purpose.
  Two playbook answers landed in the same commit: the Wpf.Ui `SimpleContentDialog` **does** carry
  `PrimaryButton`/`CloseButton`, and there is no snackbar to read at all — `ISnackbarService` is Flow, whose
  success notice is transient and unassertable while its failure notice is persistent and fully readable.
  *Deps:* none · *Effort:* **S** · *Value:* **High** (the app can hand over its own logs safely for the first time)

- [ ] **G2 · `PiaFailure` descriptor + type-keyed mapper + `AgentRuns.FailureJson`.** A
  `PiaFailure(FailureLayer Layer, string Code, bool SafeToReRun)` in `Pia.Models`, static descriptors beside
  the six named failure constants (five on the agent-run path, one on the scheduled-job path), an
  exception-**type** mapper at the four `catch` sites that today pass
  `ex.Message`, and an additive column so the value survives a claim that nulls `ExtraJson`. No UI.
  **Additive, never a replacement:** slice 1’s vocabulary is deliberately OPEN and the descriptor travels
  *alongside* the free-text reason — the first test to write is the one pinning that an unmapped `ex.Message`
  still reaches the card unchanged. **`SafeToReRun` is `IsPreModelFailure`’s meaning** (provably nothing spent,
  nothing written), **not** hermes’s "the call might work if repeated" — conflating them ships a
  duplicate-write bug on any mid-run provider fault.
  *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

- [ ] **G3 · Widen `IsPreModelFailure` to read `SafeToReRun`.** Closes the KNOWN GAP its own doc comment
  records: a `HeadlessRunLauncher` failure that provably happened before the model was called currently
  arrives as a bare message and dies on the first strike. The narrowing stays — a mid-run fault is still
  terminal — but it is decided by a value the **caller vouched for** rather than by one string comparison,
  which is exactly what that comment asks for ("never a substring match on provider error text").
  *Deps:* G2 · *Effort:* **XS** · *Value:* **Med**

- [ ] **G4 · Layer name + recovery action on the failure card.** Renders the layer beside slice 1’s reason
  line and offers the matching action. **Both actions already exist:** *Export diagnostics* (`G1`) for
  `App`/`Unclassified`, and the Providers settings category for `Provider`/`Endpoint`. Check the gating first —
  `RunProgressViewModel` gates the reason on the Failed **family**, which folds `Cancelled` in, so a run
  cancelled because a child failed carries the child’s reason.
  *Deps:* G2 · *Effort:* **S** · *Value:* **High**

- [ ] **G5 · Retry on the failure card, honouring `SafeToReRun`.** **Gated on `G-Q1` — do not start before it
  is answered.** Whatever the answer, the retry claim **must not `SET ExtraJson = NULL`**: both existing
  resume claims do, and they are safe only because they fire from `WaitingForInput`/`Paused`. A Retry adds a
  new `Failed → Running` transition that, written in the shape of its two siblings, would wipe the reason
  slice 1 reads. `FailureJson` survives it; `{"error": …}` does not.
  *Deps:* G2, G4, **G-Q1** · *Effort:* **M** · *Value:* **Med**

---

## F — Test hygiene

Not from the review. Found on 2026-08-23 while seeding a throwaway profile for the wide A read.

- [x] **F1 · `dotnet test` writes to the user's REAL profile.** The documented gate, run with no
  environment overrides, creates and mutates `%LOCALAPPDATA%\Pia\history.db`,
  `%APPDATA%\Pia\settings.json`, `%APPDATA%\Pia\providers.json`, `Logs\`, `runs\` and `workdir\`.
  **Evidence, not inference.** `AgentRuns.PersonaId` and `ReasoningEffort` exist only in code committed at
  `cb5d9ba7` (hours old, never launched), yet the real `history.db-wal` carried both after a gate run —
  and opening the `.db` *without* its `-wal` shows neither, so `MigrateSchema` ran against the real file
  during that run. Re-running the whole gate with `PIA_DATA_DIR`/`PIA_LOCAL_DATA_DIR` pointed at a scratch
  directory then produced a complete profile there — `history.db` plus **832 KB of WAL**, both json files
  and all three subdirectories — so this is not one stray `ALTER`.
  **Narrowed 2026-08-23, after checking rather than asserting.** Two writes at the real path are
  confirmed: `history.db` (the schema migration, and a WAL restamped by every gate run) and
  `%LOCALAPPDATA%\Pia\runs`, which held 17 files and was written again at 19:07 during the last gate run.
  Two are **not**: `settings.json` and `providers.json` were untouched across four gate runs — mtimes still
  predate the session and all five providers, both mode defaults and `assistantFilesFolder` read back
  intact. So the scratch-directory copies of those two are an artefact of pointing *both* env vars at one
  empty directory, not evidence of a real-path write, and the row is a schema-and-workspace leak rather
  than a settings leak. `workdir` is empty. Confirmed negative worth keeping: the redirected corpus wrote
  **zero** files into the real `Documents\Pia Assistant`, so `AssistantFilesFolder` redirection does hold.
  **Why it has gone unnoticed:** every confirmed write is additive and self-healing (a nullable column the
  app would add at next launch anyway), and `DataDirectoryRoutingTests`/`PiaPathsTests` police the
  *production* code's use of `SpecialFolder`, not the test project's own resolution of an unset override.
  **A blanket redirect is not the fix.** Nine tests fail under one, because their premise is that no
  override is set: `PiaPathsTests.RoutedMember_ObservesAnOverrideAppliedAfterItsTypeIsLoaded` (all five
  rows) and `DataRoots_WithOverride_UseTheOverrideVerbatim`,
  `AssistantWorkspaceTests.LegacyWorkdir_is_workdir_under_local_app_data_Pia`,
  `VaultPathProviderTests.Default_root_is_Pia_Vault_under_local_app_data`, and
  `FilesToolHandlerWriteTests.Write_IntoWorkdir_IsAllowed_ThroughRealResolver` — that last one writes
  through the *real* resolver by name, which is where `workdir\` comes from.
  **Fixed 2026-08-23, and the offenders were NAMED by instrumentation rather than by reading.** A temporary
  stack-trace dump in `SqliteContext` (both constructors and the first `GetConnection`), gated on an env var
  and reverted before the commit, caught every real-profile open in one gate run. There were two, not one:
  - `ScheduledJobToolIntegrationTests` constructed the **default-path** `SqliteContext` — its `<remarks>`
    called the real `history.db` "a known plan-accepted tradeoff" — so `EnsureSchema`/`MigrateSchema` ran
    against the user's database, into which the test then inserted and deleted its own `TEST_E2E_` job. It now
    opens a throwaway database under the temp directory and deletes it on dispose.
  - `WpfStaHost` **boots the whole application.** `Application`'s constructor POSTS its startup callback and
    the host pumps a dispatcher, so `App.OnStartup` ran without anyone calling `Run()` and took
    `Bootstrapper.InitializeAsync()` with it: the DI graph, the real history database, and
    `VaultIndexer.ReconcileAsync()` over the real vault. The host's own comment — *"Run() is never called, so
    OnStartup's SetLanguage() cannot mutate the process-wide culture"* — was false. The seam is
    `Dispatcher.Hooks.OperationPosted`: capture what the constructor posts and `Abort()` it before the first
    pump. Overriding `OnStartup` in a subclass is **not** a seam, measured rather than assumed:
    `LoadComponent` resolves `App.xaml` against the component's own assembly, so a test-assembly subclass
    fails with *"does not have a resource identified by the URI '/Pia.Wpf;component/app.xaml'"* and takes all
    143 view tests down with it. `WpfStaHostBootTests` is the tripwire: after the host has run,
    `Bootstrapper.ServiceProvider` must still throw.
  - `FilesToolHandlerWriteTests.Write_IntoWorkdir_IsAllowed_ThroughRealResolver` created
    `%LOCALAPPDATA%\Pia\workdir` and left it behind on a machine that had none. It now removes it
    (non-recursive) only when it was the call that created it.

  **The result is measured, not argued.** With the probe still in and the three fixes applied: **zero**
  real-profile opens, and the only failure in 4664 was the probe itself tripping
  `DataDirectoryRoutingTests.OnlyPiaPaths_ReadsTheProfileFolders`. After reverting it:
  **4665 / failed: 0 / 4611 succeeded / 54 skipped**, with `history.db`, `-wal` and `-shm` **byte-identical**
  (SHA256) across the run — where every earlier gate run grew the WAL by ~64 KB. `settings.json`,
  `providers.json`, `templates.json` and `workdir` are untouched. What is left is two directory mtimes, which
  is F3.

  **The nine tests were not touched, and none of them had to be.** The blanket redirect was never applied:
  the leak was two named tests rather than ambient `PiaPaths` unsafety, so the five
  `RoutedMember_ObservesAnOverrideAppliedAfterItsTypeIsLoaded` rows and the four literal-path facts still
  assert the real profile, and still only read strings from it.
  *Deps:* none · *Effort:* **S** · *Value:* **High** (the gate must not mutate the machine it runs on)

- [x] **F3 · Two directory mtimes are the gate's remaining footprint on the real profile.** Both were named
  while closing F1 and both are by *premise*, not by accident. `%LOCALAPPDATA%\Pia\runs` is restamped by 47
  tests in five classes (`RunWorkspacePromotionTests`, `RunWorkspaceRedirectsTests`,
  `FilesToolHandlerRunsDirGuardTests`, `FilesToolHandlerWorkspaceEscapeTests`,
  `LiveTurnExecutorPlannedRunTests`) because `RunWorkspaceRedirects.Record`'s containment gate refuses any
  root outside the real `RunsRoot`. `%LOCALAPPDATA%\Pia` itself is restamped by
  `FilesToolHandlerListTests.ListRelativeFiles_NegationCannotResurfaceSensitivePathGuardBlockedPath`, which
  needs a directory inside a root the **live** guard blocks and cannot use an override, because
  `SensitivePathGuard`'s blocked-root array is built once per process. Each creates a GUID-named child and
  deletes it, so nothing that outlives the run is created or modified: the residue is a parent directory's
  mtime, plus an orphan if a test dies mid-body. Measured separately — the five run-workspace classes move
  `runs` and not `Pia`. The fix, if it is worth one, is to make the guard's root array and the containment
  gate re-derivable so both suites can run redirected.
  **Done 2026-08-24, and the containment gate needed nothing — only the guard did.**
  `RunWorkspaceRedirects.Record` already re-derives its gate on every call, and `AssistantWorkspace.RunsRoot`
  is already a property. The whole blocker was `SensitivePathGuard`'s two `static readonly` arrays, frozen at
  type load — the exact trap `PiaPaths` exists to avoid, and one nothing had noticed because production sets
  its environment before anything loads. They now rebuild behind a lock keyed on the two routed roots, so
  production still builds once.
  - **Redirect, not rewrite.** A new `RedirectedProfileFixture` applies `PiaPaths.OverrideForTests` for a
    class's lifetime; the five run-workspace classes take it as an `IClassFixture` and move into the existing
    **`PiaPathsStatic`** collection, which is already `DisableParallelization = true`. That collection is what
    makes the redirect safe rather than a race — `OverrideForTests` sets process-wide environment variables,
    and nine other tests' premise is that no override is set. F1 refused a *blanket* redirect for exactly that
    reason; a targeted one inside the serialized collection has the same effect without the collision.
  - **The second offender needed its own class.** `ListRelativeFiles_NegationCannotResurfaceSensitivePathGuard
    BlockedPath` read `SpecialFolder.LocalApplicationData` directly and was the only thing still stamping
    `%LOCALAPPDATA%\Pia` itself. Moved to `FilesToolHandlerBlockedRootListTests` on the redirected profile, and
    it gained a **non-vacuity assertion first** — `IsBlocked` must say the path is blocked — because the test
    also passes against a root the handler simply cannot read.
  - **Two new facts hold the fix**, both in `SensitivePathGuardOverriddenProfileTests`: `IsBlocked` follows an
    override applied *after* the guard has already answered (and reverts when it is dropped), and the runs
    carve-out moves with the profile while a sibling of it stays blocked. The class's ctor reads the guard from
    the real profile first, which is what makes them non-vacuous.
  - **Measured, not argued.** Snapshot → gate run → compare: **0 of 9 changed.** `%LOCALAPPDATA%\Pia`,
    `\runs`, `\workdir` and `\Logs` mtimes all unmoved; `history.db`, `-wal`, `-shm`, `settings.json` and
    `providers.json` all byte-identical by SHA256. The first attempt at this comparison was **wrong** —
    `ConvertFrom-Json` parsed the ISO timestamps into `DateTime`s and comparing one to a string is always
    unequal, which reported `workdir` as changed since June. Compare ticks or hashes as strings.
  - Cost: **none measurable.** 4825 / failed: 0 at 29.2s, against 28.7s before the collection change.
  *Deps:* none · *Effort:* **S** · *Value:* **Low**

- [x] **F2 · A chat-history row can be DELETED by AutomationId but not opened by one.** Found on
  2026-08-23 when E9's read-half check could not resume a parked run: opening the run's chat means
  activating its history row, and the row would not activate by `ww_click`, double-click,
  `SelectionItemPattern` or Enter. The row's *only* id-addressable action is
  `AssistantChat_Delete_{ChatId}` — so the one thing a script can reliably do to a named past chat is
  destroy it, which is the wrong asymmetry to ship. `PiaAssistantChatRowContent` is already covered in
  `ViewAutomationIdTests` (1 literal, 1 bound), so this is a missing id rather than a missing test row:
  the fix is an `AssistantChat_Open_{ChatId}` (or an id on the row's own activation surface) plus the
  `[InlineData]` count bump in the same change. Two other rows share the shape and are worth doing at the
  same time — the history rows report their UIA name as `Pia.ViewModels.Models.AssistantChatRowViewModel`
  and the Routines rows as `Pia.ViewModels.RoutineRow`, i.e. a `ToString()`, which is what forced index-
  based selection during E7's pass. Unblocks E9's read-half confirmation in the app, and any future script
  that must open one named past chat.
  **Done 2026-08-23, and it lands UNVERIFIED IN THE APP** - this batch had no desktop session, so none of the
  below was exercised through UIA. It is build- and gate-verified only.
  - `AssistantChat_Open_{ChatId}` is a real per-row button on `PiaAssistantChatRowContent`, on the same hover
    strip as the trash and wired to a new row-parameterised `OpenChatCommand`; `ExecuteResumeChatAsync` now
    delegates to it, so the inspector's Resume button and the row share one body. Chosen over an id on the
    container alone because the sweep in `ViewAutomationIdTests` only inspects
    `ButtonBase`/`ComboBox`/`TextBoxBase`/`PasswordBox`/`Slider`/`Expander`/`TabItem` declared inside a
    `DataTemplate` - a container id cannot bump any count - and because one invoke that OPENS the named chat is
    what E9 needs: selecting a row only loads the inspector, and opening still takes its Resume button.
  - Floor bumped to **(2, 2)**, measured rather than guessed: raising it to 99 made the sweep report exactly 2
    interactive controls in `PiaAssistantChatRowContent`, and (2, 2) passing proves both ids are the per-item
    binding form rather than literals.
  - Both `ToString()` names are fixed on the item CONTAINER, which is the node UIA actually offers for a row:
    chat rows now carry `AssistantChat_Row_{ChatId}` plus the chat title as their name, Routines rows
    `Routines_Row_{Id}` plus the routine name, both through the list's `ItemContainerStyle`. The id sweep
    cannot see a container, so `RowContainerAutomationTests` locks both - and fails on a literal, which would
    hand every row the same id.
  - Playbook updated, including its "Known gaps" claim that a `ListBoxItem` can carry no id. It can, through
    `ItemContainerStyle`; what it cannot do is appear in the sweep.

  Gate **4667 / failed: 0 / 4613 succeeded / 54 skipped**; both configurations rebuild to 0 Warning(s).
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**

---

## Open points with no row yet

Not defects and not planned work — three things a later session should **decide** rather than inherit. Each
is a consequence of shipped code, recorded here so the decision is not made silently by nobody making it.
Carried out of the 2026-08-24 handoff prompt when that prompt was consumed.

- **The model-window catalogue is a dated snapshot that nothing refreshes on its own.**
  `OpenRouterContextWindows.SnapshotDate` is `2026-08-24`, and the table is *generated* from
  [`../openrouter_models/2026-08-24-openrouter-context-lengths.md`](../openrouter_models/2026-08-24-openrouter-context-lengths.md)
  — regenerate rather than hand-edit; that doc carries the `curl` that produced it. Live re-reads happen
  **only when an OpenRouter provider is saved**, so every other provider type, and any OpenRouter provider
  nobody re-opens, runs on the snapshot indefinitely. Decide between a refresh path, a periodically
  regenerated snapshot, and nothing.
- **Ollama models resolve to nothing and take the 128k floor.** The catalogue is keyed by OpenRouter
  basenames; Ollama uses short tags (`llama3`, `phi4`). Today this is a **no-op, not a regression** — a 4k
  local model never reaches 128k, so compaction never fires and Ollama keeps sliding its own window exactly
  as before. It becomes worth fixing only if local models gain a window source (Ollama's `/api/show` reports
  one, which is the obvious candidate).
- **`RoutineSlotKind` ships with one member and no reader.** Deliberate — the C5/C7 brief decided to ship
  `Text` only, and §2.3 of [2026-08-24-c5-c7-batch-report.md](2026-08-24-c5-c7-batch-report.md) records it as
  inert. The open call is whether to delete it until a second kind exists or keep it as the seam `Time`/`Enum`
  land on. `RoutineSlot` is not persisted, so either direction is code-only.

---

## Not yet planned

From the review's recommendation table, no plan doc written. Listed so they are not lost.

**BOTH failure-legibility items have been PROMOTED OUT of this table.** #3 shipped 2026-08-24 as `G1`, and
the card it lands on can already say what went wrong: slice 1 of #2 (`3c90aa74`) renders the failure reason.
**#2 slice 2 was planned the same day** — a named failure layer, recovery actions and Retry — and is now
`G2`–`G5` above, behind
[../failure_legibility/2026-08-24-failure-layer-plan.md](../failure_legibility/2026-08-24-failure-layer-plan.md).
That plan renames the descriptor's third member from the review's `Retryable` to `SafeToReRun`, because the
review's word means "the call might work if repeated" while the thing it says to generalise,
`IsPreModelFailure`, means "provably nothing spent and nothing written" — the two are not the same question.
The trap below is recorded in §8 of
[../failure_legibility/2026-08-24-export-diagnostics.md](../failure_legibility/2026-08-24-export-diagnostics.md):
a Retry adds a new `Failed → Running` transition, and written in the shape of its two existing siblings it
would `SET ExtraJson = NULL` and wipe the reason slice 1 reads.

| Item | Review # | Effort | Value |
|---|---|---|---|
| ~~Error layer + recovery actions on the failure card~~ **slice 1 done `3c90aa74`; slice 2 PROMOTED OUT 2026-08-24** to `G2`–`G5` above | 2 | M | High |
| ~~Send Diagnostics — consented, redacted log bundle~~ **DONE 2026-08-24 as `G1`**, scoped to *Export* | 3 | S | High |
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
