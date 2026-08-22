# Plan — Make the Verifier's Evidence Worth Having

**Status:** planned, not started. Self-contained: everything needed to execute it is below.
**Owner:** unassigned. **Written:** 2026-08-22.
**Origin:** §3.6(a) and recommendation #13 of
[`2026-08-22-hermes-update-review.md`](2026-08-22-hermes-update-review.md).

---

## 1. The finding

Pia's terminal critic was upgraded (H1) so the verdict is anchored in mechanical evidence rather than
the model's own summaries. It works. But it is pointed at the **weaker of the two artifact channels
Pia already has**, and takes the stronger one on trust.

| Channel | Who declares it | When | Persisted | Probed | How it reaches the critic |
|---|---|---|---|---|---|
| `AgentStep.ExpectedArtifact` | the **planner** | *before* the step runs | Yes — `AgentSteps.ExpectedArtifact TEXT NULL` (`SqliteContext.cs:493`) | **Yes** — filesystem only | Probed facts: `found (2.1 KB)` / `NOT FOUND` / `not a file reference` |
| `StepOutcomeClaim.ArtifactRef` | the **executor** | *after* the step runs | **No** — in-process for one step exchange | **No** | Unverified prose: `produced: <ref>` (`AgentVerifier.cs:204-205`) |

**The prediction is checked. The report is not.**

That is backwards. A planner guessing an artifact before the work happens can only ever produce a
plausible-sounding string. The executor that just created the thing knows exactly what it is — and
Pia already asks for it, already receives it, already renders it into the verify prompt, and never
looks to see whether it exists.

---

## 2. Evidence, from Pia's own source

### The planner channel is prose by design

`AgentPlanner.cs:782` (and the replan twin at `:827`) tells the model:

> *"include an expectedArtifact **when there is a concrete deliverable**"*

"A summary of the Q3 numbers" *is* a concrete deliverable in plain English. Nothing in the prompt says
"concrete" means *something the app can look up*.

The verifier already knows this. `AgentVerifier.cs:448`:

> *"Tolerant classification: `ExpectedArtifact` is planner free text ("a summary of the Q3 numbers")
> **as often as** it is a filename, so only tokens that plausibly denote a FILE are probed… Anything
> unclassified is reported as "not a file reference" — never as missing."*

So the mechanical anchor bites on roughly half the steps, and the other half emit a line like:

```
- step 3 "Summarize Q3" → not a file reference
```

into a block whose entire value is that every line is a fact the app established. That line is a fact
about the *declaration*, not about the world. It is honest and it is noise.

### The report channel is checkable by nature — and unchecked

`AgentStepTools.BuildEmitStepResultTool()` already asks for it:

```csharp
[Description("Optional. The concrete artifact this step produced — a file path, or a short "
           + "identifier. Omit when the step produced no artifact.")]
string? artifact_ref = null
```

It is captured (`StepOutcomeStore.Record`, capped at `MaxArtifactChars = 300`), carried into the
context (`RunContext.RecordStep` → `CompletedStepSummary.Outcome`), and rendered into the verify
prompt as `produced: <ref>`. `IAgentTurnExecutor.cs:105` classes it, correctly, as **model-authored
free text** — which is exactly what it stays, because nothing ever probes it.

### The resume asymmetry

`AgentRunOrchestrator.SafeSeedResumeContext` rebuilds pre-pause steps from the persisted plan:

```csharp
.Select(s => new CompletedStepSummary(
    s.Ordinal, s.Title, s.Intent ?? string.Empty, Succeeded: true, VisibleText: string.Empty,
    s.ExpectedArtifact, FromEarlierSegment: true))     // ← no Outcome
```

`ExpectedArtifact` survives a park/resume because it is a column. `ArtifactRef` does not, because it
is in-process only. So **the stronger evidence channel is precisely the one that does not survive the
durable park-and-resume** the July review called Pia's strongest divergence from hermes. Nothing
breaks — the verifier degrades quietly to a thinner picture — but the feature Pia is proudest of
weakens the verifier it later built.

