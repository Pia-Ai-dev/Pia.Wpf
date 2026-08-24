# Compaction recall — arms C, D and E, and why none of them is buildable yet

**Status:** read; no arm promoted to implementation. **Owner:** Marco Altmann. **Written:** 2026-08-24.
**Origin:** rows `B6` (arm C), `B7`→`B8` (arm D), `B9` (arm E) and `B10` (the sweep) of
[2026-08-22-hermes-followup-checklist.md](2026-08-22-hermes-followup-checklist.md), against
[2026-08-22-compaction-recall-test-plan.md](2026-08-22-compaction-recall-test-plan.md). Predecessor reading:
[2026-08-23-compaction-arm-ab-reading.md](2026-08-23-compaction-arm-ab-reading.md).

**The short version.** Arms C and D clear §11's promotion arithmetic. Neither is promotable, for reasons that
have nothing to do with their scores. Arm E was not refused — the rule was inapplicable to it. The sweep's
most useful output is not a ranking of arms: it is four defects in the instrument, one of which means **half
the corpus models a message list the compactor is never handed.** Fix those and re-run; do not build from
this.

---

## 0. Read this before the results table

### 0.1 Compaction runs on agent-run step turns only

`AgentContextCompactor.CompactAsync` has exactly four call sites, and every one of them is gated on a non-null
budget:

| Seam | What it is |
|---|---|
| `ChatSession.cs:953` (`BuildStepChatMessagesAsync`) | An **agent-run step** executed by a live session |
| `HeadlessTurnExecutor.cs:469` | An **agent-run step** executed headlessly |
| `AiClientService.cs:248` | The **in-step tool loop**, rounds > 0 |
| `AiClientService.cs:660` | The **wrap-up** call after that loop |

A plain interactive chat turn passes null — `ChatSession.cs:766` says so in the source, *"The INTERACTIVE call
site (RunTurnAsync) deliberately leaves it null"* — and `BackgroundAssistantTurnRunner.cs:174` calls
`RunExchangeAsync` with no budget at all. **Neither ever compacts.**

Two of the four transcripts are chat shapes (`chat-tool-light`, `chat-tool-heavy`). That is **30 of 60
questions modelling a list no production path produces.** Every "4 of 4" below is really 4 of 4 cells, of
which 2 are reachable.

### 0.2 Effective N is one, not four

`SyntheticTranscript.Build` draws `guidPrefix` from `new Random(options.Seed)` **before any shape branch**, and
no caller overrides `Seed = 20260822`. `Describe()` is a pure function of the fact index and that prefix. So
all four transcripts plant **byte-identical gold answers** and ask a byte-identical 15-question bank. The two
agent shapes are additionally the same generator branch (83 vs 84 messages, 17,399 vs 17,402 tokens).

§11's "wins on ≥3 of 4" clause was written to beat restatement luck. It cannot do that job here: it is one
15-item set asked four times in correlated contexts. Compounding it, the 13 extractable answers span only
**5 templates** with a constant GUID prefix, so a single line can answer 2–3 questions.

### 0.3 The window was an override, and both halves of that matter

8000/2000 came from `PIA_COMPACTION_WINDOW` / `PIA_COMPACTION_MAX_OUTPUT`; the scorecard records it as
`from harness default`. The answering provider's catalogue window is **1,048,576**, so the tested window is
**0.76%** of it, and all four transcripts (1.7–3.4% of the deployed window) would pass through **uncompacted** —
i.e. as arm A at 99.2%.

That does not make C or D worthless, and the two halves may not be printed apart. `ContextWindowDefaults`
falls back to 128,000; the same snapshot lists `deepseek-r1` at 64,000 and a distill at 8,192; and the in-step
tool loop can overflow any window from inside a single step.

### 0.4 The judge's bias points toward granting the promotions

One model answered **and** judged, at temperature 0, with the gold answer in front of it — so the risk is
lenient matching rather than self-graded reasoning. Arm B sits on the 0.0% floor and cannot be inflated, so
leniency raises exactly the quantities §11 reads: `C−B`, `D−B`, `E−B`. It acts through the `partial = 0.5`
tier, and **no per-arm correct/partial/wrong/unreadable split was captured.** That split is owed before any
implementation decision.

---

## 1. The results

