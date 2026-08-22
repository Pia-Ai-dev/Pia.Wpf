# Runbook — Collecting Artifact-Probe Logs for Gate A1

**Status:** ready to execute. Self-contained: everything needed to run it is below.
**Owner:** unassigned — needs a Windows desktop session.
**Written:** 2026-08-22.
**Origin:** gate **A1** of [`2026-08-22-hermes-followup-checklist.md`](2026-08-22-hermes-followup-checklist.md),
which asks for `probed / declared` off real-run logs. Move 1 of
[`2026-08-22-artifact-evidence-plan.md`](2026-08-22-artifact-evidence-plan.md).

---

## 1. What this produces and why

A1 is a decision gate: it closes A2–A7 if the planner's `ExpectedArtifact` is *already* file-shaped
often enough that probing a second channel buys nothing.

A first read has been taken from a client's logs (`artifacts/Logs/pia-*.log`, 39 files,
2026-06-28..2026-08-22). Only three of them carry a probe line — the H1 verifier shipped 2026-07-28
(`6fc76957`) — for **7 verifier runs and 23 declarations**:

| outcome | count | share |
|---|---|---|
| `found` | 13 | 57% |
| `not a file reference` | 10 | 43% |
| `NOT FOUND` | **0** | 0% |

That refutes *"already high"* — the plan's own §2 guessed "roughly half the steps" and measured 57%.
But 7 runs on one machine over three days, all on code-shaped tasks (`Program.cs`, `Calculator.cs`,
`PrioritizedActionPlan.md`), is not a number to tune on.

**This runbook exists to get that sample to ~25–30 completed runs on an unbiased task mix.**

