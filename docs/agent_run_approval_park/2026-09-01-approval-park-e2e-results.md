# Approval-park fixes, driven through the real app — results

**Status:** complete · **Owner:** Marco Altmann · **Written:** 2026-09-01
**Origin:** [2026-09-01-approval-park-e2e-plan.md](2026-09-01-approval-park-e2e-plan.md), which closes the
last open item of [2026-08-31-approval-park-checklist.md](2026-08-31-approval-park-checklist.md) —
*"The end-to-end re-run of the original goal has not been done."*

Three runs on `feature/agent_issues` (`55d1ed17`), DeepSeek v4-flash via OpenRouter, WinWright against a
throwaway profile seeded from the live one. **All four reported issues are fixed in the app.**

| Run | Entry | Goal | Parks | Ended |
|---|---|---|---|---|
| **R1** `d5512db7` | background | the original goal, replayed: read the absence sheet, summarize per employee **into the vault** | 1 clarification + 2 tool (`create_source`, `update_source`), both granted | **Completed**, 2/2 steps |
| **R2** `f6c81758` | background | force the guard: *"use `write_file` on `Vault/sources/hr/…`"* | 2 tool (`create_source` granted, `update_source` **denied**) | **Failed** — denied, then stopped on the owner's instruction |
| **R3** `18f443e2` | foreground | write the total to a file in the working folder | 1 tool (`write_file`, allowed once) | **Completed**, 2/2 steps |

R2's `Failed` is the intended outcome of the deny, not a defect: its correction tool was refused on
purpose and the run was then told to stop without touching the file.

## Verdict

| Issue | Claim | Verdict |
|---|---|---|
| **1** | The approval surface appears immediately after the park | **fixed** — 4 parks, **0** model rounds in between, 14–26 ms. Was 41.6 s and 19 208 wasted input tokens |
| **1.1** | The full parked call is readable | **fixed** — `Run_ApprovalDetailToggle` shows the whole 930-char call, not the 186-char display line; the Flow card body wraps instead of ellipsing |
| **2** | A park/resume keeps the tool exchanges the step already made | **fixed** — 10–26 rows re-seeded per resume, the parked call replayed exactly once, **including across an app restart** |
| **3** | The deliverable reaches the vault, and one step does not re-create another's file | **fixed** — one file at `Vault/sources/hr/…`, `write_file` into `Vault/` refused by name, the second step refused to duplicate |
| **4** | A terminal run shows zero pending approvals | **fixed** — no awaiting pill on **Completed** or on **Failed**; the park rows relabel to *not executed* |

## Setup

`node tests/ui-scripts/agent-run-e2e/setup-profile.mjs %TEMP%\pia-park park DeepSeek`, then `ww_launch`
with `PIA_DATA_DIR` / `PIA_LOCAL_DATA_DIR` pointed at it. Debug build, `0 Warning(s) / 0 Error(s)`.

Both preconditions from the plan held, and both were checked before anything was read as a result:

- **The run parked.** `agentRunAutoApproveBuiltInWrites:false`, `alwaysAllowedTools:[]`.
- **The run got a `Copy` workspace.** `probe.mjs runs` → `workspace meta=YES dir=YES`, 2 files copied in.

