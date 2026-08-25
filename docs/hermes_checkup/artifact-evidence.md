# Artifact evidence — the A track's decision record and collection protocol

**Status:** living reference, rewritten in place each time A2 is re-measured — which is why it carries no
date in its filename. Gate **A1 is answered** (56% file-shapedness; it did not close). **A2 is deferred
inside its pre-registered band**, last read 22.0% over 13 runs. Part II is executable cold on a Windows
desktop.
**Owner:** unassigned. Only the `NOT FOUND` half needs a desktop session — the file-shapedness half comes
off the database offline, from any machine that has PowerShell 7 (§1).
**Written:** 2026-08-22 as the A1 log-collection runbook. Folded 2026-08-25 to carry the whole A track:
nine A docs (the two briefs, the plan, the batch run, the pilot, the disjunction brief, the replay
reading, the wide read and the P9 reading) were deleted into it.
**Origin:** gate **A1** and rows A1–A9 / P1–P9 of
[`2026-08-22-hermes-followup-checklist.md`](2026-08-22-hermes-followup-checklist.md), which came from
§3.6(a) of [`2026-08-22-hermes-update-review.md`](2026-08-22-hermes-update-review.md).

**How to use this file.** Part I is the decision record — the band a new number is judged against, the
numbers already read, and what they settled. Part II is the protocol for taking a new reading; it is
self-contained, and its `§N` cross-references are its own. Part III is what is still open, and what was
decided against building so it is not re-proposed.

| Question | Where it stands |
|---|---|
| **A1** — is `ExpectedArtifact` already file-shaped enough that a second channel buys nothing? | **Answered.** 56% against the ≥85% it needed. The gate did not close, so A2–A7 stayed open. |
| **A2** — route `ArtifactRef` through the existing probe | **Deferred inside the band**, 22.0% of step outcomes. Neither trigger fired. |
| **A3** — tests for self-reported-but-missing | Closed behind A2; the case does not exist until A2 does. |
| **A4** — tighten the planner's wording | **Shipped** 2026-08-23 as P1, then P8. |
| **A5** — persist `ArtifactRef` | **Shipped**, and confirmed live. |
| **A6** — extract `IArtifactProbe` | Open, and **mis-sorted as an Enabler** — land it with A7 or not at all. |
| **A7** — todo / reminder / vault probes | Open, and nothing sizes it today. |
| **P3** — a per-declaration not-found counter | **Decided and not built**, twice, with a named revival condition. |
| **P9** — a step that reported success on a failed write | **Answered**; no production change. |
| Review **#13** | Unbuildable as written, and it has no checklist row anywhere. |

---

# PART I — THE DECISION

## The A2 decision rule, pre-registered

The metric is `artifactReported=True` as a share of **step outcomes** — not of runs. Twelve runs is
roughly 25–35 step outcomes, so the denominator is read off the log, never assumed.

| Reading | Decision |
|---|---|
| **≥ 40%** of step outcomes | **Build A2.** The report channel carries enough to be worth probing. |
| **≤ 12%** of step outcomes | **Drop A2**, and reopen A7's question separately. The channel is empty seven times in eight. |
| **12% – 40%** | **A2 stays deferred.** Record the number, and name the specific observation that would move it out of the band. Not "gather more data". |

The middle band was written down on purpose, because it is the likeliest landing place and the earlier
readings (12%, 25%, 29%) all sit in it. A rule with a hole where the answer probably falls is not a rule.

### The two BUILD triggers, and the one DROP trigger

- **Build A2** if a corpus of comparable size shows `artifactReported` clearing 40%.
- **Build A2** if a single run is found where **the report channel names an artifact the probe never
  saw.** This is the trigger worth watching, because it is the only thing A2 can do that the probe
  cannot — and the 2026-08-23 corpus produced its exact opposite: on the failed run `2dcc6fd2` the probe
  had three declarations to classify while the report channel fired on two steps that produced nothing
  checkable.
- **Drop A2**, and reopen A7's question separately, if the share falls back toward 12% **on a mix not
  dominated by file-producing prompts.** That is the honest risk: 9 of the 13 runs declared a file, which
  is a property of §5's six categories, not of a real day's work.

### Pre-registration — what git holds, and what it does not

The wide read's authority rests on the rule having been fixed before the number came back, and that is
checkable rather than asserted:

| Claim | Commit | What it proves |
|---|---|---|
| The band and the triggers above were fixed **before the app was opened** | **`4d59e476`** — *"Pre-register the wide A read before the app is opened"* | §1–§5 of the wide read, including this band, were committed first. This is the only reason 22.0% can be quoted against a band at all. |
| The replay arm was **scoped** before it ran | **`f7e45008`** — *"Scope the follow-on batch: kill the disjunction, then re-measure"* | Its four verbatim prompts, its protocol and its hold were prior. |
| The replay reading's own §3 criterion | **`e2facf0d`** — *"Read the two-arm replay…"* | It landed **with** the results, not before them. **The replay reading was not pre-registered**, whatever its front matter claimed; only its batch brief was. |

Two more resolve and are cited below where they matter: `52bb6163` (P1 landed, gate A1 ticked at 56%) and
`a110af19` (P8 landed). **`741d3aed`, `2dcc6fd2`, `fd561138` and `be5767e1` are agent run ids from the
`[run <id>]` log prefix, not commits** — do not present them as provenance.

### Do not average across the tally

Three cuts separate the readings, and every number has to say which side of each it came from:

1. **The counts-only tally** changed what an `Artifact probe:` line carries, so the hand-counted 57/43
   first read is not comparable to anything after it. Of the tally's fields, only
   `notFileShaped / declared` is even loosely comparable to that 43%.
2. **P1** changed the planner's wording, so file-shapedness became partly a property of the prompt.
3. **P8** changed it again, after the replay's numbers were read and written down.

That is why the band above is written against **absolute** thresholds and never against a movement.

## What has been read

| Corpus | Provider | `artifactReported` | Share | File-shapedness |
|---|---|---|---|---|
| Historical logs, 2026-08-22, pre-tally | one client, 39 log files, 7 probe lines | — | — | 57% `found` / 43% not-a-file over 23 declarations, hand-counted |
| Pilot, 2026-08-23, 4 runs | Pia Cloud (a fallback, not a choice) | 2 / 17 | 12% | **56%** — 9 distinct artifacts, 5 file-shaped |
| Replay pre-P1 arm, 2026-08-23 | Mistral Medium 3.5 | 2 / 8 | 25% | — |
| Replay post-P1 arm, 2026-08-23 | Mistral Medium 3.5 | 2 / 7 | 29% | 6 of 6, collapsed |
| **Wide read, 2026-08-23, 13 runs, post-P8** | **Mistral Medium 3.5** | **9 / 41** | **22.0%** | raw 85.7%; **78.6%** probe-comparable, **94.4%** planner-behaviour |

**A2 stays deferred: 22.0% is inside the 12–40% band.** Neither trigger fired — it is not near 12%, so
the channel is not empty enough to drop; it is not above 40%, so it is not full enough to justify an `S`.
The count is also no longer 2, which is what made the first three readings unable to size anything at all.

**The wide read's collapse, and why there are two denominators.** Twins are collapsed by hand off
`AgentSteps.ExpectedArtifact`, never off the log: `bc499b6c` declared `README.md` on two step rows,
`b9e115c1` declared `todo.md` twice, `3430f7a7` declared `migration_scope_definition.md` twice, and
`18604136` declared `outdated_preferences.md` three times plus `all_preferences_raw.md` once. **78.6%
(11 of 14)** is what the probe actually saw; **94.4% (17 of 18)** is every declaration the planner
persisted, including the ones no verify pass reached. Both are honest and they answer different
questions; the gap between them is trap 2 below, not noise.

**P8's before/after, the strongest single result on the track.** No declaration in the whole corpus
carries a rooted path; every file-shaped one is a bare filename.

| | Pre-P8 (post-P1 replay arm) | Post-P8 (`bc499b6c`) |
|---|---|---|
| declared | `/Ledger/README.md` | `README.md` (twice, one step row each) |
| `write_file` | refused — *"Path is outside the assistant files folder"* | wrote it |
| probe | `found=0 notFound=1 unresolvable=2` | `found=2 notFound=0 unresolvable=0` |
| on disk | nothing | `README.md`, 0.3 KB |

`n = 1` per arm, and the alternative reading — that the goal naming the project was enough on its own —
is not excluded by it. But the clause cost nothing, it reaches the replan turn that produced the defect,
and the failure did not recur.

