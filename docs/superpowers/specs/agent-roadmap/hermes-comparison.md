# Is the Pia.Wpf Agentic Layer on the Right Track?

_A review against **hermes-agent** (a mature, widely-adopted agent platform), used as a reference implementation to validate direction._

- **Date:** 2026-07-19
- **Method:** Multi-agent workflow — 3× grounding (read-only maps of both codebases), 4× deep-dive cluster comparisons, 1× synthesis, 1× adversarial verification. Both synthesis and verification agents read the Pia.Wpf source directly and independently reconfirmed every defect below with file:line anchors.
- **Scope:** Pia.Wpf agent system (Phase 1 + Phase 2 as shipped, Batches 01–09 as planned) vs. hermes-agent's run loop, state persistence, approval/autonomy, budgets/compression, cron/kanban, and delegation.

---

## 1. Verdict

**Yes — Pia is on the right track. Rating: B+.** Architecturally sound, and actually *ahead* of hermes in three places. Held back by one unbuilt foundation, one under-anchored gate, and three confirmed bugs in already-shipped code.

The central bet — an explicit **persisted** plan→act→replan spine rather than hermes's single ReAct loop — is well-founded, **not** over-engineered. Hermes buys the same durability only by persisting full message trajectories (`hermes_state.py`, ~7,600 lines) plus a ~3,500-line compression engine to keep them affordable. Pia's real constraints (desktop restarts, resumable budget-pause, per-step progress UI, headless scheduled runs) are served more directly by a small plan object + a 387-line orchestrator with disciplined degrade paths.

**The single most important thing to get right: anchor autonomy in reality before widening unattended reach.** Today the verifier judges the model's own prose (`AgentStep.ExpectedArtifact` is persisted but never mechanically checked), unattended runs write *and delete* into the shared deliverables folder with no isolation and no audit trail, and three confirmed defects sit in shipped code. Close behind: **context/trajectory compression is absent from the code _and_ from all nine roadmap batches**, despite the design doc's own §12 statement that 40-step runs are unreachable without it.

> **Framing correction the deep-dives surfaced:** hermes does *not* "compress to keep running" past its *iteration* budget — it breaks the loop with one grace call. Pia's durable park-and-resume is the **stronger** budget story. Compression in hermes solves *context-window* exhaustion — which is the gap Pia actually has.

---

## 2. Scorecard

