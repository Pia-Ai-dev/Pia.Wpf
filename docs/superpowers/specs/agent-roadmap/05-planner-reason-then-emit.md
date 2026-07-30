# Batch 05 — Planner reason-then-emit (boosted planning effort) — ✅ SHIPPED

**Phase 2 · Size S–M · `feature/agent-run-spine` · `7a41a68` → `d3c8c61`**
(see the chronicle in [`00-OVERVIEW.md`](00-OVERVIEW.md). The implementation spec, its as-built deviations and
the mutation measurements live in [`05-planner-reason-then-emit.impl.md`](05-planner-reason-then-emit.impl.md).)

This file now describes **the code as built**. The original spec named the wrong mechanism as the gate, and
that error is corrected in place below rather than deleted — it is exactly the kind of thing that would
otherwise be re-proposed.

> **Build:** `dotnet build -p:EnableWindowsTargeting=true --no-incremental` → **0 errors, 194 warnings**, all
> pre-existing and unchanged (the bar is *adds zero*).
> **Tests:** `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`
> → **2224 total / 0 failed / 1 skipped** at `d3c8c61`, from a pre-batch baseline of **2194 / 0 / 1** at
> `7815ce1` (**+30** cases — the only commit between `7815ce1` and this batch's first is the docs-only
> `30ebb52`, which added no tests and belongs to no batch).
> **What that does NOT cover:** the settings toggle is XAML and **no test parses the settings view**, and
> efficacy is unmeasured — the suite proves the extra round happens, degrades safely and is paid for, never
> that it produces better plans. See "Opened by Batch 05" in [`00-OVERVIEW.md`](00-OVERVIEW.md).
> **Corrected 2026-07-30, when Batch 12 merged in:** this line said "no test in this suite parses a `View`",
> which is no longer true — `AssistantViewParseTests` parses `Pia.Views.AssistantView`, the **chat** view. It
> does not reach this batch's CheckBox, which lives in the same-named but unrelated
> `Pia.Views.SettingsViews.AssistantView`. The gap is unchanged; only the reason it exists is narrower.

---

## What shipped

| Commit | What |
|---|---|
| `7a41a68` | `IAiProviderHandler.DropsReasoningEffortWithTools` — expression-bodied on **all eight** handlers, plus a table-driven conformance test and two premise pins (`ToOpenAi`'s tool gate vs `ToOpenAiResponses`', Mistral's suppression under tools) |
| `5fe637c` | `AppSettings.AgentPlanReasoningTurnEnabled` (default **OFF**) + load/save in `AssistantSettingsViewModel` + a CheckBox in `AssistantView.xaml` + three strings in all three resx files + a camelCase JSON round-trip test |
| `4bd295b` | `AgentPlanner.PlanAsync`'s gated two-call path: a tool-FREE free-form reasoning turn whose analysis seeds the constrained `emit_plan` turn |
| `2a0e537` | The implementation spec, recorded as-built |
| `6ff401a` | Anti-vacuousness proof for the new assertion helpers |
| `73e15e8` | Review fixes — four assertions that could not fail made able to fail; the flag's contract rescoped to **transport-only** |
| `ba2c266` | Polish — truncation direction, the degrade gate, the cancelled gate read: three more mutation escapes |
| `d3c8c61` | Polish — the German string named a control that does not exist; the global toggle read as a fourth scheduled-runs knob |

## What it does

A plan turn always attaches `emit_plan`, and three of the eight provider handlers omit the configured
reasoning effort as soon as tools are present. So on those providers the one turn that most needs deliberation
reasons at the model **default**, whatever the user configured.

With the opt-in on and the provider one of those three, `AgentPlanner.PlanAsync` spends a **tool-FREE
free-form turn first** — `GetChatResponseAsync(…, tools: null, …)`, which is what makes `AiClientService`
compute `hasTools: false` (`provider.SupportsToolCalling && tools is {Count: > 0}`) and therefore the only
shape that still carries the effort — and folds its analysis into the constrained turn:

- The analysis rides on the **`User`** message, never the System prompt. `TokenizingAiClientService` rewrites
  only `ChatRole.User` text to PII placeholders and hands the assistant reply back **detokenized**, so an
  analysis parked in the System prompt would ship restored PII straight past the tokenizer. Folding it into the
  single user message (goal first, then the wrapper) also keeps the request shape exactly `[System, User]`.
- Capped at `MaxAnalysisChars = 4000`, head-first, with a truncation marker. The constrained turn passes **no**
  `contextBudget`, so an unbounded block could overflow a small local model and turn a *working* plan turn into
  a failing one.
- It can never hard-fail planning: an empty answer, a timeout, any other throw — even a gate that cannot be
  *evaluated* (`GetSettingsAsync` does I/O; the handler resolver throws for an unregistered type) — degrades to
  today's single constrained turn. **Cancellation is not a degrade** and propagates from both the turn and the
  gate read.
- Every round reaches `PlanResult.Usage`, including the reasoning turn whose text was discarded and both
  degrade paths (I1). The R10 firm retry **reuses** the one analysis, so the worst case is three provider
  turns, not four.
- With the toggle off the user message is the goal byte-for-byte and the round count is unchanged.

## The gate — and what the original spec got wrong about it

**The spec said:** "Provider capability (`IProviderCapabilityService`, Responses-API vs Chat-Completions) —
gates which path runs", and framed the whole batch on the **Chat-Completions vs Responses-API** axis (Goal,
Decisions, Tests and Acceptance all said so).

