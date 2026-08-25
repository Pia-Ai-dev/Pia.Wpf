# Compaction recall — closeout

**Status:** **closed 2026-08-24, promoted nothing.** No arm was promoted, no threshold was touched, no
product code changed. §6 is a *what a re-open must fix* list, **recorded as closed, not queued work.**
**Owner:** Marco Altmann. **Written:** 2026-08-24 (as the arms C/D/E reading); folded into the track's
single record 2026-08-25.
**Origin:** §3.3 of [`2026-08-22-hermes-update-review.md`](2026-08-22-hermes-update-review.md), tracked as
rows B1–B12 of [`2026-08-22-hermes-followup-checklist.md`](2026-08-22-hermes-followup-checklist.md).

This is the whole B track in one file: the test plan, the owner answers that fixed it before any call was
spent, the arm-A/B reading, the arms-C/D/E reading, and the closure. `2026-08-22-compaction-recall-test-plan.md`
is deleted with this change. `2026-08-23-compaction-arm-ab-reading.md` is absorbed here too but stays on disk
until checklist row C6 lands, because a frozen C-track doc links it (`2026-08-24-c5-c7-batch-report.md:15`);
it is deliberately **not** linked from here, so nothing dangles when it goes.

Three claims those two docs make about the code are **false today** and are corrected below rather than
carried forward: that the first `CompactAsync` seam is an interactive chat turn (§1.4), that compaction is
opt-in, and that every provider in the profile has a null window (both §1.5).

---

## 1. What was asked, and the answer

> **Can the model still answer questions about the part of the conversation that compaction removed?**

`AgentContextCompactor` shrinks an outgoing request so a long transcript cannot overflow the model's context
window. It is careful, well-commented and, before this track, **completely unmeasured**: we knew how many
messages it removed and nothing about what the agent could still answer afterwards. Token count is the metric
we had and it is the wrong one — it measures the cost, not the damage. Two policies retaining the same number
of tokens can differ enormously in what survives.

**Nothing is summarized.** `CompactionProvider.CompactAsync(strategy, toCompact, logger, ct)` takes no chat
client, so evicted content is *gone*, not paraphrased. Hermes's compaction summarizes, so its failure mode is
a summarizer mangling identifiers; Pia's failure mode is total loss. Any technique that preserves specific
facts is therefore worth more here, not less.

**The answer is 0.0%, twice, on two providers.**

### 1.1 Run 1 — Mistral Medium 3.5, 2026-08-23, arms A and B

Provider `mistral-medium-latest` answering **and** judging, temperature 0. Budget 8000/2000. **240 calls,
5 m 27 s.** Scorecard: `<PIA_COMPACTION_EVAL_OUT>/scorecard-synthetic-8000x2000.md`.

| transcript | bank | A: uncompacted | B: current | B/A |
|---|---|---|---|---|
| synthetic-chat-tool-light | 15 | **100.0%** @ 18.6K / 83 msg | **0.0%** @ 4.2K / 21 msg | 0% |
| synthetic-chat-tool-heavy | 15 | **100.0%** @ 35.8K / 163 msg | **0.0%** @ 4.0K / 17 msg | 0% |
| synthetic-agent-run | 15 | 96.7% @ 17.4K / 83 msg | **0.0%** @ 3.9K / 21 msg | 0% |
| synthetic-agent-run-with-image | 15 | 96.7% @ 17.4K / 84 msg | **0.0%** @ 1.8K / 12 msg | 0% |
| **AVG** | 60 | **98.3%** | **0.0%** | **0%** |

The instrument cleared its own checks three ways on this run:

- **Arm A 98.3%**, above both floors (90% hard, 95% synthetic): 59.0 of 60 points — 58 correct plus 2 judged
  partial — from transcripts that still held the fact.
- **A no-context control scored 0.0%** over the same 15 questions on transcript 1: 30 extra calls, a control
  the plan did not ask for. The planted answers are formulaic (an error code is `PIA-E` plus the stage index),
  so a model able to extrapolate the pattern would have scored on arm B without recalling anything. It scored
  nothing with no transcript at all, so neither A's 98.3% nor B's 0.0% is a scoring artefact.
- **Every bank came out at the full 15.** All 15 planted facts were removed at this budget on all four shapes
  and none leaked, so arm B's retained context held no planted fact of any kind — not even a sibling to
  extrapolate a pattern from.

Arm B's replies were the literal `UNKNOWN` refusal, not an empty body: `ArmB_Zero_IsARefusal_NotAnEmptyResponse`
(6 calls) read the answer **text** for three arm-B questions and got the refusal each time, with a readable
verdict. That test exists because a zero which was really an empty-but-successful response would have scored
identically and pointed the next three steps at a phantom.

### 1.2 Run 2 — `deepseek/deepseek-v4-flash` via OpenRouter, 2026-08-24, all five arms plus a control

