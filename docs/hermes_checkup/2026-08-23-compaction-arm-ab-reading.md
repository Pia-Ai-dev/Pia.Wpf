# Compaction recall — arms A and B, the first real number

**Status:** pre-registered. §1–§8 were written and committed **before the first provider call**; §9 is the
only section added afterwards.
**Owner:** Marco Altmann. **Written:** 2026-08-23.
**Origin:** [`2026-08-22-compaction-recall-test-plan.md`](2026-08-22-compaction-recall-test-plan.md)
steps B3 + B4, with the owner answers in its §15.

---

## 1. The question, and the rule that reads the answer

> Can the model still answer questions about the part of the conversation that compaction removed?

Arm **A** is the full transcript, uncompacted — the ceiling. Arm **B** is today's behaviour. The bank is
drawn only from what arm B removed, so a question about surviving content cannot inflate either arm.

The decision rule is the plan's §11, fixed before any number exists:

| Outcome | Action |
|---|---|
| Arm A scores **< 90%** | **Stop.** The bank or the judge is broken. Fix the instrument; report no B number under a failing A. |
| Arm A **< 95%** on the *synthetic* corpus | Treat as a plumbing or bank bug rather than judge calibration: every planted answer occurs exactly once in the transcript by construction, so arm A is being asked about text it still holds. |
| B ≥ **85%** of A on every transcript | Current compaction is fine. Write that down, close the item, do not tune the thresholds. |
| An arm beats B by ≥ 10 points averaged **and** wins on ≥ 3 of 4 | Promote to an implementation proposal. (Not reachable in this batch — only A and B run.) |
| A gap that appears on exactly one transcript | Suspected restatement luck until a fifth transcript reproduces it. |

Nothing below §8 may be used to revise anything above it.

## 2. The corpus is synthetic, and that is a finding rather than a shortcut

The plan's §5 asks for four real transcripts: a tool-light chat, a tool-heavy chat, an agent run of ≥ 8
steps, and an agent run carrying an image. **This profile holds none of them.** Measured on a *copy* of
`%LOCALAPPDATA%\Pia\history.db`, so the live file was never opened:

| Fact | Value |
|---|---|
| `AssistantChats` | 43 |
| `AssistantChatMessages` | **99** (50 assistant, 49 user), ~108 KB of `Content` in total |
| Largest single chat | **9 messages, 26,374 characters** (≈ 6.6k tokens) |
| Agent runs | 9, the largest with **5** steps (§5 wants ≥ 8) |
| Messages matching `data:image`, `![`, `.png`, `.jpg`, `attachment`, `image/` | **0 each** |
| Messages matching `tool_call`, `<tool_call`, `function_call`, or a fenced `json` block | **0 each** |

Two of those rows are structural rather than incidental, and they close the door on ever extracting shapes
2 and 4 through the plan's §5 SQL. `AssistantChatMessages` has columns
`Id, ChatId, Ordinal, Role, Content, ThinkingContent, Timestamp, Tokens, ModelName, PersonaId, PersonaName,
PersonaEmoji` — **no attachment column**, and the only roles present are `user` and `assistant`. So a
persisted chat cannot carry a tool round or an image attachment at all, whatever the user does in the app.

**Therefore this measurement runs on the committed synthetic generator** (`SyntheticTranscript`, B1), which
produces all four §5 shapes — including the tool-heavy one and the fused text+image turn — with 15 uniquely
worded facts planted at known unpinned positions. Per the plan's §15 that is the arm every contributor can
run, and it needs no privacy decision from anyone.

What that buys and what it costs, stated up front so §9 cannot be read as more than it is:

- **Buys a gold standard.** Each planted answer provably occurs exactly once in the transcript — the
  generator throws at build time otherwise — so a wrong answer is a recall failure and not an ambiguity. It
  also makes the four shapes comparable, which four real transcripts of wildly different sizes are not.
- **Costs external validity.** Planted facts are needles in filler; a real conversation restates itself,
  which is precisely the restatement luck that gave hermes a 93.3% on nothing. A synthetic arm B is
  therefore a **lower bound** on real-world recall, and the honest reading of a low B is "compaction loses
  facts that are stated exactly once", not "compaction loses N% of what users ask about".
- The real corpus stays a gap. Closing it needs either a longer real conversation than this profile holds,
  or a transcript source that carries tool rounds — which today does not exist in the schema.

## 3. Budgets

Two were planned (§15 answer 2). One is measurable here.

- **Small window, primary: `8000 / 2000`.** Compaction-forcing, cheap, discriminates hardest, and the same
  pair the existing structural tests use. Every number in §9 names it.