**Both halves of that are wrong.**

1. `IProviderCapabilityService` is not that gate and cannot be. Its entire surface is
   `GetPlanningCapabilityAsync(provider) → PlanningCapability {Capable, Weak, Unknown}` plus
   `Invalidate(providerId)` — a probe-once/cache tool-calling capability check for the Agent lever (R10). It
   knows nothing about API surface or reasoning effort.
2. "Chat-Completions" is the wrong axis anyway. Four of the five excluded handlers are Chat-Completions-shaped
   and are excluded for reasons that have nothing to do with the API surface: two inject reasoning through a
   `DelegatingHandler` **unconditionally**, two never send an effort at all. Splitting a plan turn for them
   would buy a wasted round.

**The real gate** is the new transport flag, read per provider at plan time (`ShouldReasonFirstAsync`,
cheapest test first):

```
settings.AgentPlanReasoningTurnEnabled          // D1: global opt-in, default OFF
&& provider.SupportsToolCalling                 // false ⇒ emit_plan is never attached and the effort IS
                                                //   already sent, so planning is heading for the SingleTurn
                                                //   degrade regardless — nothing to buy
&& provider.ReasoningEffort is not (null or None)
&& _handlers.Get(provider.ProviderType).DropsReasoningEffortWithTools   // D2
```

## The affected set, and why the other five are excluded

| Handler | Flag | Why |
|---|---|---|
| `AzureOpenAI` | **true** | `ReasoningEffortMapping.ToOpenAi(effort, hasTools)` omits the reasoning-effort parameter entirely once tools are present |
| `Ollama` | **true** | the same `ToOpenAi` tool gate |
| `Mistral` | **true** | `ShouldEmitReasoning` returns `(false, default)` for any non-`None` effort once `hasTools`, and `CreateChatOptions` then returns a bare `ChatOptions` — **but see the caveat below: Mistral gets the split and never the boost** |
| `OpenAI` | false | reasons via the **Responses API**, and `ToOpenAiResponses` has **no tool gate** — the configured effort already survives tools |
| `OpenRouter` | false | `OpenRouterReasoningHandler` rewrites the body to `reasoning:{effort}` **unconditionally**, tool-independent, so a tool-using turn already carries the effort |
| `VLlm` | false | `VLlmThinkingHandler` sets `chat_template_kwargs.enable_thinking` unconditionally (boolean only, no effort granularity), so tools never turn thinking off |
| `OpenAICompatible` | false | never sends any reasoning field, with or without tools — nothing a second turn could recover |
| `PiaCloud` | false | `CreateChatOptions` never sets an effort, with or without tools |

**Mistral caveat (D7, corrected at the review).** The flag is a **transport** fact and is true: the field *is*
omitted under tools. It does not follow that a tool-free turn recovers a *higher* effort, and on Mistral
neither half of the model list does. A model **not** in `ReasoningCapableModels` never gets the field on
either turn (the model-list check runs before the `hasTools` check). A model **in** it keeps reasoning **on**
when the field is absent, and Mistral's ladder is `none` | `high` only — so the tool-using turn already sits on
the one ON rung that exists and the tool-free turn's explicit `high` is the same rung. Accepted deliberately:
reason-then-emit is itself the mechanism (a free-form decomposition the constrained turn consumes) and the
boosted effort is an amplifier, not the whole benefit. **Do not "fix" this by flipping the flag to `false`** —
it is read off an uninitialised instance by the conformance test, so it must stay a constant, and `false` would
contradict the request the handler demonstrably builds. Narrowing it honestly needs a model-aware member taking
`AiProvider`, which D7 rejected.

