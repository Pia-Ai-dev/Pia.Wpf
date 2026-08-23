# Brief — A4 and the unreadable negative: make the probe's verdict mean something

**Status:** ready to execute. Self-contained: paste §0 into a fresh session and it has everything.
**Owner:** unassigned. Needs a **Windows desktop session** — two rows are measurements against the
running app.
**Written:** 2026-08-23.
**Origin:** the reading in [`2026-08-23-a1-pilot-reading.md`](2026-08-23-a1-pilot-reading.md), which
answered gate **A1** of
[`2026-08-22-hermes-followup-checklist.md`](2026-08-22-hermes-followup-checklist.md) and found the
gate's designated row unreadable. Preceded by
[`2026-08-23-group-a-batch-run.md`](2026-08-23-group-a-batch-run.md), which shipped the instrument this
batch measures with.

---

## 0. The prompt

> Read `docs/hermes_checkup/2026-08-23-a4-disjunction-batch-brief.md` and implement the batch it
> scopes: rows **P1, P2, P3, P4, P5, P6, P7** — the planner prompt fix that makes the artifact probe's
> negative verdict readable, the before/after measurement that proves it, and the doc corrections the
> gate reading owes. One workflow: plan → implement → build gate → measure → simplify → review → fix →
> finalize. Review is five dimensions (correctness · CLAUDE.md conformance · tests · integration and
> architecture · scope and dead code), and every finding is killed or confirmed by two independent
> refuters with different lenses before anyone acts on it.
>
> **This batch changes prompt bytes on purpose.** That is the point of it. The freeze imposed by the
> previous two batches has served its purpose — the sample it protected has been collected and read.
>
> **The hold: A2, A6, A7 and review recommendation #13 stay closed.** A2's premise weakened when supply
> measured 2 of 17 step outcomes; P6 re-measures it and P7 records the call. Do not route `ArtifactRef`
> through the probe, do not extract `IArtifactProbe`, do not add typed-prefix probes.
>
> Three decisions the planning phase must answer in writing rather than settle silently in code:
> **(1)** how P1 forbids a *disjunction* while preserving the *conjunction* licence the schema
> deliberately grants, given both live in the same sentence; **(2)** whether P3's per-declaration
> not-found counter is still needed once P1 lands, or whether candidate-level counting becomes correct
> by construction — P3 is explicitly conditional on P2's measurement, do not build it blind;
> **(3)** what P2 does if the four-prompt replay produces a materially different plan shape than the
> pilot did, since the planner is non-deterministic and the before/after is not a controlled experiment.
>
> Everything else this brief says.

---

## 1. Where the repo is

Branch `feature/agent-run-spine`, at or after `c96d5dc0`. **Another session commits to this branch
concurrently** — rebase rather than assume, and check `git log` before trusting any line number below.

| Commit | What |
|---|---|
| `c96d5dc0` | The pilot reading's denominator correction — 56%, not 33% |
| `5d84c59b` | The pilot reading |
| `251da05b` | Parse-error fix that made `Measure-ArtifactDeclarations.ps1` runnable at all |
| `78896f3c` | Merge of the instrument batch (G1 tally, G6 supply counter, N1 fallback log, G2/G3 script and tests, G4/G5/N2 doc corrections) |

**The test gate is green and has actually been executed:** `dotnet test`, no filter →
**4655 total / 0 failed / 4601 succeeded / 54 skipped / ~32s**. Any failure is a real regression. The
54 skipped are the `[LiveApiFact]` Explicit set.

## 2. What the pilot measured — the grounding for everything below

Four runs on a throwaway profile, one per runbook prompt category, on a Debug build carrying the
instrument. Full detail in [`2026-08-23-a1-pilot-reading.md`](2026-08-23-a1-pilot-reading.md); the three
facts this batch turns on:

**(a) The gate does not close.** 9 distinct intended artifacts, 5 file-shaped — **56%**, against the
≥85% the gate needed. `A2`–`A4`, `A6`, `A7` all stay open. (The raw counters say 15 and 33%; a replan
re-declares the same artifact against a new step row, so both the vague original and its concretized
twin survive into the final facts block. Collapse the pairs.)

**(b) The negative verdict is unreadable, and the cause is a disjunction.** `notFound` went non-zero
for the first time — 4, against 0/23 historically. Every one is this shape:

```
… (e.g., criteria_list.md or criteria_summary.pdf)       → criteria_list.md: found; criteria_summary.pdf: NOT FOUND
… (e.g., approaches_summary.md or approaches_report.pdf) → approaches_summary.md: found; approaches_report.pdf: NOT FOUND
… (e.g., comparison_table.xlsx or comparison_report.md)  → comparison_table.xlsx: NOT FOUND; comparison_report.md: found
… (e.g., recommendation.md or recommendation_report.pdf) → recommendation.md: found; recommendation_report.pdf: NOT FOUND
```

The planner named alternatives, the step wrote one, the probe correctly reported the other absent. All
four files exist on disk and all four steps succeeded. Each per-candidate fact is true; the aggregate
is what lies, because `found` and `notFound` count **candidate paths**, so a two-name disjunction
contributes one of each no matter how well the step performed.

**(c) Report-channel supply is thin.** `artifactReported=` was **True on 2 of 17** step outcomes. Only
the runs that actually wrote files reported anything.

### Why this makes A4 the row to build, and why it goes first

The schema does not forbid multiple names — it invites them. `AgentPlanner.cs:159` reads:

> *"The concrete artifact(s)/result this step should produce — may name several files when they are one
> logical change"*

and `:784` (plan) and `:827` (replan) reinforce it: *"that is ONE step listing every file in
expectedArtifact."* That licence exists for a **conjunction** — one logical change touching three files,
all three of which will exist afterwards, all three correctly probed and all three correctly `found`.
The planner is instead using it for a **disjunction** — two candidate names, one of which will exist.

So the defect is not "multiple names." It is that the field cannot distinguish *all of these* from *one
of these*, and the probe assumes the former. **If P1 removes the disjunction, candidate-level counting
becomes correct by construction** and P3 may be unnecessary — which is why P1 precedes it and P3 is
conditional. Building the counter first would be fixing the symptom of a prompt defect in C#.

## 3. Scope — seven rows

None is on the checklist. P7 adds them.

### P1 — Forbid the disjunction, keep the conjunction *(Deps: none · XS · High)*

The `ExpectedArtifact` wording must say: name the artifact(s) this step **will** produce, all of which
must exist when it finishes; never offer alternatives; omit the field when there is no checkable
deliverable.

- Files: `src/Pia.Wpf/Services/AgentPlanner.cs`.
- **Three surfaces, not one.** `:159` is the tool-schema description, which `AIFunctionFactory` ships on
  **every plan and every replan turn** — it is the highest-leverage of the three and the only one that
  reaches both paths from a single edit. `:782` is the prose instruction on the plan turn. `:784` and
  `:827` are the verbatim conjunction sentence on plan and replan respectively; they are what makes
  "several files" legitimate and must survive.
- `BuildReplanMessages` (`:817`–`:830`) never mentions the field in prose. Do not add a fourth surface —
  the schema already covers replan.
- Decision (1) is the whole difficulty: the sentence that licenses the conjunction is one clause away
  from the one that must forbid the disjunction. Get both into the schema description without doubling
  its length.

### P2 — Re-run the four prompts and read the delta *(Deps: P1 · S · High)*

The pilot is the before-reading and the protocol is in §4. Replay the same four prompts on a fresh
throwaway profile against a build carrying P1, and report the same table.

- **The number that matters is not the ratio.** It is: how many probed declarations still contain an
  `or` / `e.g.` alternative. The pilot's was 4 of 4. Success is 0.
