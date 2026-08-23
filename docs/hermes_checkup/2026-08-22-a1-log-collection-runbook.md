# Runbook — Collecting Artifact-Probe Logs for Gate A1

**Status:** ready to execute. Self-contained: everything needed to run it is below.
**Owner:** unassigned. Only the `NOT FOUND` row needs a Windows desktop session — the file-shapedness
half comes off the database offline, from any machine that has PowerShell 7 (§1's closing note).
**Written:** 2026-08-22.
**Origin:** gate **A1** of [`2026-08-22-hermes-followup-checklist.md`](2026-08-22-hermes-followup-checklist.md),
which asks for the artifact **outcome** split off real-run logs — `found` / `NOT FOUND` /
`not a file reference`. It does not ask for `probed / declared`: that pair counts declarations against
candidate paths and carries no outcome at all (§6). Move 1 of
[`2026-08-22-artifact-evidence-plan.md`](2026-08-22-artifact-evidence-plan.md).

---

## 1. What this produces and why

A1 is a decision gate: it closes A2–A7 if the planner's `ExpectedArtifact` is *already* file-shaped
often enough that probing a second channel buys nothing.

A first read has been taken from a client's logs (`artifacts/Logs/pia-*.log`, 39 files,
2026-06-28..2026-08-22). Only three of them carry a probe line — the H1 verifier shipped 2026-07-28
(`6fc76957`) — for **23 declarations over 7 probe lines**:

| outcome | count | share |
|---|---|---|
| `found` | 13 | 57% |
| `not a file reference` | 10 | 43% |
| `NOT FOUND` | **0** | 0% |

That refutes *"already high"* — the plan's own §2 guessed "roughly half the steps" and measured 57%.
But 7 probe lines on one machine over three days, all on code-shaped tasks (`Program.cs`,
`Calculator.cs`, `PrioritizedActionPlan.md`), is not a number to tune on.

**Three caveats on that n=23, each of which makes it weaker than it looks.**

- **Probe lines are not runs, and the 23 are not independent samples.** A verify-fail replan re-enters the
  drain loop (`AgentRunOrchestrator.cs:530-539`) and verifies *again* over a completed-step list that is
  only ever appended to — `RunContext._completed` is written at `RunContext.cs:145` and `:168` and never
  cleared — so the second probe line of a run re-declares everything the first one declared. A resume does
  the same across segments: pre-pause steps are seeded into the fresh context at
  `AgentRunOrchestrator.cs:941`.
- **The population is conditioned on eventual success.** The drain loop breaks *before* verify on a cancel
  or an unrecovered step failure (`AgentRunOrchestrator.cs:516-517`), so a run that ended badly emits no
  probe line at all. A step that failed and was *replanned past* does stay in the probed set —
  `ctx.RecordStep` at `:491` runs before the `!r.Succeeded` check at `:506`. So `NOT FOUND = 0/23`
  describes runs that ended well, which is the population least likely to be holding a missing artifact.
- **Each of those 23 declarations produced at most one candidate path.** One filename per declaration is
  the only reason the outcome counts summed to the declaration count. On §5's mix a single declaration can
  name several files, and that identity breaks — see §6's field glossary.

**This runbook exists to get that sample to ~25–30 completed runs on an unbiased task mix.**

The row to watch is the third one. `NOT FOUND` was 0/23 when this was written, and the inference drawn
from that — *a channel that never says no is structurally incapable of saying it, so only A2 can supply
a negative* — **has since been refuted twice over.** The 2026-08-23 pilot produced a non-zero
`NOT FOUND` that was merely unreadable (§6), and the replay after the planner wording was tightened
produced one that was **true**: a declared file that genuinely was not on disk. The channel can say no.
Read a zero here as "this sample had no misses", not as a property of the instrument, and do not carry
the old inference into an A2 decision — [`2026-08-23-a4-replay-reading.md`](2026-08-23-a4-replay-reading.md)
§7 supersedes it.

**Do the offline half first — it needs no desktop, no build and no new runs.**
`scripts/Measure-ArtifactDeclarations.ps1` replays the persisted `AgentSteps.ExpectedArtifact` strings
through its own copy of the probe's classifier and prints the file-shapedness split over a machine's whole
history, not just the three days a log file covers:

```powershell
./scripts/Measure-ArtifactDeclarations.ps1 -SelfTest    # classifier parity check; no database needed
./scripts/Measure-ArtifactDeclarations.ps1              # all history
./scripts/Measure-ArtifactDeclarations.ps1 -SinceDays 30 -OutputPath ~/artifact-counts.json
```

