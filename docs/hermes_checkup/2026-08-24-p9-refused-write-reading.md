# P9 — the step that reported `succeeded=True` on a refused tool call

**Status:** answered; no production change made. **Owner:** Marco Altmann. **Written:** 2026-08-24.
**Origin:** row `P9` of [2026-08-22-hermes-followup-checklist.md](2026-08-22-hermes-followup-checklist.md),
scoped *investigate, not fix* by §11 of [2026-08-23-a4-replay-reading.md](2026-08-23-a4-replay-reading.md).
The second instance comes from §10 trap 2 of [2026-08-23-a2-wide-read.md](2026-08-23-a2-wide-read.md).

The row asked one question first: **does anything in the step outcome need to change, or is the missing piece
only that a refusal is surfaced nowhere?** It is the second — and the reason is sharper than the row's three
readings allowed for, because the call was never refused in the sense the word implies.

---

## 1. The observed call was authorized, executed, and failed

§5 of the replay reading calls it *"the sandbox refused it"*, and the payload it quotes is

```json
{"success":false, … "error":"Error: Path is outside the assistant files folder.","created":false}
```

That is not a gate decision. It is `FilesToolHandler`'s own return value — `WriteResult.Failed(…)`, built at
`src/Pia.Wpf/Services/FilesToolHandler.cs:1037` when `SafeFolderPath` rejects a rooted path. The unattended
gate had already said yes: `write_file` was in the run's grant list, `DispatchGateVerdictAsync` took its
`AutoRun` arm, and `pending.Execute()` ran and returned normally.

The distinction decides the whole row. A gate **denial** is visible: `BackgroundAssistantTurnRunner`'s
`Refuse` and default arms both emit a timeline row with `AgentTimelineOutcome.NotExecuted` and a
`ToolGateDecision` saying why, and the panel paints those `RunDecisionSeverity.Refused`. None of that fired
here, because nothing was denied.

## 2. So reading 1 is correct, and the step outcome needs no change

`HeadlessTurnExecutor` decides success at `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs:611`:

```csharp
var succeeded = claim?.Succeeded ?? !string.IsNullOrWhiteSpace(exchange.Visible);
```

Both branches are working as specified. A structured `emit_step_result` claim overrides the text heuristic in
both directions — that is hermes #9, deliberate — and the silence fallback is deliberately *not*
"no call means failure", because a `SupportsTools=false` provider would then fail every run.

Neither branch has anything to go on. **The tool result is not an input to either one.** The executor never
sees it: `HandleToolCallAsync` hands the result object straight back to the tool loop, which serializes it to
the provider, and nothing on the way keeps a counter. So the model was the only party that knew the write had
failed, and it answered in prose — which the executor is entitled to read as a step that did its work.

Reading 2 ("the executor should say so") would require the executor to know, and reading 3 (the run-level
probe is the right granularity) is what actually happened: `4da7bf96`'s probe reported `NOT FOUND` for the
declared `README.md`, and the file genuinely was not on disk. The cheap instrument caught it.

## 3. The real gap: `Ok` means "returned", not "worked"

`AgentTimelineOutcome.Ok` is documented as *"Authorized and `Execute()` returned"* — and that is exactly
what it is emitted for. The `AutoRun` arm distinguishes only two outcomes: `Error` when `Execute()` **throws**,
`Ok` otherwise. A handler that returns a failure payload is `Ok`.

The panel then flattens it further. `RunProgressViewModel.Project` sets `OutcomeSuffix` only for
`AgentTimelineOutcome.Error`, and `Severity` reads the *decision*, not the outcome — so a `write_file` that
wrote nothing renders identically to one that wrote the file: no suffix, routine severity.

Two smaller findings from the same read:

- `resultChars: (executed as string)?.Length` — `write_file` returns a `WriteResult` **record**, not a string,
  so every `write_file` row carries a null `resultChars`. The one tool with a structured return contributes
  nothing to the only result-shaped field the timeline has.