Answering and judging, temperature 0. Same committed synthetic generator, 4 transcripts × 15 planted facts,
budget 8000/2000. **~750 calls plus 38 in the pre-flight** (smoke, refusal check, no-context control),
**37 minutes** wall clock. Concurrency 2 at a ≥1.1 s pace gate; no 429 and no lost transcript.

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

**Read the two runs in that order.** Mistral ran first. The second zero is *a floor confirmed, not a
discovery*, and it is close to forced: the leak filter guarantees zero gold answers in the retained text and
the no-context control already scored 0.0%. The only new information is that nothing in the retained text made
an answer derivable. **Two OpenAI-compatible models agreeing on a floor is weak evidence of provider
independence** — a floor is the least discriminating quantity in the sweep. Never print an arm's number
without the provider and the date beside it; two zeros on two providers otherwise read as one measurement.

### 1.3 The narrowing that makes 0.0% honest

It does **not** license "compaction loses 100% of the facts". The planted facts stop well before each
transcript's tail (indices **7–63 of 83, 8–64 of 84, 13–144 of 163**), so the newest region the compactor
always retains carries no fact by construction. The supported sentence is:

> **100% of the facts planted where eviction was certain to reach.**

And because the corpus is synthetic with each fact stated exactly once, **arm B is a lower bound** — a real
conversation restates itself. The honest reading of a low B is "compaction loses facts that are stated exactly
once", not "compaction loses N% of what users ask about".

### 1.4 Where compaction actually runs — and the label that cost half the corpus

`AgentContextCompactor.CompactAsync` has exactly four call sites and every one is gated on a non-null budget.
All four are agent-run paths:

| Seam | What it is |
|---|---|
| `ChatSession.cs:953` (`BuildStepChatMessagesAsync`) | An **agent-run step** executed by a live session |
| `HeadlessTurnExecutor.cs:469` | An **agent-run step** executed headlessly |
| `AiClientService.cs:248` | The **in-step tool loop**, rounds > 0 |
| `AiClientService.cs:660` | The **wrap-up** call after that loop |

A plain interactive chat turn passes null: `ChatSession.cs:768` says so in the source — *"The INTERACTIVE call
site (RunTurnAsync) deliberately leaves it null"* — and `:353` says the interactive message list *"is
deliberately never compacted"*, structurally, by being built in a separate method body from the step builder.
`BackgroundAssistantTurnRunner` calls `RunExchangeAsync` with no budget at all. **Neither ever compacts.**

**The root cause of this track's largest defect is a label in the test plan.** Its §2 table listed the first
seam — numbered `ChatSession.cs:952` at the time — as *"Interactive chat turn"*. It is not; it is in the
step-building path. The corpus was designed against that table, so two of the four transcripts are chat shapes
(`chat-tool-light`, `chat-tool-heavy`), which is **30 of 60 questions modelling a message list no production
path produces**. Every "4 of 4" in this document is really 4 of 4 cells, of which **2 are reachable**. The
wrong label is recorded here rather than quietly replaced, because it is the entire explanation for why half
the measurement was spent on a path the product never compacts.

Line numbers drifted while the track ran: 952 → 953, `HeadlessTurnExecutor` 462 → 469. Re-anchor before
trusting any of them.

### 1.5 Compaction is not opt-in any more, and the profile is not window-less

Both dead docs say otherwise, and both are stale:

- The test plan's §2 fact 3 — *"Compaction is opt-in. `AgentContextBudget.From(provider)` returns `null`
  unless the provider has `MaxContextWindowTokens` set, so a provider predating those fields never compacts at
  all."*
