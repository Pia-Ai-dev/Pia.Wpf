# Test Plan — Does Pia's Context Compaction Lose Anything That Matters?

**Status:** planned, not started. Self-contained: everything needed to execute it is below.
**Owner:** unassigned. **Written:** 2026-08-22.
**Origin:** §3.3 of [`2026-08-22-hermes-update-review.md`](2026-08-22-hermes-update-review.md).

---

## 1. The question

`AgentContextCompactor` shrinks an outgoing request so a long transcript can't overflow the model's
context window. It is careful, well-commented, and **completely unmeasured**: we know how many
messages it removes, and nothing at all about what the agent can still answer afterwards.

> **Can the model still answer questions about the part of the conversation that compaction removed?**

Token count is the metric we have and it is the wrong one — it measures the cost, not the damage.
Two policies that retain the same number of tokens can differ enormously in what survives.

**This plan does not change compaction.** It builds the instrument that would let us change it with
evidence. Any threshold change is a separate, later piece of work that this plan's output justifies
or refutes.

---

## 2. How Pia's compaction actually works today

Needed to read the rest of this document. Source: `src/Pia.Wpf/Services/AgentContextCompactor.cs`
(377 lines, `internal static`).

It is a **pinning wrapper** around `Microsoft.Agents.AI.Compaction.ContextWindowCompactionStrategy`.
Pia decides what to withhold from the library; the library decides what to drop from the rest.

**Pia withholds (never compacted):**

| Pin | What |
|---|---|
| Head | The leading run of `System` messages, plus the first `User` message after them (the run goal) |
| Tail instruction | The **newest** `User` message (on the agent path: `"Execute step N: <intent>. Expected: <artifact>"`) |
| Images | Image-bearing turns, admitted newest-first under a sub-cap of half the remaining input budget, floored at one image |

**The library then handles everything else** with two thresholds Pia sets:

```csharp
new ContextWindowCompactionStrategy(window, maxOutput,
    ToolEvictionThreshold /* 0.45 */,
    TruncationThreshold   /* 0.70 */);
```

**Three facts that shape this plan:**

1. **Nothing is summarized.** `CompactionProvider.CompactAsync(strategy, toCompact, logger, ct)` takes
   no chat client — there is no summarization call. Evicted content is **gone**, not paraphrased.
   Hermes's compaction summarizes, so its failure mode is *a summarizer mangling identifiers*. Pia's
   failure mode is *total loss*. Any technique that preserves specific facts is therefore worth
   **more** here, not less, and a "recovery" path is the only route back to an evicted fact.
2. **Middle user messages are not pinned.** The head goal and the newest instruction are; a user
   message in between goes into `toCompact` and can be evicted like any other. So hermes's
   "user messages are never compacted" invariant is **half-held** in Pia. Whether that matters is
   one of the questions this harness answers.
3. **Compaction is opt-in.** `AgentContextBudget.From(provider)` returns `null` unless the provider
   has `MaxContextWindowTokens` set, so a provider predating those fields never compacts at all.
   Every measurement must state which budget it ran against.

**Four call sites** (all pass the same method, different budgets):

| Site | Path |
|---|---|
| `ViewModels/Models/ChatSession.cs:952` | Interactive chat turn |
| `Services/AiClientService.cs:248` | In-step tool loop |
| `Services/AiClientService.cs:660` | Wrap-up turn |
| `Services/HeadlessTurnExecutor.cs:462` | Headless / scheduled agent step |

**Existing coverage:** `tests/Pia.Wpf.Tests/Services/AgentContextCompactorTests.cs` (22 tests) and
`AiClientServiceInStepCompactionTests.cs`. These are *structural* — they assert what is pinned, what
is charged, and that faults degrade instead of throwing. None of them assert anything about recall.
**They stay exactly as they are.** This plan adds a parallel, explicit-only measurement suite.

---

## 3. What "good" looks like — hermes's numbers

Hermes ran this experiment on its own compaction in August 2026
(`evals/compaction/results/SCORECARD-2026-08-15.md`, four real ~500K-token transcripts, 15 recall
questions each). Use it as a calibration reference, not a target — their compactor summarizes and
Pia's evicts, so the absolute numbers are not comparable.

