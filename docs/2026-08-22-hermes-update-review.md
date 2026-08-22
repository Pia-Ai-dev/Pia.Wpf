# What Hermes Shipped Since July — and What Pia Should Take

_Follow-up to [`docs/superpowers/specs/agent-roadmap/hermes-comparison.md`](superpowers/specs/agent-roadmap/hermes-comparison.md) (2026-07-19)._

- **Date:** 2026-08-22
- **Cut line:** hermes `b6c7df6c` (2026-07-28, the last commit before the previous review's doc was finalised) → `fce30d81` (2026-08-22). **5,884 commits.**
- **Method:** structural diff of both trees, not a re-read. New top-level areas, new files under `agent/`, new skills, new blueprint/toolset registries, and the `feat(*)` subject lines were used to find what is *new in kind*; each candidate was then checked against the Pia source before it was recommended.
- **Not done:** no build, no `dotnet test`, no hermes execution. Every Pia-side "absent" below is a source grep, not a runtime observation.

---

## 1. Verdict

**Three things worth building, one worth measuring, and a large amount that is correctly not ours.**

Hermes's July→August work is dominated by its *gateway/fleet* story — relay, bot-mode, A2A, multi-gateway session lists, OTLP fleet monitoring. None of that transfers: Pia is a single-user desktop client and the previous review already recorded that divergence as justified. Strip it out and what remains is a smaller, sharper set, and it clusters in exactly the places Pia is currently weakest:

1. **The blank box problem.** Pia's Routines view shipped 2026-08-17 as `Name` + a freeform `Query`. Hermes replaced the same surface with a **blueprint catalog** — 15 typed, slot-parameterized automations from one definition that renders as a GUI form, an agent seed prompt, and a deep-link. This is the highest-leverage single idea in the diff.
2. **Failure legibility.** Hermes now names *which layer* failed with recovery actions, and ships a one-click redacted diagnostics bundle from the error card. Pia has two custom exceptions and no support-bundle path, despite `CLAUDE.md` already assuming users hand-attach `pia-*.log`.
3. **Compaction is now measured, and the measurement contradicts the obvious design.** Hermes built a recall-based eval harness and found a *lean* tail plus a mechanical anchor index plus a search-recovery pointer beats its previous fat-tail policy by **+22.5 recall points at 3.3× fewer tokens**. Pia's `AgentContextCompactor` is threshold-only and unmeasured.

The fourth, softer finding: hermes's `SKILL.md` house style has converged into a genuinely good **prompt-authoring standard** — and its central rule ("each numbered step carries a checkable completion criterion") is the same idea Pia already half-built as `AgentStep.ExpectedArtifact`.

---

## 2. First, what Pia already closed

The July review left 18 recommendations. Checking before re-recommending — Pia shipped most of them:

| # | 2026-07 recommendation | Status |
|---|---|---|
| 1 | Persist/restore the launch grant envelope | **Closed** — `HeadlessRunLauncher.GrantEnvelope.cs` |
| 4 | Anchor the verdict in mechanical evidence | **Closed** — `AgentVerifier.cs:22-27` probes each `ExpectedArtifact` against the run's file root, bounded and failure-isolated |
| 5 | Context-management batch | **Closed** — `AgentContextCompactor.cs` (but see §3.3) |
| 6 | Isolated root for headless | **Closed** — `RunWorkspaceService.cs` |
| 7 | Broaden destructive classification | **Closed** — `ToolClassifier.cs` |
| 8 | Batch 07 child-safety envelope | **Closed** — `ParentRunId`, `StepPersonaResolver`, `AgentStepTools.CanRequestUserInput(run.ParentRunId)` |
| 9 | Structured step-success signal | **Closed** — `StepOutcomeSignal.cs` |
| 11 | Decouple steering from sub-agents | **Closed** — `AgentRunSteeringService.cs`, `RunSteeringStore.cs` |
| 14 | Audit timeline | **Closed** — `AgentTimelineService.cs`, with an observer drain seam |
| 15 | Session-scoped grant tier | **Closed** — `SessionToolGrantStore.cs` |
| 16 | Headless needs-approval park | **Closed** — `UserInputRequestStore.cs`, `RunClarifications.cs` |
| 17 | Grounding digest into the plan turn | **Closed** — `GoalPreflight.cs` |

So this review starts from a much higher base than the last one. Nothing below repeats an open item from July.

---

## 3. New on the hermes side, ranked by fit

### 3.1 Automation Blueprints — a catalog behind the Routines box  **← build this**

`cron/blueprint_catalog.py` (799 lines). A blueprint is one definition with typed slots that every surface renders natively:

> Dashboard/GUI → a form (one field per slot) · CLI/TUI → a pre-filled slash command · Agent → a seed prompt, and it asks for any blank slot · Docs → a copy-paste command + a `hermes://` deep-link

The design note that matters: **"users never type raw cron."** A blueprint carries a fixed recurrence in `schedule_template` and parameterizes only the human-friendly parts (time-of-day, weekday set). `fill_blueprint` validates the user's values and emits `cron.jobs.create_job` kwargs — *there is no second job engine*.

The 15 shipped blueprints, and how they land on Pia's existing surfaces:

| Blueprint | Pia surface it would drive |
|---|---|
| Morning briefing · Workday start · Evening wind-down | Reminders + Todo + Assistant |
| Weekly review | Todo + Kanban + Vault (`weekly-review-planning` skill is its prompt half) |
| Important-mail monitor | — (no mail connector; skip) |
| Topic news digest · Competitor news watch | `ScheduledJobKind.Research` — already exists, just undiscoverable |
| Bills & renewals reminder · Custom reminder · Habit check-in | `ReminderService` |
| Price & availability watch | Research + web tools |
| Weekly meal plan · Daily learning drip · Gratitude prompt · On-this-day | Assistant + Vault |

Pia's gap is precise: `ScheduledJob` (`src/Pia.Wpf/Models/ScheduledJob.cs:39`) is `Name` + `Query` + `Kind` + `Recurrence` + `GrantedTools`. `Services/Scheduling/` contains only `RecurrenceCalculator`. There is no catalog, no slot schema, no seed prompt. A new user opening Routines gets a blank box and has to invent the automation *and* its prompt.

The July review already noted Pia's structured recurrence (no raw cron strings) "is exactly the `blueprint_catalog` philosophy" — Pia landed the recurrence half and not the catalog half. This closes it.

**Shape for Pia:** a `RoutineBlueprint` record (id, title, description, `ScheduledJobKind`, `RecurrenceTemplate`, `IReadOnlyList<BlueprintSlot>`, a seed-prompt template, default `GrantedTools`), a static `BlueprintCatalog`, and `FillBlueprint` returning a validated `ScheduledJob` — the *same* `ScheduledJobService.CreateAsync`, no second path. RoutinesView renders the catalog as picker cards; the existing "New routine" dialog becomes the "start from blank" escape hatch. The slot list also gives the assistant a way to create routines conversationally without inventing schema.

### 3.2 Failure legibility: error layer + recovery actions + Send Diagnostics  **← build this**

Two connected pieces, both new since the cut.

**`agent/error_surface.py`** maps an internal failure taxonomy onto a small stable descriptor — `{"layer": ..., "code": ..., "retryable": bool}` — so clients can say *"Provider error"* / *"Gateway error"* instead of, in its own words, "toasting an opaque string and leaving the user to guess whether the model, the gateway, or the app froze." Layers include `provider` (the model API rejected the call) and `endpoint` (a user-configured custom/local endpoint failed at transport). The desktop then renders a card that *names the failing layer and offers the matching recovery action*, and honours the classifier's retry verdict rather than always showing Retry.

**`feat(desktop): Send Diagnostics`** — one-click redacted debug-bundle upload from that same error card, with consent copy, log-grade redaction, a dismissal guard, and a linkless-success path for when upload succeeds but no link is available.

Pia today: `LlmTimeoutException`, `LlmTruncatedException`, `BrowserLaunchException`, and `ScheduledJobService.NoProviderFailureReason`. That last one is the right idea already — a named reason that classifies a failure as *cost-free and safe to retry* — but it exists for exactly one case and is a bare `const string`. Everything else surfaces as a message.

The diagnostics half fits Pia's existing story unusually well. `CLAUDE.md` already states the premise — *"Users may attach `%LOCALAPPDATA%\Pia\Logs\pia-*.log` when contacting support, so anything that ends up there must be safe in release"* — and Pia has already paid for the hard part: `SensitiveDebug`'s `[Conditional("DEBUG")]` erasure means the release log is *already* the redacted artifact. A bundle is a zip of `pia-*.log` + app/OS version + provider names (never keys) + the failing run id, behind an explicit consent dialog that shows what is included. The July review's own caveat ¹ applies and should be honoured: **tool output persisted into transcripts has no redaction backstop**, so the bundle must contain logs and never transcripts.

### 3.3 Compaction: hermes measured it, and lean beat fat  **← measure this**

New: `evals/compaction/` (a recall-based eval harness) and `evals/compaction/results/SCORECARD-2026-08-15.md`. Four real 500K-token transcripts, 15-question recall exam each:

```
policy            AVG recall @ retained tokens
uncompacted       96.7 @ 500K
current           45.8 @ 162K
lean              40.0 @  49K
lean+recovery     68.3 @  49K
```

**+22.5 points over the previous default at 0.30× the tokens.** The harness itself is the transferable part — it measures *recall*, not token count: generate a question bank from the region compaction will summarize away, run each policy, quiz a fresh model on post-compaction context only, judge against gold.

Four techniques in the winning arm, in descending order of value to Pia:

1. **Mechanical anchor index.** Extract exact identifiers (SHAs, ids, paths, error strings) *mechanically* instead of trusting the summarizer with them. This one change moved GUI closed-book recall 23.3 → 60.0. For Pia the analogous anchors are file paths under the run root, `ExpectedArtifact` strings, step ordinals, tool names, and run/step ids.
2. **A recovery pointer.** A footer telling the model it can search the archived region. Worth +20 to +43 points on its own. **Pia can build this today** — `AssistantChatsFts` (`SqliteContext.cs:1090`) is already an FTS5 table over chat history.
3. **Verbatim user messages, never compacted.** Hermes states the reasoning better than a summary would: the assistant's output is largely *an account of what it did* and survives summarising; the user's instructions are *the intent everything else is derived from* and cannot be reconstructed from the work that followed — "paraphrasing 'use the existing retry helper, don't add a new one' into a summary is exactly how an agent ends up confidently doing the thing you told it not to, six turns later." Pia delegates to `Microsoft.Agents.AI.Compaction` and does not assert this invariant anywhere; whether the library honours it is worth one test, and if it does, worth pinning.
4. **Lean tail** (25K clamped) over a fat one. Pia's equivalent knobs are `ToolEvictionThreshold = 0.45` and `TruncationThreshold = 0.70` — both currently reasoned-from-first-principles rather than measured. The scorecard's finding 3 is the warning: hermes's own fat-tail arm scored 93.3 on one transcript and that turned out to be "restatement luck, not policy quality."

**Recommendation is deliberately staged:** build a small recall harness against Pia's own transcripts *before* touching the thresholds. The July review said the fix was "a lightweight brief + tool-result-truncation batch, not a port of hermes's 3,500-line engine" — that was right, and it remains right. What is new is that the thresholds are now checkable.

Explicitly **do not** port `micro-compaction` itself. Hermes ships it **off by default** because each pass rewrites already-sent history and so breaks the provider prompt-cache prefix every turn — its own doc says "for some setups that cost exceeds the benefit." Take the invariant, not the mechanism.

### 3.4 The `tour` tool — the agent shows you the UI  **← strong fit, novel**

`tools/tour_tool.py` (202 lines) + a `desktop_ui` toolset. One generic tool, **no baked-in tour definitions**: the agent calls `action="targets"` to discover what is on screen, then highlights any element with its own title/text — either one step at a time (agent-paced narration) or as a step list the user pages through with Next/Prev. Actions: `targets | show | start | next | prev | stop`.

The detail that makes it work is the one Pia has already paid for. Each discovered target reports `stable: true` **when its selector keys off identity** (`data-tour`, `id`, `data-testid`, `aria-label`) rather than position, and the tool description instructs the model to prefer those and re-scan if a selector stops matching.

Pia's equivalent identity layer already exists and is already documented: `docs/ui-automation-playbook.md` lists the stable AutomationIds, and the 2026-08-16 work explicitly added AutomationIds across settings, approval, sidebar and grid buttons. A WPF tour is an `Adorner`-based spotlight over an element resolved by AutomationId, driven by a tool whose `targets` action walks the visual tree and returns only elements that *have* an AutomationId — the same stability contract, enforced structurally rather than by heuristic.

This is also the natural successor to the static `PiaHelpHint` control added 2026-08-21: a help icon answers "what is this field", a tour answers "where do I do X". Pia already has the *answers* — `docs/2026-08-16-ui-howto-coverage.md` records that every how-to question is covered in the docs corpus and indexed into the server knowledge base (67 documents, link-check passing). A tour is the surface that lets the assistant **show** that answer in the running app instead of only reciting it.

### 3.5 Second tier — cheap, self-contained

| Idea | Hermes | Pia today | Note |
|---|---|---|---|
| **Global pause (ESTOP)** | `agent/estop.py` (174 lines). A sentinel file; while it exists the cron scheduler skips dispatch, the kanban dispatcher skips spawning, new turns get a "paused" reply. **In-flight work is never killed** — pause-new-work, not panic. One `os.stat`, cheap enough to check every tick. | Absent | Obvious desktop feature — a tray toggle before a presentation or on battery. Pia's `ScheduledJobBackgroundService` tick and `HeadlessRunLauncher` are the two check sites. |
| **Empty-response guard** | `agent/empty_response_guard.py`. Unsignaled empty completions (provider reports success with zero output tokens) trigger 3 retries + a fallback chain, each re-sending full context — the "charged ~$2.33 for an empty answer" incident class. Adds deterministic-empty detection and a *cost-aware* retry budget. Signaled refusals are already terminal and excluded. | Absent | Directly relevant to unattended Routines, where nobody is watching the spend. |
| **Repetition guard** | `agent/repetition_guard.py` (95 lines). A model in a degenerate loop burns its whole output budget echoing one fragment; the `finish_reason=length` continuation path then stitches it together with a "continue, don't repeat" nudge and no sanity check. One incident produced a 60,698-char response. Detect repetition-dominated fragments *before* appending the nudge and abort with a clear error. | Pia has `LlmTruncatedException` and a continuation path | ~95 lines of pure logic, no dependencies. |
| **Per-job overrides** | `feat(cron)`: per-job `reasoning_effort`; cron agents now run with memory enabled like every other agent; per-job delivery target | `ScheduledJob` has `ProviderId` + `GrantedTools` + `QuietOnSuccess`, but no persona, no effort, no memory toggle | `QuietOnSuccess` is already Pia's version of hermes's `[SILENT]`. Persona-per-routine is the notable gap: `StepPersonaResolver` exists for runs but a routine can't pick one. |
| **Unified deadline layer** | `agent/deadline.py` (544 lines). Written because the tree carried "at least six site-local deadline mechanisms, each built for one incident, none shared… every new stall report grows that list by one." One `resolve_timeout` (config > legacy env > default) and one bounded-execution primitive. | Worth an inventory | The failure mode described is generic, and Pia has timeouts in the orchestrator, the launcher, the scheduler and the transcription pipeline. |
| **Live sub-agent orchestration** | `delegate_task action='list' \| 'steer' \| 'stop'`; optional structured-output schema on delegation; children get a dedicated SessionDB; `max_iterations`-truncated child results are *marked* for the parent; batch task quality validated before spawning | Batch 07 shipped the spine | Where Pia's sub-agents go next. The truncation marker is the cheapest and highest-value of these: a parent that can't tell "finished" from "ran out of iterations" will happily build on a partial result. |
| **Outbound webhooks** | `agent/outbound_webhooks.py` (569 lines) — a `hooks.outbound:` config list registers notify-only callbacks on the existing hook manager, so every lifecycle event can push to an external endpoint with zero call-site changes | `AgentTimelineService.Emit` + `ObserverDrainAsync` already exist | The July review asked for the observer seam; Pia built it. This is the HTTP mirror on top, and it needs no new emission points. Also the natural owner for the orphaned `AgentRunTrigger.Event`. |

### 3.6 Prompt and workflow assets

The user-facing half of the question. Hermes added 20 bundled skills and 8 optional ones since the cut. Three things are worth taking.

**(a) The `SKILL.md` house style is a prompt-authoring standard.** From `skills/software-development/hermes-agent-skill-authoring`:

- Section order: `When to Use` (bulleted triggers **+ explicit "Don't use for:" counter-triggers**) → `Prerequisites` → `How to Run` → `Quick Reference` → **`Procedure` — numbered steps, each with a checkable completion criterion** → `Pitfalls` (things that look broken but aren't) → `Verification` (how to prove it worked).
- `description` ≤ **60 characters**, one sentence, capability not implementation, no marketing words — because the system-prompt skill index truncates at 57 chars, so the trigger must be self-contained in that window.
- Target ~100 lines simple / ~200 complex. Bulk goes to `references/`, `templates/`, `scripts/` — pointed to, not inlined. "Don't expect the model to inline-write parsers or non-trivial logic every call — ship a helper script."

In practice the steps read: *"Confirm timezone, review period, planning horizon, authoritative task store, and allowed writes. **Done when source-of-truth conflicts have a declared winner.**"* That "Done when" clause is the same construct as Pia's `AgentStep.ExpectedArtifact` — and Pia now *probes* it (`AgentVerifier.cs:22-27`). The transferable move is to push the discipline upstream into `AgentPlanner`: the planner should not be allowed to emit a step whose `ExpectedArtifact` is unprobeable prose. Hermes's phrasing is a good few-shot exemplar for that prompt.

**(b) Four skills whose *procedures* map onto surfaces Pia already has.** These are prompt assets, not code — the value is the decomposition, not a port.

- **`productivity/meeting-action-items`** — "Turn meeting notes into cited decisions, owners, tickets." Its step 1 is *establish meeting evidence*: identify title/date, participants, source files, transcript completeness, and **whether speaker/time references exist**, and it is done only when *missing portions and low-confidence transcription are stated*. Pia is on `feature/speaker-attribution` and has `MeetingAttendee`, `LiveTranscription`, transcript-to-vault, Todo and Kanban. This is the missing prompt between the transcript and the todo list, and its evidence-first framing is exactly right for a diarizer whose speaker labels are uncertain.
- **`productivity/weekly-review-planning`** — a bounded weekly reset; "default to recommendations/drafts, not mutations." Pairs with the `weekly-review` blueprint from §3.1.
- **`productivity/document-to-action-items`** — documents → cited obligations, deadlines, tasks; low-confidence OCR must remain *visible*; detect duplicate/revised copies before analysis. Maps onto Pia's Vault + `IngestToolHandler`.
- **`research/grounded-citations`** — "a ledger script owns the `url → [n]` mapping so the numbers and URLs come from retrieval, never from memory — the model only ever emits small integers it was handed." Verbatim quotes are rejected unless they literally appear in the fetched page text; model-knowledge claims are flagged `[unverified]`. Pia has `WebCitationExtractor.cs`; the ledger inversion (hand the model integers, never let it author URLs) is a hallucination fix Pia can adopt directly.

Note the recurring safety default across all four: **read + draft, never send/delete** — *"'handle my inbox' does not imply permission to send or delete."* That is the same instinct as Pia's deny-by-default gate, expressed at the prompt layer.

**(c) `optional-skills/dogfood/adversarial-ux-test`.** Roleplay the worst-case user, then filter through a "pragmatism layer" to separate real friction from "I hate computers" noise. Targets cold-start problems, empty states, confusing terminology, too many steps. Pia just did a pass on page headers and empty-state vocabulary (2026-08-21) and has a WinWright replay harness in `tests/ui-scripts/` — this is a cheap, funny, and genuinely useful complement.

---

## 4. Explicitly not ours

Recording these so a later reader doesn't re-derive them:

- **Gateway / relay / bot-mode / A2A / multi-gateway session lists.** Largest single share of the diff. Single-user desktop; the July review already classed the inbound-authorization layer as inapplicable and nothing has changed.
- **OTLP fleet monitoring** (`agent/monitoring/`, `docs/observability/monitoring.md`) — gauges, DataDog collectors, fleet alert queries. Built for operators running many gateways. The one idea worth remembering is its **content-free invariant** for the metrics plane; the plane itself is not Pia's problem.
- **`micro-compaction`** — see §3.3. Off by default in hermes, breaks the prompt-cache prefix every turn.
- **`agent/native_compaction.py`** — server-side compaction via OpenAI Responses `context_management`. Deliberately narrow even in hermes: gpt-5.6 family, direct OpenAI routes only, opaque `encrypted_content` sealed to the issuing endpoint. Revisit only if Pia's provider mix narrows, which is the opposite of its direction.
- **`agent/verify/`** — recipe detection + build/test/start/readiness-poll for a *software project*. Ported from grok-cli, runs project commands with `shell=True`. Pia has no shell tool by construction; that containment decision was affirmed in July and this would reverse it for a use case Pia doesn't have.
- **The optional-skills marketplace** (~120 skills: blockchain, gaming, MLOps, drug discovery). Pia's persona/template model is not a distribution channel.

---

## 5. Recommendations

| # | Priority | Recommendation | Size |
|---|---|---|---|
| 1 | **Should** | **Routine blueprint catalog.** `RoutineBlueprint` + typed slots + `FillBlueprint` → the existing `ScheduledJobService.CreateAsync`, no second job engine. Ship 6–8 blueprints that use surfaces Pia already has (morning briefing, weekly review, topic digest, bills/renewals, habit check-in, custom reminder). Keep "New routine" as the blank-start path. | M |
| 2 | **Should** | **Error layer + recovery actions.** A `PiaFailure(Layer, Code, Retryable)` descriptor — generalise `NoProviderFailureReason` rather than adding a parallel scheme — mapped at the boundary and rendered on the failure card as a named layer plus the matching action. Honour the retryable verdict; don't always show Retry. | M |
| 3 | **Should** | **Send Diagnostics.** Consent dialog listing exactly what is included → zip of `pia-*.log` + versions + provider *names* + failing run id. **Logs only, never transcripts** (July caveat ¹: tool output in transcripts has no redaction backstop). | S–M |
| 4 | **Should** | **Recall harness for the compactor**, then tune. Measure before moving `ToolEvictionThreshold`/`TruncationThreshold`. Add the recovery pointer first — `AssistantChatsFts` already exists and it was worth +20–43 pts standalone. | M |
| 5 | **Should** | **Pin the "user messages are never compacted" invariant** with a test against `Microsoft.Agents.AI.Compaction`. If the library doesn't honour it, that is a finding worth having early. | S |
| 6 | Should | **Mechanical anchor index** in the compactor: file paths under the run root, `ExpectedArtifact` strings, step ordinals, tool names, run/step ids — extracted, not summarised. Biggest single win in hermes's scorecard (23.3 → 60.0). | M |
| 7 | Should | **Global pause.** Tray toggle + a flag checked by the scheduler tick and the headless launcher. Never kills in-flight work. | S |
| 8 | Should | **Repetition guard** before the truncated-response continuation nudge. ~95 lines, no dependencies. | S |
| 9 | Should | **Empty-response guard** with a cost-aware retry budget for unsignaled empties; exclude signaled refusals, which are already terminal. Matters most for unattended Routines. | S–M |
| 10 | Should | **Mark iteration-truncated child results** so a parent can distinguish "finished" from "ran out of budget". | S |
| 11 | Nice | **Per-routine persona + reasoning effort** on `ScheduledJob`. `StepPersonaResolver` already exists; the routine just can't reach it. | S |
| 12 | Nice | **`tour` tool** over AutomationId-resolved targets, WPF `Adorner` spotlight, `targets`/`show`/`start`/`next`/`prev`/`stop`. Only offer elements that *have* an AutomationId — stability by construction. Successor to `PiaHelpHint`. | L |
| 13 | Nice | **Planner discipline:** reject a plan step whose `ExpectedArtifact` is unprobeable prose; use hermes's "Done when …" step phrasing as the few-shot exemplar. Cheap, and it strengthens the verifier Pia already built. | S |
| 14 | Nice | **Citation ledger inversion** in `WebCitationExtractor` — hand the model integers, never let it author a URL; flag unsourced claims `[unverified]`. | M |
| 15 | Nice | **Meeting → action items prompt**, evidence-first (state transcript completeness and low-confidence spans before extracting). Natural companion to the speaker-attribution work in flight. | S |
| 16 | Nice | **Outbound webhooks** on the existing `AgentTimelineService` observer drain; gives `AgentRunTrigger.Event` an owner. | M |
| 17 | Nice | **Timeout inventory**, then one resolver if the count justifies it. Don't build the abstraction first. | S |
| 18 | Nice | **Adversarial UX test** as a recorded WinWright flow + prompt, with the pragmatism filter. | S |

**Bottom line:** the July review found Pia missing foundations. This one doesn't — Pia closed twelve of eighteen items in four weeks and the agentic spine is no longer the weak part. What hermes has that Pia lacks now is mostly *surface*: discoverability (blueprints, tours), legibility (error layers, diagnostics), and measurement (the compaction harness). Items 1–4 are the ones that change what a user experiences.

---

## Appendix — Method

Structural diff between `b6c7df6c` (2026-07-28) and `fce30d81` (2026-08-22): 5,884 commits, aggregated by added-file directory and by `feat(scope)` subject line to isolate what is new *in kind* rather than re-reading fixes. Candidates were read at source (`agent/verify/`, `agent/estop.py`, `agent/error_surface.py`, `cron/blueprint_catalog.py`, `tools/tour_tool.py`, `toolsets.py`, `docs/micro-compaction.md`, `evals/compaction/`, and the new `SKILL.md` files) and then checked against Pia by grep before being recommended. Hermes figures quoted above are read directly from those files, not carried over from the July review.

Single-agent review; no adversarial verification pass, unlike the 2026-07-19 report — treat the sizing column as a first estimate.