- The arm-A/B reading, in **two** places (its §3 and again in §9's "what this number does not say") — *"All
  five providers in this profile have `maxContextWindowTokens = null` … the shipped default for this user is
  'no compaction, ever'."*

**Both are false since B11.** `ProviderService.cs:64` and `:85` stamp
`provider.MaxContextWindowTokens ??= ContextWindowDefaults.For(provider.ModelName)` on every provider read,
with a 128,000 floor (`ContextWindowDefaults.Fallback`), so compaction now fires for real chats.

The other half of that correction has to be written too, or it contradicts the code:
**`AgentContextBudget.From` itself still returns null for a bare unconfigured provider**
(`AgentContextBudget.cs:34`). The default deliberately lives where providers are constituted rather than in
`From`, because putting it in `From` failed 80 tests. State both halves or neither.

---

## 2. How it was measured

### 2.1 The corpus is synthetic, and that is a finding rather than a shortcut

The plan's §5 asked for four real transcripts: a tool-light chat, a tool-heavy chat, an agent run of ≥ 8
steps, and an agent run carrying an image. **This profile holds none of them.** Measured on a *copy* of
`%LOCALAPPDATA%\Pia\history.db`, so the live file was never opened:

| Fact | Value |
|---|---|
| `AssistantChats` | 43 |
| `AssistantChatMessages` | **99** (50 assistant, 49 user), ~108 KB of `Content` in total |
| Largest single chat | **9 messages, 26,374 characters** (≈ 6.6k tokens) |
| Agent runs | 9, the largest with **5** steps (the plan wanted ≥ 8) |
| Messages matching `data:image`, `![`, `.png`, `.jpg`, `attachment`, `image/` | **0 each** |
| Messages matching `tool_call`, `<tool_call`, `function_call`, or a fenced `json` block | **0 each** |

Two of those rows are structural, and they close the door on ever extracting shapes 2 and 4 through that SQL.
`AssistantChatMessages` has columns `Id, ChatId, Ordinal, Role, Content, ThinkingContent, Timestamp, Tokens,
ModelName, PersonaId, PersonaName, PersonaEmoji` — **no attachment column**, and the only roles present are
`user` and `assistant`. A persisted chat cannot carry a tool round or an image attachment at all, whatever the
user does in the app. **That is why the corpus is synthetic**, not a shortcut.

What that buys and costs: it buys a gold standard (each planted answer provably occurs exactly once — the
generator throws at build time otherwise — so a wrong answer is a recall failure and not an ambiguity, and the
four shapes are comparable in a way four real transcripts of wildly different sizes are not); it costs
external validity (§1.3). The real corpus stays a gap, and closing it needs either a longer real conversation
than this profile holds or a transcript source that carries tool rounds, which the schema does not have.

Privacy rules, unchanged and still binding: **fixtures and question banks are never committed**, they live
outside the repo and the harness takes a path; the scorecard is **numbers only**, because a question string is
transcript-derived; and the harness must be runnable with no real corpus at all, which is what the committed
generator is for.

### 2.2 The bank, the leak filter, and the cache key

1. Run the transcript through **`AgentContextCompactor.CompactAsync`** — the shipped path, not a
   reimplementation — at the budget under test.
2. Removed set = input messages absent from the output **by reference identity**, never by index: compaction
   reorders, re-attaching the pinned instruction and any pinned image turns at the end.
3. Candidate questions = the planted facts whose carrying message is in the removed set. No generation call is
   spent on the synthetic corpus; the generator ships a question and a verbatim gold answer per fact. (The
   fixture path, for a real corpus, does spend one generation call per transcript. Implemented, unused here.)
4. **Verbatim-leak filter.** Reject any candidate whose gold answer appears in the *retained* text — computed
   with `SyntheticTranscript.Trace`, which flattens message text **plus tool-call arguments plus tool-result
   payloads**, so an answer hiding in a tool result cannot slip past a text-only check. This is the guard
   against the restatement luck that inflated hermes's 93.3% (§3.1).
5. Cache the survivors beside the fixtures, never in the repo.

**The bank cache key is `(transcript fingerprint, window, max output)`.** The plan's §6 said to hash the
transcript alone and the owner's answer corrected it: the removed set is a property of *(transcript, budget)*,
so a transcript-only key would answer the second budget's questions from the first budget's removed set —
**a wrong number that looks like a right one.**

### 2.3 Budgets: an override, and both halves matter

- **Small window, the only one measured: `8000 / 2000`**, from `PIA_COMPACTION_WINDOW` /
  `PIA_COMPACTION_MAX_OUTPUT`; the scorecard records it as `from harness default`. Compaction-forcing, cheap,
  discriminates hardest, and the same pair the existing structural tests use.
- The answering provider's catalogue window is **1,048,576**, so the tested window is **0.76%** of it, and all
  four transcripts (1.7–3.4% of the deployed window) would have passed through **uncompacted** — i.e. as arm A
  at 99.2%. **Never print either half without the other.**
- That does not make the measurement worthless: `ContextWindowDefaults` falls back to **128,000**, the same
  snapshot lists `deepseek-r1` at **64,000** and a distill at **8,192**, and the in-step tool loop can
  overflow any window from inside a single step.
- The window table's source is [`../openrouter_models/2026-08-24-openrouter-context-lengths.md`](../openrouter_models/2026-08-24-openrouter-context-lengths.md).
  It is **regenerated from the OpenRouter models endpoint, never hand-edited**; a regeneration bumps the doc
  and `OpenRouterContextWindows.SnapshotDate` together.
- **The second budget — a real configured provider's window — was never measured, and is still owed.**

### 2.4 The judging protocol

**This is the only prose record of it.** The harness README covers how to *run* the suite, not how a score is
produced, so cutting this means the next re-run reconstructs the protocol from `CompactionRecallHarness.cs`.

1. For each (transcript, arm): build the post-compaction message list.
2. For each of the 15 questions: send `[post-compaction context] + [question]` to a **fresh** model with no
   other history. **One question per call — batching lets one answer leak into the next.**
3. Judge each answer against gold with a **separate** judging call: `correct / partial / wrong`. **Score
   `partial` as 0.5.** Use the same judge model across all arms and record which model it was.
4. Record per (transcript, arm): recall %, tokens retained, messages retained, wall-clock.

**Determinism.** Temperature 0 where the provider allows it. Even so, **expect ±3–5 points of run-to-run noise
from the judge — a 2-point difference is not a result.**

**Cost.** 4 transcripts × 5 arms × 15 questions × 2 calls (answer + judge) = **600 calls per five-arm sweep**
on paper; ~750 plus 38 pre-flight and 37 minutes in practice. The two-arm gate run was 240. Cache the banks so
re-runs pay only for answering and judging.

The judging model was resolved by name or id from `providers.json` through `PIA_COMPACTION_PROVIDER`, with the
key decrypted by the real `DpapiHelper`; absent or unnamed, the sweep **skips** rather than falling back to a
local model. That is a design requirement, not a run-time one: a contributor with only cloud providers has to
be able to run the sweep, and an owner who prefers a local judge has to be able to point it at one without
editing code.

### 2.5 The pre-flight, and why it mattered

Compaction **preserves tool-call/result pairing** at the small window on 20- and 40-turn tool-heavy
transcripts (`ToolPairingTests`, an ordinary gate test). This was checked **before spending a call**, because
an unpaired survivor is a provider 400 on a real user's step rather than a harness detail — and because
"repairing" it inside the harness would have hidden a shipped bug *and* depressed arm B by dropping retained
content. It holds, so both arms send the compactor's output verbatim.

One guardrail the harness was not allowed to weaken, and must not become a reason to weaken: the compactor
**operates on the request copy only** — it takes an `IReadOnlyList` and returns a new `List`, and is
type-incapable of reaching a persisted transcript (the HARD GUARDRAIL remark on `AgentContextCompactor`).

---

## 3. The pre-registered rules, and which fired

### 3.1 Calibration only — hermes's numbers, and the arm that scored 93.3% by luck

Hermes ran this experiment on its own compaction in August 2026 (four real ~500K-token transcripts, 15 recall
questions each). Their compactor **summarises** and Pia's **evicts**, so the absolute numbers are not
comparable and this table is a calibration reference, never a target.

| Policy | Tokens retained | Recall |
|---|---|---|
| Uncompacted (ceiling) | 500K | 96.7% |
| Their old default — big tail | 162K | **45.8%** |
| Lean tail | 49K | 40.0% |
| Lean tail **+ recovery pointer** | 49K | **68.3%** |

**One of their arms scored 93.3% by luck.** That transcript happened to restate its own facts later, so
nothing was really lost; the same policy scored 30–33% on two fresh transcripts. **A single good number proves
nothing** — that finding is what dictated the four-transcript floor and the verbatim-leak filter in §2.2.

**Their +20…+43 recovery-pointer band is dropped, not cited.** Different codebase, real transcripts, a
summarising compactor and an actual retrieval path; magnitudes coinciding is not corroboration (§4.3).

### 3.2 The decision rules, verbatim, fixed before any number existed

Pre-registration is checkable rather than asserted. `750385cd` (2026-08-23, *"Answer the compaction plan's
three open questions before spending calls"*) landed the owner answers; `89f4eb70` (2026-08-23,
*"Pre-register the compaction A/B read, and the corpus it cannot have"*) landed §1–§8 of the arm-A/B reading
with §9 present but reading only *"Empty by design until the sweep has run. Everything above this line was
committed first."* The results commit is `fe9adf2f`.

| Outcome | Action |
|---|---|
| Arm A (uncompacted) scores < 90% | **Stop.** The bank or the judge is broken. Fix the instrument before reading anything else. |
| B ≥ 85% of A's score on all four transcripts | Current compaction is fine. Write that down, close the item, and don't tune the thresholds. **This is a legitimate and useful outcome.** |
| An arm beats B by **≥ 10 points averaged over all four**, and wins on **at least 3 of 4** | Promote to a real implementation proposal against `AgentContextCompactor.cs`. |
| An arm beats B by < 10 points, or wins on only 1–2 transcripts | Record it and stop. Below the noise-plus-luck floor. |
| Any arm wins big on exactly one transcript | Treat as suspected restatement luck until a fifth transcript reproduces it. |

Threshold tuning (`0.45` / `0.70`) was deliberately **not** an arm: get the instrument trustworthy first; a
threshold sweep is cheap to add afterwards and meaningless before.

### 3.3 Which rules fired

| Rule | Fires? |
|---|---|
| Arm A < 90% ⇒ stop, instrument broken | **No.** A = 99.2%. |
| B ≥ 85% of A on all four ⇒ current compaction is fine | **No.** B/A = 0.0% on every transcript. The pre-registered "legitimate and useful outcome" is **refused, 4 of 4**. |
| Beats B by ≥10 avg **and** wins ≥3 of 4 ⇒ promote | **C (+48.3, 4 of 4), D (+24.2, 4 of 4)** — and **C0 (+20.0, 4 of 4)**, the control. |
| Beats B by <10, or wins on 1–2 ⇒ record and stop | **E (+6.7, 1 of 4)** — but see §4.4. |
| Wins big on exactly one transcript ⇒ suspected restatement luck until a fifth transcript | **E** (its only win). On the plain reading of "big", also **C0** (40.0 against 13.3 ×3 — the cell where its block is 1201 tokens, not 152). |

**The pre-registered rules have no control carve-out, so C0 satisfies the promotion rule as written.** That is a gap in the plan,
not a result: recorded here rather than quietly exempted, because a doc that grades its own homework is the
failure this track was built to avoid.

**Arithmetic checks that passed.** All five averages match the pooled counts to 1 dp (A 59.5/60, B 0/60,
C 29/60, C0 12/60, D 14.5/60, E 4/60). `C tokens = B + C0` exactly on all four cells. Every arm's correct
count sits inside its own `gold` bound. Drop-one sensitivity: removing `chat-tool-heavy` leaves C +46.7,
C0 +13.3 and D +21.1 all still promoting, while **E falls to +0.0 on 0 of 3** — E's entire result is that one
transcript.

---

## 4. Arm by arm

Arms C, D and E were prototypes inside the harness. **None of them modified `AgentContextCompactor.cs`** —
they post-process its output, so the shipped code stayed untouched.

### 4.1 A and B

Arm A is the full transcript, the ceiling. Arm B is today's behaviour. B/A is **0%** against a "≥ 85% and
current compaction is fine" rule, on 4 of 4 cells: **arm B is indistinguishable from having no transcript at
all**, and 0.0% is exactly the no-context control's score. Dropping ~78% of the tokens (18.6K → 4.2K on the
tool-light shape) took **100%** of the reachable planted facts with it. The measurement did not discover the
mechanism — §1's "nothing is summarized" already implied it — it **sized the hole**, and the hole is the whole
gap between 0.0% and 98.3%.