- Report `declared`, `fileShaped`, `notFileShaped`, `probed`, `found`, `notFound` per run *and* the
  distinct-artifact collapse, so the two readings are comparable.
- Decision (3): the planner is non-deterministic, so a different plan shape is likely and is not a
  failure. Say what you compared and on what basis.

### P3 — A per-declaration not-found counter, **only if P2 says it is still needed** *(Deps: P2 · XS · Med)*

If disjunctions survive P1, add a counter that registers a declaration as not-found only when **every**
candidate misses, alongside the existing per-candidate counts. If they do not survive, write down that
it is unnecessary and why, and do not build it.

- Files: `src/Pia.Wpf/Services/AgentVerifier.cs` (the tally is a `record struct` at `:372`, populated at
  `:456`, logged at `:310`), `tests/Pia.Wpf.Tests/Services/AgentVerifierTests.cs`.
- **Do not make this unconditional.** For a genuine conjunction, "not-found only when all miss" is the
  *wrong* rule: a step that owed three files and wrote one has partially failed and must register.

### P4 — Record the gate reading *(Deps: none · XS · High)*

The checklist still shows A1 unticked with the pre-pilot 23-declaration reading, and the runbook still
carries a conclusion the pilot refuted.

- `2026-08-22-hermes-followup-checklist.md`: tick **A1** with the 56% reading and its `n`; move **A4**
  ahead of A2 with the reason; note that A2's `Deps: A1` is satisfied but its priority dropped on the
  supply number.
- `2026-08-22-a1-log-collection-runbook.md` §6: strike the reading-table row that says a near-zero
  `notFound` proves the channel cannot produce a negative signal. It can; the pilot did; the verdict is
  simply unreadable at candidate granularity.
- Do **not** renumber or re-date either file. Both are dated when written.

### P5 — Fold the three collection traps into the runbook *(Deps: none · XS · Med)*

The runbook's §4 loop is unsound as written, and the pilot hit all three:

1. `declared` accumulates across verify passes *and* carries replan twins — read the last line per run
   id, then collapse the pairs.
2. Runs execute **concurrently**. §4's "poll until the *N*th `Artifact probe:` line, then send the next
   prompt" mis-attributes: two pilot runs overlapped by 36 seconds. Attribute on the `[run <id>]`
   prefix.
3. Answer-only runs complete via the SingleTurn fallback with `offered=False` and emit **no probe line
   at all**, so a whole prompt category never enters the population.

Also correct §3's profile advice: `PIA_DATA_DIR` does **not** isolate the vault or the assistant files
folder. The vault is `<AssistantFilesFolder>\Vault` (`Bootstrapper.cs:310` → `AssistantWorkspace.cs:37`)
and that folder is a settings value, so a throwaway profile must repoint `AssistantFilesFolder` too.
§4's `DefaultWindowMode` requirement is in §4 of this brief.

### P6 — Re-measure report-channel supply *(Deps: P2 · XS · High)*

From P2's run, count `artifactReported=True` over all step outcomes. The pilot's was **2 of 17**. A
planner told to name checkable artifacts may change what steps report, in either direction.

This is the number that sizes A2, and it is the only evidence that can justify or kill its `S`.

### P7 — Write the A2 recommendation and update the checklist *(Deps: P6 · XS · High)*

A short section in the pilot-reading doc — or a successor next to it — that states, with P2's and P6's
numbers: build A2, defer it, or drop it. Then add rows P1–P7 to the checklist and tick what landed,
verified against `git diff` rather than trusted.

**Do not decide this in advance.** If supply stays near 12%, A2 routes a channel that is empty seven
times out of eight through the probe, and the honest recommendation is to drop it and reopen A7's
question separately.

## 4. The measurement protocol

This reproduces the pilot exactly. Deviating from it breaks the before/after.

**Profile.** A throwaway root (`C:\temp\pia-a4`), seeded from the real `settings.json` so the providers
and their DPAPI-encrypted tokens work — same user, same machine, so they decrypt.