| Capability | Hermes approach | Pia status | Alignment | On track? |
|---|---|---|---|---|
| Run/step spine + persistence + state machine | Session-level SQLite `SessionDB`, WAL+repair, typed end_reason | **Shipped** (per-run rows, CAS resume, crash sweep) | Mostly aligned | Yes |
| Orchestration loop (plan→act→replan) | Single synchronous ReAct loop, emergent planning | **Shipped** (explicit persisted spine) | Divergent (justified) | Yes, caveats |
| Planner / `emit_plan` | No planner — decomposition emergent in-loop | **Shipped**; Batch 05 planned | Divergent (justified) | Yes |
| Verify / critic | Evidence-anchored (executed-check ledger + on-disk file-mutation verify) | **Shipped** (prose-judged) | Partial | **Yes, caveats** |
| Context/trajectory compression | Pluggable `ContextEngine`, fired 3× in loop + reactively | **Absent — and absent from roadmap** | Divergent | **Concerning** |
| Tool/MCP approval gate | Central `_run_approval_gate`, 3 modes, re-checked core | **Shipped** (deny-by-default, exec-time re-check) | Mostly aligned | Yes |
| Per-run autonomy policy | ~4 global knobs + frozen `--yolo`, no DSL | **Planned** (Batch 04; `PolicyJson` NULL) | Partial | Yes, caveats |
| Destructive-action floor | Ordered denylist fires *before* bypass | **Shipped** (structural: no shell, single file chokepoint) | Mostly aligned | Yes, caveats |
| Budgets + budget-pause/resume | Two iteration gates + one grace call, no durable pause | **Shipped (durable park/resume — _ahead of hermes_)** | Mostly aligned | Yes |
| Cost ledger | Static price table, 5 token classes, per-model UPSERT, `pricing_version` | **Deliberately divergent** — pricing withdrawn 2026-07-30; Batch 02 now *removes* `CostUsd`. Tokens + active wall-clock only | Divergent by decision | n/a — see [`02`](02-cost-ledger.md) |
| Audit timeline / observability | Queryable messages + versioned fail-open Observer Hooks | **Planned** (Batch 03) + shipped `RunChanged` | Partial | Yes |
| Privacy-first logging | Runtime `RedactingFormatter` (defeatable heuristic) | **Shipped** `[Conditional("DEBUG")]` IL erasure (_harder — for log sites_) | Mostly aligned | Yes, caveats¹ |
| Headless/scheduled execution | Cron carve-out, fail-closed `cron_mode: deny` | **Shipped** (default-deny headless, anti-retry) | Mostly aligned | Yes, caveats |
| Workspace isolation | OS-level boundary; isolate-then-run | **Reserved seam** (Batch 06); writes shared folder today | Partial | **Yes, caveats** |
| Sub-agents / multi-persona | Model-invoked delegation, flat depth, leaf roles, auto-deny in children | **Reserved seam** (Batch 07) | Partial | Yes, caveats |
| Scheduled jobs + UI | Hardened cron, single-firer CAS, 3-min hard interrupt | **Shipped** (emission) + Batch 09 (UI) | Mostly aligned | Yes |
| Live steering / plan mutation | `steer()` (non-pausing) + `interrupt()` (in-flight) | **Reserved seam** (Batch 08) | Partial | Yes, caveats |
| Progress UI + Flow deep-links | TUI subagent overlay (after-the-fact) | **Shipped** (live plan tracker, Flow cards) | Aligned | Yes |

---

## 3. What Pia Gets Right (validated by hermes)

- **The persisted plan spine is the correct desktop-shaped bet.** Hermes gets durable resume only via full-trajectory persistence + a 3,500-line compressor; Pia's small plan object + `Safe*` failure-isolated orchestrator is cheaper for its constraints. The degrade ladder (SingleTurn fallback, shared bounded replan budget, `Completed+truncated "unverified"`) matches hermes's fail-soft philosophy.
- **Single-gate discipline.** Re-deriving eligibility at execution time from the *service*, not the card (`ChatSession.cs:794-836`), is exactly hermes's "one decision core, no drift." Modeling MCP as a typed `PluginToolCall` is **structurally cleaner** than hermes's string-pattern heuristics — Pia intercepts the call object, not attacker-influenced text.
- **Containment-by-construction beats the arms race.** No shell tool + single `FilesToolHandler` chokepoint + `SafeFolderPath` symlink/junction canonicalization is a categorically stronger *default* than a regex denylist over Turing-complete shell strings — hermes concedes its gate is "a heuristic, not a boundary" (SECURITY.md §2.4). Pia correctly did **not** cargo-cult Docker.
- **Compile-time privacy is a harder guarantee** _for log call-sites_: `SensitiveDebug`'s `[Conditional("DEBUG")]` erases the call and its argument evaluation from release IL; hermes's `RedactingFormatter` is a defeatable runtime regex. (Qualifier below.¹)
- **Durable budget-pause/park/resume is strictly ahead of hermes**, which just breaks the loop and forgets. Parked runs survive restart via CAS `TryBeginResumeAsync`; the sweep asymmetry (interrupted→Cancelled, parked→survives) is the same invariant hermes converged on (`suspended` vs `resume_pending`).
- **Unattended default-deny + anti-retry** (`BackgroundAssistantTurnRunner.cs:347-354`) mirrors `cron_mode: deny`; per-job `GrantedTools` is *finer*-grained than hermes's single global `cron_mode`. `OwnerDeviceId` single-firer independently reinvents hermes's machine-id CAS claim; auto-disable after 5 failures = hermes's `failure_limit`.
- **Structured recurrence (no raw cron strings)** is exactly the `blueprint_catalog` philosophy hermes converged on for GUI surfaces — Batch 09 lands on proven ground.