**Declarations still offering alternatives: 0.** Named denominator, because trap 7 disqualifies the
obvious one: **0 of the 41 non-null `AgentSteps.ExpectedArtifact` rows**, which is the untruncated source.
Not one string contains ` or `, `e.g.` or a `|`. Two contain a comma —
`verdict_document.md, judgment_document.md` and `revised_explanation.md, comparison_table.md` — and both
are **conjunctions**, the one multi-name form P1 still licenses.

### The gate answer, as history

**This records a decision already taken. It is not a rule anything can still trigger.** The table below
used to sit in §6 as a live prospective rule; it survives because the thresholds explain the numbers.

| The rule, as written in advance | What was read | What it decided |
|---|---|---|
| file-shapedness **≥85%** on an unbiased mix → the gate closes, drop A2–A7 | **56%** — 9 collapsed distinct artifacts, 5 file-shaped, pilot 2026-08-23 | **The gate did not close.** A2–A4, A6 and A7 stayed open. Ticked at 56% in `52bb6163`. |
| file-shapedness around half → A2–A4 stand, proceed | the same 56% | This is the arm that fired. A4 was promoted from an afterthought to the first row built, and shipped as P1 then P8. |
| `NOT FOUND` turns out to be common → A2 matters *less* | `notFound` read **4** | **Spent, and it never meant what the row assumed.** All four came from declarations naming *alternatives* — `(e.g., A or B)` — where the step wrote one of the pair. Every file existed and every step succeeded. |

**A non-zero `NOT FOUND` is not automatically a real one**, and a zero is not a property of the
instrument. `found` and `notFound` count **candidate paths**, so a two-name disjunction contributes one of
each however well the step performed — `notFound=4` there and `notFound=4` on a run where four artifacts
were never written are the same line. Two corpora have since produced a *true* negative: the replay's,
and `d09af076` in the wide read, which declared three artifacts of which `dotnet-latest-release-notes.md`
genuinely was not on disk. The old inference that this channel is structurally incapable of saying no is
dead; do not carry it into an A2 decision.

The 85% threshold was calibrated on the pre-P1 field, and 56% and 78.6%/94.4% sit on opposite sides of
all three cuts above. They are separate baselines, not a movement.

## Predictions versus outcomes

Three dropouts were predicted in advance. **One was half right, two were wrong, and three outcomes were
unpredicted.** This ledger is kept whole — wrong predictions included — because its value is the habit of
writing them down first, and a record that keeps only the predictions that survived teaches the opposite
habit.

| Prediction | Outcome |
|---|---|
| 7 and 8 (vault) find nothing | **Half right.** 7 (`bd61e53e`) **Failed** — harder than "finds nothing". 8 (`3430f7a7`) **completed**, by *inventing* `migration_scope_definition.md` rather than reporting an empty vault. |
| 4 (diarization) parks as ungroundable | **Wrong.** `314b972c` emitted `cannotGround: true` on its early plan turns — twice, asking *"What are the two specific speaker diarization approaches you want me to compare?"* — then planned anyway and completed with two file-shaped declarations. The two-arm replay saw this prompt park on both arms; the wide read did not. |
| 1 (empty folder) is a legitimate "nothing to declare" | **Wrong.** It declared `folder-inventory.md` and wrote it, twice over. An empty folder is something to write *about*, not a reason to declare nothing. |

Unpredicted, and each one a finding:

- **10 (kanban, `697ab5ad`) parked as ungroundable** — no plan, no steps, asking *"Where is the kanban
  board data stored?"*. The parking category moved from B to E between the replay and the wide read, on
  the same provider. **Which prompt parks is not a stable property of the prompt.**
- **12 (`2dcc6fd2`, Agent vs Chat) Failed**, and it is the *only* source of prose declarations in the
  corpus — its first three are sentences, its last two comma conjunctions of real filenames. The run that
  failed is also the run whose planner drifted furthest from P1's wording.
- **11 (`fd561138`) completed, was verified twice, and emitted no probe line at all**, because it declared
  nothing. The answer-only control behaving exactly as designed is **structurally invisible** to every
  ratio here — a third mechanism beyond the two in trap 6.

**What the wide read cannot settle.** 13 runs, one machine, one provider, one afternoon, one operator —
an existence proof, not a rate. One prompt ran twice, so the twelve-prompt mix is weighted 2× on category
A #1. Category D had no vault to read, by construction, because the throwaway profile redirects
`AssistantFilesFolder` and the vault is a subfolder of it; its two runs are still in every total, one
failed and one invented an artifact. The corpus ran against a copy of the real profile's chat and todo
history, which is a strength for realism and a weakness for reproducibility — nobody else's machine
reproduces those runs.

---

# PART II — THE PROTOCOL

How to take a fresh reading. Self-contained: everything needed to run it is below, and the `§N`
references in this part point inside it.

## 1. What this produces, and the cheap route first

One `Artifact probe:` line per verify pass carries the whole tally. It has two halves and they cost
wildly different amounts:

- **File-shapedness** — a property of the declaration string alone, so it is recoverable **offline**
  from the persisted `AgentSteps.ExpectedArtifact` rows over a machine's whole history. Minutes, no app
  launch.
- **`found` versus `NOT FOUND`** — a property of the filesystem at the moment the run settled, so it
  needs live runs on a Windows desktop. §4 is how to produce them.

Three properties of the sample never go away, whatever the route:

- **Probe lines are not runs, and declarations within one run are not independent samples.** A
  verify-fail replan re-enters the drain loop (`AgentRunOrchestrator.cs:530-539`) and verifies *again*
  over a completed-step list that is only ever appended to — `RunContext._completed` is written at
  `RunContext.cs:145` and `:168` and never cleared — so the second probe line of a run re-declares
  everything the first one declared. A resume does the same across segments: pre-pause steps are seeded
  into the fresh context at `AgentRunOrchestrator.cs:941`.
- **The population is conditioned on eventual success** (§2.5), which is the population least likely to
  be holding a missing artifact.
- **A declaration can name several files.** The pre-tally corpus summed to a clean count only because it
  was code-shaped — one filename per declaration. On §5's mix that identity breaks; §6's field glossary
  says how.

**Do the offline half first — it needs no desktop, no build and no new runs.**
`scripts/Measure-ArtifactDeclarations.ps1` replays the persisted strings through its own copy of the
probe's classifier and prints the file-shapedness split:

```powershell
./scripts/Measure-ArtifactDeclarations.ps1 -SelfTest    # classifier parity check; no database needed
./scripts/Measure-ArtifactDeclarations.ps1              # all history
./scripts/Measure-ArtifactDeclarations.ps1 -SinceDays 30 -OutputPath ~/artifact-counts.json
```

`-SinceDays 0` means all history; `-Force` allows overwriting `-OutputPath`; `-DatabasePath`,
`-Sqlite3Path` and `-CasesPath` override discovery. Output is three sections — `DECLARATIONS`,
`FILE-SHAPEDNESS`, `BY STEP STATUS` — counts only, and it hard-refuses any `-OutputPath` inside this
repository with no override switch. The database is opened read-only, so it is safe to run while Pia is
open. Run `-SelfTest` first: **the script has never produced a recorded number**, which also makes it the
cheapest unclaimed measurement on the whole track.

Three bounds on what it can mean:

- A replan **deletes** every step row that is not `Done` or `Skipped`, so declarations that were
  replanned away are gone and the sample is biased toward steps that survived to the end.
- **`found` versus `NOT FOUND` is not recoverable this way** — the filesystem has moved on, and a per-run
  workspace is torn down when the run settles.
- Deleting a chat **cascades** `AssistantChats → AgentRuns → AgentSteps` with foreign keys enforced, so
  "nothing ever deletes a run row" holds for explicit deletes only. A deleted chat takes its declarations
  with it.

It needs **PowerShell 7** (`pwsh`). So run the script for the ratio, and book a desktop session only for
the `NOT FOUND` row. §4.3 is the cheap way to produce that row; §4.1–§4.2 is the expensive one.

## 2. The probe only fires on one path

Four gates, all of which must be open — and every one of them is a property of the **interactive** route
(§4.1–§4.2). The scheduled route in §4.3 opens §2.2, §2.3 and §2.4 by construction and leaves only §2.5 to
get wrong. The build requirement that used to head this list is gone; §2.1 says why.

### 2.1 Either configuration carries the tally — Debug only for the declaration text

The outcome tally is a plain `LogInformation` (`AgentVerifier.cs:309-312`) and the file sink's `MinLevel`
is `Information` in Release (`Bootstrapper.cs:359`), so the counts §6 harvests reach `pia-*.log` on **any**
build:

```
Artifact probe: declared=6 fileShaped=4 notFileShaped=2 overReportCap=0 probed=5 found=3 notFound=1 folder=1 unresolvable=0 uninspectable=0 overPathCap=0
```

Build **Debug** only when you need the per-declaration *text* — which declaration produced which outcome.
That block is `SensitiveDebug` (`AgentVerifier.cs:313`), and `SensitiveDebug` is `[Conditional("DEBUG")]`
(`src/Pia.Wpf/Logging/SafeLog.cs:19`), so the C# compiler **erases it from Release IL entirely**: no log
level brings it back, and `Bootstrapper.cs:351` raises the level only under `IsDevMode`, which is itself
`#if DEBUG`.

```powershell
dotnet build                      # Debug is the default; do not pass -c Release
```

Read that block on the machine and paste none of it anywhere. It is not the source of record for
declaration *strings* — the log truncates tool-call arguments (trap 7), so those come from
`AgentSteps.ExpectedArtifact` in the database, which is also the only way to size **A7**. §6 says why
the counters deliberately cannot.

### 2.2 Agent mode, not Chat

Only a Planned run reaches `AgentVerifier`. The composer's `Chat | Agent` lever drives
`AgentModeEnabled`, persisted as `AppSettings.AssistantAgentModeDefault` (default **false**). Both
segments are id-addressable: `Assistant_Mode_Chat` (`Views/AssistantView.xaml:525`) and
`Assistant_Mode_Agent` (`:531`).

The lever is disabled unless the active persona has tools — the whole segmented control binds its
`IsEnabled` to `ActivePersona.ToolScope` (`:505-511`) and its tooltip says *"Agent mode needs a persona
with tools."* Pick a tool-capable persona and a tool-capable provider first; the small warn dot on the
Agent segment means *"This provider may not plan reliably."*

### 2.3 Use **Run in background**, not Send

`AgentRunOrchestrator.cs:250`:

```csharp
if (plan.Steps.Count >= 3 && executor.SupportsPlanApproval)
    await ParkForPlanApprovalAsync(...);   // parks at "plan-approval" and returns
```

`SupportsPlanApproval` is `true` only on `LiveTurnExecutor` (`ViewModels/Models/LiveTurnExecutor.cs:91`);
the interface default is `false` (`Services/Interfaces/IAgentTurnExecutor.cs:268`). So:

- **Send** (`Assistant_Send`) in Agent mode → any plan of 3+ steps stops dead and waits for you to approve
  it.
- **Run in background** (`Assistant_RunInBackground`, the green button, present only in Agent mode) →
  headless executor, no approval park, runs straight through.

Run-in-background is the highest-yield action in this whole document: one click, one completed run,
one probe line, no babysitting. The scheduled route in §4.3 never reaches the park either, for the same
reason — it dispatches through the same headless executor.

### 2.4 Turn on auto-approve for built-in writes

`AppSettings.AgentRunAutoApproveBuiltInWrites` defaults to **false** (`Models/AppSettings.cs:232`).
With it off every write tool call parks for a per-call decision, so an unattended run never drains.

Settings → Assistant → Agent runs, checkbox
`Settings_Assistant_Agent_AutoApproveBuiltInWrites`. (The same property is rendered a second time on
the Tool access tab as `Settings_Assistant_ToolPermissions_AutoApproveBuiltInWrites` — either one moves it.)

Read the label before ticking it: *"a run with this permission can overwrite files in your assistant
folder unattended."* Deletes, Git and MCP tools are never covered by it. This is a reason to prefer the
throwaway profile in §3.

A scheduled **Agent run** routine does not need this setting for *file* writes: the job's granted-tools
list becomes the run's pre-granted writes, and an empty list still resolves to `{write_file}`
(`ScheduledJobBackgroundService.cs:516` → `HeadlessRunLauncher.cs:336`). Every *other* built-in write still
parks — §4.3 owns that caveat, and it is the one that decides whether a routine produces a probe line at
all.

### 2.5 Let runs finish

`AgentRunOrchestrator.cs:516-517` breaks out of the loop **before** verify on a cancel or an unrecovered
step failure. A run you stop yields nothing. Give each one time to settle.

Three consequences to hold while reading the numbers:

- A step that failed and was **replanned past** *is* in the probed set — `ctx.RecordStep` at `:491` runs
  before the `!r.Succeeded` check at `:506`. The sample is not "only clean steps"; it is "only runs that
  ended well".
- A **verify-fail** replan verifies again, so one run can emit several probe lines (§1).
- A run that **declares nothing logs nothing**: the probe returns before any line when no completed step
  carries a non-blank `ExpectedArtifact` (`AgentVerifier.cs:267-269`). A missing line is not a failure.

The files folder needs to be set and to exist, or the probe logs `Artifact probe skipped`. There were
zero such lines in the existing corpus, so a normal profile is already fine.

---

## 3. Profile setup

The `tests/ui-scripts/` harness runs hermetically against `fixtures/settings.ui-test-seed.json` — but
that fixture has **six keys and no provider configuration**, so it cannot do an agent run at all. It is
built for settings walkthroughs, not for this. Two workable modes instead:

### Mode A — a named throwaway profile that carries your providers (recommended)

Isolates the chat history, the todos and the assistant files this exercise creates, while keeping the
provider credentials that make an agent run possible. DPAPI-encrypted tokens in `settings.json` decrypt
fine because it is the same user on the same machine.

**Copying `settings.json` is not enough**, twice over.

`PIA_DATA_DIR` does **not** isolate the vault or the assistant files folder: the vault is
`<AssistantFilesFolder>\Vault` (`Bootstrapper.cs:310` → `AssistantWorkspace.cs:37`) and that folder is a
**settings value**, so an unedited copy points every file an agent run writes back at the real one.
Three more keys have to be overridden, or the runs park and the profile is not throwaway.

And the **providers live in `providers.json`, not in `settings.json`** — `settings.json` holds only the
provider *id* the mode defaults to. Copy it alone and that id resolves to nothing: the app writes a
fresh `providers.json` carrying the built-in Pia Cloud entry and quietly runs on that instead. If Pia
Cloud is not signed in, every run then dies in the plan turn on a 401 and emits no probe line at all.
That is a wasted session, and it is what produced the pilot's "one provider (Pia Cloud)" — a fallback,
not a choice. Verified after the fact: the pilot's throwaway `providers.json` holds exactly one entry,
the auto-created Pia Cloud, while the real profile it was seeded from holds five.

```powershell
$p = "C:\temp\pia-a1"
foreach ($d in @("$p\roaming", "$p\local", "$p\files")) { New-Item -ItemType Directory -Force $d | Out-Null }
Copy-Item "$env:APPDATA\Pia\providers.json" "$p\roaming\providers.json"   # NOT optional — see above
$j = Get-Content "$env:APPDATA\Pia\settings.json" -Raw | ConvertFrom-Json
$j.AssistantFilesFolder             = "$p\files"   # PIA_DATA_DIR does NOT cover this
$j.DefaultWindowMode                = 1            # 1 = Assistant; 0 opens Optimize, which has no Assistant nav item
$j.AssistantAgentModeDefault        = $true        # skips the Chat/Agent lever in §4.2 step 3
$j.AgentRunAutoApproveBuiltInWrites = $true        # else every write parks and the run never drains (§2.4)
$j | ConvertTo-Json -Depth 100 | Set-Content "$p\roaming\settings.json" -Encoding utf8NoBOM
```

Then launch through WinWright, handing it that profile through `ww_launch`'s **`env`** parameter — the
same move `tests/ui-scripts/README.md` documents for recording against a seeded profile:

```
ww_launch  <- src\Pia.Wpf\bin\Debug\net10.0-windows10.0.17763.0\Pia.Wpf.exe
           env = { PIA_DATA_DIR       = "C:\temp\pia-a1\roaming"
                   PIA_LOCAL_DATA_DIR = "C:\temp\pia-a1\local" }
           mainWindowSelector = automationId=Assistant_Send
```

`mainWindowSelector` makes the launch call block until the Assistant view is actually up.
Logs land in **`C:\temp\pia-a1\local\Logs\pia-*.log`**, not in `%LOCALAPPDATA%\Pia\Logs`.

> **Do not use `Invoke-UiScripts.ps1` for this.** It deletes its throwaway profile after a *passing*
> run — which would delete exactly the logs you came for. If you use it anyway, pass `-KeepDataDir`.

### Mode B — your real profile

Simplest, and the most representative of real settings. Just launch the Debug build normally; logs go
to `%LOCALAPPDATA%\Pia\Logs\pia-*.log`. The cost is real chat history, real todos and real files
written by 24 unattended agent runs. Only pick this if that is acceptable.