Provider: `deepseek/deepseek-v4-flash` via OpenRouter, answering and judging, temperature 0. Corpus: the
committed synthetic generator, 4 transcripts × 15 planted facts. Budget 8000/2000 (see §0.3). 37 minutes,
~750 calls.

`gold` = how many of that transcript's 15 gold answers the arm's own context held **verbatim**.

| transcript | A | B | C | C0 *(control)* | D | E |
|---|---|---|---|---|---|---|
| chat-tool-light | 100.0% | 0.0% | 40.0% | 13.3% | 20.0% | 0.0% |
| chat-tool-heavy | 96.7% | 0.0% | 53.3% | 40.0% | 33.3% | 26.7% |
| agent-run | 100.0% | 0.0% | 53.3% | 13.3% | 30.0% | 0.0% |
| agent-run-with-image | 100.0% | 0.0% | 46.7% | 13.3% | 13.3% | 0.0% |
| **AVG** | **99.2%** | **0.0%** | **48.3%** | **20.0%** | **24.2%** | **6.7%** |
| gold held (of 60) | 0 | 0 | 52 | 52 | **0** | 5 |
| retained tokens | 17.4–35.8K | 1.8–4.2K | 1.9–5.2K | 152–1201 | 1.9–4.3K | 2.4–5.7K |

`A 96.7%` and `D 30.0%` are off the k/15 grid only because a `partial` judge verdict scores 0.5 (14.5/15 and
4.5/15).

### Rule applications, by name

| §11 row | Fires? |
|---|---|
| Arm A < 90% ⇒ stop, instrument broken | **No.** A = 99.2%. |
| B ≥ 85% of A on all four ⇒ current compaction is fine | **No.** B/A = 0.0% on every transcript. The pre-registered "legitimate and useful outcome" is refused. |
| Beats B by ≥10 avg **and** wins ≥3 of 4 ⇒ promote | **C (+48.3, 4 of 4), D (+24.2, 4 of 4)** — and **C0 (+20.0, 4 of 4)**, see below. |
| Beats B by <10, or wins on 1–2 ⇒ record and stop | **E (+6.7, 1 of 4)** — but see §4. |
| Wins big on exactly one transcript ⇒ suspected restatement luck until a fifth transcript | **E** (its only win). On the plain reading of "big", also **C0** (40.0 against 13.3 ×3 — the cell where its block is 1201 tokens, not 152). |

**§11 has no control carve-out, so C0 satisfies the promotion rule as written.** That is a gap in the plan, not
a result: record it rather than let the doc grade its own homework.

### Arithmetic checks that passed

All five averages match the pooled counts to 1 dp (A 59.5/60, B 0/60, C 29/60, C0 12/60, D 14.5/60, E 4/60).
`C tokens = B + C0` exactly on all four cells. Every arm's correct count sits inside its own `gold` bound.
Drop-one sensitivity: removing `chat-tool-heavy` leaves C +46.7, C0 +13.3 and D +21.1 all still promoting,
while **E falls to +0.0 on 0 of 3** — E's entire result is that one transcript.

---

## 2. Arm B reproduces 0.0% on a second provider — a floor confirmed, not a discovery

Arm B scored 0.0% on all four, matching the Mistral run exactly. The replies were literal `UNKNOWN` with
readable verdicts, so this is refusal rather than an empty body.

But this is close to forced: the leak filter guarantees zero gold answers in the retained text and the
no-context control already scored 0.0%. The only new information is that **nothing in the retained text made
an answer derivable.** Two OpenAI-compatible models agreeing on a floor is weak evidence of provider
independence — a floor is the least discriminating quantity in the sweep.

It also does **not** license "compaction loses 100% of the facts". The planted facts stop well before each
transcript's tail (indices 7–63 of 83, 8–64 of 84, 13–144 of 163), so the newest region the compactor always
retains carries no fact by construction. The supported sentence is: **100% of the facts planted where eviction
was certain to reach.**

---

## 3. Arm C — the rule fires; what the score consists of is unmeasured

> **Arm C's +48.3 was earned against a context that was, by construction, a 100%-precision answer key holding
> 13 of the 15 answers in 152 tokens, produced from a filler vocabulary built to be unextractable. On real
> content the same extractors fire 5–23 times per 1,000 tokens, so both the block's cost and its precision are
> unmeasured outside this corpus.**