- The step-outcome log line carries `offered / confirmed / succeeded / declarations / artifactReported`.
  Nothing about tool calls, so the log cannot answer this either.

**Net: an executed-but-failed tool call is surfaced nowhere** — not the step outcome, not the timeline, not
the panel, not the log. Only in the model's prose, and only if it chooses to mention it.

## 4. Why there is no cheap fix, which is why this stayed an investigation

Two constraints, both real:

1. **The timeline is metadata-only** — see the class remarks on `AgentTimelineEvent`: never an argument, never
   a result, never a path, and never a *hash* of one. So it cannot store `"Path is outside the assistant files
   folder."`, and the fix cannot be "record the error".
2. **There is no shared failure envelope to read.** `write_file` alone returns a structured record with a
   `success` field. Every other failing path returns a bare string with an `"Error: "` prefix — around 118
   such sites across `src/Pia.Wpf/Services`, in at least `FilesToolHandler`, `GitToolHandler`,
   `IngestToolHandler` and `MemoryToolHandler`. Neither convention is enforced anywhere, and a plugin or MCP
   tool is under no obligation to follow either.

A generic "did this tool fail" signal therefore means either sniffing free-form payloads for a prefix — which
silently mis-classifies any tool whose successful output happens to start with `Error:`, and misses every one
that reports failure some other way — or introducing a return contract that every built-in handler must adopt
and no external tool can be held to. Neither is XS, and neither belongs to this row.

## 5. What would actually be worth building, if anything

Ranked, for whoever picks this up. **None of it is required by P9**, which is answered above.

1. **A fourth outcome, `Failed`, populated only where the shape is unambiguous** — i.e. from
   `WriteResult.success == false`, plus any handler later given the same envelope. That is a real signal with
   no guessing, it needs no result text, and `RunProgressViewModel.Project` already has the branch to hang a
   suffix on. It leaves the ~118 string sites unclassified, which is honest: `Ok` keeps meaning "returned",
   and only rows that can prove failure claim it.
2. **A tool-failure count on the step-outcome log line**, same source. Cheapest possible version of the same
   fact, and it makes the next reading of this channel possible at all.
3. **Nothing in the step outcome.** Making `succeeded` depend on tool results would fail steps whose model
   correctly recovered from a failed call, and it is the model — not the executor — that knows whether the
   step's work got done.

## 6. The second instance is a different defect, and it is a measurement one

`2dcc6fd2` persisted five declarations and **failed**, yet its only verify pass saw three.

The mechanism is not a bug. `AgentRunOrchestrator`'s drain loop reaches

```csharp
if (cancelled || failed)
    break;
```

immediately before the verify pass, so **a run that fails never verifies again** — by design; there is no
clean drain to critique. Every declaration made after the last verify pass therefore goes unprobed, and the
`Artifact probe:` counters on that last line under-report the run.

So the consequence is entirely about the instrument:

- **Never quote a probe line's `declared` as a run total.** Read `AgentSteps.ExpectedArtifact` from the
  database, which is untruncated and is what the verifier itself reads. This is already trap 1 and trap 2 of
  the A2 wide read; this section is the *why*.
- One cheap improvement exists if a future row wants it: `TryBuildArtifactFactsAsync` is a **pure filesystem
  probe with no provider call** — it runs first inside `VerifyAsync`, before any capture. Running it (and only
  it) on the failure path would complete the tally at zero token cost, and would also surface "this run failed,
  and here is what it declared but never produced", which is the case a user most wants to see. That is A6/A7
  territory, not P9's.

## 7. What this reading cannot settle

- **Whether the observed step emitted an `emit_step_result` claim at all**, or fell through to the prose
  heuristic. The `offered=…/confirmed=…` log line would say, but that run's profile was a throwaway and the
  log is gone. It does not change the conclusion: neither branch can see a tool result, so both reach the same
  place by different routes.
- **How often this shape occurs.** `n = 1` for instance 1, `n = 1` for instance 2. The value of item 5.2 is
  precisely that it would make the frequency measurable.