---

## 4. Producing the runs

Two routes reach the same probe line.

| Route | What it costs | Where |
|---|---|---|
| **Scheduled Agent-run routines** — create a few, walk away | ~10 minutes of editor work, then no attention | **§4.3 — start here** |
| **Interactive WinWright loop** — one invoke per sample | 24 prompts babysat one at a time | §4.1–§4.2 |

Both need a Windows box with Pia running. The scheduled route removes the babysitting, not the machine.

Selector reference: [`../ui_automation/ui-automation-playbook.md`](../ui_automation/ui-automation-playbook.md).
Read its ground rules first — `ww_click` returns success for no-ops, so verify every state change.

### 4.1 Selectors this flow needs

| Step | Selector |
|---|---|
| Assistant view | `automationId=NavItem_Assistant` (`ww_invoke`) |
| Settings | `automationId=NavItem_Settings` |
| Settings category | `ww_select(selector="automationId=Settings_CategoryList", optionText="Assistant")` |
| Agent tab | `ww_select(selector="type=TabControl", optionSelector="automationId=Settings_Assistant_Tab_Agent")` |
| Auto-approve | `automationId=Settings_Assistant_Agent_AutoApproveBuiltInWrites` |
| Composer | `automationId=InputTextBox` (ValuePattern) |
| Reply text | `ww_get_value(selector="automationId=MarkdownViewer")` — TextPattern |
| **Agent lever** | `automationId=Assistant_Mode_Agent` (Chat segment: `Assistant_Mode_Chat`) |
| **Run in background** | `automationId=Assistant_RunInBackground` |
| Send | `automationId=Assistant_Send` |

> These are real `AutomationProperties.AutomationId` values on `src/Pia.Wpf/Views/AssistantView.xaml`
> — `Assistant_Mode_Chat` (`:525`), `Assistant_Mode_Agent` (`:531`), `Assistant_RunInBackground` (`:557`),
> `Assistant_Send` (`:570`), `InputTextBox` (`:305`), `MessageScrollViewer` (`:111`) — all registered in
> [`../ui_automation/ui-automation-playbook.md`](../ui_automation/ui-automation-playbook.md). The flow no
> longer depends on the UI language, and `winwright heal` can revalidate every selector in it. If the Agent
> lever will not take, that is §2.2's persona-with-tools gate and not a selector problem: the whole
> segmented control is disabled. Swap the persona, not the selector.

### 4.2 The loop

Once per session:

1. `ww_launch` (Mode A env, or plain for Mode B).
2. `NavItem_Settings` → category *Assistant* → tab `Settings_Assistant_Tab_Agent` → tick
   `Settings_Assistant_Agent_AutoApproveBuiltInWrites`. Confirm with
   `ww_assert_value(property="value", expected="On")`.
3. `NavItem_Assistant`. `ww_invoke` on `automationId=Assistant_Mode_Agent`, then assert
   `ww_count("automationId=Assistant_RunInBackground") == 1`. The button's visibility is bound through
   `BooleanToVisibilityConverter`, so on Chat it is `Collapsed` and absent from the UIA tree entirely —
   its presence *is* the assertion that the lever moved.

Then per prompt, for each of the 24 in §5:

4. Set `automationId=InputTextBox` via ValuePattern to the prompt text. *Run in background* is disabled
   until the composer is non-empty, so a disabled button at this point means the set did not take.
5. `ww_invoke` on `automationId=Assistant_RunInBackground`.
6. Wait for the run to settle before the next one. Poll the log rather than the UI — it is cheaper and
   unambiguous, but poll for **this run's id**, not for a line count:

```powershell
$log = "C:\temp\pia-a1\local\Logs\pia-$(Get-Date -f yyyy-MM-dd).log"
$id  = '<the run id this prompt started>'   # from the run panel, or the newest [run <guid>] in the log
# -ErrorAction: the first poll of the session runs before the log file exists.
while (-not (Select-String -Path $log -Pattern "\[run $id\].*Artifact probe:" -ErrorAction SilentlyContinue)) {
    Start-Sleep -Seconds 15
}
```

Budget roughly two minutes per run, and up to three verify passes on a run that replans. A run that
fails or is cancelled never emits the line, so cap the wait (5 minutes is generous for these tasks)
and move on rather than blocking the session — a skipped prompt costs one sample, a wedged
session costs all of them.

#### Eleven collection traps

Assembled from every reading taken so far. They are **collection** defects, not analysis ones: get them
wrong and no amount of care downstream recovers the sample. Traps 1–6 and 9 apply to §4.3's unattended
route just as much as to the click loop.

1. **`declared` accumulates across verify passes.** The verifier reads `ctx.CompletedSteps`, which grows
   after each replan, so one run can emit `declared=3`, then `6`, then `7` (`741d3aed` did exactly that).
   Summing the lines triple-counts. Read the **last line per run id**.
2. **…and the last line can UNDERCOUNT.** `if (cancelled || failed)` sits immediately before the verify
   pass, so a run that fails after its last verify never re-probes, by design. `2dcc6fd2` persisted five
   declarations and its only probe line saw three; `18604136` shows the opposite direction, reading
   `declared=1 notFound=1` on an early pass before its file existed and `declared=4 found=4` on its last.
   Reconcile the probe line against the step rows before quoting a total, and expect **two legitimate
   denominators** — what the probe saw, and what the planner actually declared.
3. **A replan re-declares the same artifact against a new step row, so twins must be collapsed by hand.**
   The vague original and its concretized twin both survive into the final facts block, the original in
   `notFileShaped` and the twin in `fileShaped`. This is the entire difference between a raw 33% and the
   reported 56%; any harvest that counts step rows inflates the denominator. The pairing is unmistakable
   once seen — `741d3aed` step 1 *"Confirmation of domain renewal (e.g., receipt or updated expiry date)"*
   against step 4 *"Domain renewal confirmation (receipt or updated expiry date)"*, and five more like it
   across two runs.
4. **Probe lines within one run are not independent samples** (§1).
5. **Runs execute concurrently, so line order does not track prompt order.** Counting to the *N*th
   `Artifact probe:` line mis-attributes: `741d3aed` and `be5767e1` overlapped by 36 seconds. Attribute on
   the `[run <id>]` prefix, which is why the poll above waits on an id.
6. **A run can leave the population entirely, and which prompts do is not stable.** Every ratio the gate
   reads is conditioned on the run having produced a probe line, and **three** mechanisms remove one:
   an answer-only prompt (§5 category F) completing through the SingleTurn fallback with `offered=False`,
   no plan and no declarations; a prompt whose plan turn **declines as ungroundable** and parks for
   clarification; and a run that **planned, ran and was verified twice** and still declared nothing
   (`fd561138`). The parking category has been observed moving between §5 categories on the *same*
   provider between two sessions, so do not assume a category is invisible — check which prompts actually
   dropped out before quoting a denominator.
7. **The log TRUNCATES tool-call arguments, so never harvest declaration text from it.** On the
   2026-08-23 corpus **40 of 99** `emit_plan args:` lines ended in `…` — enough to lose every declaration
   of two runs whose probe lines showed declarations. Read `AgentSteps.ExpectedArtifact` out of the
   database instead: untruncated, and it is what the verifier itself reads. The `Artifact probe:` counters
   stay authoritative for *counts*; only the *strings* need the database.
8. **Count guard VALUES, not guard names.** Every probe line literally contains `overReportCap=0`, so a
   substring grep for the counter name reports one hit per run and looks like a censored sample. Match
   `=[1-9]`.
9. **The tally mixes two granularities on one line.** `declared`, `fileShaped` and `notFileShaped` count
   declarations; `probed`, `found`, `notFound` and the rest count candidate paths — 7 declarations against
   8 candidates on `be5767e1`. **A "found share" computed across the line is a share of nothing.** §6's
   glossary is the fix.
10. **An empty composer after a dispatch is the SUCCESS signal, not a failure.** `ww_set_value` on
    `InputTextBox` does work; but reading the composer back after `ww_invoke` shows it empty and *Run in
    background* disabled, because the dispatch cleared it. Reading that as "the set did not take" cost the
    2026-08-23 corpus a duplicate run on one prompt. Verify a dispatch by the run row appearing, never by
    the composer's contents afterwards.
11. **Two that silently ruin a "runs since I started" filter.** `AgentRuns.CreatedAt` is stored in
    **UTC**, so a baseline written in local time excludes everything. And a run can create `ScheduledJobs`
    rows of its own, which then fire and add runs nobody dispatched — the wide read's .NET-release prompt
    created five and each fired once before it was noticed. Filter on `TriggerKind`, and check for new
    jobs before blaming the loop.

