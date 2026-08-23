# A4 replay — killing the disjunction, and what the re-measurement said

**Status:** measured. Decisions §1–§3 were pre-registered before the replay; §4 onward reports it.
P1 landed, P3 was not built, A2 is deferred. Read §8 before quoting a number.
**Owner:** unassigned. **Written:** 2026-08-23.
**Origin:** [`2026-08-23-a4-disjunction-batch-brief.md`](2026-08-23-a4-disjunction-batch-brief.md),
rows P1–P7, which in turn came from [`2026-08-23-a1-pilot-reading.md`](2026-08-23-a1-pilot-reading.md)
— the pilot that read gate **A1** and found the probe's negative verdict unreadable.

Sections 1–3 were written **before** the replay ran, so the success criterion could not be chosen to
fit the number that came back.

---

## 1. Decision (1) — forbidding a disjunction without losing the conjunction

The two rules live one clause apart, and the brief calls that the whole difficulty. It is tractable
because they are about **different things**:

- The conjunction licence at `AgentPlanner.cs:784` (plan) and `:827` (replan) is about **step
  granularity** — "if one reason requires editing several files, that is ONE step listing every file in
  `expectedArtifact`". It is an anti-step-splitting rule.
- The disjunction defect is about **candidate names inside one declaration** — `(e.g., A or B)`, where
  the step writes one of the pair.

One clause settles both: **every name listed must exist when the step finishes.** A conjunction
satisfies it (all three files get written); a disjunction cannot (one of the pair never will). It also
implies the omission rule the row asks for: a step with no file to name has nothing that can satisfy
"must exist".

Entailment alone was judged insufficient. The success metric in §3 is a **string** property — does the
declaration still contain an alternative — and a model can honour a semantic rule while still writing
`e.g.`. So the wording bans the observed surface form explicitly, by example, alongside the entailment.
**Forbid what you measure.**

Landed wording, `PlanStepArg.ExpectedArtifact` (`:159`), the tool-schema description `AIFunctionFactory`
ships on **every** plan and replan turn:

> The file(s) this step will produce — every name listed must exist when the step finishes, so name
> several only when it writes all of them. Never offer alternatives ("A or B", "e.g. A"). Omit when the
> step produces nothing checkable.

and the plan-turn prose (`:782`):

> …include an expectedArtifact only when the step will write files, naming exactly the files it will
> write — every one of them must exist when the step finishes, so never offer alternatives to choose
> between.

`:784` and `:827` are untouched, verbatim. `BuildReplanMessages` gains no prose surface: the schema
description reaches the replan turn already, and the brief forbids a fourth surface.

**No test pins any of this, deliberately.** Nothing in `tests/` asserts prompt bytes today — the
existing planner tests drive `emit_plan` through a fake client and assert on the parsed plan, and the
one thing they hard-code is the argument *name* `expectedArtifact`, which this change does not touch.
A test over the description string would pin the wording rather than the behaviour, and would have to
be edited by whoever lands P8. The evidence for this change is the live before/after in §5, not a unit
test. Worth revisiting only if a future change needs the replan turn's inheritance of the rule to be
load-bearing rather than incidental.

**Two schema surfaces deliberately left alone.** `:139` and `:148` — the `steps` array descriptions on
`emit_plan` and `emit_revised_plan` — say "an optional expected artifact". That is a summary of what an
array element holds, not a description of the field's semantics, and the per-property description at
`:159` is nested directly inside them. Restating the rule there would duplicate it on every turn for no
extra reach.

## 2. Decision (2) — is P3 still needed?

**P3 is conditional on the replay and is not to be built blind.** The rule, fixed in advance:

- **Disjunctions survive P1** (any probed declaration in the replay still offers alternatives) → build
  the per-declaration counter. Candidate-level counting is still lying, and prompt wording did not fix
  it.
- **No disjunction survives, and the sample is non-vacuous per §3** → do **not** build it, and record
  why. With every listed name required to exist, a candidate miss *is* a declaration miss, so
  per-candidate and per-declaration counting agree by construction, and the extra counter would be a
  second way to say the same thing.

The brief's own warning is the reason not to build it unconditionally: for a genuine conjunction,
"not-found only when every candidate misses" is the **wrong** rule. A step that owed three files and
wrote one has partially failed and must register. P1 makes the conjunction the only legitimate
multi-name form, which makes that wrong rule the *only* rule P3 could implement.

> **Verdict: P3 is not built.** The replay's second branch fired — 0 of 6 probed declarations offer
> alternatives (§5), and the sample cleared the non-vacuity floor with `probed` **up** from 2 to 6 and
> `fileShaped` at 6 against the pilot's 5. There is no disjunction left for a per-declaration counter to
> disambiguate, and building it would install the wrong rule for the one multi-name form P1 still
> licenses. `AgentVerifier.cs` is untouched by this batch.

