# Batch 11 — Context compaction (`Microsoft.Agents.AI.Compaction`) — ✅ SHIPPED

**Phase 2 · Size S–M · `feature/agent-run-spine` · `74f964c` → `a06358d`**
(plus the joint fix pass `aab9a06` → `601090e`, shared with Batch 10 — see the chronicle in
[`00-OVERVIEW.md`](00-OVERVIEW.md))

This file now describes **the code as built**. Two of the original spec's recommendations were overturned at
design time and one at the fix pass; each is kept below with the reason, because both were plausible and would
otherwise be re-proposed.

> **Build:** `dotnet build -p:EnableWindowsTargeting=true --no-incremental` → 0 errors, 194 pre-existing
> warnings; **`MAAI001` appears zero times**, so the single-pragma containment holds — and it is now pinned by
> a test rather than by the build bar alone (see "Closed after the fact").
>
> **Tests: WRITTEN AND NOW EXECUTED — green.** This supersedes the original note ("written, never executed /
> execution deferred to Windows/CI"). Measured on Windows 11 at `8add90c`: **2149 total, 0 failed, 2148 passed,
> 1 skipped**. Critically, **both fixture-sensitive assertions flagged below PASS** — the fix-pass fixtures are
> sound and no threshold tuning was needed.

---

## What shipped

| Commit | What |
|---|---|
| `74f964c` | `Microsoft.Extensions.AI` + `.AI.OpenAI` 10.5.0 → **10.6.0 in lockstep**, `Microsoft.Agents.AI` **1.15.0** pinned. Own commit, verified green before any compaction code |
| `0023119` | `AiProvider.MaxContextWindowTokens` / `.MaxOutputTokens` (`int?`, null = off) + the edit dialog + 3 strings in all three resx files + a reflection round-trip test |
| `0815fba` | `AgentContextBudget` (readonly record struct) + `AgentContextCompactor` (internal static) + 16 adapter tests |
| `8355498` | The step request is compacted on the **Headless** path and the **Live** path |
| `a06358d` | The in-step tool loop is bounded behind an **opt-in** `AgentContextBudget?` parameter |
| `575ec9b` | Fix pass: **pin the step instruction too**, and charge the pinned prefix **once**; repair two fixtures |
| `7f3f7a3` | Fix pass: assert that the **Live** step path compacts and relays the budget |
| `c47a3ad` | Fix pass: stop labelling max-output-tokens “0 = disabled” |

## The finding (still accurate)

Compaction is **not** in `Microsoft.Agents.AI.Harness`. It is in `Microsoft.Agents.AI` 1.15.0, namespace
`Microsoft.Agents.AI.Compaction` — MIT, netstandard2.0/net8.0+. The static entry point
`CompactionProvider.CompactAsync(strategy, messages, logger, ct)` has zero agent/session coupling, over
`Microsoft.Extensions.AI.ChatMessage`, which is exactly what both executors already build. We adopt no
`AIAgent`, no `AgentSession`, no `ChatHistoryProvider`. `ContextWindowCompactionStrategy` runs tool-result
eviction then truncation with **no LLM call**. Privacy re-confirmed: every log line and telemetry tag is
metadata only, so passing our real `ILogger` is safe.

The Learn docs' type names remain stale (`CompactionMessageIndex`/`CompactionMessageGroup`/
`CompactionGroupKind`; `MessageIndex`/`MessageGroup` do not exist).

## As built

- **`AgentContextBudget`** (`Models/`, no compaction types so no `MAAI001`): `readonly record struct
  AgentContextBudget(int WindowTokens, int MaxOutputTokens)` with `static From(AiProvider?)` returning `null`
  unless the window is > 0 and the output cap is strictly below it. *Deviation:* it accepts a **nullable**
  provider and treats a null `MaxOutputTokens` as **0** rather than rejecting it, so a user who configures only a
  window gets the whole window as input budget — a config that looks set and silently does nothing would be
  worse.
- **`AgentContextCompactor`** (`Services/`, `internal static`): one method
  `CompactAsync(IReadOnlyList<ChatMessage>, AgentContextBudget?, ILogger, CancellationToken)` returning
  `List<ChatMessage>`, plus `internal const ToolEvictionThreshold = 0.45` / `TruncationThreshold = 0.70` and a
  private `MinimumCompactableMessageCount = 4`. **No compaction type appears in any signature, field or return
  type** — only inside method bodies — which is what confines the single
  `#pragma warning disable MAAI001` to this one file and keeps the test project free of the package reference.
  Static also keeps `DiRegistrationTests` and `NamingConventionTests` quiet (a static class is `abstract sealed`
  in IL) and avoids touching the 9-param `HeadlessTurnExecutor` ctor and its 4 test construction sites.