### 4.2 Arm C (mechanical anchor index) — the rule fires; what the score consists of is unmeasured

> **Arm C's +48.3 was earned against a context that was, by construction, a 100%-precision answer key holding
> 13 of the 15 answers in 152 tokens, produced from a filler vocabulary built to be unextractable. On real
> content the same extractors fire 5–23 times per 1,000 tokens, so both the block's cost and its precision are
> unmeasured outside this corpus.**

That sentence and the +48.3 belong on the same page; read apart they reconstruct the misreading this section
exists to prevent.

**The stimulus is a pure answer key.** On three of four shapes the block is exactly 13 lines, one anchor per
line, and **all 13 are gold answers — zero distractors, precision 13/13.** Lines read literally
`#N assistant: PIA-E4003`. Nothing else survives extraction, because the generator's filler is *"lowercase
letters, spaces and periods only"* (`SyntheticTranscript.cs:64`) — a choice made for an unrelated reason
(keeping the exactly-once postcondition honest) that simultaneously produces the 152-token block **and** the
100% precision. **The cost and the benefit are one design choice stated twice.**

**Linking is free, so C0 cannot measure it.** 11 of the 13 anchors carry the question's own stage number
inside the value (`ingest-07.md`, `PIA-E4009`, `probe_stage_12`); only the two quantities are unlabelled. A
self-keyed answer sheet needs no conversation to be matched to *"Which error code did stage 09 abort with?"*

