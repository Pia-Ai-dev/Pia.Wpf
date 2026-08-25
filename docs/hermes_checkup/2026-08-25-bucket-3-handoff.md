# Bucket-3 handoff — what is left of the hermes follow-up, and what is finished

**Status:** Open. This is the deferred remainder of the hermes follow-up after buckets 1 and 2 landed
(`8e00f8ee`, `52328597`). Nothing below is in flight.
**Owner:** Marco Altmann.
**Written:** 2026-08-25.
**Origin:** the recommendation table of [2026-08-22-hermes-update-review.md](2026-08-22-hermes-update-review.md),
tracked in [2026-08-22-hermes-followup-checklist.md](2026-08-22-hermes-followup-checklist.md), carried forward
after the per-track plan docs were collapsed into one survivor each.

Executable cold: a session with no memory of this work should be able to pick any section below and either do
it or correctly decide not to. **Part 2 is the half that saves the most time** — it lists work that is
finished or withdrawn, so nobody re-opens it.

**Scales.** *Effort:* `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new
surface · `L` a week or more, a new subsystem. *Value:* `High` user-visible or a real risk closed · `Med`
worthwhile, not headline · `Low` parked · `Enabler` little standalone value, unblocks a High.
**Desk-completable** means it can be finished without a desktop session driving the real app and without
spending live provider calls.

One forward reference: the G-track survivor is
[`../failure_legibility/2026-08-24-failure-legibility.md`](../failure_legibility/2026-08-24-failure-legibility.md);
the `G-Q1` answer is its §15.

---

# Part 1 — The deferred remainder

## 1. The A-track supply re-read, then `A2` → `A3` → `A6` → `A7`

*Effort:* the re-read `S`, then A2 `S`, A3 `S`, A6 `S` (Enabler), A7 `M` · *Value:* **High** ·
**Not desk-completable.**

The gate is a **measurement**, not a decision: it needs real agent runs on a Windows desktop. The band was
pre-registered before the app was ever opened — **build above 40%, drop below 12%, defer 12–40%** — and the
last read was **22% on 13 runs**. Nine of those 13 declared a file, which is a property of the runbook's
prompt categories and **not** of a real day, so the number is not a verdict about ordinary use.

**Do not restate the procedure here — run it from [artifact-evidence.md](artifact-evidence.md).** That file is
the living protocol: the decision band, the two BUILD triggers and the DROP trigger, the profile recipe, the
six prompt categories verbatim, the harvest commands, the field glossary and the collection traps. Its Part I
is the decision, Part II the protocol, Part III what is settled. Shortening the prompt mix to file-producing
tasks measures ~95%, closes the gate falsely, and is the exact failure that protocol exists to prevent.

**Touches:** `AgentVerifier`, `AgentPlanner`'s schema descriptions, the artifact probe and its integration
tests under `tests/Pia.Wpf.Tests/Integration/ArtifactProbe/`. A6 and A7 land **together or not at all** — A6's
"behaviour-preserving refactor" framing conflicts with A7's need for an async seam.

## 2. Retry on the failure card — **WITHDRAWN, not deferred**

*Effort:* `M`–`L` if ever revived · *Value:* **Med** · Desk-completable analysis, already done.

Gate `G-Q1` was **answered 2026-08-25 and both arms are closed.** Re-dispatch is dead on arrival: a Retry
gated on `SafeToReRun` can never enable, because both descriptors that carry `true` are produced where no
failure card exists. Resume-from-step is the only shape that does not duplicate writes, and a failed run does
not leave behind a drainable step ledger. The row is therefore **withdrawn as specified**, not queued.

Read the answer and the prerequisite list — "What a Retry would require", three items that put the work above
the original `M` — in the G-track survivor. Do not re-derive it. The one trap any future attempt inherits: a
retry claim **must not `SET ExtraJson = NULL`** the way both existing resume claims do.

## 3. OpenRouter window-snapshot refresh

*Effort:* `S` · *Value:* **Med** · **Desk-completable.**

`OpenRouterContextWindows` is generated from
[`../openrouter_models/2026-08-24-openrouter-context-lengths.md`](../openrouter_models/2026-08-24-openrouter-context-lengths.md)
— regenerate, never hand-edit; that doc carries the `curl` that produced it. Live re-reads fire **only when an
OpenRouter provider is saved**, so every other provider type, and any OpenRouter provider nobody re-opens,
runs on the dated snapshot indefinitely.

**This is a decision, not a task:** choose a refresh path, a periodically regenerated snapshot, or nothing.
Doing nothing is defensible — say so in the doc rather than leaving the question open a third time.

## 4. Repetition guard (#8) and empty-response guard (#9)

*Effort:* `S` and `S`–`M` · *Value:* **Med** each · **Desk-completable** to build; both want one live check.

Two independent guards on the turn loop. **#9 matters most for unattended routines**, where nobody is watching
the spend: an unsignaled empty response today buys nothing and costs a call. Signaled refusals are already
terminal and must stay excluded from any retry budget. The design for both is in the review's §5 rows 8 and 9
— **read them there** (see Part 3).

**Touches:** the truncated-response continuation nudge and the turn loop around it.

## 5. Three mid-tier items

| Item | Effort | Value | Desk-completable | Touches |
|---|---|---|---|---|
| **`meeting-followup` citations + an on-demand past-meeting path** | `S` | Med | mostly — the prompt half is desk work, the result needs one real meeting | the blueprint's prompt text and its scheduling shape |
| **ESTOP global pause (#7)** | `S` | Med | no — the tray toggle needs a desktop check | tray surface, the scheduler tick, `HeadlessRunLauncher`; never kills in-flight work |
| **Citation-ledger inversion in `WebCitationExtractor` (#14)** | `M` | Med | yes | `WebCitationExtractor`; hand the model integers, never let it author a URL |

On `meeting-followup`: the blueprint already ships the evidence-first framing the review asked for (it states
the meeting title and date, the front matter's attendees, whether the transcript reads as complete, and
whether speaker labels are real names or placeholders, before extracting anything; it queries existing todos
first so a re-run is not a duplicate factory). What is left is the **citation back to the transcript passage**
and a path that points it at **one named past meeting** rather than only today's. The *decisions* half is
section 7 below, not here.

## 6. The low tail — parked, stated plainly

*All rated `Low`.* None of these is next, and none should be picked up because it is small.

- **Timeout inventory (#17)**, `S` — count first; do not build the resolver before the count justifies it.
- **Outbound webhooks (#16)**, `M` — on the existing `AgentTimelineService` observer drain; would give
  `AgentRunTrigger.Event` an owner.
- **Adversarial-UX test (#18)**, `S` — a recorded WinWright flow plus prompt, with the pragmatism filter. Not
  desk-completable.

## 7. Deferred to the routines session — not to a later bucket

Both of these live in `src/Pia.Wpf/Models/RoutineBlueprint.cs`, which the routines session has heavily
modified. Doing them here would guarantee a conflict; doing them there is nearly free.

- **The `meeting-followup` *decisions* half.** The blueprint extracts action items only; the source prompt
  also produces the meeting's *decisions*. `XS` once the prompt is open in front of you.
- **`RoutineSlotKind` — keep or delete.** It ships with one member (`Text`) and no reader, deliberately.
  `RoutineSlot` is not persisted, so either direction is code-only: delete it until a second kind exists, or
  keep it as the seam `Time` / `Enum` land on. Whoever adds the second kind should make the call.

---

# Part 2 — Recorded as closed. Do not redo.

**The whole B-track (compaction recall) is closed, and it promoted nothing.** Arms A–E were built and swept;
no arm shipped. Read [2026-08-24-compaction-recall-closeout.md](2026-08-24-compaction-recall-closeout.md)
before spending anything here. Its **§7 is a nine-item instrument fix list, seven of which cost no provider
call** — that list is *what a re-open would have to fix first*, not queued work. The headline reason: the
instrument must be trustworthy before any arm's number can drive a change, starting with the fact that
compaction only ever runs on agent-run **step** turns, so half the corpus modelled a message list the product
never hands the compactor.

**`G5` and gate `G-Q1` are answered and closed.** See Part 1 §2. The row is withdrawn, not open.

**Review #10 — "mark iteration-truncated child results" — is already implemented.** Verified against the code
2026-08-25, and it is **two separate mechanisms**, not one:

- **The budget signal.** A child that parks at its own halved budget (`AgentRunOrchestrator.cs:1422` halves the
  parent's wall clock) parks under the budget vocabulary (`"step-cap"` / `"wall-clock"`). The parent sees
  `children.AnyParked` and **re-parks rather than building on it**, writing
  `ChildrenParkedReason = "children-parked"` (`AgentRunOrchestrator.cs:47`, `:381-391`) — deliberately no
  `SafeEndRun` and no promotion, because a park is not terminal and one Continue on the parent re-dispatches
  the group. That is the "finished versus ran out of budget" distinction the review asked for.
- **The length guard, separately.** An over-long child answer is stamped `"… (truncated)"` at
  `AgentRunOrchestrator.cs:1551` above `MaxChildAnswerChars` (4000). This is a size cap on the answer text, not
  the budget signal — do not read the two as one feature.

**D2–D6 and D8 are owner-parked** (2026-08-24) — [`../guided_tour/2026-08-24-d-track-parked.md`](../guided_tour/2026-08-24-d-track-parked.md)
is the resume point and holds the design. `D7` (AutomationId gap-fill) was severed from that track and is a
**tag-along**: fold it into any UI change that touches the affected views, never schedule it on its own.

**Ollama context windows are a no-op, not a regression.** The catalogue is keyed by OpenRouter basenames;
Ollama uses short tags. A 4k local model never reaches the 128k floor, so compaction never fires and Ollama
keeps sliding its own window exactly as before. Worth fixing only if local models gain a window source
(Ollama's `/api/show` reports one).

**`BlueprintKey` stays data-only** — owner decision, 2026-08-24. No UI reads it, the question it answers needs
months of real use, and it is answerable by SQL against `history.db` in the meantime.

**`A2`'s drop branch is not licensed.** 22% sits inside the deferral band, which by its own pre-registered
rule means *defer while naming what would move it* — not drop. Dropping A2 on the 22% would be reading the
band backwards.

**The F-track (gate profile hygiene) is closed** and now has its own doc:
[`../test_hygiene/2026-08-24-gate-profile-hygiene.md`](../test_hygiene/2026-08-24-gate-profile-hygiene.md).
One instance of the same defect class is still live there (three architecture tests reflect-invoke
`Bootstrapper.ConfigureServices` against the un-redirected profile); it is recorded, not scheduled.

**The E-track (per-routine persona and effort pins) is shipped and its decision is recorded** in
[2026-08-24-routine-pin-sync-decision.md](2026-08-24-routine-pin-sync-decision.md). Neither pin goes on the
sync wire; that doc holds the three reversal triggers and the two questions still open for the owner.

---

# Part 3 — The still-unbuilt recommendations live in the review. Link them; do not paraphrase them.

The review's **§5 recommendation table** is the design record for the items nobody has built — **#7, #8, #9,
#10, #14, #16, #17 and #18** as it was written, of which **#10 has since turned out to be implemented**
(Part 2). Its **§3.5 / §3.6** carry the design substance behind those rows, and its **§4 "Explicitly not
ours"** is the sole record of six deliberate non-adoptions.

**Read them at the source:** [2026-08-22-hermes-update-review.md](2026-08-22-hermes-update-review.md) §5, and
§3.5 / §3.6 / §4.

This handoff carries only a pointer, a size and a reason to care, on purpose. **A second paraphrase is how the
substance gets lost while both documents still look fine** — the summary reads complete, the original looks
redundant, and the next collapse deletes the only copy of the design.

**Therefore: the review must NOT be collapsed, trimmed or superseded on the strength of this handoff
existing.** It is an origin document, not a track artefact. It is also cited from outside this folder
(`docs/vault_skills/2026-08-23-vault-skills-design.md`), which is an independent reason it cannot be deleted.
If a future pass proposes folding it, that pass is wrong.

---

# Suggested order

Cheapest decisive work first, then the things that need a desktop or a provider bill.

```
3 (OpenRouter snapshot — decide, S)        # a decision, not a build; closes a recurring question
4 (#8 repetition guard, then #9)           # desk work, real value on unattended routines
5 (#14 citation ledger)                    # the only M in the mid tier that is fully desk-completable
1 (A supply re-read → A2 → A3 → A6 + A7)   # needs a Windows desktop session; the gate is a measurement
5 (ESTOP, meeting-followup citations)      # each wants a desktop or a real meeting
6 (the low tail)                           # only if something makes one of them matter
```

Section 7 goes to the routines session whenever it next runs, regardless of this order.