- **Three insertions:** `HeadlessTurnExecutor.RunExchangeStepAsync` right after `exchangeMessages` is built (this
  one insertion also covers `RunSingleTurnFallbackAsync` and the whole **resume** path, because resume growth
  enters through `_messages` and `exchangeMessages` is a copy of it — so no separate resume seam was needed);
  `ChatSession.BuildStepChatMessagesAsync` just before its return; and `AiClientService.cs:189`, at the top of
  the round body **before** the streaming/non-streaming branch, so one insertion covers both provider paths.
  `LiveTurnExecutor.cs` was **not** edited — it builds no message list at all.
- **The whole degrade path is one try/catch inside `CompactAsync`**, wrapping **both** the strategy construction
  and the call. `catch (OperationCanceledException) { throw; }` first, then a metadata-only `LogWarning` and
  `return messages.ToList()`. No call site has a try/catch. Construction *must* be inside the isolation:
  the ctor throws `ArgumentOutOfRangeException` on bad numbers, so a typo in the provider dialog would otherwise
  become a failed step — the exact outcome this batch exists to remove.
- **`.ToList()` at the boundary, always.** `CompactAsync` returns a deferred `SelectMany` iterator over a
  still-mutable index, and `AiClientService` re-enumerates `workingMessages` on its tool-disabled retry path.

## Decisions taken, and what was overturned

- **The spec's “scale `maxContextWindowTokens` to ~70 % of the real window” was REJECTED.** Verified
  empirically: `ContextWindowCompactionStrategy(5734, 8192)` **throws** — and 5734 is exactly 8192 × 0.7, i.e.
  the spec's own hack applied to a plausible 8k-window/8k-output config turns a settings typo into a failed step.
  The same 30 % conservatism now lives on the two **thresholds** (0.5 → 0.45, 0.8 → 0.70), a knob that cannot
  throw, and it keeps `InputBudgetTokens` honest for the log line. Both consts are pinned by a test so the
  conservatism cannot drift back into the throwing shape.
- **The goal is PINNED, not trusted to the library.** Verified, not inferred: an agent-step-shaped list
  `[System, User("THE GOAL"), 8× Assistant, User("Execute step 9")]` through
  `ContextWindowCompactionStrategy(8000, 2000)` came back with **the goal gone** — the first casualty of an
  over-budget agent step was what it had been asked to do. The strategy exposes no pin/protect hook, so the
  adapter splits off the leading `System` run plus the first following `User` message, compacts the middle
  (with the system messages re-included so the library still sees a `System` group), and re-concatenates.
- **The fix pass had to pin the step instruction too.** The first implementation pinned only the goal, and
  measurement showed truncation deleting `"Execute step N: <intent>. Expected: <artifact>"` in **3 of 4
  configurations** once a step made a few tool rounds. *Deviation:* the pinned instruction is re-attached at the
  **end** of the compacted request rather than at its original mid-list position — the library may
  synthesize/collapse tool groups, so there is no reliable identity anchor to reinsert at. For every step request
  the executors build the instruction is already last, so this reproduces the caller's order exactly; only in the
  tool-loop case does it move from mid-list to the end, which keeps call/result pairs adjacent.
- **The pinned prefix was being charged twice** (subtracted from the window *and* re-added to the compacted
  middle where the library counts it), so compaction was evicting history that fit with thousands of tokens to
  spare. Fixed in `575ec9b`.
- **The library's default `ToolCallFormatter` is kept.** Measured what it actually emits, and it is not what the
  spec assumed: it merges each assistant-call + tool-result pair into **one** assistant text message
  (`[Tool Calls]\nlist_files:\n  - {full result JSON}`), dropping the call *arguments* and the message overhead
  but preserving the result content verbatim. So “tool-result eviction” is really *tool-group collapse*, and the
  shrink comes from the subsequent truncation phase. A truncating formatter (measured: 15,211 → 217 bytes) would
  make the model lose data it just fetched and very plausibly re-call the same tool against a 10-round cap.
- **`SummarizationCompactionStrategy` skipped**, as recommended — and for a further reason: Pia has no bare
  `IChatClient` at either seam (clients are built per-request inside `AiClientService`), so wiring it would leak
  chat-client construction into the executors. Its failure log also interpolates a raw provider error string at
  **Warning** level, which lands in a support-attachable log.
- **`SlidingWindowCompactionStrategy` skipped** — and it would have *caused* the regression this batch guards
  against: its vocabulary is *turns*, a turn starts at a `User` group, and a headless step request has exactly
  two `User` groups (the goal and the ephemeral instruction). Turn-windowing's only removable turn **is the goal
  turn**.
- **Our own prior-step brief is kept** (`RunContext.SeedCompletedSteps`), as recommended. It is ~100–200 bytes
  per Done step, bounded, structured, free, and it never enters the step message list — so it was never the
  overflow path.