**C0 is not a one-variable control.** It also loses the pinned system prompt and the head goal, drops from
~4,346 tokens to 152, and — decisively — the shared answering instruction *"Answer only from the conversation
above… otherwise reply exactly UNKNOWN"* is **unsatisfiable** when the entire context is one system-role index
whose own header says the surrounding wording is gone. **A refusal / instruction-following confound is a live
alternative explanation for C0's floor.** C0's own level moves **26.7 points (13.3% → 40.0%) on block size
alone**, gold held constant at 13 — a nuisance property of the appended block, no retained conversation
involved, already reproducing most of the C−C0 gap.

So: **C0's 20.0% shows the block alone is not sufficient. It does not establish what the remaining 28.3 points
are doing.** Do not print the converse either — list-reading is not proven; the inference is unsupported.

**What arm C converts.** 6/13, 8/13, 8/13, 7/13 — pooled **55.8%** of what it holds. C0 on the identical held
set: 2/13, 6/13, 2/13, 2/13 — pooled **23.1%**. At 0.5 per partial, 7.25 points is not seven answers
recovered.

**Cost, corrected.** The block is 152 tokens on three transcripts and 1,201 on the fourth: **3.6%, 3.9%, 8.7%
and 30.2%** of arm B's retained context (pooled 4.6% over the three; ×7.9 the token cost on the fourth for the
same +53.3 points). An earlier draft of the reading said "3.6% on three transcripts" — wrong on two of them.

**Density off this corpus, measured.** Distinct anchors per 1,000 tokens, deduped exactly as
`CompactionArms.Extract` does: synthetic filler **0.0**; repo docs and plans 5.2; `CLAUDE.md` 10.3; a git diff
11.1; `git log --stat` 12.6; C# source 22.6. Projected onto the same ~14.4K-token dropped region that produced
the 152-token block: **~800–1,700 tokens.** Tail case: a plain `find src -type f` listing — a routine tool
result in an agent run — runs **57.4** anchors/1K and yields a block **99.8% the size of the body it indexes**
(7,820 of 7,835 tokens), i.e. zero compression. **There is no cap**, and adding one introduces a selection
policy whose precision is the entire question and which no arm measured.