The row to watch is the third one. `NOT FOUND` was 0/23. If it stays at or near zero across a bigger,
honest sample, *that* is the finding: the planner channel is structurally incapable of producing a
negative signal, which is the whole argument for A2 (probe the executor's `ArtifactRef` instead).

---

## 2. The probe only fires on one path

Four gates, all of which must be open, plus a build requirement. Getting any of them wrong yields a
session with zero usable output.

### 2.1 Build Debug, not Release — this is the big one

`SensitiveDebug` is `[Conditional("DEBUG")]` (`src/Pia.Wpf/Logging/SafeLog.cs:19`), so the C# compiler
**erases the facts line from Release IL entirely**. No log-level setting brings it back. And
`Bootstrapper.cs:351` only sets `LogLevel.Debug` when `IsDevMode`, which is itself `#if DEBUG`.

A Release build gives you only the count line:

```
Artifact probe: 5 declaration(s), 3 path(s) probed.
```

and that is a **biased proxy**: `probed` counts *candidate paths*, not declarations
(`AgentVerifier.cs:397` increments inside a per-candidate loop nested in the per-declaration loop), so
one declaration can contribute several. The 13/23 in §1 came from the facts block, not from this line.

```powershell
dotnet build                      # Debug is the default; do not pass -c Release
```

### 2.2 Agent mode, not Chat

Only a Planned run reaches `AgentVerifier`. The composer's `Chat | Agent` lever
(`Views/AssistantView.xaml:487`) drives `AgentModeEnabled`, persisted as
`AppSettings.AssistantAgentModeDefault` (default **false**).

The lever is disabled unless the active persona has tools — its tooltip says
*"Agent mode needs a persona with tools."* Pick a tool-capable persona and a tool-capable provider
first; the small warn dot on the Agent segment means *"This provider may not plan reliably."*

### 2.3 Use **Run in background**, not Send

`AgentRunOrchestrator.cs:250`:

```csharp
if (plan.Steps.Count >= 3 && executor.SupportsPlanApproval)
    await ParkForPlanApprovalAsync(...);   // parks at "plan-approval" and returns
```

`SupportsPlanApproval` is `true` only on `LiveTurnExecutor` (`ViewModels/Models/LiveTurnExecutor.cs:91`);
the interface default is `false`. So:

- **Send** in Agent mode → any plan of 3+ steps stops dead and waits for you to approve it.
- **Run in background** (the green button, visible only in Agent mode) → headless executor, no
  approval park, runs straight through.

Run-in-background is the highest-yield action in this whole document: one click, one completed run,
one probe line, no babysitting.

### 2.4 Turn on auto-approve for built-in writes

`AppSettings.AgentRunAutoApproveBuiltInWrites` defaults to **false** (`Models/AppSettings.cs:232`).
With it off every write tool call parks for a per-call decision, so an unattended run never drains.

Settings → Assistant → Agent runs, checkbox
`Settings_Assistant_Agent_AutoApproveBuiltInWrites`. (The same property is rendered a second time on
the Tool access tab as `Settings_Assistant_ToolPermissions_AutoApproveBuiltInWrites` — either one moves it.)

Read the label before ticking it: *"a run with this permission can overwrite files in your assistant
folder unattended."* Deletes, Git and MCP tools are never covered by it. This is a reason to prefer the
throwaway profile in §3.

### 2.5 Let runs finish

`AgentRunOrchestrator.cs:517` breaks out of the loop **before** verify on a cancel or an unrecovered
step failure. A run you stop yields nothing. Give each one time to settle.

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

```powershell
$p = "C:\temp\pia-a1"
New-Item -ItemType Directory -Force "$p\roaming", "$p\local" | Out-Null
Copy-Item "$env:APPDATA\Pia\settings.json" "$p\roaming\settings.json"
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

## 4. Driving the session with WinWright

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
| **Agent lever** | `type=RadioButton[name='Agent']` — **name only, no AutomationId** |
| **Run in background** | `type=Button[name='Run in background']` — **name only, no AutomationId** |

> `src/Pia.Wpf/Views/AssistantView.xaml` carries **zero** `AutomationProperties.AutomationId` values —
> `InputTextBox` and `MessageScrollViewer` reach UIA only via `x:Name`. The Chat/Agent lever and the
> Run-in-background button are addressable by localized name alone, so this flow requires the UI
> language set to English and `winwright heal` cannot repair either selector. That is a concrete entry
> for the D7 AutomationId gap-fill row; out of scope here, noted so it is not rediscovered.

### 4.2 The loop

Once per session:

1. `ww_launch` (Mode A env, or plain for Mode B).
2. `NavItem_Settings` → category *Assistant* → tab `Settings_Assistant_Tab_Agent` → tick
   `Settings_Assistant_Agent_AutoApproveBuiltInWrites`. Confirm with
   `ww_assert_value(property="value", expected="On")`.
3. `NavItem_Assistant`. Invoke `type=RadioButton[name='Agent']`. Confirm the green
   `type=Button[name='Run in background']` has appeared — it is bound to `AgentModeEnabled`
   visibility, so its presence *is* the assertion that the lever moved.

Then per prompt, for each of the 24 in §5:

4. Set `automationId=InputTextBox` via ValuePattern to the prompt text.
5. `ww_invoke` on `type=Button[name='Run in background']`.
6. Wait for the run to settle before the next one. Poll the log rather than the UI — it is cheaper and
   unambiguous:

```powershell
$log = "C:\temp\pia-a1\local\Logs\pia-$(Get-Date -f yyyy-MM-dd).log"
$want = 1   # increment per dispatched run
while ((Select-String -Path $log -Pattern 'Artifact probe:' -SimpleMatch).Count -lt $want) {
    Start-Sleep -Seconds 15
}
```

A run that fails or is cancelled never emits the line, so cap the wait (5 minutes is generous for these
tasks) and move on rather than blocking the session — a skipped prompt costs one sample, a wedged
session costs all of them.

Do **not** record this with `ww_record`. A recording captures actions with no preconditions, it would
bake in the 24 prompt strings, and replaying it a second time would double-write every todo it created.
This is a one-shot data-collection session, not a regression script.

---

## 5. Prompt samples

**This section is the methodological core.** The ratio is only meaningful on an unbiased mix.

If you run a batch of *"write me a file"* tasks you will measure ~95% file-shaped and falsely close the
gate. The 43% prose in §1 is supposed to include the runs that legitimately produce no file — that is
the population being measured, not noise to be engineered away.

Six categories, four prompts each, 24 runs. **Run all six categories.** If you have to shorten the
session, drop one prompt from every category rather than dropping a category.

Keep plans moderate in size: `MaxReportedDeclarations = 20` and `MaxProbedPaths = 12`
(`AgentVerifier.cs:238`) truncate a long plan's facts block, which skews the ratio.

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

From the log directory (§3 tells you which one):

```powershell
# Ground truth — the classification of every declaration. Needs a Debug build.
Select-String -Path pia-*.log -Pattern '^- step \d+ .* → .*$' |
  ForEach-Object { $_.Line -replace '.*→ ', '→ ' -replace 'found \([^)]*\)', 'found' } |
  Group-Object | Select-Object Count, Name | Sort-Object Count -Descending

# How many verifier runs the session produced.
(Select-String -Path pia-*.log -Pattern 'Artifact probe:' -SimpleMatch).Count

# Should be zero. Any hit means the files folder was misconfigured for those runs.
Select-String -Path pia-*.log -Pattern 'Artifact probe skipped' -SimpleMatch
```

The equivalent on a Unix box, once the logs are copied over:

```bash
grep -hoE '^- step [0-9]+ .* → .*$' pia-*.log |
  sed -E 's/.*→ /→ /; s/found \([^)]*\)/found/' | sort | uniq -c
```

Three buckets come out: `found`, `NOT FOUND`, `not a file reference`. Record all three plus the run
count, and note the task mix that produced them — a ratio without its mix is not interpretable.

### Reading the result

| Outcome | What it means |
|---|---|
| `found` share is **high** (say ≥85%) on an unbiased mix | The gate closes. Write it down in the checklist and drop A2–A7. |
| `found` share is around half, as the first read suggests | A2–A4 stand. Proceed. |
| `NOT FOUND` stays at or near **zero** | The strongest result available here: the planner channel produces no negative signal at all, so probing the executor's `ArtifactRef` (A2) is where the only real evidence can come from. |
| `NOT FOUND` turns out to be common | A2 matters *less* than assumed — the existing probe is already catching missing artifacts. Say so before building anything. |

**Privacy.** These logs contain the artifact names and step titles your prompts produced, which is
user content. `artifacts/` is gitignored; keep them there or outside the repo. Commit the derived
counts, never the log.