Harvest before you walk away: the file sink rolls at 10 MB and keeps 7 files
(`Bootstrapper.cs:360-361`), so a long stretch can retire the file holding your earliest probe lines.
Pull the lines into a scratch file every three or four runs — a Debug corpus is verbose.

#### Five instrument guards — check these before reading anything else

The first three must read **zero**. A non-zero value in any of them means the numbers are instrument
error or a censored sample, never a finding.

| Grep | Must be | Why |
|---|---|---|
| `Working subpath did not resolve` | 0 | Otherwise `notFound` is the probe failing, not a miss. |
| `Artifact probe skipped` | 0 | A verify that produced no tally at all. |
| `Artifact probe failed` | 0 | The probe faulted or blew its 2-second budget. |
| `overReportCap=[1-9]` | 0 | Non-zero ⇒ declarations past `MaxReportedDeclarations` were never classified, so file-shapedness loses its tail. |
| `overPathCap=[1-9]` | 0 | Non-zero ⇒ candidates past `MaxProbedPaths` were never probed, so delivery loses its tail. |

The two cap counters are guards rather than footnotes because a six-category corpus makes censoring
materially likelier than a four-prompt pilot does. All five read zero on the 2026-08-23 wide read.

Do **not** record this with `ww_record`. A recording captures actions with no preconditions, it would
bake in the prompt strings, and replaying it a second time would double-write every todo it created.
This is a one-shot data-collection session, not a regression script.

If nothing here needs to be interactive, do not do it interactively: §4.3 produces the same probe lines
from a routine, at no clicks per sample.

### 4.3 The unattended route — a scheduled Agent-run routine

The cheapest sample generator is a routine, not WinWright. A routine whose **Kind** is *Agent run* is
dispatched as a headless Planned run: `ScheduledJobBackgroundService.cs:475` routes by kind, `:507` calls
the launcher, and `HeadlessRunLauncher.cs:488` enters the same `orchestrator.RunAsync` an interactive run
enters. It plans, drains and verifies on the identical path, and emits the identical probe line.

What that buys, gate by gate: no composer and no lever, so §2.2 does not apply; **no plan-approval park**,
because `SupportsPlanApproval` is `true` only on `LiveTurnExecutor.cs:91` and the 3+-step park is
`AgentRunOrchestrator.cs:250`; and **no auto-approve setting needed for file writes**, because
`ScheduledJobBackgroundService.cs:516` turns the job's granted tools into the run's pre-granted writes and
an empty list becomes `null` becomes `{write_file}` (`HeadlessRunLauncher.cs:336`,
`IHeadlessRunLauncher.cs:51`). §2.1, §2.5 and §3 still apply unchanged.

#### Creating one

`NavItem_Routines` → `Routines_NewJob`. The editor opens already set to **Agent run**
(`RoutinesViewModel.cs:424`), so a routine created from scratch needs no Kind change — only one started
from a blueprint card does.

| Field | Selector | How |
|---|---|---|
| Name | `Routines_Field_Name` | ValuePattern |
| Goal | `Routines_Field_Goal` | ValuePattern — one of §5's prompts |
| Kind | `Routines_Field_Kind` | `ww_select`; the option label is localized (*Agent run* in English) |
| Recurrence | `Routines_Field_Recurrence` | `ww_select` — `Daily` is the finest available |
| Time | `Routines_Field_Time` | ValuePattern, `HH:mm` 24-hour |
| Provider | `Routines_Field_Provider` | `ww_select` — must be tool-capable |
| Persona | `Routines_Field_Persona` | `ww_select` — must have tools, same requirement as §2.2 minus the lever |
| Granted tools | `Routines_Tools_Picker` | expand it, then `ww_set_checked` per `Routines_Tools_Allow_<tool>`; nothing ticked ⇒ `{write_file}` for an agent routine, `{}` for a research one |
| Quiet on success | `Routines_Field_Quiet` | tick it, or every run raises a Flow item and a toast |
| Save | `Routines_Save` | `ww_invoke` |

The time is exact-parsed. Anything that is not `HH:mm` refuses the save and writes a validation message
into `Routines_StatusMessage` (`RoutinesViewModel.cs:532-536`): nothing is persisted and the editor stays
open, so assert the editor closed rather than assuming a save happened. Quiet-on-success suppresses the
notification, not the record — the run's chat and the job's `LastFiredAt` are written either way
(`ScheduledJobNotificationSurface.cs:53-67`) — and a *failure* is never suppressed.

**Prove the wiring on one routine before creating twenty-four.** `Routines_JobList` → select it →
`Routines_RunNow` dispatches through the same leg as the scheduler tick, so one probe line from
`Routines_RunNow` proves the whole chain in seconds instead of at 09:00 tomorrow.

#### A schedule that actually produces samples

- **One routine per §5 prompt.** A Daily routine re-runs the *same* goal, so its days are repeats, not
  independent samples. The mix §5 insists on comes from having 24 goals, not 24 firings of one.
- **Stagger by ~15 minutes** inside a waking window. Two headless runs execute at once by default
  (`AppSettings.DefaultParallelBackgroundRuns = 2`) and a scheduled run's wall clock reaches 45 minutes
  (`ScheduledWallClockMinutes = 45`), so a tight cluster queues on the slot pool and its tail may not fire
  inside the window at all.
- **Leave Pia running and keep the box awake.** The scheduler is in-process, and a job that comes due while
  the app is closed becomes a missed run that asks a human yes/no once the grace window has passed — which
  is precisely the babysitting this route exists to remove.
- **Harvest daily.** The log rolls at 10 MB and keeps 7 files (`Bootstrapper.cs:360-361`).

#### What still parks — read this before choosing prompts

Only `write_file` is pre-granted. A todo, reminder, memory, kanban or vault write that is neither named in
the job's granted-tools list nor covered by an autonomy policy makes the run **park and wait for a
person**: the unattended gate's last arm returns `Park` (`ToolAutonomy.cs:139-145`), and a scheduled root
run is allowed to park (`HeadlessRunLauncher.cs:144`). A parked run never reaches verify, so it yields **no
probe line at all** — only a Continue card nobody is there to press.

Two fixes, either is fine: tick `Settings_Assistant_Agent_AutoApproveBuiltInWrites` once (§2.4), or name
the tools per routine in `Routines_Tools_Picker`. §5's categories **C** (todos and reminders) and
**E** (memory and kanban) are exactly the ones this bites, which is also why dropping them would bias the
ratio being measured. Keep deletes and MCP tools out of these prompts entirely: the gate refuses to *ask*
about a delete-like or external tool and denies it outright.

#### Where the run writes

A headless run works inside its own workspace, `%LOCALAPPDATA%\Pia\runs\<runId>`
(`AssistantWorkspace.cs:47`), and the probe stats **that** root — it prefers `ctx.WorkspaceRoot` over the
configured assistant files folder. So `found` on these lines means *found in the run's workspace*, which is
the right question for "did the step produce what it declared". What the run delivered is promoted into the
assistant files folder afterwards, leaving a second counts-only line worth harvesting:

```
Run <id> promoted 3 file(s), skipped 0, 0 conflict(s)
```

(`RunWorkspaceService.cs:494-496`.) One failure mode this route cannot hit: a headless run carries no
working subpath (`HeadlessTurnExecutor.cs:199`), so the probe-root divergence an interactive chat with a
working directory can produce is structurally absent.

#### The eight blueprint cards produce zero samples

Every card in `Routines_BlueprintList` ships `Kind: Research` (`RoutineBlueprint.cs:38`, `:58`, `:79`,
`:98`, `:119`, `:144`, `:167`, `:188`), and the Research leg never reaches the orchestrator — so it never
plans, never verifies and never logs a probe line. Starting from a card is fine, but flip
`Routines_Field_Kind` to *Agent run* before saving.

#### Triage — no probe line

Every line below is release-visible and carries ids, counts and enum names only.

| What you see | What happened |
|---|---|
| `Planner degrade → SingleTurn fallback` (`AgentPlanner.cs:220`) | no valid plan, so no steps and nothing declared |
| `Run <id> → WaitingForInput (paused)` (`AgentRunService.cs:350`) | parked — almost always a write the run was not granted |
| `Artifact probe skipped …` | the files folder is unset or does not exist (§2.5) |
| `Artifact probe failed …` | the probe faulted or blew its 2-second budget |
| `Scheduled agent job <id> failed: no provider available` (`ScheduledJobBackgroundService.cs:496`) | the routine's provider no longer resolves |
| `Scheduled job <id> not dispatched: a run of it is still executing` (`:462`) | the previous firing is still going — stagger wider |
| `Scheduled agent job <id> run did not complete: <state>` (`:587`) | it settled somewhere other than Completed |
| nothing at all | the run never reached verify, or it declared nothing (§2.5) |