| Policy | Tokens retained | Recall |
|---|---|---|
| Uncompacted (ceiling) | 500K | 96.7% |
| Their old default — big tail | 162K | **45.8%** |
| Lean tail | 49K | 40.0% |
| Lean tail **+ recovery pointer** | 49K | **68.3%** |

Three findings worth carrying into our design:

- A **mechanical anchor index** — exact identifiers (ids, paths, error strings) extracted verbatim
  instead of trusted to a summarizer — moved one transcript from 23.3% to 60.0%.
- A **recovery pointer** — a footer telling the model the removed region is searchable — was worth
  +20 to +43 points on its own.
- **One arm scored 93.3% by luck.** That transcript happened to restate its own facts later, so
  nothing was really lost. The same policy scored 30–33% on two fresh transcripts. **A single
  good number proves nothing.** This is the single most important lesson from their run and it
  dictates the corpus size in §5.

---

## 4. Scope

**In scope:** measuring recall after compaction on the agent-step path and the interactive chat path;
producing a reproducible scorecard; testing three candidate improvements against the current
behaviour.

**Out of scope (explicitly):**

- Changing `ToolEvictionThreshold` or `TruncationThreshold`. Separate work, gated on this output.
- Replacing `Microsoft.Agents.AI.Compaction`.
- Porting hermes's micro-compaction. It is off by default in hermes because it rewrites already-sent
  history and breaks the provider prompt-cache prefix every turn.
- Anything touching persisted transcripts. The compactor's hard guardrail is that it operates on a
  request copy only (`AgentContextCompactor.cs:26-31`); the harness must not weaken that, and must
  not become a reason to.

---

## 5. Corpus

**Target: 4 transcripts minimum.** Not fewer — see the luck finding in §3. Each should be long enough
that compaction actually fires against a realistic budget.

| # | Shape | Source |
|---|---|---|
| 1 | Long interactive chat, tool-light | `AssistantChatMessages` |
| 2 | Long interactive chat, tool-heavy (file reads, web fetches) | `AssistantChatMessages` |
| 3 | Multi-step agent run, ≥8 steps | Headless run transcript |
| 4 | Agent run with at least one image attachment | Headless run transcript |

**Extraction.** Real transcripts live in the app's own SQLite DB:

```sql
SELECT Role, Content, Ordinal
FROM AssistantChatMessages
WHERE ChatId = @chatId
ORDER BY Ordinal;
```

Dump to a JSON fixture: `{ "id": "...", "messages": [ {"role": "...", "content": "..."} ] }`.

