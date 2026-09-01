# Validate the approval-park fixes in the real app — a WinWright plan

**Status:** executable · **Owner:** Marco Altmann · **Written:** 2026-09-01
**Origin:** the last open item of
[2026-08-31-approval-park-checklist.md](2026-08-31-approval-park-checklist.md) — *"The end-to-end
re-run of the original goal has not been done."* All 20 code steps landed; nothing has been seen
working outside the unit suite.

Root causes and the four reported issues: [2026-08-31-approval-park-defects.md](2026-08-31-approval-park-defects.md).
Selector reference: [../ui_automation/ui-automation-playbook.md](../ui_automation/ui-automation-playbook.md).
Harness: `tests/ui-scripts/agent-run-e2e/`.

---

## What is being validated

| Issue | Claim to prove in the app |
|---|---|
| **1** | The approval surface appears **immediately** after the park, not after N more provider round-trips |
| **1.1** | The full, untruncated parked call is readable from the run panel |
| **2** | A park/resume keeps the tool exchanges the step already made — no amnesia, no "I cannot read the file" |
| **3** | The deliverable lands in the **vault**, a `Vault/` write inside a run workspace is refused, and one step does not re-create another step's file |
| **4** | A terminal run shows **zero** pending approvals |

---

## Two preconditions that void the whole exercise

Check both before reading any result. A run that fails either is **void**, not green.

1. **The run actually parked.** `agentRunAutoApproveBuiltInWrites` must be `false` *and*
   `alwaysAllowedTools` empty in the seeded copy — the seed copies the real `settings.json`, so a
   persisted "Always" grant would ride along and silently auto-approve every write. Evidence: the
   `Background turn parked … for human approval` log line.
2. **The run got a `Copy` workspace.** `VaultTargetPolicy`'s refusal is scoped to a non-null
   `TaskAmbient.Current?.WorkspaceRoot`, so with no workspace D1 cannot fire and *not seeing the
   refusal proves nothing*. Evidence: `probe.mjs runs` printing `workspace meta=YES dir=YES`.

## Setup

Throwaway profile, live credentials. `setup-profile.mjs … park` copies the real `settings.json` /
`providers.json` / `templates.json` (the DPAPI-encrypted key and the sign-in survive only as bytes),
then patches the copy:

| Key | Value | Why |
|---|---|---|
| `syncEnabled` | `false` | never talk to the real account |
| `assistantFilesFolder` | `<root>\files` | **this is what redirects the vault** — `Bootstrapper` calls `paths.SetRoot(AssistantWorkspace.VaultRootFor(settings.AssistantFilesFolder))`, so `PIA_DATA_DIR` alone would not |
| `agentRunAutoApproveBuiltInWrites` | `false` | precondition 1 — the seed's own default is `true` |
| `alwaysAllowedTools` | `[]` | precondition 1 |
| `autoIngestSources` | `false` | keep the indexer out of the vault write |
| `modeProviderDefaults.Assistant` | the named BYOK provider | `syncEnabled:false` rules Pia Cloud out |
| `assistantAgentModeDefault` | `true` | the composer opens in Agent mode |