---

## 4. Where Pia Diverges from Hermes

### (a) Justified divergences — desktop/single-user context makes hermes's choice inapplicable

- **Explicit plan spine vs ReAct loop** — justified by product features hermes lacks (durable cross-restart resume, per-step progress UI, budget-pause affordance).
- **No dangerous-command tokenizer** — Pia has no shell tool, so hermes's ~2,000-line `detect_dangerous_command` problem class simply doesn't exist. Correctly *not solved* rather than under-built.
- **No kanban-style durable multi-agent queue** — hermes needs it for multi-profile/multi-gateway fleets; Pia's durable `AgentRun` rows + `ScheduledJob` cover the single-user desktop durability story. Document it as a non-goal.
- **No inbound-authorization layer / network-egress proxy** — single-user desktop app, no network-facing message surface. (Egress *is* an undocumented residual risk for MCP/HTTP plugins — see §5.)
- **Batch 05 (reason-then-emit)** is a correctly-gated fix, not gold-plating: opt-in, scoped to the exact Chat-Completions asymmetry (`ReasoningEffortMapping` drops the effort param when tools are present) that hermes never faces.

### (b) Concerning divergences — Pia is genuinely missing or mis-ordering something hermes proved necessary

1. **The verifier has zero evidence anchoring.** `BuildVerifyMessages` judges only self-reported `VisibleText` summaries — it cannot read a produced file or check `ExpectedArtifact` existence (*persisted but never checked anywhere*). Hermes's entire verification design exists *because* LLM self-verdicts over self-summaries rubber-stamp. For unattended runs this critic is the **only** gate before a "Completed" Flow card — the most likely silent-failure mode of the whole Phase-2 system.
2. **Three confirmed live defects** (both opus agents read all three):
   - **Resume grant-escalation.** `ResumeAsync` hardcodes `grants = new[] { "write_file", "delete_file" }` (`HeadlessRunLauncher.cs:224`) while `LaunchAsync` honors narrower `req.GrantedWrites` (`:126`), and nothing persists the original (`PolicyJson` bound to `DBNull` at `AgentRunService.cs:117`). A scheduled job launched with narrow `GrantedTools` that budget-pauses **silently acquires write+delete over the shared folder on resume** — escalation toward *delete over real user files*.
   - **Wall-clock inflation.** `AddUsageAsync` recomputes `ledger.WallClockMs = ElapsedMs(startedAt)` from the original `StartedAt` (`AgentRunService.cs:181`; same computation in `RefreshLedgerWallClock:668`). A run parked 3 days then resumed records ~72h — poisoning the ledger strip now and any later usage analytics.
   - **Scheduler head-of-line block.** `ExecuteAgentTaskAsync` holds `_runLock` across `await handle.Completion` (`ScheduledJobBackgroundService.cs:166-198`) — one long/stuck job (up to the 45-min wall clock) delays every other due job. This is exactly the scheduler-monopolization failure hermes's 3-min cron hard-interrupt prevents. _(Corrected diagnosis — see §8.²)_