`-SinceDays 0` means all history; `-Force` allows overwriting `-OutputPath`; `-DatabasePath`,
`-Sqlite3Path` and `-CasesPath` override discovery. Output is three sections — `DECLARATIONS`,
`FILE-SHAPEDNESS`, `BY STEP STATUS` — counts only, and it hard-refuses any `-OutputPath` inside this
repository with no override switch.

Two things it cannot tell you. Declarations that were **replanned away** are gone, because a replan deletes
every step row that is not `Done` or `Skipped` — its sample is biased toward steps that survived to the
end. And **`found` versus `NOT FOUND` is not recoverable from the database**: the filesystem has moved on
and a per-run workspace is torn down when the run settles. It needs **PowerShell 7** (`pwsh`), which is not
installed on the machine these docs were written on; `sqlite3` is.

So run the script for the ratio, and book a desktop session only for the `NOT FOUND` row. §4.3 is the cheap
way to produce that row; §4.1–§4.2 is the expensive one.

---

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

Read that block by eye on the first session and paste none of it anywhere. It is the only place a
declaration string is visible, and it is the only way to size **A7** — §6 says why the counters
deliberately cannot.

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
```

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

4. Set `automationId=InputTextBox` via ValuePattern to the prompt text.
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

A run that fails or is cancelled never emits the line, so cap the wait (5 minutes is generous for these
tasks) and move on rather than blocking the session — a skipped prompt costs one sample, a wedged
session costs all of them.

#### Seven traps this loop used to walk into

The 2026-08-23 pilot hit all three. They are collection defects, not analysis ones: get them wrong and
no amount of care downstream recovers the sample.

1. **`declared` accumulates across verify passes, and carries replan twins.** The verifier reads
   `ctx.CompletedSteps`, which grows after each replan, so one run can emit `declared=3`, then `6`, then
   `7`. Summing the lines triple-counts. Read the **last line per run id** — and then collapse by hand:
   a replan re-declares the same artifact against a new step row with sharper wording, so the vague
   original and its concretized twin both survive into the final facts block, the original in
   `notFileShaped` and the twin in `fileShaped`. Any harvest that counts step rows inflates the
   denominator.
2. **Runs execute concurrently, so line order does not track prompt order.** Counting to the *N*th
   `Artifact probe:` line mis-attributes: two pilot runs overlapped by 36 seconds. Attribute on the
   `[run <id>]` prefix, which is why the poll above waits on an id.
3. **A run can leave the population entirely, and which prompts do is provider-dependent.** Any run
   that never reaches a plan emits **no probe line at all**, so every ratio the gate reads is
   conditioned on the run having produced one. Two mechanisms have been observed, on two providers:
   an answer-only prompt (§5 category F) completing through the SingleTurn fallback with
   `offered=False`, and a research prompt (category B) whose plan turn **declined as ungroundable** and
   parked for clarification. Neither is a failed collection — but do not assume a *category* is
   invisible: on the 2026-08-23 replay, category F planned and wrote a file on both arms while category
   B vanished on both. Check which prompts actually dropped out before quoting a denominator.
   A third mechanism turned up on 2026-08-23: a run that **planned, ran and was verified twice** and still
   emitted no probe line, because it declared nothing at all. The answer-only category behaving exactly as
   designed is *structurally invisible* to every ratio here, not merely absent from one.
4. **The log TRUNCATES tool-call arguments, so do not harvest declaration text from it.** On the
   2026-08-23 corpus, 40 of 99 `emit_plan args:` lines ended in `…` — enough to lose every declaration of
   two runs whose probe lines showed declarations. Read `AgentSteps.ExpectedArtifact` out of the database
   instead: untruncated, and it is what the verifier itself reads. The `Artifact probe:` counters stay
   authoritative for *counts*; only the *strings* need the database.
5. **The last probe line can UNDERCOUNT, not only overcount.** Trap 1 is still right — `declared`
   accumulates, and one run read `declared=1 notFound=1` on an early pass before its file existed and
   `declared=4 found=4` on its last. But a run that **fails after its last verify pass** never reports what
   it declared afterwards: one run persisted five declarations and its only probe line saw three.
   Reconcile the probe line against the step rows before quoting a total, and expect two legitimate
   denominators — what the probe saw, and what the planner actually declared.
6. **Count guard VALUES, not guard names.** Every probe line literally contains `overReportCap=0`, so a
   substring grep for the counter name reports one hit per run and looks like a censored sample. Match
   `=[1-9]`.
7. **An empty composer after a dispatch is the SUCCESS signal, not a failure.** `ww_set_value` on
   `InputTextBox` does work; but reading the composer back after `ww_invoke` shows it empty and *Run in
   background* disabled, because the dispatch cleared it. Reading that as "the set did not take" cost the
   2026-08-23 corpus a duplicate run on one prompt. Verify a dispatch by the run row appearing, never by
   the composer's contents afterwards.

Two smaller ones, and both silently ruin a "runs since I started" filter: `AgentRuns.CreatedAt` is stored
in **UTC**, so a baseline written in local time excludes everything; and a run can create `ScheduledJobs`
rows of its own, which then fire and add runs nobody dispatched. Filter on `TriggerKind`, and check for
new jobs before blaming the loop.

Harvest before you walk away: the file sink rolls at 10 MB and keeps 7 files
(`Bootstrapper.cs:360-361`), so a long stretch can retire the file holding your earliest probe lines.

Do **not** record this with `ww_record`. A recording captures actions with no preconditions, it would
bake in the 24 prompt strings, and replaying it a second time would double-write every todo it created.
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
| Granted tools | `Routines_Field_GrantedTools` | ValuePattern, comma-separated; empty ⇒ `{write_file}` |
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
the tools per routine in `Routines_Field_GrantedTools`. §5's categories **C** (todos and reminders) and
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
gate. The 43% prose in §1 is supposed to include the runs that legitimately produce no file — that is
the population being measured, not noise to be engineered away.

Six categories, four prompts each, 24 runs. **Run all six categories.** If you have to shorten the
session, drop one prompt from every category rather than dropping a category.

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

### C — Todos and reminders (the `todo:` / `reminder:` shapes Move 4 wants to probe)

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
fourth bucket. The 13/10/0 first read in §1 was interpretable only because that corpus was code-shaped:
one filename per declaration, so line and candidate were the same thing.

If you need the declaration **text**, read the Debug facts block by eye (§2.1) and paste none of it
anywhere. That is also the only way to size **A7**: the tally deliberately does *not* subdivide the
not-a-file-reference bucket by shape. Doing so would need a prefix classifier that does not exist and that
would have to run before `FileCandidates` — A6's territory, and gate-blocked — and the one free proxy
(counting a `kind:` prefix) would read ~0 today because nothing asks the planner for prefixes, so a zero
meaning "never requested" would be indistinguishable from "not applicable".

### Reading the result

Name the denominator before quoting a share. **File-shapedness** is per declaration:
`fileShaped / (fileShaped + notFileShaped)`. **Delivery** is per candidate path:
`found / (found + notFound + folder + unresolvable)`, and the two single numbers worth writing down are
`found / probed` and `notFound / probed`.

| Outcome | What it means |
|---|---|
| **file-shapedness** is **high** (say ≥85%) on an unbiased mix | The gate closes. Write it down in the checklist and drop A2–A7. |
| **file-shapedness** is around half, as the first read suggests | A2–A4 stand. Proceed. |
| `NOT FOUND` turns out to be common | A2 matters *less* than assumed — the existing probe is already catching missing artifacts. Say so before building anything. |

**A non-zero `NOT FOUND` is not automatically a real one.** An earlier revision of the table above
carried a row reading "`NOT FOUND` at or near zero → the planner channel produces no negative signal at
all". The 2026-08-23 pilot refuted it: the channel produced 4, and every one came from a declaration
that named *alternatives* — `(e.g., A or B)` — where the step wrote one of the pair and the probe
correctly reported the other absent. All four files existed and all four steps succeeded. `found` and
`notFound` count **candidate paths**, so a two-name disjunction contributes one of each however well the
step performed. Before reading a negative as a miss, check the Debug facts block for alternatives; the
planner wording that produced them was tightened on 2026-08-23, so a fresh corpus should not show them.

**The second cut, 2026-08-23.** The planner wording that produces `ExpectedArtifact` was tightened to
forbid alternatives and to ask for file names — so `fileShaped / (fileShaped + notFileShaped)` is now
partly a property of the *prompt*, not only of the planner's habits. A post-change reading is not
comparable to a pre-change one, and the ≥85% threshold in the table above was calibrated on the old
field. Say which side of this cut a number came from too.

**The cut.** A reading taken before the tally shipped is not comparable to one taken after. The old line
carried `declared` and `probed` only, and §1's 57/43 was hand-counted out of the Debug facts block; of the
new fields only `notFileShaped / declared` is even loosely comparable to that 43%. Do not average across
the cut, and say which side of it a number came from.

**Privacy.** These logs contain the artifact names and step titles your prompts produced, which is
user content. `artifacts/` is gitignored; keep them there or outside the repo. Commit the derived
counts, never the log. The two halves are not equally sensitive: the `Artifact probe:` tally is counts only
and is safe to paste into the checklist or a diagnostics bundle, while the facts block one line below it is
artifact names and step titles — user content, and it stays on the machine.