- **The budget is per-provider, not global, and opt-in.** `AiProvider` had neither value. A user with both a
  200k and an 8k provider is not served by one global number, and per-provider-type defaults would be a silent
  lie (OpenAI spans 8k to 1M; `OpenAICompatible`/`VLlm`/`Ollama` endpoints are arbitrary). Null-means-off makes
  the upgrade-regression risk **zero by construction**. Deliberately absent from `SyncProvider` (device-local,
  per the `SupportsStreaming`/`ReasoningEffort` precedent) and from `ProviderFingerprint.Compute` (the capability
  cache records tool/streaming probe outcomes a window size cannot change) — both recorded in comments so a
  future reader sees a decision, not an oversight. *The `SyncProvider` omission has a consequence the design
  missed — see “Still open”.*
- **The interactive chat path is never compacted**, and the guardrail is **structural**: the two builders are
  physically separate methods, with a comment at the interactive one saying why. No runtime flag to get wrong.
- **The hard guardrail (compaction never touches `_persisted`) is enforced by the type split, not by code.**
  `_messages` is `List<ChatMessage>`; `_persisted` is `List<SyncAssistantChatMessage>`; they are appended in
  parallel, never cross-read, with zero aliasing, and the only route to the DB is `Messages = [.. _persisted]`.
  A pass over any `List<ChatMessage>` is *type-incapable* of reaching `_persisted`. Verified the adapter also
  cannot mutate its input: the strategies only set `IsExcluded` and insert **new** `ChatMessage` instances, and
  an under-budget pass returns the caller's exact instances (`ReferenceEquals` true, element by element).
  Because that safety is currently true by accident of type rather than by design, it is asserted on the
  persisted rows.
- **The provider's own reported `InputTokenCount` was NOT used as the gate.** `CompactionMessageIndex.Create` is
  `internal`, so a measured count cannot be injected — a Pia-side outer gate could only decide *whether* to
  invoke, not *what* the library drops. And gating on last-observed usage only helps from step 2 onward: the
  first overflow has already happened, which is the failure being removed. Revisit if `Create` becomes public.
- **The tool loop was touched, not deferred** — without it the batch ships its title without its mechanism. It
  is behind an **opt-in `AgentContextBudget? contextBudget = null` placed after `cancellationToken`**, so all 6
  production call sites and all 43 test invocations across 15 files kept compiling and interactive chat,
  background turns, `AgentPlanner`, `AgentVerifier`, `TextOptimizationService` and `AssistantViewModel` are
  bit-for-bit unchanged. Guarded on `round > 0` (W4 already compacted round 0's list).

## Deviations worth knowing

- **Nearly every line reference in the design was stale** and each seam was located by content instead. Real
  positions at build time: `ChatSession.BuildStepChatMessages` `:737` (not `:712`), its call site `:667` (not
  `:642`), `RunModelExchangeAsync` `:490` (not `:465`), the interactive builder `:328` (not `:303`).
- **Two files outside the plan's lists had to change**, both mechanical: `AiIngestSynthesisServiceTests.cs` (two
  hand-written `IAiClientService` stubs no longer satisfied the widened interface) and all six NSubstitute stub
  sites in `HeadlessTurnExecutorTests.cs` widened with `Arg.Any<AgentContextBudget?>()`. The second is the
  design's own named NSubstitute risk: without it the two new compaction tests, whose fixtures *do* configure a
  window, would have hit an unstubbed call and failed confusingly.
- **`BuildStepChatMessagesAsync` gained a `CancellationToken`** (the design named only the async rename) and
  deliberately does **not** use `ConfigureAwait(false)`, matching the file's UI-affine convention.
- **The resume assertion landed as a new paired-run test** (`ParkAndResumeUnderCompaction_TranscriptMatchesThe
  UncompactedBaseline`) rather than extra assertions inside the existing park/resume test, whose short literal
  replies would have had to be rewritten — i.e. the very assertions the plan says must pass unchanged. The new
  test runs the identical scenario twice (4000/1000 window with long replies, then no window) and asserts
  **identical** persisted transcripts, which is strictly stronger than a row count.
- **The compaction figures in the commit messages and test comments are measured, not reasoned.** Because
  net10.0-windows cannot execute here, the fix pass built a throwaway net10.0 console harness that
  `<Compile Include>`s the **real** `AgentContextCompactor.cs` against the real packages and ran every fixture
  before and after. That is what confirmed the evicted instruction, the premature eviction, the dropped image,
  and the two broken fixtures. It does **not** prove the xUnit assertions themselves.

## ⚠️ Two shipped assertions are fixture-sensitive