## Decisions

### D1 — a **global** `AppSettings` toggle, default OFF (not per-provider)

`AgentPlanReasoningTurnEnabled` sits beside the interactive and scheduled budget knobs. Default OFF because
turning it on buys plan quality with a whole extra provider round on *every* plan turn, and a plan turn already
costs ≥2 rounds (§16 R6). Global rather than per-provider because the same answer applies to interactive,
detached and scheduled runs — and threading one value through the two per-run budget envelopes would mean four
`RunProfile.FromBudget` call sites each having to remember to pass it, where a forgotten one disables the
feature on that path with no test to notice.

### D2 — the "drops effort under tools" fact is a **flag on `IAiProviderHandler`**, not a `ProviderType` switch

Whether effort survives tools is decided by the exact request a handler builds in `CreateChatOptions` (or by
the `DelegatingHandler` it installs), so the knowledge belongs next to the handler that *has* it. A table
living in the planner could be silently wrong for a handler added later; an interface member cannot be missed —
it will not compile. The conformance test reads the flag off `RuntimeHelpers.GetUninitializedObject` (no
constructor runs, so `PiaCloud`'s three ctor deps need no container) and keys its expected map on the **CLR
`Type`**, not `AiProviderType`, so a handler duplicating a provider type cannot satisfy the count check
unchecked. Reading it off an uninitialised instance also makes an initialised auto-property fail loudly instead
of quietly reading back `false` for all eight.

### D3 — **plan-only**; `ReplanAsync` keeps its single constrained turn

A replan already carries the completed-step summaries and the failure detail, so it has the context a fresh
reasoning turn would have to reconstruct — and it can run up to `MaxReplans` times per run, so doubling *its*
cost multiplies over the run instead of being paid once. The asymmetry is written into `ReplanAsync` as a
comment so it reads as a decision, not an oversight. Revisit only with evidence that replans specifically plan
worse.

## Guardrails, as held

- **No reliability regression.** The constrained `emit_plan` turn, `ValidatePlan`, the single firm retry and
  the SingleTurn degrade are behaviourally unchanged with the toggle OFF, and are the fallback whenever the
  reasoning turn fails or returns nothing.
- **Cost-aware.** One `Information` line per two-call plan says the plan-turn cost is doubled, identifying the
  provider by `ProviderType`.
- **Privacy.** Goal text, plan text and the analysis go only through `SensitiveDebug`. The degrade warning logs
  `ex.GetType().Name`, not the exception, because `LlmTimeoutException`'s message embeds the provider **name**,
  which is user-named. A capturing logger asserts this over release-visible levels only.

## Tests, as written

- Toggle ON + a handler that declares the drop → **two** turns; the emitted plan still validates.
- Toggle OFF, effort `None`/null, `SupportsToolCalling` false, or **flag `false`** → **one** turn, user message
  is the goal byte-for-byte. (The original spec's "Responses-API → single turn" is right for `OpenAI`, but for
  the wrong reason, and misses `OpenRouter`/`VLlm`/`OpenAICompatible`/`PiaCloud`, which are single-turn too.)
- Reasoning turn throws / returns nothing → single-turn fallback still yields a valid plan, **and the reasoning
  turn is asserted to have fired** (without that, a dead gate left both tests asserting nothing — `ba2c266`).
- Usage from a discarded reasoning turn still reaches `PlanResult.Usage` (I1).
- Cancellation propagates from the reasoning turn **and** from the gate's settings read (`ba2c266`).
- Truncation keeps the **head** of the analysis and drops the tail, against a bound tight enough to catch a
  raised cap (`ba2c266`).
- The analysis is on the `User` message, and the goal **leads** it (`StartsWith`, not `Contains` — `73e15e8`).
- `ReplanAsync` with the toggle ON still runs **one** constrained turn — D3 is pinned, not just commented.
- Flag conformance over all eight handlers, plus a behavioural cross-check that each declaring handler really
  does omit the effort in the request it builds (`73e15e8`).

## Acceptance

Met, with one honest gap: plans on the three affected providers reason at the configured effort when the
opt-in is on, single-turn reliability and the degrade path are unchanged, the build is green and the suite is
green — but *that the resulting plans are better* is unproven and needs the human smoke round. Open items are
recorded in [`00-OVERVIEW.md`](00-OVERVIEW.md) → "Opened by Batch 05".
