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

*Empty by design until the sweep has run. Everything above this line was committed first.*