- **A real configured provider's window: not available.** All five providers in this profile have
  `maxContextWindowTokens = null`, so `AgentContextBudget.From` returns null for every one of them and
  **compaction never fires for this user today** — the plan's §2 item 3, observed rather than inferred.
  There is no configured window to measure against, so the second measurement is deferred rather than faked.
  The harness takes `PIA_COMPACTION_WINDOW` / `PIA_COMPACTION_MAX_OUTPUT` and the scorecard records where
  the budget came from, so the second run is a re-invocation and not a code change.

The bank cache key is `(transcript fingerprint, window, max output)`. The plan's §6 says to hash the
transcript alone and §15 answer 2 corrects it: the removed set is a property of *(transcript, budget)*, so a
transcript-only key would answer the second budget's questions from the first budget's removed set.

## 4. Provider

**Mistral Medium 3.5 (`mistral-medium-latest`) for both the answering and the judging call**, per §15
answer 1. Resolved by name or id from `providers.json` through `PIA_COMPACTION_PROVIDER`, with the key
decrypted by the real `DpapiHelper`; absent or unnamed, the sweep **skips** rather than falling back to a
local model. The chat client is built through the production provider handler with
`ChatOptions { Temperature = 0 }`, because the shipped `CreateChatOptions` sets no temperature and §8 asks
for determinism. Real transcript content would reach Mistral on a fixture run; on this run the content is
synthetic, so nothing personal leaves the machine.

## 5. How the bank is built, and the filter that makes it honest

1. Run the transcript through **`AgentContextCompactor.CompactAsync`** — the shipped path, not a
   reimplementation — at the budget under test.
2. Removed set = input messages absent from the output **by reference identity**, never by index:
   compaction reorders, re-attaching the pinned instruction and any pinned image turns at the end.
3. Candidate questions = the planted facts whose carrying message is in the removed set. No generation call
   is spent on the synthetic corpus: the generator already ships a question and a verbatim gold answer per
   fact, which is cheaper and more deterministic than asking a model. (The fixture path, for a real corpus,
   does spend one generation call per transcript. It is implemented and unused here.)
4. **Verbatim-leak filter.** Reject any candidate whose gold answer appears in the *retained* text —
   computed with `SyntheticTranscript.Trace`, which flattens message text **plus tool-call arguments plus
   tool-result payloads**, so an answer hiding in a tool result cannot slip past a text-only check.
5. Cache the survivors under the key in §3, beside the fixtures, never in the repo.

Both arms face the identical bank. A cell whose bank came out smaller than 15 says so in §9.

## 6. Cost

4 transcripts × 2 arms × ≤ 15 questions × 2 calls (answer + judge) = **≤ 240 calls**, all at the small
window. No generation calls on this corpus. The 600 in the plan's §8 is the five-arm sweep, which is B10.

## 7. What is not in this batch

Arms C, D and E (B6, B8, B9), the full sweep (B10), and the message-level search granularity that B8 depends
on (B7). All sit behind this gate, and the gate can close them: the third row of §1's table is a legitimate
outcome, not a failure to find something.

## 8. One thing already settled on the way in

Compaction **preserves tool-call/result pairing** at the small window on 20- and 40-turn tool-heavy
transcripts (`ToolPairingTests`, an ordinary gate test). This was checked before spending a call because an
unpaired survivor is a provider 400 on a real user's step rather than a harness detail — and because
"repairing" it inside the harness would have hidden a shipped bug *and* depressed arm B by dropping retained
content. It holds, so both arms send the compactor's output verbatim.


## 9. Results

Run 2026-08-23, small window, `mistral-medium-latest` answering and judging at temperature 0. 240 calls,
5 m 27 s. Generated scorecard: `<PIA_COMPACTION_EVAL_OUT>/scorecard-synthetic-8000x2000.md`.

| transcript | bank | A: uncompacted | B: current | B/A |
|---|---|---|---|---|
| synthetic-chat-tool-light | 15 | **100.0%** @ 18.6K / 83 msg | **0.0%** @ 4.2K / 21 msg | 0% |
| synthetic-chat-tool-heavy | 15 | **100.0%** @ 35.8K / 163 msg | **0.0%** @ 4.0K / 17 msg | 0% |
| synthetic-agent-run | 15 | 96.7% @ 17.4K / 83 msg | **0.0%** @ 3.9K / 21 msg | 0% |
| synthetic-agent-run-with-image | 15 | 96.7% @ 17.4K / 84 msg | **0.0%** @ 1.8K / 12 msg | 0% |
| **AVG** | 60 | **98.3%** | **0.0%** | **0%** |

### The instrument cleared its own checks, three ways

