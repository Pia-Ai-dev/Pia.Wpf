# Compaction recall

Measures what Pia's context compaction actually costs the model — not how many tokens it removed, but
what can still be answered afterwards. Closeout:
[`docs/hermes_checkup/2026-08-24-compaction-recall-closeout.md`](../../../../docs/hermes_checkup/2026-08-24-compaction-recall-closeout.md).

## What is here

| File | What |
|---|---|
| `UserMessagePinningTests.cs` | Characterizes what Pia pins: head goal + newest user message, by reference identity. Middle user messages are **not** pinned, so hermes's "user messages are never compacted" invariant is only half held here. |
| `SyntheticTranscript.cs` | Committed generator — four transcript shapes, seeded, no clock, no real user data, with 15 uniquely-worded facts planted at known unpinned positions. |
| `SyntheticTranscriptTests.cs` | The generator's own self-tests: determinism, answer uniqueness, over-budget, tool pairing, filler alphabet, the single fused image turn. |
| `CompactionRecallHarness.cs` | The instrument: corpus, budgets, the question bank + leak filter, one arm runner, the judge, provider resolution, pacing/backoff, and the scorecard writer. |
| `CompactionArms.cs` | Arms C (mechanical anchor index), D (recovery pointer + a working search) and E (pin every user message), plus the gold-answers-held count every appending arm's score must be read against. **None of them touch `AgentContextCompactor`** — they post-process its output. |
| `CompactionArmsTests.cs` | The arms' mechanics in the default gate, and the pre-registration: what each arm holds before a provider call. |
| `CompactionRecallTests.cs` | The bank facts (gate) and the live entry points: smoke, refusal check, no-context control, and the sweep at each budget. |
| `../../../../scripts/Export-CompactionCorpus.ps1` | Turns one real chat into a JSON fixture, outside the repo. |

## Read the pre-registration before reading a score

`CompactionArmsTests.EveryArmsHoldingsAreReported_BeforeAnythingIsSpent` prints, per transcript, how many of
the bank's gold answers each arm's own context already contains **verbatim**. The leak filter only guarantees
zero for arm B's retained text; any arm that *appends* reintroduces the answers it is being asked to recall.
An arm handed 13 of 15 is being asked whether it can read a list. Run that test first — it is free.

## Conventions

Namespace is `Pia.Tests.Integration.Compaction`, mirroring `Integration/Providers`.

Everything currently in this folder is an ordinary `[Fact]` in the default gate — in-process,
deterministic, no network, no file and no database. Anything that reaches a provider must use
`[LiveApiFact]` / `[LiveApiTheory]` from `../../TestInfrastructure/LiveApiAttributes.cs`, which sets
xunit v3's `Explicit = true` and so reports as `Not Run` in a default run.

```bash
dotnet test                                                              # the gate; the bar is failed: 0
dotnet test -- --explicit only --filter-namespace "Pia.Tests.Integration.Compaction"
```

The built exe takes xunit's native single-dash options instead, which is the only way to run ONE live entry
point — start with the two-call smoke test, never with the sweep:

```bash
PIA_COMPACTION_PROVIDER=<provider name or id> \
  tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe \
  -explicit only -diagnostics \
  -method "Pia.Tests.Integration.Compaction.CompactionRecallTests.OneQuestionEndToEnd_BeforeTheSweepSpendsAnything"
```

**The answering + judging model is part of the measurement.** An arm run on one provider cannot be compared to
a baseline measured on another, so every arm has to run in the same sweep — that is why the sweep runs all six
columns rather than letting you add one cheaply.

The suite runs on **Windows or CI only**. Authoring and compiling on macOS is fine
(`-p:EnableWindowsTargeting=true`); the `net10.0-windows` tests cannot execute there.

## Two rules that are easy to break

**No file in this folder may name a `Microsoft.Agents.AI.Compaction` type.** The whole experimental
(MAAI001) surface is contained inside `src/Pia.Wpf/Services/AgentContextCompactor.cs`, and
`Architecture/ExperimentalApiContainmentTests.cs` scans `tests/` too — a second suppression anywhere
in the solution fails the gate. Every assertion goes through
`AgentContextCompactor.CompactAsync`, which is also what keeps this measuring the shipped path
instead of a reimplementation.

**Diff the removed set by reference identity, not by index.** Compaction reorders: the pinned
instruction is re-attached at the end of the request, and pinned image turns just before it.
`PlantedFact.MessageIndex` is an index into the *input* list — map it to `Messages[index]` and
compare references.

## Real transcripts and where they live

Extracted transcripts contain real conversation content. **They are never committed.** They live
outside the repo, the harness takes a path, and `Export-CompactionCorpus.ps1` refuses to write inside
the repository at all; `.gitignore` carries `*.corpus.json` / `*.bank.json` as the second line of
defence for a file copied in by hand.

```powershell
./scripts/Export-CompactionCorpus.ps1 -List
./scripts/Export-CompactionCorpus.ps1 -ChatId <guid> -Id chat-toolheavy
```

The live database is `%LOCALAPPDATA%\Pia\history.db`, or `<PIA_LOCAL_DATA_DIR>\history.db` when that
override is set. The script opens it `-readonly`, so it is safe to run while Pia is open.

| Variable | Names |
|---|---|
| `PIA_COMPACTION_CORPUS_DIR` | Where fixtures are written. Defaults under the system temp directory. |
| `PIA_COMPACTION_EVAL_OUT` | Where the scorecard will be written. Never inside the repo — it would carry transcript-derived question text. |

Contributors with no local transcripts still get a full run from `SyntheticTranscript`.