## 3. Decision (3) — the planner is non-deterministic, so what is actually being compared?

The primary metric is not a rate. It is an **absolute property of each probed declaration**: does it
offer alternatives to choose between? A disjunction is a disjunction whatever plan shape comes back, so
plan-shape variance does not threaten it. The pilot's reading was **4 of 4**; success is **0**.

What non-determinism *does* threaten is the **denominator**, and P1 attacks the denominator on purpose:
"omit when the step produces nothing checkable" drives declarations down. If the replay probes one
declaration, or none, the disjunction count reads 0 and the batch looks like a clean win while the
instrument has gone blind.

So the criterion is pre-registered with a non-vacuity floor:

> **Success = 0 disjunctions among probed declarations, AND a sample that could have shown one** —
> `probed` and `fileShaped` in the same order of magnitude as the pilot's 9 and 5.

Every run row reports `declared / fileShaped / notFileShaped / probed / found / notFound` next to the
disjunction count, and the distinct-artifact collapse, so the two readings are comparable field by
field.

Three outcomes, each with its own reading:

| Replay shows | Reading |
|---|---|
| 0 disjunctions, `probed` comparable to 9 | P1 worked. P3 unnecessary (§2). |
| Disjunctions survive | P1 insufficient. Build P3, and say what the surviving wording looked like. |
| `probed` collapses toward 0 | **Not a success.** Either P1 overshot into suppressing legitimate declarations, or these prompts simply planned differently today. |

The contingency for the third row, named in advance so it is not invented afterwards: replay the same
four prompts in the same session against a **pre-P1** Debug build (stash the planner edit, rebuild,
re-run). That separates "P1 suppressed declarations" from "the planner planned differently today".
It costs a rebuild and eight minutes, and is only spent if the collapse happens.

Also fixed in advance, from the pilot's trap 3: **three probed runs, not four, is the expected shape.**
The answer-only control completes through the SingleTurn fallback and emits no probe line at all. Its
absence is not a miss.

---

## 4. What ran — and the one protocol deviation

**The pilot's provider was unavailable.** Pia Cloud points at `https://localhost:8081`, and every
request to it returned **401** — `POST /auth/refresh` included, so the refresh token in the seeded
`settings.json` has rotated and cannot be renewed without a fresh sign-in. The first four dispatches
died in the plan turn with `Authentication` and emitted no probe line at all. The pilot's protocol is
not reproducible today.

Two consequences, both recorded rather than papered over:

- The replay ran on the profile's configured Assistant default, **Mistral Medium 3.5**
  (`mistral-medium-latest`), a direct-API provider that needs no local server.
- Because the provider moved, **the pilot is no longer the before-reading.** Comparing a post-P1
  Mistral run against a pre-P1 Pia Cloud run would confound the prompt change with a model change. So
  the replay was run as **two arms on the same provider** — the contingency §3 named, spent
  deliberately rather than as a rescue:

| Arm | Build | Profile | Prompts |
|---|---|---|---|
| **pre** | Debug, planner edit reverted (`git checkout` of `AgentPlanner.cs` only) | `C:\temp\pia-a4-pre` | the four, verbatim |
| **post** | Debug, P1 in place | `C:\temp\pia-a4` | the same four, verbatim |

Separate throwaway roots on purpose: one shared files folder would let the pre arm's writes satisfy the
post arm's probe.

**The profile recipe in the brief and the runbook is incomplete.** Copying `settings.json` alone leaves
`providers.json` behind, so the configured default provider id does not resolve and the app silently
falls back to the built-in Pia Cloud entry it auto-creates. That is almost certainly why the pilot
recorded "one provider (Pia Cloud)" — not a choice, a fallback. Both files must be copied.

Per arm: four prompts dispatched through `Assistant_RunInBackground`, attributed on the `[run <id>]`
prefix, last probe line per run, replan twins collapsed by hand. Instrument guards read **zero in both
arms** — no `Working subpath did not resolve`, no `Artifact probe skipped`, no `Artifact probe failed`
— so no outcome below is instrument error.

## 5. The reading

### Pre-P1 arm

| Run | Category | declared | fileShaped | notFileShaped | probed | found | notFound | unresolvable |
|---|---|---|---|---|---|---|---|---|
| `c1dde388` | A — file-producing | 2 | 2 | 0 | 2 | 2 | 0 | 0 |
| `4c682d9d` | C — todos | 5 | 0 | 5 | 0 | 0 | 0 | 0 |
| `7af16adb` | B — research | — no probe line — | | | | | | |
| `f462b008` | F — answer-only | 1 | 0 | 1 | 0 | 0 | 0 | 0 |
| **total** | | **8** | **2** | **6** | **2** | **2** | **0** | **0** |