3. **Unattended runs operate unisolated on the shared folder with delete grants and no audit.** The §17.2 owner amendment (`HeadlessTurnExecutor.Initialize(workspaceRoot: null)`) *inverted the plan's own finding* that "isolation is the safety prerequisite." Batch 06 (isolation) and Batch 03 (audit) are both unbuilt, yet Phase-2 unattended producers already ship writing+deleting to the shared root — the plan's own red-team scenario, running in production order.
4. **Destructive-MCP heuristic too narrow.** `IsDeleteLike` = "delete" substring + "forget" only (`ToolPermissionService.cs:59`). `purge_records`, `drop_table`, `wipe_cache`, `remove_all`, `erase_history` are **not** delete-like, are grantable-as-a-class, and after one "always allow" click auto-execute forever. Hermes never name-classifies external tools as *safe*.
5. **Batch 07 omits hermes's mandatory child-safety envelope:** no auto-deny of approvals in child threads (a child prompting deadlocks the parent surface — the exact reason hermes auto-denies), no depth cap (hermes defaults `MAX_DEPTH=1`; Pia's persisted `ParentRunId` makes unbounded recursion *durable and self-resuming* — worse than process-local), no leaf tool-stripping, no child-result re-entry size budget.

---

## 5. Gaps the Roadmap Misses Entirely

- **Context/trajectory compression — the headline gap.** Zero code, zero roadmap coverage (grep of `agent-roadmap/*.md` for compact/compress returns nothing), yet design doc §12 says "only with compaction does _run for 40 steps_ become reachable." A provider context-length overflow currently surfaces as a generic *failed step that burns a replan*. Invisible on frontier models (200k windows); will surface as flaky scheduled-run failures on exactly the small/local providers Batch 05 targets. **Mitigant:** Pia's per-step exchange isolation (only visible replies cross step boundaries) is genuine implicit trajectory compression — so the fix is a **lightweight brief + tool-result-truncation batch, not a port of hermes's 3,500-line engine.**
- **Live telemetry/export seam.** Batch 03 is a *persisted* table, not a live contract. Route its emission through a small in-proc `IRunObserver` (fail-open) so one event stream serves both audit and a future OTel/Langfuse/file-trace consumer; `RunChanged` today carries only `(RunId, State, StepId?)`.
- **Aggregate cross-run usage view.** Hermes's `InsightsEngine` answers "what did this month cost me"; per-run ledger strips can't. Users budget monthly. **Scoped down by the 2026-07-30 pricing withdrawal:** if this is ever built it aggregates *tokens and active time*, never money.
- **Event/API triggers orphaned.** `AgentRunTrigger.Event` is reserved in the enum but no batch owns it; hermes treats cron+webhook+API as one triad. Reserve a design note at least.
- **A trust-model / SECURITY.md-style candor doc.** State plainly that MCP stdio subprocesses run with full user privileges *entirely outside* `SafeFolderPath`, and that containment is per-chokepoint, not per-process. Hermes's most transferable security asset is naming which layer is load-bearing.
- **Dead mid-run correction path.** Step success = `!string.IsNullOrWhiteSpace(exchange.Visible)` (`HeadlessTurnExecutor.cs:227`), so a step that politely explains its *failure* records `Done`. Failure-only replan almost never fires for semantic failure. (`RunContext.Scratchpad` is declared but read/written nowhere — the fix seam already exists.)

---

## 6. Roadmap Critique — Is the Batch Ordering Right?