That sentence and the +48.3 belong on the same page. Read apart, they reconstruct the misreading this section
exists to prevent.

**The stimulus is a pure answer key.** Porting the extractor over the generator's 15 planted statements: on
three of four shapes the block is exactly 13 lines, one anchor per line, and **all 13 are gold answers — zero
distractors, precision 13/13.** Lines read literally `#N assistant: PIA-E4003`. Nothing else survives, because
the generator's filler is *"lowercase letters, spaces and periods only"* (`SyntheticTranscript.cs:64`) — a
choice made for an unrelated reason (keeping the exactly-once postcondition honest) that simultaneously
produces the 152-token block **and** the 100% precision. **The cost and the benefit are one design choice
stated twice.**

**Linking is free, so C0 cannot measure it.** 11 of the 13 anchors carry the question's own stage number
inside the value (`ingest-07.md`, `PIA-E4009`, `probe_stage_12`); only the two quantities are unlabelled. A
self-keyed answer sheet needs no conversation to be matched to *"Which error code did stage 09 abort with?"*

**C0 is not a one-variable control.** It also loses the pinned system prompt and the head goal, drops from
~4,346 tokens to 152, and — decisively — the shared answering instruction *"Answer only from the conversation
above… otherwise reply exactly UNKNOWN"* is **unsatisfiable** when the entire context is one system-role index
whose own header says the surrounding wording is gone. A refusal/instruction-following confound is a live
alternative explanation for C0's floor. And C0's own level moves **26.7 points (13.3% → 40.0%) on block size
alone**, with its gold held constant at 13 — a nuisance property of the appended block, no retained
conversation involved, already reproduces most of the C−C0 gap.

So: **C0's 20.0% shows the block alone is not sufficient. It does not establish what the remaining 28.3 points
are doing.** Do not print the converse either — list-reading is not proven; the inference is unsupported.

**What arm C converts.** 6/13, 8/13, 8/13, 7/13 — pooled **55.8%** of what it holds. C0 on the identical held
set: 2/13, 6/13, 2/13, 2/13 — pooled **23.1%**. At 0.5 per partial, 7.25 points is not seven answers recovered.

**Cost, corrected.** The block is 152 tokens on three transcripts and 1,201 on the fourth: **3.6%, 3.9%, 8.7%
and 30.2%** of arm B's retained context (pooled 4.6% over the three; ×7.9 the token cost on the fourth for the
same +53.3 points). An earlier draft of this reading said "3.6% on three transcripts" — wrong on two of them.

**Density off this corpus, measured.** Distinct anchors per 1,000 tokens, deduped exactly as
`CompactionArms.Extract` does: synthetic filler **0.0**; repo docs and plans 5.2; `CLAUDE.md` 10.3; a git diff
11.1; `git log --stat` 12.6; C# source 22.6. Projected onto the same ~14.4K-token dropped region that produced
the 152-token block: **~800–1,700 tokens.** Tail case: a plain `find src -type f` listing — a routine tool
result in an agent run — runs 57.4 anchors/1K and yields a block **99.8% the size of the body it indexes**
(7,820 of 7,835 tokens), i.e. zero compression. There is no cap; adding one introduces a selection policy
whose precision is the entire question and which no arm measured.

**Two further problems for a shipped version.** The block is appended *after* the compactor has already fit
the window (`AnchorIndex` is `retained.ToList()` plus one message), so arm C **never pays** its own cost — at
800–1,700 tokens a real version would evict more conversation to afford it, eating the retained context. And
arm C's structural half is **inert where it would ship**: both round-0 seams build text-only messages
(`AssistantMessage.ToChatMessage()` emits only `TextContent`/`DataContent`; `HeadlessTurnExecutor` rebuilds
persisted rows as `new ChatMessage(role, m.Content)`), so the `FunctionCallContent.Name`/`CallId` reads arm C
is credited with never execute there. They only ever run in the in-step tool loop — whose messages are never
persisted.

**Verdict.** The rule promotes arm C, and promotion under §11 means *write a proposal*, not *validated*. The
proposal cannot be written from this data: it needs the block's cost at real density, and a cap plus selection
policy that nothing here measured.

---

## 4. Arm D — the one clean empirical result, and it is not a compactor change