> **Privacy — hard rule.** These contain real user content. **Fixtures are never committed.** They
> live outside the repo and the harness takes a path. Hermes made the same call ("Transcripts are NOT
> committed — they contain real session data"). The repo gets a **synthetic generator** instead, with
> planted facts at known positions, so CI can smoke-test the harness's own plumbing without any real
> data. Contributors without local transcripts can still run the synthetic arm.

---

## 6. Question bank

For each transcript, generate 15 factual recall questions **drawn only from the region compaction
removes**. A question about surviving content measures nothing.

Procedure:

1. Run the transcript through the current compactor at the chosen budget. Diff input against output
   to get the **removed set** (by message identity, not index — the compactor reorders).
2. Ask a generation model for 15 short factual questions answerable *only* from the removed set,
   each with a gold answer. Prefer questions with a checkable answer: a filename, a number, a name,
   a decision, an error string.
3. **Cache the bank per transcript** (hash the transcript, store the bank next to the fixture). Every
   arm must face the identical bank or the comparison is meaningless.

**Trap to avoid:** if a fact appears in both the removed set *and* the surviving tail, the question
is unusable — that is precisely the restatement luck that inflated hermes's 93.3%. Add a mechanical
filter: reject any question whose gold answer string appears verbatim in the retained messages.

---

## 7. Arms

Every arm runs on every transcript with the same bank and the same budget.

| Arm | What it is | Why |
|---|---|---|
| **A — uncompacted** | Full transcript, no compaction | The ceiling. If A is not near 100%, the bank or the judge is broken, not the compactor. |
| **B — current** | Today's behaviour, unmodified | The baseline every change is measured against. |
| **C — anchor index** | B, plus a mechanically extracted verbatim list of identifiers from the removed set, appended as one block | Tests §3's biggest single win. See below. |
| **D — recovery pointer** | B, plus a footer telling the model the removed region is searchable, and a working search path | Tests the cheapest win. |
| **E — pin all user messages** | B, but every `User` message is withheld, not just first and newest | Tests whether Pia's half-held invariant (§2.2) costs anything. |

**Arm C, what to extract.** Mechanical means regex/parse, never a model. Candidates for Pia:

- Absolute and relative file paths, especially under the run's workspace root
- `ExpectedArtifact` strings and step ordinals
- Tool names and tool-call ids
- GUIDs (run ids, step ids, chat ids)
- Quoted error messages and exception type names
- Numbers with units, dates, and version strings

**Arm D, the constraint to check first.** A recovery pointer is only honest if the search actually
works. `AssistantChatsFts` (`SqliteContext.cs:1090`) is FTS5 over `(ChatId UNINDEXED, Title, Body)` —
it is indexed **per chat, not per message**. A search returns *which chat* mentioned something, not
*which message*. For within-run recovery that granularity is too coarse. Resolve before building D:
either add a message-level index, or scope recovery to a direct query over
`AssistantChatMessages WHERE ChatId = @id` with FTS or LIKE. **This is a real prerequisite, not a
detail** — budget for it.

Arms C, D and E are prototypes inside the harness. **None of them modify
`AgentContextCompactor.cs`.** They post-process its output, so the shipped code stays untouched until
the numbers justify a change.

---

## 8. Execution and judging

1. For each (transcript, arm): build the post-compaction message list.
2. For each of the 15 questions: send `[post-compaction context] + [question]` to a **fresh** model
   with no other history. One question per call — batching lets one answer leak into the next.
3. Judge each answer against gold with a **separate** judging call: `correct / partial / wrong`.
   Score `partial` as 0.5. Use the same judge model across all arms; record which model it was.
4. Record per (transcript, arm): recall %, tokens retained, messages retained, wall-clock.

**Determinism.** Temperature 0 where the provider allows it. Even so, expect ±3–5 points of run-to-run
noise from the judge — do not treat a 2-point difference as a result. The bar for "this arm wins" is
set in §11.

**Cost.** 4 transcripts × 5 arms × 15 questions × 2 calls (answer + judge) = **600 calls per sweep**,
each carrying a large context. Budget for it, and cache the question banks so re-runs only pay for
answers and judging.

---

## 9. Where it lives

**Recommended: an explicit-only xunit class**, no new project.

```
tests/Pia.Wpf.Tests/Integration/Compaction/
    CompactionRecallHarness.cs      # corpus load, arms, question bank, judge, scorecard writer
    CompactionRecallTests.cs        # [LiveApiFact] entry points, one per arm sweep
    SyntheticTranscript.cs          # committed generator — planted facts, no real data
    README.md                       # how to point it at a local fixture
```

This reuses `tests/Pia.Wpf.Tests/TestInfrastructure/LiveApiAttributes.cs`. `LiveApiFactAttribute` sets
xunit v3's `Explicit = true`, so **these never run in the default gate** and report as `Not Run` — no
caller-side filter needed. Per `CLAUDE.md`, the gate is `dotnet test` with no filter and the bar is
`failed: 0`; nothing here may change that.

```bash
dotnet test                                            # the gate — harness excluded, unchanged
dotnet test -- --explicit only \
    --filter-namespace "Pia.Tests.Integration.Compaction"   # run the sweep
```

The scorecard is written to a path given by an env var (e.g. `PIA_COMPACTION_EVAL_OUT`), defaulting
under the scratch/temp dir. **Never** into the repo — it would carry transcript-derived question text.

*Alternative considered:* a standalone console runner, closer to hermes's `evals/`. Rejected for v1 —
it needs a new project, its own DI wiring and its own provider config, to buy only a nicer CLI.
Revisit if the harness outgrows a test class.

**Build note:** per the project's build environment, `net10.0-windows` compiles on macOS with
`-p:EnableWindowsTargeting=true` but tests cannot execute there. **This suite runs on Windows or CI
only.** Authoring and compiling on macOS is fine; measuring is not.

---

## 10. Deliverable

A scorecard in the shape hermes used, because it reads well and forces the token column to sit next
to the recall column:

```
transcript      A:uncompacted   B:current     C:anchor      D:recovery    E:pin-users
chat-toollight  96.7 @ 180K     52.0 @ 41K    58.0 @ 43K    71.3 @ 41K    54.7 @ 44K
chat-toolheavy  ...
agent-8step     ...
agent-image     ...
AVG             ...
```

Plus a short findings section — what won, by how much, and **explicitly what was luck**. Commit the
scorecard *markdown* (it is derived numbers, no user content) and never the fixtures or banks.

---

## 11. Decision rules

Fix these now, before any number exists, so the result can't be rationalised after the fact.

| Outcome | Action |
|---|---|
| Arm A (uncompacted) scores < 90% | **Stop.** The bank or the judge is broken. Fix the instrument before reading anything else. |
| B ≥ 85% of A's score on all four transcripts | Current compaction is fine. Write that down, close the item, and don't tune the thresholds. **This is a legitimate and useful outcome.** |
| An arm beats B by **≥ 10 points averaged over all four**, and wins on **at least 3 of 4** | Promote to a real implementation proposal against `AgentContextCompactor.cs`. |
| An arm beats B by < 10 points, or wins on only 1–2 transcripts | Record it and stop. Below the noise-plus-luck floor. |
| Any arm wins big on exactly one transcript | Treat as suspected restatement luck until a fifth transcript reproduces it. |

Threshold tuning (`0.45` / `0.70`) is deliberately **not** an arm. Get the instrument trustworthy
first; a threshold sweep is cheap to add afterwards and meaningless before.

---

## 12. Risks

| Risk | Mitigation |
|---|---|
| Questions leak into surviving context → every arm scores high, nothing discriminates | Mechanical filter in §6: reject a question whose gold answer appears verbatim in retained messages |
| Judge is inconsistent between arms | One judge model, temperature 0, fixed prompt, recorded in the scorecard |
| Four transcripts is too few to beat luck | It is the floor, not the goal. The §11 "wins on ≥3 of 4" rule is the guard. Add a fifth if a result hinges on one transcript |
| Real transcripts leak into the repo | Fixtures gitignored by path convention; committed generator is synthetic; scorecard contains numbers only |
| Cost runs away | Cache banks; run one transcript end-to-end first and extrapolate before the full sweep |
| Harness drifts from the shipped compactor | It calls `AgentContextCompactor.CompactAsync` directly — same code path as production, not a reimplementation |
| Result is "no change needed" and feels like wasted work | It isn't. Two undocumented constants currently sit in the request path on reasoning alone. Evidence that they are fine is worth having |

---

## 13. Work breakdown

| Step | Notes |
|---|---|
| 1 | Synthetic transcript generator + planted facts — unblocks everything, needs no real data |
| 2 | Corpus extraction script (SQL → JSON fixture) |
| 3 | Question-bank generator + caching + the verbatim-leak filter |
| 4 | Arms A and B + judge + scorecard writer — **first real number** |
| 5 | Arm C (anchor index) |
| 6 | Arm D — **includes** resolving the FTS granularity prerequisite in §7 |
| 7 | Arm E (pin all user messages) — smallest arm, do it whenever |
| 8 | Full sweep, scorecard, findings |

Steps 1–4 are the minimum viable version and answer the headline question on their own. Stop there
if 4 says current compaction is fine.

---

## 14. Open questions for the owner

1. **Which provider/model** for answering and judging? Affects cost and reproducibility. A cheap
   local model may be fine for judging short factual answers and would make re-runs nearly free.
2. **Which budget** to measure against — a real configured provider's window, or a deliberately small
   synthetic one that forces heavy compaction on shorter transcripts? The small window is cheaper and
   more discriminating; the real window is what users actually hit. Possibly both.
3. **Is arm E worth it** given the head+tail pins already cover the two positions that matter most on
   the agent path? Cheap to include; the answer is probably "run it once and find out."