---

## 3. The rule this is an instance of

Hermes's skill-authoring guide, writing-quality principles:

> **3. End steps with completion criteria.** Checkable and, when it matters, exhaustive:
> *"every modified file accounted for"* beats *"summarize changes."*
>
> **6. Prune duplication and no-ops.** *"Be careful"* and *"use best practices"* don't change model
> behavior — **replace with a checkable criterion or delete.**

#6 is the sharper one here because it names a binary: **checkable, or gone.** Pia currently has a
third state — unprobeable prose — and that state is worse than `null`, because `null` produces no
line while prose produces a non-fact dressed as one.

---

## 4. The moves

Ordered cheapest first. Each is independently shippable; **stop after any of them.**

### Move 1 — Count it. Zero code.

The instrumentation already exists (`AgentVerifier.TryBuildArtifactFactsAsync`):

```csharp
_logger.LogInformation("Artifact probe: {Declared} declaration(s), {Probed} path(s) probed.",
    declared.Count, probed);
```

Read `probed / declared` off real runs.

- **High** → the planner is already producing file-shaped artifacts. This whole thread closes. Write
  that down.
- **Low** → that ratio is the number that justifies moves 2–4.

Same discipline as the compaction test plan: measure before changing. This one is free.

### Move 2 — Probe `ArtifactRef` too. Small.

Point the **existing** `ProbeDeclarations` machinery at the second source. The `produced:` line
becomes a fact:

```
- step 3 "Write the summary" → produced: out/q3-summary.md → found (2.1 KB)
- step 4 "Export the deck"   → produced: out/deck.pptx     → NOT FOUND
```

A `NOT FOUND` on a *self-reported* artifact is a much stronger negative than a missing predicted one:
the step claims it made a thing and the thing is not there. That is the single highest-value signal
this plan can hand the critic, and the plumbing for it is already end-to-end.

Keep H1's guardrails unchanged: bounded, time-boxed, failure-isolated, and it can never itself fail a
verdict — the LLM still renders the verdict.

### Move 3 — Fix the planner prompt. Two lines.

`AgentPlanner.cs:782` and `:827`. State what checkable means, and say to omit the field otherwise —
hermes's #6, applied literally. An `expectedArtifact` that names nothing the app can look up should be
**absent**, not softened into prose.

Sequenced after move 2 deliberately: once the report channel is probed, the prediction matters less,
and it is worth knowing whether move 2 alone is sufficient before tightening a prompt.

### Move 4 — Widen the probe past the filesystem. Medium.

The largest idea here. Pia's steps do not only produce files. They produce **todos, reminders, kanban
cards, vault entries, memories, scheduled jobs** — every one a record Pia can query through a service
it already owns. The probe looks at the filesystem and nothing else.

Give `artifact_ref` an optional typed prefix and dispatch per kind:

```
file:out/q3-summary.md      → File.Exists + size
todo:Call the vendor        → ITodoService lookup by title
vault:2026-08-14-notes      → vault reference resolve
reminder:Renew the cert     → IReminderService lookup
```

Notes on shape:

- **Unprefixed stays file-probed**, exactly as today. Backwards compatible, no flag day.
- **Unknown prefix → "not probed"**, never "missing". The tolerance rule from H1 stands: the probe
  reports what it established and nothing else.
- **`ArtifactRef` is not persisted**, so the report channel needs no migration. Persisting it is worth
  doing anyway (§5) but it is not a prerequisite.
- **The verifier's dependencies grow.** It takes `(IAiClientService, ISettingsService, ILogger)` today.
  Per-kind probing means more services, or — better — a small `IArtifactProbe` with one implementation
  per kind, so `AgentVerifier` keeps one dependency and the kinds stay independently testable.

---

## 5. Worth doing alongside: persist `ArtifactRef`

