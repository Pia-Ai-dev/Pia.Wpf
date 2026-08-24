# Prompt — the next workflow-driven session

**Status:** ready to paste. **Written:** 2026-08-24. **Origin:** the C5/C7 batch and the B11 decision.
Everything below §0 is the prompt; paste it whole into a fresh session.

---

## 0. The prompt

> **Use a workflow for this.** Multi-agent orchestration is explicitly authorised for the whole session:
> fan out where the work is independent, and adversarially verify findings before acting on them.
>
> Repo `C:\projects\Pia.Wpf`, branch `feature/agent-run-spine`, at or after `2f51d3c0`. **Another session
> may commit to this branch.** Re-read the checklist and `git log` before trusting any line number or row
> state below — this prompt is a snapshot, the checklist is the state.
>
> **Read first, in this order:** `docs/hermes_checkup/2026-08-22-hermes-followup-checklist.md` (the tracking
> surface — every row carries what it actually shipped), then `CLAUDE.md`. Read a plan doc only when you
> take a row that cites one.
>
> **The gate is `dotnet test` with no filter and the bar is `failed: 0`** (4740 total as of `2f51d3c0`;
> 1 skipped and 58 `Not Run` are expected — the live-provider rows). A feature is not done until
> `dotnet build -t:Rebuild` reports **0 Warning(s) in both Debug and Release**; `TreatWarningsAsErrors` is on,
> so a warning is already a build failure. Tick each checklist row **in the commit that lands it**, carrying
> what it actually shipped and what it deliberately left out.
>
> ### Three gates can cancel work below them — resolve before building the dependants
>
> 1. **`D-Q1` blocks D3–D8.** Is the guided tour onboarding (a canned tour, no LLM) or arbitrary
>    "where do I…" questions? Unanswered. D2 is *not* blocked and is the first visible result.
> 2. **Plan §11 Q4 blocks C6** — the slot prompt's shape. The recommendation on record is an inline slot
>    block above `Routines_Field_Goal`, visible only for blueprints with slots, which stops re-rendering the
>    goal once the user has hand-edited it. Adopt or overrule it in writing.
> 3. **`A2` is deferred with two named build triggers**, in §8 of `2026-08-23-a2-wide-read.md`. Re-read
>    supply before building A2/A3/A6/A7 — the last read was 22% on 13 runs, and the band was fixed in
>    advance: build above 40%, drop below 12%.
>
> ### The work, grouped for fan-out
>
> **Wave 1 — three cheap rows, no open questions.** `E10` (a resume passes `explicitProviderId: null`, so a
> job that pinned a provider runs its remaining steps on whatever the ladder answers; the resolved provider
> is already on the run's stub chat, so no new column), `E11` (**decided: freeze both directions** — record
> that the launch resolved its pins, so a null means *resolved to nothing* rather than *predates the
> columns*; `ReasoningEffort.None` cannot be the sentinel, it is a real pinnable value), and `P9`
> (**investigate, not fix** — a step reported `succeeded=True` after the sandbox refused its only
> `write_file`; answer whether the step outcome is wrong or whether refusals simply surface nowhere).
> **E10 and E11 both touch the resume seam — do not fan them out against the same working tree.** P9 writes
> no production code and is safe to run in parallel with either.
>
> **Wave 2 — the B-track, which is now live rather than theoretical.** `B11` (landed 2026-08-24) means
> compaction actually fires: before it, no provider had a context window, so `AgentContextBudget.From`
> returned null and an over-window chat failed at the provider instead. `B4` measured that path at **0.0%
> recall of evicted facts against arm A's 98.3%**, on 4 of 4 transcripts — so `B6` (arm C, anchor index),
> `B7 → B8` (message-level search, then the recovery pointer) and `B9` (arm E, pin all user messages) each
> have the full gap to play for, and `B10` is the sweep that reads them. B6, B7 and B9 are independent of
> each other; B8 needs B7; B10 needs all three arms. **The instrument already exists** — `B3`/`B4` built
> `Integration/Compaction/CompactionRecallHarness.cs`; read
> `2026-08-23-compaction-arm-ab-reading.md` before spending a single provider call, especially its
> operational notes (concurrency 3 earns a 429; one provider fault used to discard three transcripts).
>
> **Wave 3 — gated.** `C6` behind §11 Q4. `A2 → A3 → A6 → A7` behind the supply re-read. `D2`, then
> `D3 → D5 → D6`/`D4`/`D8` behind D-Q1; `D7` (AutomationId gap-fill) is independent of the gate and feeds
> `docs/ui_automation/ui-automation-playbook.md`.
>
> **Also open, deliberately low priority.** `F3` (two directory mtimes are the gate's last footprint on the
> real profile; the fix is making `SensitivePathGuard`'s root array and `RunWorkspaceRedirects`'s containment
> gate re-derivable). And the "not yet planned" table — **if you take anything from it, take #2 and #3
> together**: they are the only two `High`s and they are one feature area, failure legibility.
>
> ### Invariants and traps — these were each paid for once
>
> - **Do not put policy in a widely-shared pure reader.** B11's first attempt defaulted the context window
>   inside `AgentContextBudget.From` and **80 tests failed** across `ChatSession`, `LiveTurnExecutor`,
>   `AgentRunOrchestrator` and `MidPlanAsk`, with the suite going 27s → 55s, because every bare `AiProvider`
>   in the process — including stubs that never came from persistence — acquired a budget. Moving it to
>   `ProviderService.LoadProvidersAsync`, where providers are *constituted*, fixed all 80 without touching
>   one of them.
> - **Line endings break byte-identical raw-string tests.** `PersonaPromptCompositionTests
>   .DefaultOutputFormat_MatchesPiaBuiltInsOutputFormat` compares literals across two assemblies. It can also
>   **fail on an incremental build and pass on a clean `-t:Rebuild` with no source change** — rebuild before
>   diagnosing. And the diagnostics lie: `sed -n 'Np' f | od -c` and `cat -A` both strip `\r` in this Git
>   Bash. Byte-check with `od -c` on the file, or `grep -c $'\r'` against `wc -l`.
> - **Records may not live in the `Pia.Services` root namespace** — `NamingConventionTests` enforces it. Put
>   a feature DTO in `Pia.Models` or a feature sub-namespace.
> - **`MapJob` and `MapRun` read by ordinal.** A new column is appended to the END of the SELECT list, with
>   both migration halves (`PRAGMA table_info` + `ALTER TABLE`).
> - **A new device-local column stays off the wire** — absent from `SyncScheduledJob`, `SyncMapper` and
>   `UpsertFromSyncAsync`'s SET list, or the server nulls it back out on the first push-pull cycle (E1b).
> - **A new tool whose arguments become persisted text must join
>   `TokenizingAiClientService.WriteOperations`,** or the approval card shows detokenized text while the
>   stored record keeps `[ORG_1]` — and the token map dies with the session.
> - **Adding an optional parameter to `IScheduledJobService.CreateAsync` ripples** to three hand-written
>   fakes and every NSubstitute `Arg.Any<…>()` matcher chain; the test project will not compile until all of
>   them are updated.
> - **Every new interactive control needs an `AutomationProperties.AutomationId`** and the matching
>   `[InlineData]` count bump in `ViewAutomationIdTests`, in the same change.
> - **The gate must not write to the developer's real Pia profile** (row F1). Redirect with `PIA_DATA_DIR` /
>   `PIA_LOCAL_DATA_DIR`, or override `JsonPersistenceService.DirectoryPath`, which is `protected virtual`
>   for exactly that.
> - **Prove a test non-vacuous** by reverting the half it covers and watching that assertion fail. Several
>   rows here were tightened only because that step caught an assertion observing the default.
>
> ### Two things worth deciding rather than inheriting
>
> - **`ContextWindowDefaults` is a hand-authored table and the real answer is live discovery.** OpenRouter's
>   `/models` returns `context_length` and the Anthropic Models API returns `max_input_tokens`, but
>   `ProviderService.FetchModelsAsync` returns `List<string>` and throws the rest away. Widening that return
>   type would retire most of the table. Not yet a row — make it one if you agree.
> - **`RoutineSlotKind` ships with one member and no reader.** Carried because the C5/C7 brief listed it as
>   decided. Remove it until a second kind exists, or leave it as the seam `Time`/`Enum` land on.
>
> Work the waves in order, commit each row as it lands, and report at the end what you deliberately did not
> do and why.