#### The honest limit

This still needs a Windows machine with Pia running, so it does not answer *"without a human"* outright.
What it removes is §4.2's 24-prompt babysitting loop — the expensive part.

---

## 5. Prompt samples

**This section is the methodological core.** The ratio is only meaningful on an unbiased mix.

If you run a batch of *"write me a file"* tasks you will measure ~95% file-shaped and falsely close the
gate. The prose share is supposed to include the runs that legitimately produce no file — that is the
population being measured, not noise to be engineered away.

Six categories, four prompts each, 24 runs. **Run all six categories.** If you have to shorten the
session, drop one prompt from every category rather than dropping a category.

**The corpora already read, so a comparison is reproducible.** The 2026-08-23 wide read used twelve of
these — **A** 1 and 4, **B** 1 and 2, **C** 1 and 2, **D** 1 and 3, **E** 1 and 3, **F** 1 and 2 — two per
category, all six categories. Twelve runs on four prompts would not be twelve samples, which is exactly
why the pilot's four (**A** 4, **B** 2, **C** 2, **F** 1) were not reused. Prompt **A** 4, the Ledger
README, is the one prompt worth re-running on its own: it produced the rooted-path defect P8 fixed, so a
post-change observation on it means something even at `n = 1`.

Keep plans moderate in size. `MaxProbedPaths = 12` and `MaxReportedDeclarations = 20`
(`AgentVerifier.cs:238-239`), plus `MaxCandidatesPerDeclaration = 3` (`:240`), censor a long plan:
declarations past the report cap are never classified, and candidates past the path cap are never probed.
**Both of §6's ratios skew** — file-shapedness loses its tail to `overReportCap`, delivery loses its tail
to `overPathCap`. A nonzero value in either field says the sample is censored, so read those two counters
before quoting a share.

### A — File-producing (expect file-shaped declarations)

```
Write a one-page markdown summary of what my assistant files folder currently contains, grouped by file type, and save it as folder-inventory.md.

Create a CSV called weekly-hours.csv with columns Date, Project, Hours and seven example rows for last week.

Read every .md file in my assistant files folder and produce a single combined index.md linking to each one with a one-line description.

Draft a short README.md for a project called "Ledger" describing what it does, how to install it, and how to run it.
```

### B — Research and web (expect prose declarations)

```
Find out what changed in the most recent .NET release and summarise the three changes most likely to affect a WPF desktop app. Cite your sources.

Compare two approaches to speaker diarization for meeting transcripts and tell me which one you would pick and why.

What are the current recommendations for storing API credentials in a Windows desktop application? Give me the tradeoffs.

Look up what SQLite's WAL mode actually guarantees and explain whether it helps an app that writes from two processes.
```

### C — Todos and reminders (the `todo:` / `reminder:` shapes A7 would probe)

```
Go through my open todos, identify the three that have been sitting longest, and create a reminder for tomorrow morning to deal with them.

Create a todo for each of: renew the domain, back up the vault, review the Q3 numbers. Set the domain one as high priority.

Look at my reminders for the next seven days and tell me which ones collide with each other, then reschedule the collisions.

Turn my notes about the vendor call into a set of todos, one per action item, and tell me which ones have no owner.
```

### D — Vault, meetings and notes

```
Find the most recent meeting transcript in my vault and extract the action items. State how complete the transcript is before you extract anything.

Summarise the vault notes I wrote this month into a single "what I worked on" note.

Search my vault for anything mentioning the migration and tell me what the current state of it is.

Take the last meeting transcript and write a follow-up email draft to the participants.
```

### E — Memory and kanban

```
Look at what you remember about my working preferences and tell me which of them are now out of date.

Move every kanban card that has not changed in two weeks into a "stalled" state and tell me what you moved.

Review my kanban board and tell me which column is the bottleneck, with evidence from the card ages.

Remember that I prefer evidence-first summaries, then tell me what you now know about how I like things written.
```

### F — Answer-only (the control group — these *should* declare little or nothing)

```
Explain the difference between my todos and my reminders and when I should use each.

What can you actually do in Agent mode that you cannot do in Chat mode?

Walk me through what happens when I click "Run in background" — what runs, and where does the result go?

Is there anything in my current setup that would stop a scheduled routine from working?
```

---

## 6. Harvest

From the log directory (§3 tells you which one). One line per verify carries the whole tally, so the
harvest is a sum over its `key=value` pairs — on **any** build configuration (§2.1):

```powershell
Select-String -Path pia-*.log -Pattern 'Artifact probe: ' -SimpleMatch |
  ForEach-Object { [regex]::Matches($_.Line, '([A-Za-z]+)=(\d+)') } |
  Group-Object { $_.Groups[1].Value } |
  ForEach-Object { [pscustomobject]@{ Field = $_.Name
      Total = ($_.Group | ForEach-Object { [int]$_.Groups[2].Value } | Measure-Object -Sum).Sum } } |
  Sort-Object Field

# How many verifies produced a tally, and how many distinct runs they came from.
(Select-String -Path pia-*.log -Pattern 'Artifact probe:' -SimpleMatch).Count
(Select-String -Path pia-*.log -Pattern 'Artifact probe:' -SimpleMatch |
  ForEach-Object { [regex]::Match($_.Line, '\[run ([0-9a-f-]{36})').Groups[1].Value } |
  Sort-Object -Unique).Count

# Should be zero. Any hit is a verify that produced no tally at all.
Select-String -Path pia-*.log -Pattern 'Artifact probe skipped', 'Artifact probe failed'
```

The equivalent on a Unix box, once the logs are copied over:

```bash
grep -h 'Artifact probe: ' pia-*.log | grep -oE '[A-Za-z]+=[0-9]+' | awk -F= '{s[$1]+=$2} END {for (k in s) printf "%-14s %d\n", k, s[k]}' | sort
grep -h 'Artifact probe:' pia-*.log | wc -l
grep -h 'Artifact probe:' pia-*.log | grep -oE '\[run [0-9a-f-]{36}' | sort -u | wc -l
grep -hE 'Artifact probe (skipped|failed)' pia-*.log
```

The bash form was executed against synthetic probe lines and produces the sums; the PowerShell form was
not, because `pwsh` is not installed on the machine these docs were written on — read its output once
before trusting it.

The distinct-run count works because `ScopeRenderingLoggerProvider` prefixes the whole formatted message
with `[run <guid>] ` from the scope opened at `AgentRunOrchestrator.cs:154`, and the file sink is wrapped by
that decorator (`Bootstrapper.cs:381-383`). If a future change drops the decorator the run count silently
reads 0, so the **line** count is the backstop — record both. Note also what the pattern excludes:
`Artifact probe:` does not match `Artifact probe skipped` or `Artifact probe failed`, which is why those get
their own grep.

### Field glossary — two units, never mixed

| Field | Unit | Meaning |
|---|---|---|
| `declared` | declaration | completed steps with a non-blank `ExpectedArtifact` that reached the probe |
| `fileShaped` / `notFileShaped` | declaration | whether the classifier found any file-looking token |
| `overReportCap` | declaration | past `MaxReportedDeclarations` — **never classified**, shape genuinely unknown |
| `probed` | candidate path | paths actually stat-ed |
| `found` / `notFound` / `folder` / `unresolvable` / `uninspectable` | candidate path | the five probe outcomes |
| `overPathCap` | candidate path | file-shaped candidates the budget refused — **not part of `probed`** |

Three identities hold on every line, and checking them is how you know you read it right:

```
declared               == fileShaped + notFileShaped + overReportCap
probed                 == found + notFound + folder + unresolvable + uninspectable
file-shaped candidates == probed + overPathCap
```

`overPathCap` being **disjoint** from `probed` is the one that trips people: the per-candidate budget arm
records its fact and continues *before* the increment, and the per-declaration budget arm never enters the
candidate loop at all.

`probed` increments inside the per-*candidate* loop (`AgentVerifier.cs:427`), capped at 3 per declaration
and 12 per verify, so **`probed / declared` is a ratio of two different units and means nothing on its
own** — the only inference it ever supported was `probed == 0` versus `> 0`. And neither the line count nor
the run count is a count of runs that *tried*: a verify with nothing declared logs no line at all
(`AgentVerifier.cs:267-269`), while one run can log several (§1).

### Why the old shell pipeline is gone