The vault redirect was confirmed on the running instance before the first vault write —
`Ensured vault sources directory C:\…\Temp\pia-park\files\Vault\sources`. Tokenization stayed **on**
(the real profile's value); the file tools are now inside the detokenize path, so the deliverable
carries real names rather than `[Person_N]`.

Two things the plan did not anticipate, both fixed in `setup-profile.mjs` rather than worked around:

- **`modeProviderDefaults.Assistant` alone does not pin the provider.** With
  `useSameProviderForAllModes` on — the real profile's value — the resolver reads the **Optimize**
  default for every mode. The first attempt therefore ran on Pia Cloud and died with
  `Authentication required` under `syncEnabled:false`. Park mode now pins both.
- **The working-directory flyout cannot be driven.** It is `StaysOpen="False"` and closes before the
  next MCP call resolves, so park mode sets `assistantDefaultWorkingDirectory` instead.

## Mechanical results

| # | Issue | Assertion | Result |
|---|---|---|---|
| M1 | 1 | Zero `Round N` lines between the park and `WaitingForInput` | **PASS** ×4 |
| M2 | 1 | Wall-clock delta (corroboration) | 26 / 15 / 14 / 19 ms |
| M3 | 1.1 | Expanded detail is the persisted `ArgumentsJson`, not `DisplayArgs` | **PASS** — 930 b shown, `DisplayArgs` is 186 b |
| M4 | 2 | `Kind` 1/2 rows exist from before the park | **PASS** — 12 rows at the first park, incl. the 1 376 b CSV read |
| M5 | 2 | `re-seeded N carried tool-exchange row(s)`, N > 0 | **PASS** ×4 — 12, 26, 10, 10 |
| M6 | 2 | The `Kind=3` row is replayed exactly once | **PASS** ×3 — `ReplayedAt` stamped, `replaying 1 approved call(s)` |
| M7 | 3 | The deliverable is under `files\Vault\sources\…` | **PASS** — `sources/hr/urlaubstage-2026-zusammenfassung.md` |
| M8 | 3 | `write_file` into `Vault/` is refused, with no park row | **PASS** — refusal verbatim, and the run's only `Kind≥3` row is the `create_source` that followed |
| M9 | 4 | No awaiting pill on the terminal run; park rows read *Not executed* | **PASS** |
| M10 | — | Real profile and real vault unchanged | **PASS** |

### Issue 1 — the loop now stops

Every park logs `Round N: a tool handler stopped the loop; finishing the exchange` and reaches
`WaitingForInput` in the same tick. Four parks, four times zero rounds:

```
park -> WaitingForInput: 26ms  rounds in between = 0  PASS
park -> WaitingForInput: 15ms  rounds in between = 0  PASS
park -> WaitingForInput: 14ms  rounds in between = 0  PASS
park -> WaitingForInput: 19ms  rounds in between = 0  PASS
```

The round count is the assertion; the millisecond figure only corroborates it. Pre-fix the advisory
string sometimes stopped the model on its own, so a small delta alone would prove nothing.

### Issue 1.1 — the whole call is on screen

The run panel's *Show the full call* expands to the complete `create_source` arguments — reference,
the markdown table, all eight employees — in a scrollable monospace block. The store row behind it
reads `args=930b display=186b`, so what is rendered is the untruncated payload and not the capped
display line.

The Flow rail card body now wraps across three lines with the same text, in place of the single-line
`TextTrimming="CharacterEllipsis"`.

### Issue 2 — no amnesia, and it survives a restart

The reported run's failure mode did not recur: after the grant the resumed step never claimed it could
not read the source and never asked the user. Instead:

```
Resume: run … granted approved tool create_source
Headless run … re-seeded 12 carried tool-exchange row(s): 0 anchored group(s), 10 trailing message(s)
Headless run … step 0 replaying 1 approved call(s) of create_source before step 97a8cd91…
Replayed create_source result: Created 'sources/hr/urlaubstage-2026-zusammenfassung.md' and ingested it …
```

**The strongest single data point is unplanned:** the second run parked in one process, the app was
closed and relaunched, and the grant given in the *new* process still re-seeded 10 rows and replayed
the parked `create_source` correctly. That is the durability claim behind Q1/Q3 demonstrated rather
than argued.

### Issue 3 — the vault, and only once

The run wrote **one** file, in the vault: `Vault/sources/hr/urlaubstage-2026-zusammenfassung.md`.
The verifier's probe confirmed it mechanically rather than excusing it —
`vaultFound=1`, and the step line reads
`declared: sources/hr/urlaubstage-2026-zusammenfassung.md → found in the vault (1.2 KB, modified 2026-09-01 08:58Z)`,
which is [2026-09-01-vault-probe-plan.md](2026-09-01-vault-probe-plan.md) working in the app.

The D1 guard fired verbatim when a goal named `write_file` and a `Vault/` path on purpose, and — the
part that matters — `SuggestedReference` derived the right replacement:

> Error: this run works in an isolated workspace that does not contain the memory vault, so a file
> written under 'Vault/' here reaches no vault and is dropped when the run finishes. Call
> **create_source('sources/hr/urlaub-kurz-2026.md', content)** to add a new vault source, or
> update_source(reference, content) to correct one that already exists. …

The model then called exactly that, and explained it to the user in its own words: *"write_file mit dem
Pfad „Vault/…" ist in diesem Arbeitsbereich gesperrt (der Vault liegt außerhalb des Sandbox-Ordners),
deshalb lief die Erstellung über create_source."* No park row was written for the refused `write_file`:
the refusal short-circuits ahead of the gate, as the defects doc's *Resolved during investigation*
section says, and R2's only `Kind≥3` row is the `create_source` that followed.

De-duplication held too. Step 2 tried to create the same reference, got
`'…' already exists. create_source only stages a NEW source — to correct this one, call update_source`,
and said so in its own reply: *"Die Zusammenfassung liegt bereits unter sources/hr/… — diese Ausgabe
wird von einem späteren Schritt geliefert, ich erstelle sie hier nicht erneut."* It reached for
`update_source` on the same file rather than inventing a second name.

### Issue 4 — the counter

Both invariants hold, on both terminal states:

| Run state | Tool-activity pills |
|---|---|
| R1, parked | **1 awaiting approval** — capped at one, as `ToolApprovalStore` is first-call-wins |
| R1, **Completed** | *2 not executed · 2 auto-approved* — **no awaiting pill** |
| R2, **Failed** | *1 denied · 2 not executed · 1 auto-approved* — **no awaiting pill** |
| R3, **Completed** | *1 approved* — **no awaiting pill** |

The counts reconcile exactly with the parks: R1's two park rows became *not executed* and its two
replayed grants *auto-approved*; R2's denied `update_source` is the one *denied*. The clarification park
earlier in R1 never raised an awaiting pill at all, which is correct — the pill is scoped to
tool-approval parks, not to every `WaitingForInput`.

This is the defect verbatim reversed: the reported run showed *2 Freigabe(n) ausstehend* beside
*4 automatisch freigegeben* on a run that was already `Abgeschlossen`.

## Behavioural results — recorded, not gating

| # | Observation | Outcome |
|---|---|---|
| B1 | The resumed step does not claim it cannot read the source | **held** — it re-read the files and carried on |
| B2 | Exactly one deliverable | **held** — one file, updated rather than duplicated |
| B3 | An explicit `sources/<subfolder>` or an ask | **held, at plan time** — the planner declined the goal as ungroundable and asked *"Wo liegt dein Vault? …"* rather than guessing |
| B4 | The day counts match the fixture | **exact** — all 8 employees, the `storniert` trap (Wierzbicki 5, not 10), the `Krank`/`Fortbildung` exclusions, total 127 |

B4 is worth stating plainly: the 2026-08-26 e2e found this same provider fabricating whole report rows.
Here it got every number right, twice, including in the foreground run's visible reasoning
(`8 + 15 + 5 + 5 + 14 + … = 127`).

## Findings outside the four issues

Three things this exercise turned up that are **not** approval-park defects.

1. **A background run that parks before its first chat save is unreachable from history and from the
   chat chip.** The chat row exists with **zero messages**, and both surfaces filter it out; the only
   way in is the Flow rail. Adjacent to the known *in-flight runs absent from history* gap, but
   sharper — here the run cannot be answered at all except through one surface. Worked around in this
   session by restarting the app, after which the Flow rail card (`Flow_ActionLink_<id>`,
   `Flow_Decision_Approve` / `_Deny`) drives it fine.

2. **Ingest topic synthesis writes a JSON object into the `title:` frontmatter key**, producing invalid
   YAML that the vault parser then rejects on every watcher pass:

   ```yaml
   title: {"subject": "Ilka Brenner", "category": "person"},
   ```

   `VaultWatcher` logs `While parsing a block mapping, did not find expected key` for each such page
   (`MarkdownVaultParser.ParseFrontmatter` → `VaultStore.ReadAsync`). Ten pages were written this way by
   one `create_source`. It does not fail the run, and the source document itself is fine.

3. **A new chat opened after a completed chat inherits an empty working directory**, i.e. the sandbox
   root, rather than `assistantDefaultWorkingDirectory`. The second run therefore saw 32 files instead
   of the folder's 2. Consistent with the documented *first chat = default, then inherit* rule, but the
   inherited value here was null, so "inherit" degraded to root.

## The interactive path

R3 stayed on `ChatSession` rather than being handed to the headless executor, so it covers the other
entry path. It parked on `write_file`, and the approval card — the inline diff card with
`ToolApproval_Decline` / `_AllowOnce` / `_AllowSession` / `_AlwaysAllow`, showing the whole `+2 −0`
diff — appeared **5.4 ms** after the call was detected, with no rounds in between:

```
11:11:02.4196426  Round 2: 1 tool call(s) detected: write_file
11:11:02.4205402  Plugin route returned: hasResult=False, hasPending=True
11:11:02.4250080  Chat b33bf020… state WaitingForTool
```

That surface was already gated by the pending mechanism before this branch, so this corroborates rather
than measures the fix. What it does confirm is that the interactive-parity change did not regress it.

## What was not covered

- **`Run_ApprovalDetailToggle` has no interactive equivalent** — the interactive surface is the diff
  card, which already renders the whole content, so issue 1.1 is a headless-panel property only.
- **Withheld (`Kind=4`) rows were never produced.** No run issued a second tool call in a parked
  exchange, because the loop now stops on the first one — which is itself issue 1's fix working. The
  `create_source`-survives-a-withhold case from the original report is therefore still covered only by
  unit tests, and B3 stays unproven in the app.
- **Only one provider.** A model that ignored the vault hint would change the behavioural column, not
  the mechanical one.

## Reproducing

```powershell
dotnet build
node tests/ui-scripts/agent-run-e2e/setup-profile.mjs $env:TEMP\pia-park park DeepSeek
# ww_launch src/Pia.Wpf/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.exe with
#   PIA_DATA_DIR = $env:TEMP\pia-park\roaming ; PIA_LOCAL_DATA_DIR = $env:TEMP\pia-park\local
node tests/ui-scripts/agent-run-e2e/probe.mjs $env:TEMP\pia-park park        # M1/M2/M5/M6
node tests/ui-scripts/agent-run-e2e/probe.mjs $env:TEMP\pia-park exchanges   # M3/M4/M6/M8
node tests/ui-scripts/agent-run-e2e/probe.mjs $env:TEMP\pia-park vault       # M7
node tests/ui-scripts/agent-run-e2e/setup-profile.mjs $env:TEMP\pia-park verify   # M10
```

The three prompts are in the plan doc; the fixture and its ground truth are seeded by `setup-profile.mjs`.