```powershell
$p = 'C:\temp\pia-a4'
foreach ($d in @("$p\roaming","$p\local","$p\files")) { New-Item -ItemType Directory -Force $d | Out-Null }
$j = Get-Content "$env:APPDATA\Pia\settings.json" -Raw | ConvertFrom-Json
$j.AssistantFilesFolder            = "$p\files"   # PIA_DATA_DIR does NOT cover this
$j.DefaultWindowMode               = 1            # 1 = Assistant; 0 opens Optimize, which has no Assistant nav item
$j.AssistantAgentModeDefault       = $true        # skips the Chat/Agent lever
$j.AgentRunAutoApproveBuiltInWrites= $true        # else every write parks and the run never drains
$j | ConvertTo-Json -Depth 100 | Set-Content "$p\roaming\settings.json" -Encoding utf8NoBOM
```

**Launch** a **Debug** build through WinWright with
`env = { PIA_DATA_DIR = "$p\roaming"; PIA_LOCAL_DATA_DIR = "$p\local" }`. Logs land in
`$p\local\Logs\pia-*.log`. Pass `mainWindowSelector=automationId=Assistant_Send` so the launch call
blocks until the Assistant view is actually up.

Release works for the tally but **not** for the facts block — `SensitiveDebug` is `[Conditional("DEBUG")]`
(`src/Pia.Wpf/Logging/SafeLog.cs:19`), and the per-declaration lines are the only place the disjunction
is visible. P2 needs Debug.

**Drive.** Per prompt: `ww_set_value` on `automationId=InputTextBox`, then `ww_invoke` on
`automationId=Assistant_RunInBackground`. The button is disabled until the composer is non-empty.

**The four prompts, verbatim** — one per runbook category, do not substitute:

```
Draft a short README.md for a project called "Ledger" describing what it does, how to install it, and how to run it.

Create a todo for each of: renew the domain, back up the vault, review the Q3 numbers. Set the domain one as high priority.

Compare two approaches to speaker diarization for meeting transcripts and tell me which one you would pick and why.

Explain the difference between my todos and my reminders and when I should use each.
```

**Harvest.** Group by run id; take the last probe line per run; then collapse replan twins by hand off
the facts block.

```powershell
$log = "C:\temp\pia-a4\local\Logs\pia-$(Get-Date -f yyyy-MM-dd).log"
Select-String $log -Pattern 'Artifact probe:'    # one per verify pass, NOT one per run
Select-String $log -Pattern 'artifactReported='  # supply, P6
Select-String $log -Pattern 'Working subpath did not resolve'  # must be zero, else notFound is instrument error
Select-String $log -Pattern 'Artifact probe skipped'           # must be zero
```

A run that fails or is cancelled emits no probe line — `AgentRunOrchestrator` breaks before verify — so
cap the wait and move on rather than blocking. Expect roughly two minutes per run and up to three verify
passes on a run that replans.

## 5. Constraints

Read `CLAUDE.md` in full first. The ones that bite here:

- **Privacy logging.** A declared artifact path is user content. The per-declaration facts are at
  `SensitiveDebug` for that reason and must stay there; the tally is release-safe **only** because it
  logs integers. Never add a name, a path or a declaration to a `LogInformation`. Do not hand
  `ProbeDeclarations` or `Probe` a logger — they are `static` and logger-free on purpose.
- **Comment discipline.** Default to no comment; one short line when the WHY is non-obvious. **Never
  cite a task, batch, gate, spec or ticket ID in source or XAML** — no "P1", no "A4", no "§3". Existing
  files in this area violate this (`AgentVerifier.cs` carries "H1"); do not imitate them and do not go
  on a cleanup spree either.
- **Zero-warning policy.** `TreatWarningsAsErrors=true`. Verify with a **rebuild**, both configurations:
  `dotnet build -t:Rebuild -v:n` and again `-c Release`. Read the count off MSBuild's `N Warning(s)`
  line, not by grepping (`-v:n` prints each warning twice).