**Two further problems for a shipped version.** The block is appended *after* the compactor has already fit
the window (`AnchorIndex` is `retained.ToList()` plus one message), so **arm C never pays its own cost** — at
800–1,700 tokens a real version would evict more conversation to afford it. And arm C's structural half is
**inert where it would ship**: both round-0 seams build text-only messages
(`AssistantMessage.ToChatMessage()` emits only `TextContent`/`DataContent`; `HeadlessTurnExecutor` rebuilds
persisted rows as `new ChatMessage(role, m.Content)`), so the `FunctionCallContent.Name`/`CallId` reads arm C
is credited with never execute there. They only ever run in the in-step tool loop — whose messages are never
persisted.

**Verdict.** The rule promotes arm C, and promotion means *write a proposal*, not *validated*. The proposal
cannot be written from this data: it needs the block's cost at real density, and a cap plus a selection policy
that nothing here measured.

### 4.3 Arm D (recovery pointer) — the one clean empirical result, and it cannot ship as a compactor change

**What is established.** Arm D's context held **gold = 0 on all four** transcripts, arm B scored 0.0%, and the
no-context control scored 0.0%. So its +24.2 really is **search-earned** rather than read off an appended
list. That is a genuine result and it is where the finding stops.

**Why it cannot be promoted.** The rule promotes an arm "to a real implementation proposal against
`AgentContextCompactor.cs`". The compactor returns a `List<ChatMessage>`; the only part of arm D it could emit
is the **footer**, and the footer alone earns nothing. The search half lives in the turn/tool layer and has no
product surface at all:

- `IAssistantChatService.SearchMessagesAsync` had **zero production callers** — written for this row (B7),
  never exposed to a model. It was deleted on 2026-08-25 for exactly that reason, so a grep for it now finds
  nothing.
- `AssistantChatMessages` persists user/assistant **text** plus a `ToolCallCount` **count**;
  `AssistantMessage.ToChatMessage()` never emits `FunctionCall`/`FunctionResult` content. A real recovery
  search is therefore **blind to exactly the tier eviction reaches first** (`ToolEvictionThreshold = 0.45`).
- **The existing full-text index cannot serve it either.** `AssistantChatsFts` is FTS5 over
  `(ChatId UNINDEXED, Title, Body)` with **one aggregated row per chat** — its own schema comment says a
  single FTS row represents an aggregated chat document — so a search returns *which chat* mentioned
  something, never *which message*. That granularity is too coarse for within-run recovery. Closing it (a
  message-level index, or a direct query over `AssistantChatMessages WHERE ChatId = @id`) was checklist row
  **B7**, arm D's named prerequisite — a real prerequisite, not a detail, and it was never built.
- At the in-step tool loop seam there is no store to search: `AiClientService.cs:236-241` says
  *"workingMessages is the ONLY list in Pia that ever holds FunctionCallContent / FunctionResultContent
  messages … Nothing here is persisted: workingMessages is discarded when the loop ends."*

So the footer's promise — *"They are NOT lost: they are still stored and searchable by exact substring"* — is
**false in the shipping product for that content.** The harness searched an in-memory `removed` set with tool
arguments and results rendered in via `SyntheticTranscript.Trace`, i.e. **strictly more capable than anything
shippable**. The tell is in the numbers: `chat-tool-heavy` is arm D's **best** transcript (33.3%) and it is
the one shape whose facts land in tool payloads — 5 of its 15 golds sit in `FunctionResultContent` JSON.
**Arm D looks strongest precisely where it would degrade most.**

**The search-rate lever is real; the number is not diagnosable.** Arm D converted 14.5 points from 28 searches
— **51.8% per search** (50%, 83%, 56%, 25%) — and chose to search on only 6–8 of 15. Both levers are worth
about the same: searching all 15 at today's conversion gives ~51.8%; perfect conversion on today's 28 searches
gives 46.7%. So "the biggest lever" overstates it. Worse, the instrument cannot separate the cases:
`RunArmAsync` increments `recovered` as soon as a `SEARCH:` term **parses**, before any hit exists, and
`RenderHits` returns a non-null *"no matches"* string — so 6–8 mixes *searched, got hits, answered wrong* with
*searched, got nothing*, and never counts *didn't search*. **No trace callback was passed for arm D**, so no
term, hit count or second-round answer was captured anywhere; a re-run must log all three.

Two unexamined search-**quality** causes sit inside the 48% loss, both invisible in the recorded numbers: hits
are returned **earliest-first** (harness `.Take(5)`, production `ORDER BY Ordinal ASC`), which systematically
excludes the answer-bearing message for a broad term about a high stage number; and the harness hands back the
**whole rendered message** where production returns a ±120-character window around the first occurrence.