Not required by any move above, but it is the cheap fix for §2's resume asymmetry, and it unlocks two
other things: the run timeline can show what each step actually produced, and a resumed run's critic
sees the same evidence an uninterrupted one does.

`AgentSteps` already has an `ExtraJson` column in use elsewhere, so this needs no schema change.

**Sensitivity:** `ArtifactRef` is model-authored text and may echo user content (a filename is often a
document title). Persisting it is fine; **logging it is not** — it already goes through
`SensitiveDebug` at `ChatSession.cs:867` and `HeadlessTurnExecutor.cs:619`, and any new site must do
the same.

---

## 6. What not to do

- **Do not make `expectedArtifact` required.** It is optional in the schema on purpose
  (`AgentPlanner.cs` — a required member would make every plan turn carry it). Some steps genuinely
  produce nothing lookup-able, and forcing a value would produce exactly the prose this plan is
  trying to remove.
- **Do not let a failed probe fail a verdict.** H1's contract is that the probe informs the critic and
  the critic still decides. A missing artifact is a fact for the prompt, not a veto.
- **Do not fuzzy-match.** If `todo:Call the vendor` doesn't resolve exactly, report "not found",
  not "found something similar". A probe that guesses is a summarizer with extra steps.
- **Do not widen the probe before move 1.** If `probed / declared` is already high, move 4 is
  solving a problem Pia doesn't have.

---

## 7. Work breakdown

| Step | Move | Notes |
|---|---|---|
| 1 | 1 | Read `probed / declared` off real-run logs. **Decision gate for everything below** |
| 2 | 2 | Route `ArtifactRef` through the existing probe; `produced:` lines carry found/not-found |
| 3 | 2 | Tests: self-reported-but-missing is the case that matters; keep the failure-isolation tests green |
| 4 | 3 | Planner + replan prompt wording |
| 5 | — | Persist `ArtifactRef` into `AgentSteps.ExtraJson`; seed it in `SafeSeedResumeContext` |
| 6 | 4 | `IArtifactProbe` + file implementation (behaviour-preserving refactor of today's probe) |
| 7 | 4 | Todo / reminder / vault probes; typed prefix in the `artifact_ref` tool description |

Steps 1–3 are the vertical slice and deliver most of the value.

---

## 8. The wider standard this came from

Two more of hermes's rules apply outside the planner. Neither is part of this plan; recording them so
they are not rediscovered:

- **"The description is paid for every turn."** Hermes caps skill descriptions at 60 characters
  *because its system-prompt index truncates at 57*. Pia's tool descriptions also ship on every turn
  and have no equivalent discipline.
- **Counter-triggers.** Every hermes skill carries an explicit *"Don't use for: …"*. Pia has exactly
  one, in `BuiltInPluginDefaults.cs:42` — *"Do not use write_file for a vault source…"* — evidently
  added after that precise confusion bit someone. Pia found the value empirically, once; hermes made
  it a house rule. That same string is also a good example of hermes's pitfall #10 (*"when adding a
  rule, remove the old wording it replaces"*): it is now one run-on paragraph carrying five rules.

Personas in Pia are **user-authored** — there is no seeded built-in system prompt — so the
persona-authoring half of hermes's standard would be user-facing documentation, not code. Lower
priority than anything above.

---

## 9. Open questions for the owner

1. **Does move 2 make move 3 unnecessary?** Plausible. If the executor's report is probed, a vague
   planner prediction costs little. Decide after the move-2 numbers, not before.
2. **Typed prefix, or a second tool argument?** `artifact_kind` alongside `artifact_ref` is cleaner to
   validate; a `kind:ref` string is cheaper and degrades to today's behaviour when omitted. The string
   is recommended for exactly that reason.
3. **Should a self-reported-but-missing artifact do more than inform the critic** — mark the step
   unconfirmed, or trigger a replan? Tempting, and it violates §6's second rule. If it is ever done,
   it should be a separate, deliberate decision with its own tests.