- **Arm A 98.3%**, above both floors in §1 (90% hard, 95% synthetic). The bank and the judge work: 59.0 of 60
  points, which is 58 correct plus 2 judged partial, from transcripts that still held the fact.
- **A no-context control arm scored 0.0%** over the same 15 questions on transcript 1 — 30 extra calls, and a
  control the plan did not ask for. The planted answers are formulaic (an error code is `PIA-E` plus the stage
  index), so a model able to extrapolate the pattern would have scored on arm B without recalling anything.
  It scored nothing with no transcript at all, so neither A's 98.3% nor B's 0.0% is a scoring artefact.
- **Every bank came out at the full 15.** All 15 planted facts were removed at this budget on all four
  shapes, and none leaked, so arm B's retained context held *no planted fact of any kind* — there was not
  even a sibling in context to extrapolate a pattern from.

### The verdict, against §1 and nothing else

B/A is **0%** against a "≥ 85% and current compaction is fine" rule, on 4 of 4 transcripts. So the
close-the-item branch does not apply, and this is not a near miss: **arm B is indistinguishable from having
no transcript at all.** 0.0% is exactly the no-context control's score.

That is the plan's §2 fact 1 with a number on it. Nothing is summarized, so an evicted message is gone and
no route back exists — dropping 78% of the tokens (18.6K → 4.2K on the tool-light shape) took **100%** of the
removed facts with it. The measurement did not discover the mechanism; it sized the hole, and the hole is the
whole gap between 0.0% and 98.3%.

**B6 (arm C, anchor index), B8 (arm D, recovery pointer, and B7 before it) and B9 (arm E) therefore stay
open**, each with the entire 98.3-point gap to play for. Per the batch that produced this reading, the gate
is **recorded and not acted on**: no arm beyond A and B was run and no threshold was touched.

### What this number does not say

- **One budget only.** At `8000/2000` the compactor drops ~78% of the tokens. A user on a 128k window whose
  transcript is 30k loses nothing, because compaction never fires; and today *no* configured provider in this
  profile sets a window at all (§3), so the shipped default for this user is "no compaction, ever". This is
  the worst case that fires, not the typical case. The second budget is still owed.
- **Synthetic transcripts, facts stated exactly once.** A real conversation restates itself, so a real arm B
  would score above 0. The claim this run supports is narrow and strong: *when a fact is stated once and
  lands in the evicted region, it is gone* — 60 of 60 questions, four shapes, no exception.
- **The image shape is read text-only.** Image parts are stripped at send time because the generator's image
  is random bytes and no provider can decode it. What the image did to *which messages were removed* is
  untouched — the compactor applied its pin and its token charge before the send — and it shows: that shape
  retained the least of the four (1.8K / 12 messages), because the pinned image turn eats the budget.
- **One model family judges its own answers.** §12's judge-inconsistency risk is reduced by one prompt and
  temperature 0, not removed. The 0.0% floor is not judge-sensitive, and that is checked rather than assumed:
  a follow-up probe (`ArmB_Zero_IsARefusal_NotAnEmptyResponse`, 6 calls) read the answer TEXT for three arm-B
  questions and got the literal `UNKNOWN` refusal each time, with a readable verdict. A zero that was really
  an empty-but-successful response would have scored identically and pointed the next three steps at a
  phantom. A future arm scoring in the middle *will* be judge-sensitive.
- **The verdict parser was wrong in a way this run could not show.** It took letters from the start of the
  reply, so `**partial**` or `- correct` parsed to nothing and scored 0 - invisible at 0% and at ~100%, and
  biased against exactly the mid-range results arms C and D are expected to produce. Fixed after the run
  (skip leading non-letters, whole-word match, and an **unreadable-verdict count** now surfaced per cell), so
  for THIS run unreadability is unmeasured outside the probe above - one more reason 0.0% and 98.3% are the
  only two numbers it is safe to read.

### Operational notes, so the next run does not relearn them

- **Concurrency 3 earned a 429** from Mistral 95 s into the first attempt, losing the run. The sweep now
  paces at ≥ 1.1 s between calls with exponential backoff (6 attempts, capped at 60 s) at concurrency 2:
  240 calls in 5 m 27 s.
- **A per-transcript try/catch was added after that**, because one provider fault on the fourth transcript
  discarded every call the first three had already paid for. A partial scorecard that says it is partial
  beats no scorecard.
- The scorecard's number formatting became culture-invariant only *after* this run, so the generated file
  reads `100,0%` on a German machine. The table above is the same data with dots.
- Total spend for this reading: 240 (sweep) + 2 (smoke) + 60 (control, run twice to read its own number) and
  roughly 60 more lost to the rate-limited and abandoned first attempts.