Collapsed: 5 distinct intended artifacts, 1 file-shaped — **20%**.

### Post-P1 arm

| Run | Category | declared | fileShaped | notFileShaped | probed | found | notFound | unresolvable |
|---|---|---|---|---|---|---|---|---|
| `4da7bf96` | A — file-producing | 3 | 3 | 0 | 3 | 0 | 1 | 2 |
| `c508147b` | C — todos | 1 | 1 | 0 | 1 | 1 | 0 | 0 |
| `95d1d48d` | B — research | — no probe line — | | | | | | |
| `db92d3e6` | F — answer-only | 2 | 2 | 0 | 2 | 2 | 0 | 0 |
| **total** | | **6** | **6** | **0** | **6** | **3** | **1** | **2** |

Collapsed: 3 distinct intended artifacts, 3 file-shaped — **100%**.

### The number that matters

**Declarations offering alternatives: 3 of 8 before, 0 of 6 after. Among probed declarations: 0 of 2
before, 0 of 6 after.** The pilot's was 4 of 4 probed.

Every pre-arm alternative was prose — the shape *"a backup file **or** confirmation of completion"* —
and prose never reaches the probe, because the classifier calls it not-a-file first. So this provider
did **not** reproduce the pilot's exact failure mode, which was two *file names* in one declaration.
That limit is stated plainly in §8, item 3.

Two secondary movements, both large and both in the intended direction:

- **`notFileShaped`: 6 of 8 → 0 of 6.** Every post-P1 declaration is a bare file name. The pre arm's
  todo run declared five prose outcomes; the post arm's declared one file.
- **`probed`: 2 → 6.** The non-vacuity floor §3 fixed in advance is met and then some — P1's omission
  clause did *not* blind the instrument. It moved declarations out of prose and into probeable names
  rather than deleting them.

### The first readable negative

`4da7bf96` declared `README.md` and the probe said **NOT FOUND** — and the file genuinely is not on
disk. Checked directly: the arm's files folder holds `todo_list.md` and `structured_explanation.md`
and no README. This is the first `notFound` in this corpus that means what the gate wants it to mean.

### Why that run had nothing to find, n = 1

The same run produced the arm's only two `unresolvable` candidates, and the cause is not a probe
quirk. Its replan re-declared the artifact as **`/Ledger/README.md`** — a **rooted** path — and the
executor then called `write_file` with that exact path. The sandbox refused it:

```
{"success":false, … "error":"Error: Path is outside the assistant files folder.","created":false}
```

The model did not retry with a relative path; it answered in prose and the step closed. So the rooted
path is not a declaration-only defect: the same string went into the write tool and cost the run its
artifact. The pre arm's equivalent run declared plain `README.md`, wrote it, and found it.

Three readings, and one run per arm cannot separate them:

1. **The goal named the project.** The prompt asks for a README for a project called *"Ledger"*, and
   putting it in a `Ledger` folder is an ordinary response to that, with or without P1. This is the
   cheapest explanation and nothing rules it out.
2. Noise. The planner is non-deterministic and reached for a subfolder this time.
3. **P1 pushed it there.** "Naming exactly the files it will write" invites a *path*, and neither the
   schema description nor the prose says the path is relative to the working folder. Note that P1 never
   says "path" — so this is the weakest of the three, not the default.

The fix is the same one clause under all three, and it is cheap — but it must not be smuggled in now. Changing the
wording after reading the number it produced, without re-measuring, would make this section
unfalsifiable. It is logged as a candidate row instead (**P8**, checklist §A).

Two things worth noticing on the way past, neither in this batch's scope:

- The step whose only tool call was **rejected** still reported `succeeded=True`.
- That failure was invisible to the report channel — `artifactReported=False`, nothing to route — and
  visible to the probe, as `NOT FOUND`. The channel A2 was going to widen would have said nothing
  here; the channel P1 just fixed said the right thing. §7 leans on that.

## 6. Report-channel supply — P6

`artifactReported=True` over all step outcomes:

| Reading | True | Step outcomes | Share |
|---|---|---|---|
| Pilot (Pia Cloud, pre-P1) | 2 | 17 | 12% |
| Pre-P1 arm (Mistral) | 2 | 8 | 25% |
| Post-P1 arm (Mistral) | 2 | 7 | 29% |