Provider: **DeepSeek v4-flash via OpenRouter** (owner's choice, 2026-09-01), the same provider as the
2026-08-26 e2e, so its findings are comparable. Debug build, so the log runs at `Debug` level and the
`SensitiveDebug` payload lines are available as corroboration.

**Tokenization stays ON** (the real profile's value). It is not a confound any more: the file tools were
absent from the detokenize allowlist until this branch, and `TokenizingAiClientService` now detokenizes
every tool call's arguments, so the deliverable carries real names. The fixture is nevertheless designed
so the ground truth survives tokenization either way — see below.

### The real profile must come back untouched

`setup-profile.mjs` hashes `settings.json`, `providers.json`, `templates.json` and `history.db` at seed
time. This plan adds a fifth: an inventory (sorted relative path + size) of the **real vault**, derived
from the real `assistantFilesFolder` + `\Vault`, not from `%LOCALAPPDATA%`. Vault writes are the point
of scenario S1, so the vault is the one tree a routing mistake would damage. `verify` re-checks all five.

## The fixture

`<root>\files\Absence\` — the original goal's shape, in a form whose answer can be checked.

- `Fehlzeitenübersicht-2026.csv` — 22 absence rows over 8 employees, columns
  `mitarbeiter,abteilung,typ,von,bis,tage,notiz`.
- `urlaubsregeln.md` — the rule that makes the answer non-guessable: only `typ=Urlaub` counts, and a row
  whose `notiz` reads `storniert` does not.

Ground truth (holiday days per employee): Ilka Brenner **23**, Tomasz Wierzbicki **5** (his second row is
`storniert` — the trap), Nadeschda Orlow **19**, Ruben Castellanos **22**, Yannick Dubois-Peil **10**,
Halima Ceesay **23**, Gero Pflüger **5**, Marlis Ostrowski **20**. Total **127** over 8 employees.

Why it survives tokenization: the checkable facts are the **day counts**, which the PII detector does not
touch. The names are tokenized on the way to the model and detokenized on the way into the write, so they
check the detokenizer rather than the model's memory; the `von`/`bis` dates arrive as `[Phone_N]`
placeholders (the known `yyyy-MM-dd` behaviour) and the fixture therefore never requires the model to
compute a span — `tage` is given.

## Scenarios

Three runs, one app session, a fresh chat each.

### S1 — the original goal, replayed (issues 1, 1.1, 2, 3, 4)

Working directory `Absence`, Agent mode, **Run in background**. Background rather than foreground on
purpose: it puts the run on `HeadlessTurnExecutor` directly, which is where all four defects live, with
no dependency on the ≥3-step plan-approval gate that decides whether a foreground run is handed over.

> Lies die Datei `Fehlzeitenübersicht-2026.csv` im Arbeitsordner, beachte `urlaubsregeln.md`, und
> schreibe eine Zusammenfassung der Urlaubstage pro Mitarbeiter in meinen Vault.

Deliberately underspecified about the vault subfolder — that is D3's case.

Expected: step 1 reads (auto-allowed), the first write **parks**, the approval surface appears at once,
`Run_ApprovalDetailToggle` shows the whole call, `ToolApproval_AllowOnce` grants it, the resumed step
carries the extract, and one summary file lands under `files\Vault\sources\…`.

### S2 — force the D1 refusal (issue 3, the guard)

Working directory `Absence`, **Run in background**. Names the wrong tool on purpose:

> Erstelle mit `write_file` die Datei `Vault/sources/hr/urlaub-2026.md` mit einer kurzen Zusammenfassung
> der Urlaubstage aus `Fehlzeitenübersicht-2026.csv`.

Expected: `write_file` is refused **before the gate** (`PrepareWriteFile` returns an error, and
`HandleToolCallAsync` short-circuits ahead of the approval gate, so there is no park row), with the
refusal naming `create_source` / `update_source`; the model then reaches the vault through `create_source`.

### S3 — the foreground entry path, and a deny (issue 4's terminal invariant)

Working directory `Absence`, Agent mode, **foreground** — the second entry path, and the one the report
came from. Same goal as S1. At the park, click **Deny** instead of allowing.

Expected: the run reaches a terminal state, the timeline's park row relabels to **Not executed**, and the
awaiting pill is gone.

> **What actually ran, 2026-09-01.** The two halves of S3 were split across the runs, because S2 parked a
> second time and offered the deny for free. The **deny** landed on S2's `update_source` park (background
> path); S3 was spent on the **foreground** half instead, with a different, smaller goal so the run would
> stay under the ≥3-step plan gate and remain on `ChatSession` rather than being handed to the headless
> executor:
>
> > Lies `Fehlzeitenübersicht-2026.csv` und schreibe die Gesamtsumme der Urlaubstage als Datei
> > `urlaub-summe.txt` in den Arbeitsordner.
>
> It parks on `write_file` — the only run of the three to park on a files tool — and was granted with
> **Allow once**. Both terminal states are therefore covered (S2 `Failed` after the deny, S3 `Completed`),
> which is what the invariant needs.

## Assertions

Split deliberately: the mechanical ones are properties of the code and a single run can settle them; the
behavioural ones are properties of the model and a single run can only *record* them. Reporting "all four
validated" off a run where two of them were the model behaving well is the failure mode to avoid here.

### Mechanical — these gate the verdict

| # | Issue | Assertion | Where read |
|---|---|---|---|
| M1 | 1 | **Zero** `Round N: … tool call(s) detected` lines between `Background turn parked … for human approval` and `Run … → WaitingForInput (paused)` | `probe.mjs park` over `local\Logs\pia-*.log` |
| M2 | 1 | The wall-clock delta of that same pair, as corroboration only — a small delta alone is also consistent with the model having stopped by luck | same |
| M3 | 1.1 | `Run_ApprovalDetailToggle` is present while parked; expanded, its body is longer than the 400-char display cap and equals the persisted `ArgumentsJson`, not `DisplayArgs` | UIA + `probe.mjs exchanges` |
| M4 | 2 | `AgentToolExchanges` holds `Kind` 1/2 (call/result) rows created **before** the park | `probe.mjs exchanges` |
| M5 | 2 | After the grant, the log carries `re-seeded N carried tool-exchange row(s)` with `N > 0` | `probe.mjs park` |
| M6 | 2 | The `Kind=3` (ParkedCall) row has `ReplayedAt` set — exactly once, and the log carries `replaying N approved call(s)` | `probe.mjs exchanges` |
| M7 | 3 | The deliverable exists under `<root>\files\Vault\sources\…` | `probe.mjs vault` |
| M8 | 3 | S2's `write_file` result is the `VaultTargetPolicy` refusal and names `create_source`; **no** park row was written for it | log + `probe.mjs exchanges` |
| M9 | 4 | On the terminal run, the run panel shows no `… awaiting approval` pill, and the park row reads **Not executed** | UIA |
| M10 | — | Real `settings.json` / `providers.json` / `templates.json` / `history.db` **and the real vault inventory** unchanged | `setup-profile.mjs … verify` |

### Behavioural — recorded, never gating

- **B1 (2)** — the resumed step does not claim it cannot read the source, and does not re-ask the user.
- **B2 (3, E1/E2)** — exactly one deliverable, not one per step under two names.
- **B3 (3, D3)** — the run either picks an explicit `sources/<subfolder>/…` or asks which subfolder.
- **B4** — the day counts in the deliverable match the ground truth above. A mismatch here is the
  2026-08-26 fabrication finding, not an approval-park regression; it is recorded as its own line.

## Known noise, not findings

- **The stale-latch window on the approval detail**, carried open in the checklist: if a park clears
  while a store read is in flight, the late read can set the latch against the new state. Do not assert
  the toggle's *absence* in the moments right after a park, and do not chase a flicker.
- **Withheld rows outliving several park/resume cycles** is deliberate (it is what keeps the reported
  run's `create_source` alive); extra `Kind=4` rows are expected, not a leak.

## Order

1. `dotnet build` (Debug), then seed and verify the two preconditions.
2. S1 — the long one, and the only one that needs a live grant.
3. S2 — cheap, and independent of S1's outcome.
4. S3 — last, because a denied run is the terminal state issue 4 is about.
5. `setup-profile.mjs … verify`, then write the results doc beside this one.