- **Test gate.** `dotnet test`, no filter, bar is `failed: 0`. Baseline is 4655/0. Do not carry forward
  the old `--filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"` — that namespace is gone and
  the flag is a silent no-op.
- **Documentation layout.** Docs live in `docs/<topic>/`, `YYYY-MM-DD-<slug>.md`, dated when written —
  the date does **not** change when a doc is revised, so P4 and P5 edit the checklist and the runbook in
  place under their existing names. Links between docs in one folder stay relative.
- **Line endings.** The repo is CRLF. The `Write` tool emits LF, and `sed -i` in Git Bash rewrites a
  whole file to LF. Convert back and verify: `grep -c $'\r$' <file>` must equal `wc -l`.
- **PowerShell is not syntax-checked by writing it.** The previous batch shipped a `.ps1` that could not
  parse, through five review dimensions and 32 refuters, because the authoring machine had no `pwsh`.
  Parse-check anything you write:
  `[System.Management.Automation.Language.Parser]::ParseFile($p,[ref]$t,[ref]$e)`, then inspect `$e`.

## 6. Workflow shape

1. **Plan** — grounded by reading the source, not this brief. Must answer §0's three decisions in writing.
2. **Implement** — P1 (planner), P4 + P5 (docs) are disjoint and parallel. P3 is gated on P2 and must not
   start early.
3. **Build gate** — serialized, Debug then Release, driven to zero.
4. **Measure** — P2 and P6, on a Debug build, per §4. This is the row that cannot be delegated to a
   reviewer's judgment: it is a number or it did not happen.
5. **Simplify** — quality only.
6. **Review** — five dimensions, two independent refuters per finding. Point them at two things: any new
   log line that could carry user content into Release, and whether P1's wording actually forbids a
   disjunction rather than merely discouraging it.
7. **Fix** — apply what survived, rebuild both configurations, re-run the gate.
8. **Finalize** — P7. Checklist rows added and ticked against `git diff`.

## 7. Done means

- `ExpectedArtifact`'s schema description and the plan-turn prose forbid alternatives and preserve the
  multi-file conjunction, and the replan turn inherits it through the schema (P1).
- A four-run replay on a Debug build, reported as a table next to the pilot's, with the count of probed
  declarations still containing an alternative — the pilot's was 4 of 4 (P2).
- P3 built with a stated reason, or **not** built with a stated reason. Both are acceptable outcomes;
  silence is not.
- A1 ticked with the 56% reading, A4 re-sequenced ahead of A2, and the runbook's refuted reading-table
  row struck (P4).
- The runbook's three collection traps and its profile-isolation error corrected (P5).
- A supply number from the new runs and an A2 recommendation that follows from it (P6, P7).
- `dotnet test` no filter at `failed: 0`; both configurations rebuild clean.
- Nothing committed unasked. One commit per row group; checklist ticks ride in the commit that earns them.

## 8. What stays closed

**A2** (route `ArtifactRef` through the probe), **A6** (`IArtifactProbe`), **A7** (todo/reminder/vault
probes + typed prefix), and review **#13** (reject a plan step whose `ExpectedArtifact` is unprobeable
prose).

Two warnings for whoever opens them later:

- **#13 has no seam.** `ValidatePlan` (`AgentPlanner.cs`, the `:637`–`:651` region) is all-or-nothing and
  a `false` return degrades the entire plan to the SingleTurn fallback. Rejecting one prose artifact
  today means throwing away the plan. P1 is the cheap approximation of #13 and may retire it outright.
- **A6 is mis-sorted as an enabler.** Its acceptance criterion — "behaviour-preserving refactor of
  today's probe" — conflicts with A7, which needs an async seam (`ITodoService` and `IReminderService`
  are `Task`-returning while `Probe` is synchronous inside a `Task.Run` with a 2 s box). Land it with
  A7 or not at all.
