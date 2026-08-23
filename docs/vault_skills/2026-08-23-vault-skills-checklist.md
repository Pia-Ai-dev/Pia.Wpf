# Implementation Checklist — Vault Skills

Tracking file for [`2026-08-23-vault-skills-plan.md`](2026-08-23-vault-skills-plan.md), which implements
[`2026-08-23-vault-skills-design.md`](2026-08-23-vault-skills-design.md). One row per task. Tick as they
land, in the commit that lands them.

**Effort** — `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface
· `L` a week or more, a new subsystem.

**Value** — `High` user-visible improvement or a real risk closed · `Med` worthwhile, not headline ·
`Enabler` little standalone value, unblocks a High.

---

## Decision gates

Two gates. One is already closed; the other can cancel half the plan.

| Gate | Closes | Question it answers |
|---|---|---|
| **D1** | — | Is a new `ScheduledJobKind` safe on a peer running an older build? **Closed 2026-08-23: yes.** The dispatcher is a ternary and does *not* treat an unknown kind as inert, but the due-jobs query is owner-pinned (`ScheduledJobService.cs:122`) so a peer stores the row and never fires it. Full trace in design §11.1. `T13` carries the test that keeps this true. |
| **G1** | T9–T15 | **Does a loaded skill visibly change an answer in the real app?** Answered by hand at the end of Chunk 2, before any producer work. If it does not, the fault is the `description` wording or step 0's position in the tool tree — and every task below is premature until that is fixed. |
| **G2** | Phase 2 (design §6.3) | Does the naive harvest propose anything worth keeping? Read the first real run's proposals before building the counted-repetition store or the rejected-output signal. |

---

## Chunk 1 — The skill document type

- [ ] **T1 · `type: skill` is a canonical type.**
  One `CanonicalGroups` row plus an `InferTypeFromPath` arm makes skills visible in the Vault view and
  `browse_index` together; the test covers both walks because they have drifted before.
  *Deps:* none · *Effort:* **XS** · *Value:* **Enabler**

- [ ] **T2 · A sectioned skill collapses to one browse entry.**
  Generalises the `memory/topics/` special case in `BuildEntries` so a three-section skill is one row, not
  three.
  *Deps:* T1 · *Effort:* **XS** · *Value:* **Med**

- [ ] **T3 · `RecallHit.Tier` returns `"skill"`.**
  Makes a procedure hit distinguishable from a personal fact in the recall payload; the third value the
  field's own "binary on purpose" comment anticipated.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**

- [ ] **T4 · `SkillPage` parses and validates.**
  Truncate an overlong `description` on read, reject it on write — asymmetric on purpose, so a
  hand-edited file still works while a page Pia writes must be correct.
  *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

## Chunk 2 — The index and `load_skill`

- [ ] **T5 · `ISkillCatalog` + `SkillCatalog`.**
  Enumerate, parse, sort by slug, cap at 40 skills / 4 KB, name the omitted count, invalidate on the vault
  watcher. Sorting is a correctness property here: an unsorted index varies between reads.
  *Deps:* T4 · *Effort:* **M** · *Value:* **Enabler**

- [ ] **T6 · The `## Skills` section, byte-stable.**
  The section goes in the system prompt and a test pins that two composes over an unchanged vault are
  byte-identical — that stability is the whole reason it is not matched per turn.
  *Deps:* T5 · *Effort:* **S** · *Value:* **High**

- [ ] **T7 · Tool-selection tree step 0.**
  Skills must be considered before the reminder and todo branches, or "write my status report" routes to
  Todo. The test asserts ordering, not presence.
  *Deps:* T6 · *Effort:* **XS** · *Value:* **High**

- [ ] **T8 · `load_skill`.**
  A read tool that delegates to `ReadTopicAsync` for its guard chain but additionally pins the result under
  `memory/skills/`, so the slug does not become a general read primitive with a friendlier name.
  *Deps:* T6 · *Effort:* **S** · *Value:* **High**
  **This is the vertical slice — G1 is answered here, by hand, before Chunk 3.**