`AgentContextCompactorTests.OverBudget_ShrinksButKeepsSystemAndGoalFirst` and
`HeadlessTurnExecutorTests.CompactionShrinksTheRequest_…` assert that the list actually shrinks. The library's
`minimumPreservedGroups` defaults are not published and could not be read without executing code. The first pass
shipped fixtures that **provably never triggered compaction** (measured in = out); the fix pass repaired them
(12 replies) and added a second over-budget test at a 6000 window, so “over budget” is pinned by two independent
knobs. **If either goes red on CI, tune the fixture, not the thresholds** — check the measured in/out counts in
the test comments first.

> **RESOLVED 2026-07-29: both pass.** The suite was executed on Windows and neither assertion is red, so the
> repaired fixtures do trigger compaction and the thresholds were never the problem. The warning is kept because
> the underlying fragility is real — the defaults are still unpublished, so a package bump can still move them,
> and the instruction above still applies if that happens.

## Still open (see 00-OVERVIEW “Opened by Batch 11”)

### Closed after the fact

- **The adapter dropped *every* returned `System` message, not just the pinned prefix.** Closed by `045edea`.
  The re-concatenation filtered `kept` by `ChatRole.System`; it now skips by **reference identity** over the
  instances that went into `head`, so a library-synthesized or non-leading system message survives. Measured on
  the new fixture: in=16, kept=10 containing two system messages, out was 11 with the mid-list reminder deleted
  and is 12 now. Red before, green after.
- **Nothing enforced the `MAAI001` containment premise.** Closed by `6895b89`
  (`ExperimentalApiContainmentTests`). A reflection walk over every declared surface in the assembly —
  recursed through generic arguments, array/by-ref/pointer elements and nullable underlying types — comparing
  **namespace strings**, so the test project stays free of the `Microsoft.Agents.AI` reference, which is itself
  part of the containment; plus a source scan pinning the pragma to exactly one occurrence in
  `AgentContextCompactor.cs` and asserting no csproj / `Directory.Build.props` / `.editorconfig` mentions
  `MAAI001`. **The build bar provably cannot catch this**: injecting a `static ContextWindowCompactionStrategy`
  field into the pragma'd file builds with **zero** `MAAI001` warnings, and the reflection test names the field.
- **A sync pull silently reset the configured window to `null`.** Closed by `1c49b08` — `SyncMapper` now
  preserves `MaxContextWindowTokens`/`MaxOutputTokens` from the existing local row across a pull
  (`SyncMapper.cs:342-343`), keeping them device-local without letting the pull erase them.
- **The tool-loop insertion had no test at any level.** Closed by `261410f`.

### Still open

An image attachment is the first thing evicted on the Live agent path (measured: in=7 → out=6, no `DataContent`
survives; the pin protects the goal that refers to it, so the step answers about an image it cannot see) ·
`bytes/4` accounting is wrong in both directions and unfixable while `Create` is `internal` ·
`ToolEvictionThreshold` is close to inert, so truncation does all the work · the step-1 request is
never compacted, so an oversized *goal* still fails — though **at planning, not at step 1**: `AgentPlanner`
passes no `contextBudget` at all (`AgentPlanner.cs:118-119`), so the run settles `Failed` at Planning and step 1
never runs · compaction is invisible to the user in every **release** build, because the log level is
compile-time only (`Bootstrapper.cs:307`/`:317` read `IsDevMode`, which is `#if DEBUG`) — so the two `LogDebug`
outcome lines are unrecoverable, not merely inconvenient · **the comment at `:139-142` is backwards**: it claims
under-charging a pinned image "errs toward compacting", but a smaller `pinnedCost` yields a *larger* `window` and
therefore *less* compaction, i.e. it errs toward silently overflowing · the package bump's streamed tool-call
coalescing and the seven `OPENAI001` pragma sites are unverified beyond compiling (`74f964c` is the first commit
to revert if provider behaviour regresses).

The **fallback** (vendoring ~700 LOC) was not needed and should not be revisited: the bump was clean.

## Acceptance — met for accumulated context, and unproven at runtime

A step whose tool loop would overflow now compacts instead of failing ✅ *in the adapter, unit-tested; the
tool-loop wiring is covered as of `261410f`* · persistence unchanged ✅ (type-enforced and asserted) ·
build green with `MAAI001` contained ✅ **and now pinned by a test, not by the build bar** · **tests written AND
executed green ✅** (2149 total, 0 failed, on Windows 11 at `8add90c`; both fixture-sensitive shrink assertions
pass).

**The manual Windows smoke list is still undone** — an executed unit suite is not a smoke test: unconfigured
provider behaves as on main; interactive chat unchanged with a window configured; a long run with a small window
completes and still obeys its step goal; park/resume keeps every prior step reply; an image attachment on the
Live agent path loses neither the attachment nor the goal (this last one is **expected to fail** — see “Still
open”, hazard C).