The batch *targets* are right (they independently rediscover hermes's answers: null-on-unknown, decision-as-audit-fact, fail-open emission, isolate-then-promote, un-loosenable destructive floor). But sequencing repeatedly schedules foundational safety/capability **behind** flashier features:

- **Insert a context-management batch, before Batch 07 & 08.** Only cluster item with zero roadmap coverage; design doc says it gates run length; retrofitting *after* sub-agents multiplies the surface (every child context needs it).
- **Pull Batch 06 (isolation) forward — it's Phase 3 but the risk shipped in Phase 2.** At minimum, default headless runs onto the isolated root with promotion-on-success *before* Batch 09 broadens unattended producers.
- **Decouple Batch 08 (live steering) from Batch 07 (sub-agents).** Hermes shipped `steer()`/`interrupt()` with zero delegation dependency; Pia's user-pause + nudge need only the *already-shipped* budget-pause CAS machinery. Higher user value than sub-agents, currently scheduled behind the riskiest unstarted batch.
- **Decouple Batch 09 (scheduler UI) from Batch 04 (autonomy policy).** `GrantedTools` is already a working per-job least-privilege surface — finer than hermes's global `cron_mode`. Hermes shipped cron years before smart approvals.

**Over-engineering watch:** Batch 04's three tool-class lists (`autoApprove`/`alwaysPrompt`/`neverAuto`) is more DSL than hermes ever needed (it runs a far larger tool surface on ~4 knobs + a hard floor). Keep v1 to a posture enum + the existing grant-name list; defer the taxonomy until a concrete consumer demands it.

---

## 7. Prioritized Recommendations

| # | Priority | Recommendation | Batch |
|---|---|---|---|
| 1 | **Must** | Fix `ResumeAsync` grant-escalation: persist the launch grant envelope (`PolicyJson`/`ExtraJson`), restore on resume; a missing set resumes with the **narrowest** envelope, not widest. | 04 (first slice) |
| 2 | **Must** | Scheduler: **stop awaiting `handle.Completion` inside the tick loop** — dispatch runs bounded only by the launcher slot semaphore, move bookkeeping to a continuation. Removing `_runLock` is a corollary, not the fix.² | new/09 |
| 3 | **Must** | Post-resume wall-clock: persist an accumulated-active-ms counter at pause; `ElapsedMs` adds only the post-resume delta (the current pause "freeze" is itself unsound across repeated pauses³). | 01 |
| 4 | **Must** | Anchor the verdict in mechanical evidence: check each step's `ExpectedArtifact` against the filesystem and feed the result into the verify prompt; a missing declared artifact is an automatic verify-relevant fact. | new |
| 5 | **Must** | Add a context-management batch (per-exchange token estimation, in-step tool-result truncation, a running brief of prior-step visibles) — **before** Batch 07. | new (before 07) |
| 6 | **Must** | Default headless onto the isolated root + promotion-on-success **before** broadening producers; change default grants from `{write_file, delete_file}` to `{write_file}` (or `{}`). | 06/04 |
| 7 | **Must** | Broaden destructive-MCP classification (remove/purge/wipe/drop/erase/destroy stems) **and** consume MCP `ToolAnnotations.destructiveHint`/`readOnlyHint` when servers provide them. | 04 |
| 8 | **Must** | Batch 07 child-safety envelope: pin children to headless default-deny (auto-deny, never raise interactive prompt), flat depth cap at spawn, leaf tool profile. Prefer **persona-per-child-run over per-step** (per-step multiplies weak capability probes and defeats prompt caching⁴). | 07 |
| 9 | Should | Replace non-empty-text step-success with a structured signal (`emit_step_result{succeeded, artifactRef}`); wire the dead `RunContext.Scratchpad`. | new |
| 10 | ~~Should~~ **WITHDRAWN** | ~~Batch 02 fidelity: cache-token classes before computing cost, stamp `pricingVersion`, render as "est.", attribute per-model.~~ Moot — Pia will not render a money figure (decision 2026-07-30). Nothing in Pia's usage path carries provider-reported spend, so any figure would be a silently-stale bundled-table estimate. The *token-class* half (cacheRead/cacheWrite/reasoning) survives as an optional future ledger-fidelity item with no currency attached. | ~~02~~ |
| 11 | Should | Decouple Batch 08 from 07; ship a non-pausing nudge (thread-safe accumulate, drain at next step boundary, never rewrite transcript) + in-flight interrupt via the run CTS. | 08 |
| 12 | Should | Batch 04: freeze policy at launch (never re-read live settings mid-run; no tool can mutate it); fail-restrictive on unknown policy values (hermes PR #4682 shipped exactly the "unknown mode = bypass" bug). | 04 |
| 13 | Should | Enable WAL + `busy_timeout` on the shared `SqliteContext` **plus** integrity-check/repair-on-open (hermes `_db_opens_cleanly` + `repair_state_db_schema`).⁵ | new |
| 14 | Should | Batch 03: add `toolCallId` + per-step ordinal correlation IDs; record `requestedAt`/`decidedAt` (incl. timeout) for gated calls; drop per-run cap/eviction (evicting audit rows defeats the feature). | 03 |
| 15 | Should | **Add a session-scoped grant tier** (once/session/always) — Pia has only `AllowOnce` + permanent `AlwaysAllow`, pushing users toward standing grants.⁵ | 04 |
| 16 | Should | **Headless needs-approval park:** reuse the shipped `WaitingForInput`+Continue-card machinery so a headless run hitting an un-granted promptable capability parks for a human decision instead of hard-denying.⁵ | new/01 |
| 17 | Should | Inject a compact grounding digest (tool names, files listing, memory hits) into `BuildPlanMessages`; decouple Batch 09 (ship goal+schedule+budget+`GrantedTools`) from Batch 04. | 05/09 |
| 18 | Nice | Grace turn on budget pause; quiet-mode for monitor jobs (`[SILENT]` analog); per-job run-history list; `ILogger.BeginScope(runId/stepId)` correlation + a small release-mode redacting sink backstop; one-page trust-model doc. | 01/03/09 |

**Bottom line:** the architecture is sound and in several respects ahead of a mature reference platform. The work now is *hardening what already ships* (the three confirmed bugs, evidence-anchor the verifier, isolate unattended writes) and adding the *one foundational capability the roadmap forgot* (context compression) before sub-agents multiply the surface.

---

## 8. Adversarial Pass — Corrections Applied

The verification agent (high confidence) reconfirmed every defect and calibrated the B+ verdict as well-founded. Five corrections are already folded into the sections above:

- **¹ Privacy claim over-broad.** Compile-time erasure is harder *for log call-sites only*. There is **no** redaction for tool **output** persisted into transcripts/chat rows (a secret a tool returns — e.g. a catted `.env` — has no backstop). Not blanket superiority over hermes.
- **² Scheduler diagnosis was incomplete.** `ExecuteOnceAsync` (`ScheduledJobBackgroundService.cs:89-96`) already dispatches due jobs via a sequential `foreach { await RunJobAsync }`, awaited by the outer loop (`:73`) — so jobs serialize *regardless* of `_runLock`. Removing the lock alone would **not** fix head-of-line blocking; the real fix is not awaiting `handle.Completion` inline in the tick.
- **³ "PauseAsync correctly freezes it" was too generous** — the pause path uses the same `ElapsedMs(startedAt)` snapshot (`AgentRunService.cs:665-670`), so the freeze is itself inflated across repeated pauses.
- **⁴ Per-step-persona prompt-cache risk** (restored to Rec #8): multi-persona-per-step with per-persona providers multiplies weak capability-probe failures and defeats prompt caching — prefer persona-per-child-run.
- **⁵ Restored dropped `should`-grade gaps:** session-scoped grant tier (#15), headless needs-approval park (#16), and SQLite integrity/repair-on-open alongside WAL (#13).

---

## Appendix — Method & Provenance

| Phase | Agents | Model | Output |
|---|---|---|---|
| Ground | 3 (Pia map, hermes core, hermes cross-cutting) | Sonnet | Read-only capability maps (11 / 7 / 7 dimensions) |
| Deep-dive | 4 (orchestration, security, state/cost/audit, multi-agent/sched) | Fable | Per-dimension comparison + recommendations |
| Synthesize | 1 | Opus | This report; read Pia source to confirm the three defects |
| Verdict | 1 | Opus (adversarial) | Reconfirmed all defects; produced §8 corrections |

_All hermes-side figures were verified by the adversarial pass: `conversation_loop.py` 5,736 lines, `context_compressor.py` 3,521, `hermes_state.py` 7,617, `approval.py` 3,928; `cron_mode` default `"deny"`; one-shot `_budget_grace_call`._