## Chunk 3 — Reach and starters

- [ ] **T9 · Four starter skills.**
  Seeded like `AGENTS.md` — written only when absent, never overwritten. They fix the cold-start empty
  state and are the only worked examples of the format most owners will see.
  *Deps:* T1, **G1** · *Effort:* **S** · *Value:* **High**

- [ ] **T10 · Reach: agent runs and both routine legs.**
  May pass with no production code once T6 lands — run the tests first and touch only what fails. The
  negative case (voice mode excluded) is as much the point as the positives.
  *Deps:* T6, **G1** · *Effort:* **S** · *Value:* **Med**

- [ ] **T11 · `RoutineBlueprint.SkillSlug`.**
  The `skills=(…)` field the hermes port dropped, now that there is something to reference. A test asserts
  every slug a blueprint names actually ships.
  *Deps:* T9 · *Effort:* **XS** · *Value:* **Med**

## Chunk 4 — The harvest

- [ ] **T12 · `save_skill`.**
  Returns a pending `MemoryToolCall` with a diff, like `remember`. There is no central write-tool registry
  — write-ness is emergent from returning a pending action — but `TokenizingAiClientService.WriteOperations`
  and `ActionCardBuilder` both need the name and are easy to miss.
  *Deps:* T4, **G1** · *Effort:* **M** · *Value:* **High**

- [ ] **T13 · `ScheduledJobKind.SkillHarvest` + the dispatcher switch.**
  Appended (never reordered — it crosses the sync wire as an int), and the ternary at
  `ScheduledJobBackgroundService.cs:475` becomes a switch. Carries the owner-device test that keeps D1's
  answer true.
  *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

- [ ] **T14 · `SkillHarvestService`.**
  Composes recent chats into one background turn granted exactly `["save_skill"]`. The composition is why
  there is no `search_chats` tool: chat history reaches one turn, by construction.
  *Deps:* T12, T13 · *Effort:* **M** · *Value:* **High**

- [ ] **T15 · The weekly job and its blueprint card.**
  Gives the harvest a visible, editable, switch-off-able home in Routines — which is what makes the
  proposal loop inspectable rather than ambient.
  *Deps:* T14 · *Effort:* **S** · *Value:* **High**

## Verification

- [ ] **T16 · The gate and the smoke test.**
  `dotnet test` with no filter at `failed: 0`; `dotnet build -t:Rebuild` at `0 Warning(s)` in **both**
  Debug and Release; a hand-written skill visibly changing an answer; a harvest run against real chat
  history parking a diff rather than writing; and one open against a **pre-change profile**.
  *Deps:* T15 · *Effort:* **XS** · *Value:* **High**

---

## Suggested order

Cheapest decisive work first, then the vertical slice, then the producer.

```
T3 · T4 → T1 → T2            # cheap, independent, and T4 unblocks both halves
T5 → T6 → T7 · T8            # the index and the loader — the vertical slice
                             # >>> G1: try it in the real app before going further <<<
T9 → T11 · T10               # starters and reach, once firing is proven
T13 · T12 → T14 → T15        # the harvest; T13 is independent and can go early
T16                          # the gate, the warning sweep, the pre-change profile
```

`T3` and `T4` are under a day together and neither depends on anything. `G1` sits deliberately in the
middle: it is the only point where the plan can be abandoned cheaply.

## Not yet planned

From design §6.3, listed so they are not lost. Both are gated on **G2**.

| Item | Design § | Effort | Value |
|---|---|---|---|
| Counted-repetition signal store — offer at the third near-duplicate instruction | 6.3 | M | Med |
| Rejected-output signal — a declined approval diff feeds the harvest | 6.3 | S | Med |
| Staleness — a skill whose convention has changed still fires | 9 | S | Low |
| Voice-mode reach | 5 | S | Low |