Earlier revisions of this section parsed the per-declaration fact lines out of the Debug block and grouped
them into three buckets. The counters above are not a corrected version of that — they are a different
measurement, and the parser is deleted rather than fixed. The reasons, so nobody rebuilds it:

One fact line can carry **several** candidates joined by `"; "` (`AgentVerifier.cs:443`), so a line-level
grouping counts a composite declaration once and loses which of its files was missing. The bare `found` /
`NOT FOUND` form is emitted **only** when a declaration has exactly one candidate that is byte-identical
(`Ordinal`) to the flattened, truncated declaration (`:439-441`), so `"a summary saved to report.md"`
prints `report.md: NOT FOUND` and never matches a pattern anchored on the arm alone. And there are **eight**
outcome sites carrying **seven** distinct strings (`:409`, `:414`, `:424`, `:472`, `:478`, `:480`, `:481`,
`:485` — the two probe-budget arms share their text) plus a tally line at `:454`, so
`s/found (…)/found/`-style normalisation leaves `found, but it is a folder, not a file` standing as a
fourth bucket. The 13/10/0 first read in Part I was interpretable only because that corpus was
code-shaped: one filename per declaration, so line and candidate were the same thing.

If you need the declaration **text**, read `AgentSteps.ExpectedArtifact` out of the database. The Debug
facts block is *not* the source of record for strings: the log truncates tool-call arguments (trap 7), and
the database column is untruncated and is what the verifier itself reads. Paste neither anywhere — both
are user content. The database is also the only way to size **A7**: the tally deliberately does *not*
subdivide the not-a-file-reference bucket by shape. Doing so would need a prefix classifier that does not
exist and that would have to run before `FileCandidates` — A6's territory — and the one free proxy
(counting a `kind:` prefix) would read ~0 today because nothing asks the planner for prefixes, so a zero
meaning "never requested" would be indistinguishable from "not applicable".

### Reading the result

Name the denominator before quoting a share. **File-shapedness** is per declaration:
`fileShaped / (fileShaped + notFileShaped)`. **Delivery** is per candidate path:
`found / (found + notFound + folder + unresolvable)`, and the two single numbers worth writing down are
`found / probed` and `notFound / probed`.

Then collapse replan twins by hand (trap 3) and report **both** denominators — what the probe saw and
what the planner persisted (trap 2).

**There is no live threshold left in this section.** Gate A1's prospective table used to sit here; it is
answered, and it is recorded as history in Part I under *"The gate answer, as history"* — including why
its `NOT FOUND` row never meant what it assumed. A2's band, its two build triggers and its drop trigger
are in Part I too, and so are the three comparability cuts that decide whether a fresh number may be
compared to any of the numbers already recorded. **Do not average across the tally.**

**Privacy.** These logs contain the artifact names and step titles your prompts produced, which is
user content. `artifacts/` is gitignored; keep them there or outside the repo. Commit the derived
counts, never the log. The two halves are not equally sensitive: the `Artifact probe:` tally is counts only
and is safe to paste into the checklist or a diagnostics bundle, while the facts block one line below it is
artifact names and step titles — user content, and it stays on the machine.

---

# PART III — WHAT IS SETTLED, AND WHAT IS STILL OPEN

## Decisions already made — do not re-litigate these

### P1 — one clause forbids the disjunction without losing the conjunction

The two rules live one clause apart, and that is tractable because they are about **different things**.
The conjunction licence at `AgentPlanner.cs:784` (plan) and `:827` (replan) is about **step
granularity** — *"if one reason requires editing several files, that is ONE step listing every file in
`expectedArtifact`"* — an anti-step-splitting rule. The disjunction defect is about **candidate names
inside one declaration**, `(e.g., A or B)`, where the step writes one of the pair.

One clause settles both: **every name listed must exist when the step finishes.** A conjunction satisfies
it; a disjunction cannot. It also implies the omission rule — a step with no file to name has nothing
that can satisfy "must exist".

Entailment alone was judged insufficient. The success metric is a **string** property, and a model can
honour a semantic rule while still writing `e.g.`. So the wording bans the observed surface form
explicitly, by example, alongside the entailment. **Forbid what you measure.**

Landed wording on `PlanStepArg.ExpectedArtifact` (`:159`) — the tool-schema description `AIFunctionFactory`
ships on **every** plan and replan turn:

> The file(s) this step will produce — every name listed must exist when the step finishes, so name
> several only when it writes all of them. Never offer alternatives ("A or B", "e.g. A"). Omit when the
> step produces nothing checkable.

and on the plan-turn prose (`:782`):

> …include an expectedArtifact only when the step will write files, naming exactly the files it will
> write — every one of them must exist when the step finishes, so never offer alternatives to choose
> between.

**Four surfaces deliberately left alone.** `:784` and `:827` are untouched, verbatim.
`BuildReplanMessages` gains **no** prose surface: the schema description already reaches the replan turn,
and a fourth surface was forbidden. `:139` and `:148` — the `steps` array descriptions on `emit_plan` and
`emit_revised_plan` — summarise what an array element holds, and `:159` nests directly inside both;
restating the rule there would duplicate it every turn for no extra reach.

**Why `:159` is load-bearing**, and why P8 landed there as well as on `:782`: a **replan turn gets no
grounding fence at all**, so on the very turn that produced the rooted path, the schema description is
the only place the working folder is named.

**No test pins any of this, deliberately.** Nothing in `tests/` asserts prompt bytes; the existing planner
tests drive `emit_plan` through a fake client and assert on the parsed plan, hard-coding only the argument
*name* `expectedArtifact`. A test over the description string would pin wording rather than behaviour, and
would have to be edited by whoever changes it next. The evidence is the live before/after in Part I.
Revisit only if the replan turn's inheritance of the rule becomes load-bearing rather than incidental.

### P3 — a per-declaration not-found counter, decided and not built

The rule was fixed in advance, in two branches: **disjunctions survive P1** ⇒ build the counter, because
candidate-level counting is still lying; **no disjunction survives, on a non-vacuous sample** ⇒ do not
build it, and record why.

The second branch fired, twice. The replay read 0 of 6 probed declarations offering alternatives, with
`probed` **up** from 2 to 6; the wide read read 0 of 41 persisted declarations, on a corpus three times
the size. `AgentVerifier.cs` was not touched.

**Why building it blind would have been wrong.** With every listed name required to exist, a candidate
miss *is* a declaration miss, so the two counters agree by construction. And for a genuine conjunction,
"not-found only when every candidate misses" is the **wrong** rule — a step that owed three files and
wrote one has partially failed and must register. P1 makes the conjunction the only legitimate multi-name
form, which makes that wrong rule the only rule P3 could implement.

**Revival condition:** a fresh corpus in which any probed declaration still offers alternatives to choose
between.

### P9 — a step that reported `succeeded=True` on a failed write

**Answered 2026-08-24; no production change.** The row asked whether the step outcome needs to change, or
whether the missing piece is only that a refusal is surfaced nowhere. It is the second, and the reason is
sharper than the row allowed for: **the call was never refused.**

The payload — `{"success":false, … "error":"Error: Path is outside the assistant files folder.","created":false}`
— is not a gate decision. It is `FilesToolHandler`'s own `WriteResult.Failed(…)`, built at
`src/Pia.Wpf/Services/FilesToolHandler.cs:1037` when `SafeFolderPath` rejects a rooted path. The
unattended gate had already said yes: `write_file` was in the run's grant list, `DispatchGateVerdictAsync`
took its `AutoRun` arm, and `pending.Execute()` ran and returned normally. A gate **denial** would have
been visible — `NotExecuted` plus a `ToolGateDecision`, painted `RunDecisionSeverity.Refused`.

`HeadlessTurnExecutor.cs:611` decides success as
`claim?.Succeeded ?? !string.IsNullOrWhiteSpace(exchange.Visible)`. Both branches work as specified, and
**the tool result is not an input to either one** — `HandleToolCallAsync` hands the result object straight
back to the tool loop, which serialises it to the provider, and nothing on the way keeps a counter. The
model was the only party that knew the write had failed.

**The real gap: `AgentTimelineOutcome.Ok` means "Execute() returned", not "worked."** The `AutoRun` arm
distinguishes only `Error` (Execute threw) from `Ok`, so a handler returning a failure payload is `Ok`;
`RunProgressViewModel.Project` sets `OutcomeSuffix` only on `Error` and reads `Severity` off the
*decision*. Two smaller findings from the same read: `resultChars: (executed as string)?.Length` is null
for every `write_file` row, because it returns a record; and the step-outcome log line carries
`offered / confirmed / succeeded / declarations / artifactReported` and nothing about tool calls. **Net:
an executed-but-failed tool call is surfaced nowhere.**