**What is established.** Arm D's context held **gold = 0 on all four** transcripts, arm B scored 0.0%, and the
no-context control scored 0.0%. So its +24.2 really is **search-earned** rather than read off an appended list.
That is a genuine result and it is where the finding stops.

**Why it cannot be promoted.** §11 promotes an arm "to a real implementation proposal against
`AgentContextCompactor.cs`". The compactor returns a `List<ChatMessage>`; the only part of arm D it could emit
is the **footer**, and the footer alone earns nothing. The search half lives in the turn/tool layer and has no
product surface at all:

- `IAssistantChatService.SearchMessagesAsync` had **zero production callers** — it was written for this row
  (B7) and nothing exposed it to a model. It was deleted on 2026-08-25 for exactly that reason, so a grep for
  it now finds nothing.
- `AssistantChatMessages` persists user/assistant **text** plus a `ToolCallCount` **count**;
  `AssistantMessage.ToChatMessage()` never emits `FunctionCall`/`FunctionResult` content. So a real recovery
  search is **blind to exactly the tier eviction reaches first** (`ToolEvictionThreshold = 0.45`).
- At the in-step tool loop seam there is no store to search: `AiClientService.cs:236-241` says
  *"workingMessages is the ONLY list in Pia that ever holds FunctionCallContent / FunctionResultContent
  messages … Nothing here is persisted: workingMessages is discarded when the loop ends."*

So the footer's promise — *"They are NOT lost: they are still stored and searchable by exact substring"* — is
**false in the shipping product** for that content. The harness searched an in-memory `removed` set with tool
arguments and results rendered in via `SyntheticTranscript.Trace`, i.e. **strictly more capable than anything
shipped**. And the inversion that hides in the numbers: `chat-tool-heavy` is arm D's best transcript (33.3%)
and it is the one shape whose facts land in tool payloads — 5 of its 15 golds sit in `FunctionResultContent`
JSON. **Arm D looks strongest precisely where it would degrade most.**

**The hermes +20…+43 band is dropped.** Different codebase, real ~500K transcripts, a summarising rather than
an evicting compactor, and an actual retrieval path. Magnitudes coinciding is not corroboration.

**The search-rate lever is real; the number is not diagnosable.** Arm D converted 14.5 points from 28 searches
— **51.8% per search** (50%, 83%, 56%, 25%) — and chose to search on only 6–8 of 15. Both levers are worth
about the same: searching all 15 at today's conversion gives ~51.8%; perfect conversion on today's 28 searches
gives 46.7%. So "the biggest lever" overstates it. Worse, the instrument cannot separate the cases:
`RunArmAsync` increments `recovered` as soon as a `SEARCH:` term **parses**, before any hit exists, and
`RenderHits` returns a non-null *"no matches"* string — so 6–8 mixes *searched, got hits, answered wrong* with
*searched, got nothing*, and never counts *didn't search*. **No trace callback was passed for arm D**, so no
term, hit count or second-round answer was captured anywhere.

Two unexamined search-**quality** causes sit inside the 48% loss, both invisible in the recorded numbers:
hits are returned **earliest-first** (harness `.Take(5)`, production `ORDER BY Ordinal ASC`), which
systematically excludes the answer-bearing message for a broad term about a high stage number; and the harness
hands back the **whole rendered message** where production returns a ±120-character window around the first
occurrence.