**Cost, understated by construction.** `ArmResult.ApproximateTokens` counts the **first** call's context only.
Arm D actually makes **88 answering calls against 60** for B and C, roughly **+51% input tokens** before hit
payloads (against arm C's +12%), plus a second **sequential** provider round-trip the user waits on. Per unit
of extra input, arm C buys ~4.0 points per 1% and arm D ~0.4 — an order of magnitude apart.

**Verdict.** Clears the bar in the harness. Shipping it is **a new assistant tool + a turn-loop change + a
persistence change**, scoped and estimated separately — *not* a change to `AgentContextCompactor` — and its
measured score was earned against an oracle no such implementation can have.

### 4.4 Arm E (pin every user message) — inapplicable, not refused

On three of four transcripts **every planted fact sits on an assistant message**, so pinning user messages had
nothing to pin: arm E's context held **zero gold by construction** and its 0.0% there is **forced, not
measured**. (Reproduced from the generator's index arithmetic and matching the reported gold = 0/0/0/5
exactly: stride 4 over 79 candidates with fixed parity on the three small shapes; stride 7 over 119 with
period 3 on `chat-tool-heavy`, splitting 5 user / 5 tool / 5 assistant.)

Averaging three forced zeros is what pulls **+26.7 down to +6.7**. On the one transcript where the treatment
could act, arm E **cleared the rule's bar**, and its 3.325-point shortfall against the 10-point line sits
inside the plan's own ±3–5 judge noise — so even on its own terms only the "3 of 4" clause refused it, a
clause decided by three cells where the treatment was a no-op. The anti-luck rule therefore applies: suspected
restatement luck until a fifth transcript reproduces it.

**Withholding user messages from compaction is untested, not refused. Do not write that pinning user messages
does not help.**

Arm E is also the only arm that tests whether Pia's **half-held** "user messages are never compacted"
invariant costs anything. Pia pins the head goal and the newest user message; a user message in between goes
into `toCompact` and can be evicted like any other. `UserMessagePinningTests` pins that this really is
half-held. The question survives this closure.

---

## 5. What the instrument could not see

### 5.1 Four defects in the instrument

1. **Two of the four shapes model no compacting path** (§1.4) — half the corpus, and the root cause is the
   test plan's mislabelled seam.
2. **Effective N is 1, not 4.** `SyntheticTranscript.Build` draws `guidPrefix` from `new Random(options.Seed)`
   **before any shape branch**, and no caller overrides `Seed = 20260822`. `Describe()` is a pure function of
   the fact index and that prefix, so all four transcripts plant **byte-identical gold answers** and ask a
   byte-identical 15-question bank; the two agent shapes are additionally the same generator branch (83 vs 84
   messages, 17,399 vs 17,402 tokens). The "wins on ≥ 3 of 4" clause was written to beat restatement luck and
   **could not do that job here**. Compounding it, the 13 extractable answers span only 5 templates with a
   constant GUID prefix, so a single line can answer 2–3 questions.
3. **The window was an override at 0.76% of the answering provider's catalogue window** (§2.3), and never
   print one half of that without the other.
4. **One model answered and judged**, at temperature 0, with the gold answer in front of it — so the risk is
   lenient matching rather than self-graded reasoning. Arm B sits on the 0.0% floor and cannot be inflated, so
   leniency raises exactly the quantities the promotion rule reads: `C−B`, `D−B`, `E−B`. It acts through the
   `partial = 0.5` tier.

### 5.2 Measurement traps, so the next run does not relearn them

- **The verdict parser took letters from the start of the reply**, so `**partial**` or `- correct` parsed to
  nothing and scored 0 — **invisible at 0% and at ~100%, and biased against exactly the mid-range arms C and D
  produce.** Fixed after the arm-A/B run (skip leading non-letters, whole-word match) and an
  **unreadable-verdict count** is now surfaced per cell; for that run, unreadability is unmeasured outside the
  refusal probe. One more reason 0.0% and 98.3% are the only two numbers it is safe to read from run 1.
- **Arm D's `recovered` counter and `RenderHits`** conflate searched-and-wrong with searched-and-empty and
  never count didn't-search (§4.3).
- **`ArmResult.ApproximateTokens` counts only the first call's context**, so arm D's cost is understated by
  construction (§4.3).
- **Concurrency 3 earned a 429** from Mistral 95 s into the first attempt and lost the run. The sweep now
  paces at ≥ 1.1 s between calls with exponential backoff (6 attempts, capped at 60 s) at concurrency 2.
- **A per-transcript try/catch was added after that**, because one provider fault on the fourth transcript
  discarded every call the first three had already paid for. A partial scorecard that says it is partial beats
  no scorecard.
- **The scorecard was culture-sensitive** and reads `100,0%` on a German machine; the numbers became
  culture-invariant only after run 1.
- **The image shape is read text-only.** Image parts are stripped at send time because the generator's image
  is random bytes and no provider can decode it. What the image did to *which messages were removed* is
  untouched — the compactor applied its pin and its token charge before the send — and it shows: that shape
  retained the least of the four (1.8K / 12 messages), because the pinned image turn eats the budget.

### 5.3 Still owed, and never captured

- **The per-arm correct / partial / wrong / unreadable split.** Never captured on either run. It is the split
  the judge's leniency acts through, so it is owed before any implementation decision.
- **The second budget** — a real configured provider's window. Authorised by the owner, never measured.

---

## 6. Recorded closed, not queued

**Nothing below is scheduled work.** The track closed 2026-08-24 having promoted nothing, and these are the
conditions a re-open would have to satisfy first. They are written down so a future sweep does not spend
another ~750 calls on the same instrument, not because anyone owes them.

Everything here is instrument work, and **none of it needs a provider call except the last two**. Cheapest
first:

1. **Drop or re-label the chat shapes.** They model no compacting path (§1.4). Either replace them with two
   more agent-run shapes or say plainly that half the corpus is out of scope.
2. **Vary the seed per shape.** One line. Today all four transcripts share a byte-identical bank, which is
   what disarms the anti-luck clause.
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
8. **Two cheap new arms would settle §4.2**, ~60 calls each: a **framing control** (block + pinned system
   message + head goal, nothing else) and a **mis-keyed block** (the same 13 anchors permuted across stage
   numbers). Together they separate "the model recalled" from "the model pattern-filled".
9. **Use a different judging model from the answering one** (§5.1 item 4), or publish the split and accept the
   direction of the bias.

**Items 1 and 2 are the difference between a trustworthy instrument and another 750 wasted calls.** Anything
below them is refinement.

### Sentences this track must not produce

Each one is a specific misreading the numbers invite.

- ~~"Four transcript shapes confirm the result."~~ → *one 15-item question set, asked in four correlated
  contexts from one generator at one seed, of which two map to a live compaction seam.*
- ~~"Arm C is not merely reading an appended answer key, because C0 scores 20.0% against C's 48.3%."~~ →
  *C0's 20.0% shows the block alone is not sufficient; it does not establish what the remaining 28.3 points
  are doing.*
- ~~"Pinning user messages does not help."~~ → see §4.4.
- ~~"Compaction loses 100% of the facts."~~ → *100% of the facts planted where eviction was certain.*
- ~~"Arm D lands inside hermes's +20…+43 band."~~ → dropped entirely; see §4.3.
- ~~"The anchor block is cheap."~~ → its size scales with the extractable content of the dropped region; it
  moved 152 → 1,201 tokens inside this one sweep.
- ~~"The image shape shows compaction's effect on multimodal recall."~~ → image parts are stripped at send
  time (random bytes, undecodable); that shape is a budget-pressure variant of the agent-run shape.
- ~~"provider-independent"~~, ~~"model-independent"~~, ~~"validated"~~ — unqualified, for any arm.

---

## 7. Provenance, and what to read instead of this

**The instrument survives as code.** `tests/Pia.Wpf.Tests/Integration/Compaction/` (9 files) plus
`scripts/Export-CompactionCorpus.ps1`, and that folder's `README.md` documents the run recipe, the environment
variables, the pre-registration test to run first, and the fixture/privacy conventions better than the plan
did. **Point at it; do not re-document it here.** Two rules in it are easy to break and cheap to re-break:

- **No file in that folder may name a `Microsoft.Agents.AI.Compaction` type** — the experimental (MAAI001)
  surface is contained inside `src/Pia.Wpf/Services/AgentContextCompactor.cs`, and a second suppression
  anywhere in the solution fails the gate.
- **Diff the removed set by reference identity, not by index** — compaction reorders.

The harness is explicit-only (`[LiveApiFact]`, xunit v3 `Explicit = true`), so it never runs in `dotnet test`
and reports as `Not Run`. It runs on Windows or CI only.

| Commit | What it fixed, and when |
|---|---|
| `750385cd` (2026-08-23) | The owner's three answers — provider, budget, arm-E deferral — landed **before any provider call**. |
| `89f4eb70` (2026-08-23) | §1–§8 of the arm-A/B read pre-registered, §9 an explicit empty placeholder. |
| `fe9adf2f` (2026-08-23) | Run 1's numbers (arms A and B, Mistral). |
| `eb215739` (2026-08-23) | The refusal probe and the verdict-parser fix (§5.2). |
| `109693a5` (2026-08-24) | Run 2 (arms C, D, E and the control) — the reading this document grew out of. |

**Why the result was worth having even though nothing shipped.** Two undocumented constants (0.45 / 0.70) sat
in the request path on reasoning alone. This track did not justify changing them and did not license leaving
them alone either; what it produced is a sized hole, an instrument with four known defects, and a written
reason why the two arms that cleared the promotion arithmetic cannot be built as compactor changes.