**Why there is no cheap fix**, which is why this stayed an investigation. The timeline is
**metadata-only** by design — never an argument, never a result, never a path, never a hash of one — so
it cannot store the error text. And there is no shared failure envelope to read: `write_file` alone
returns a structured record with a `success` field, while around **118** other failing paths across
`src/Pia.Wpf/Services` return a bare string with an `"Error: "` prefix, and neither convention is
enforced anywhere. A generic signal therefore means sniffing free-form payloads, or a return contract no
external tool can be held to. Neither is `XS`.

**What it cannot settle:** whether that step emitted an `emit_step_result` claim at all (the throwaway
profile's log is gone — it changes nothing, since neither branch can see a tool result); and how often
this shape occurs, `n = 1` per instance.

The ranked *"what would be worth building"* list this reading produced, and the drain loop's
`if (cancelled || failed)` coordinate, belong to the failure-legibility track and live in
[`../failure_legibility/2026-08-24-failure-legibility.md`](../failure_legibility/2026-08-24-failure-legibility.md).

### Four rules on what not to do

- **Do not make `expectedArtifact` required.** It is optional in the schema on purpose; some steps
  genuinely produce nothing lookup-able, and forcing a value produces exactly the prose this track exists
  to remove.
- **Do not let a failed probe fail a verdict.** The probe informs the critic and the critic still decides.
  A missing artifact is a fact for the prompt, not a veto.
- **Do not fuzzy-match.** If `todo:Call the vendor` does not resolve exactly, report "not found", not
  "found something similar". *A probe that guesses is a summarizer with extra steps.*
- **Do not widen the probe before the outcome split has been read.** The gate is the outcome share, not
  `probed / declared`, which carries no outcome at all.

## Open rows, and what each waits on

**A2 · route `ArtifactRef` through the existing probe.** Waits on the band in Part I. Its honest cost is
**above its `S` rating**: a message-role decision (System vs User), a probe-budget decision, the
`artifact_ref` tool-description edit, and roughly ten assertion strings that move. The budget decision is
the one nobody has recorded: `MaxProbedPaths = 12` and `MaxReportedDeclarations = 20`
(`AgentVerifier.cs:238-239`) are consumed inside one per-declaration loop in step-ordinal order, and a
second channel can present two declarations per step — 40 on a 20-step plan. **Whether the budgets are
shared or split per channel decides whether the new, stronger evidence gets starved behind the old
prose**, and unless the split is recorded, pre-A2 and post-A2 A1 numbers are not comparable.

**A3 · tests for self-reported-but-missing.** Blocked transitively — the case does not exist until A2
does.

**A6 · extract `IArtifactProbe`. Mis-sorted as an Enabler: land it with A7, or not at all.** Its `Deps: A2`
line is recorded correctly and should not be changed. A6-first loses in every branch: if the gate had
closed, A6 sat inside the drop set ("drop A2–**A7**"); if A7 proceeds, A6's signature is fixed by A7,
which is downstream; if A7 is dropped, A6 is an interface with one implementation and one caller forever.
The decisive reason is a shape conflict only A7 can resolve — `Probe` is
`private static string Probe(string root, string candidate)`, synchronous by construction inside a
`Task.Run` with a 2 s `WaitAsync` box, while every kind A7 names is async (`ITodoService.GetAsync` /
`GetAllAsync` are `Task`-returning, as is all of `IReminderService`). So A6's own acceptance criterion,
*"behaviour-preserving refactor of today's probe"*, and A7's requirements are in direct conflict:
extracted sync it is the wrong shape, extracted async it is guessing A7 without A7. Two changes the row
needs: **strike** "behaviour-preserving refactor of today's probe" and replace it with *async,
per-declaration seam; budget passed in, not owned; no new constructor dependency on `AgentVerifier`; zero
prompt-byte change*; and **mark it "land with A7 — not viable standalone."**

**A7 · todo / reminder / vault probes plus a typed prefix.** Nothing sizes it. `LooksLikeFileName` decides
file-ness purely from extension shape, so `todo:Call the vendor` already falls out as "not a file
reference" — **correct by accident**. Cite it by name, never by line: it already moved once when the
tally shipped. The `notFileShaped` bucket is therefore the *only* evidence for A7's size, and
nothing subdivides it; the one free proxy, counting a `kind:` prefix, would read ~0 today because nothing
asks the planner for prefixes, so zero would be indistinguishable from not-applicable. And the prefix
dispatch cannot live inside `FileCandidates` — it has to run **before** it, which is exactly what makes
A6 its prerequisite.

**Review #13 · reject a plan step whose `ExpectedArtifact` is unprobeable prose.** It has **no checklist
row anywhere** (verified: `grep '#13'` on the checklist returns nothing), and it is **unbuildable as
written**: `ValidatePlan` (`AgentPlanner.cs:637-651`) is all-or-nothing and a `false` return degrades the
**entire** plan to the SingleTurn fallback (`:218-222`); the only per-step *drop* precedent (`:653-660`)
drops a persona assignment, never a step. Rejecting a prose artifact today means throwing away the plan.
#13 needs that seam designed first, or it is A4 wearing a different hat — in which case it has shipped as
P1 and should stop being tracked twice.

### Two owner questions still open

1. **Typed prefix, or a second tool argument?** `artifact_kind` alongside `artifact_ref` is cleaner to
   validate; a `kind:ref` string is cheaper and **degrades to today's behaviour when omitted**, which is
   exactly why the string is recommended.
2. **Should a self-reported-but-missing artifact do more than inform the critic** — mark the step
   unconfirmed, or trigger a replan? Tempting, and it violates the second no-veto rule above. If it is
   ever done it needs its own deliberate decision and its own tests.

A third question — *does move 2 make move 3 unnecessary?* — is **moot**: move 3 landed first, as P1 and
P8, before move 2 was ever opened.

## If the only Windows time is short

Strict value per minute. **Nothing on this list closes anything** — items 1 and 2 are what make the next
reading possible at all.

1. **Run `scripts/Measure-ArtifactDeclarations.ps1`** (§1). Minutes, no app launch, read-only database,
   safe while Pia is open. `-SelfTest` first — it has still never produced a recorded number. Highest
   information per minute on the whole track. It cannot answer `found` versus `NOT FOUND`.
2. **Create a few hand-made `AgentTask` routines per §4.3 and walk away.** ~10 minutes of editor work,
   then no attention. They must be created as *Agent run* — all eight blueprint cards ship
   `Kind: Research` and produce zero samples.
3. **Only if time remains: §4.2's interactive loop.** One click, one run, one probe line — still the most
   expensive way to buy a sample, and item 2 supersedes it for anything unattended.

Do not open A2 on a short week. Its honest cost is above its `S` rating, and the band can still move
against it.

## Recorded so they are not rediscovered

- **A build predating the counts-only tally emits the old `Artifact probe:` form**, and no parser for it
  survives — the per-declaration pipeline was deleted rather than fixed (§6). Harvest the facts block in
  that case, or install a newer build first.
- **The probe stays logger-free on purpose.** `ProbeDeclarations` and `Probe` are `static` and take no
  logger, and the tally's survival into Release — with the sensitive lines erased — was confirmed against
  the **Release IL**, not inferred from the source.
- **A5 is confirmed live**, not just by test: the pilot's throwaway database held 17 step rows, 15 with a
  non-blank `ExpectedArtifact`, and exactly **2** with an artifact in `ExtraJson` — matching the 2
  `artifactReported=True` outcomes one for one.
- **Pia's tool descriptions ship on every turn with no length discipline.** Hermes caps skill descriptions
  at 60 characters *because its system-prompt index truncates at 57*; Pia has no equivalent rule.
- **Pia has exactly one counter-trigger**, `BuiltInPluginDefaults.cs:42` — *"Do not use write_file for a
  vault source…"* — evidently added after that confusion bit someone. It is now a single run-on paragraph
  carrying five rules, which is the pitfall of adding a rule without removing the wording it replaces.
- **Personas in Pia are user-authored**; there is no seeded built-in system prompt, so the
  persona-authoring half of that standard would be user-facing documentation, not code.
- **Review #3 (diagnostics export) does not depend on this track.** It shipped 2026-08-24 as G1, scoped to
  Export. The old argument that a release-visible tally would "largely retire the we-had-to-hand-copy-logs
  case for it" is dead. One genuine synergy survives: the tally is *counts*, so a diagnostics bundle can
  carry it with no new redaction work.
- **The wide read's §12 was not A-track content.** It settled E7 and recorded that E9's write half is
  confirmed end to end while its read half rests only on a unit test; that material is carried in the
  E-track decision record, not here.