**The share moved; the count did not.** It is **2** in all three readings, on three different
denominators. At that n the rate difference is not distinguishable from the denominator shrinking, and
nothing here supports a claim that P1 changed what steps report.

## 7. The A2 recommendation — P7

**Defer A2. Do not build it now, and do not drop it.**

The brief named the condition for dropping: *"if supply stays near 12%, A2 routes a channel that is
empty seven times out of eight."* It did not stay near 12% — it read 25% and 29%. The drop trigger did
not fire, so dropping would overreach the evidence.

Building now would too, for three reasons:

1. **The supply evidence is 2 events wide.** Three readings, two reporting outcomes each. That sizes
   nothing, and A2 is an `S`.
2. **A2's purpose was to find a channel that could produce a negative**, because the planner channel
   appeared unable to. It can. P1 turned it into one that produced a **true** `NOT FOUND` on a
   four-run replay, and pushed file-shapedness from 20% to 100% on the collapsed count. The evidence
   A2 was going to buy is now partly available for free — and on the one run in this replay where a
   write genuinely failed, the probe caught it while the report channel had nothing to report (§5).
3. The cheapest next measurement is not A2. It is **a wider corpus on the fixed planner channel** —
   the same protocol over 12–24 runs, which the runbook already describes and which needs no code.

Concretely: re-read supply over **≥12 runs** on a post-P1 build. Build A2 if `artifactReported` clears
roughly 40% of step outcomes there; drop it, and reopen A7's question separately, if it falls back
toward the pilot's 12%.

**A6 and A7 stay closed**, unchanged by anything here. Review recommendation #13 stays closed and P1 is
its cheap approximation, as the brief predicted.

## 8. Limits

Read these before quoting anything above.

1. **Four runs per arm, one machine, one provider, one afternoon.** Three probed runs per arm. Every
   number in §5 is an existence proof, not a rate.
2. **Not the pilot's provider.** Pia Cloud was unreachable (§4). The pilot is a third data point on a
   different model, not this reading's baseline.
3. **The pilot's exact failure mode was not reproduced in the pre arm.** Mistral's alternatives are
   prose and never reach the probe; Pia Cloud's were file names and did. So the controlled comparison
   proves P1 eliminated *alternatives and prose declarations* on this provider. That the specific
   probed-disjunction shape is gone rests on the pilot's 4-of-4 plus P1's explicit ban on it, not on a
   post-P1 observation of that shape being absent where it had been present.
4. **One prompt category is missing from both arms for the same reason.** The research prompt made the
   planner decline as ungroundable and park for clarification, in **both** arms — a provider property,
   not a P1 effect, which is exactly what the two-arm design was for. It is a different absence from
   the pilot's trap 3 (the answer-only control fell through the SingleTurn fallback there; here it
   planned and wrote a file).
5. The `/Ledger/README.md` regression is n = 1 per arm (§5).

## 9. UI verification of the replay

The numbers above come from the log and the filesystem, which is what §4 of the brief specifies. They
were afterwards checked a second time **through the running UI**, against the post-P1 profile, to
confirm the log is describing the app the user would see.

- **Run inventory.** The Flow rail shows **8** agent runs: three *"Finished — tap to review"*, one
  *"This run couldn't tell what you wanted — open it to answer."*, and four *"Ended with an error"* —
  the three completed post-P1 runs, the parked research run, and the four Pia Cloud 401s. That is the
  log's split, arrived at independently.
- **The parked run.** Opening it shows *"Waiting for you to clarify the goal in the chat"* and the
  planner's question in the transcript, matching the logged clarification verbatim.
- **The `NOT FOUND` is real, and the transcript says why.** The README run's own reply reads: *"The
  /Ledger folder is outside the configured assistant files folder, so I cannot write directly to it."*
  It then proposes, unprompted, exactly the fix P8 records — *"save the file to a relative path inside
  the assistant files folder instead"*. That is the strongest available support for P8 and it is not an
  inference from the tally.
- **The delivered work.** The todos run produced three real todos — *Renew the domain* at high
  priority, the other two at medium — so its single file-shaped declaration sits on top of a task that
  actually completed, not instead of it.
- **Provider.** The message footer reads `mistral-medium-latest`, confirming in the UI that both arms
  ran on one provider.
- **Chat history** separates the two sessions cleanly: the four failed Pia Cloud runs still carry raw
  prompt text as their titles (they died before summarising), the four post-P1 runs carry generated
  ones.

Assertions were machine-checked with `ww_assert_value` where the text is exposed as a UIA name (the
todo count, the provider footer); the rendered markdown is a `RichTextBox` read through `TextPattern`,
so the two transcript quotes above are read from `ww_get_value` and the screenshots rather than from a
name assertion.