**Cost, understated by construction.** `ArmResult.ApproximateTokens` counts the **first** call's context only.
Arm D actually makes **88 answering calls against 60** for B and C, roughly **+51% input tokens** before hit
payloads (against arm C's +12%), plus a second **sequential** provider round-trip the user waits on. Per unit
of extra input, arm C buys ~4.0 points per 1% and arm D ~0.4 — an order of magnitude apart.

**Verdict.** Clears the bar in the harness. Shipping it is **a new assistant tool + a turn-loop change + a
persistence change**, scoped and estimated separately — not a change to `AgentContextCompactor` — and its
measured score was earned against an oracle no such implementation can have.

---

## 5. Arm E — the rule was inapplicable, not refusing

On three of four transcripts **every planted fact sits on an assistant message**, so pinning user messages had
nothing to pin: arm E's context held **zero gold by construction** and its 0.0% there is **forced, not
measured**. (Reproduced from the generator's index arithmetic, and matching the reported gold = 0/0/0/5
exactly: stride 4 over 79 candidates with fixed parity on the three small shapes; stride 7 over 119 with
period 3 on `chat-tool-heavy`, splitting 5 user / 5 tool / 5 assistant.)

Averaging three forced zeros is what pulls **+26.7 down to +6.7**. On the one transcript where the treatment
could act, arm E **cleared the rule's bar**. And its 3.325-point shortfall against the 10-point line sits
inside the plan's own stated ±3–5 judge noise, so even on its own terms only the "3 of 4" clause refused it —
a clause decided by three cells where the treatment was a no-op.

§11's anti-luck row therefore applies: **suspected restatement luck until a fifth transcript reproduces it.**

**Withholding user messages from compaction is untested, not refused.** Do not write that pinning user
messages does not help.

---

## 6. Sentences this reading must not contain

Kept as a list because each one is a specific misreading the numbers invite.

- ~~"Four transcript shapes confirm the result."~~ → *one 15-item question set, asked in four correlated
  contexts from one generator at one seed, of which two map to a live compaction seam.*
- ~~"Arm C is not merely reading an appended answer key, because C0 scores 20.0% against C's 48.3%."~~ →
  *C0's 20.0% shows the block alone is not sufficient; it does not establish what the remaining 28.3 points
  are doing.*
- ~~"Pinning user messages does not help."~~ → see §5.
- ~~"Compaction loses 100% of the facts."~~ → *100% of the facts planted where eviction was certain.*
- ~~"Arm D lands inside hermes's +20…+43 band."~~ → dropped entirely; see §4.
- ~~"The anchor block is cheap."~~ → its size scales with the extractable content of the dropped region; it
  moved 152 → 1,201 tokens inside this one sweep.
- ~~"The image shape shows compaction's effect on multimodal recall."~~ → image parts are stripped at send
  time (random bytes, undecodable); that shape is a budget-pressure variant of the agent-run shape.
- ~~"provider-independent"~~, ~~"model-independent"~~, ~~"validated"~~ — unqualified, for any arm.

---

## 7. What the next sweep must fix, cheapest first

Everything here is instrument work. **None of it needs a provider call except the last two.**

1. **Drop or re-label the chat shapes.** They model no compacting path (§0.1). Either replace them with two
   more agent-run shapes or say plainly that half the corpus is out of scope.
2. **Vary the seed per shape.** One line. Today all four transcripts share a byte-identical bank, which is
   what disarms §11's anti-luck clause.
3. **Give the filler an extractable vocabulary.** The 100%-precision anchor block is an artifact of a filler
   alphabet chosen for an unrelated reason. Until the filler contains paths, codes and quantities that are
   *not* answers, arm C's precision is unmeasurable.
4. **Plant facts on every role.** Arm E is untestable while three of four shapes put all 15 facts on assistant
   messages.
5. **Log arm D's per-question detail** — term, hit count, whether the second round ran, and the answer. Three
   fields; without them the search-rate diagnosis is unfalsifiable.
6. **Publish the per-arm correct/partial/wrong/unreadable split**, and run the zero-call check on the existing
   transcripts: how many of arm C's `correct` answers are whole-anchor-line dumps? The judge is asked only
   *"Does the given answer state the expected answer?"*, so a dump scores correct without the model selecting
   anything.
7. **Charge the anchor block against the budget** so arm C pays for itself, and add a cap so the `find`-listing
   case cannot produce a block the size of its own body.
8. **Two cheap new arms would settle §3**, ~60 calls each: a **framing control** (block + pinned system
   message + head goal, nothing else) and a **mis-keyed block** (the same 13 anchors permuted across stage
   numbers). Together they separate "the model recalled" from "the model pattern-filled".
9. **Use a different judging model from the answering one** (§0.4), or publish the split and accept the
   direction of the bias.

---

## 8. What this cost

37 minutes wall-clock, ~750 provider calls on `deepseek/deepseek-v4-flash`, plus 38 in the pre-flight (smoke,
refusal check, no-context control). Concurrency 2 at a ≥1.1 s pace gate; **no 429 and no lost transcript** —
the pacing and the per-transcript try/catch that the arm-A/B reading added both held.
