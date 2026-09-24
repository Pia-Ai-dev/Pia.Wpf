# Routine working directory, driven through the real app — results

**Status:** complete · **Owner:** Marco Altmann · **Written:** 2026-09-08
**Origin:** step 8 of [2026-09-08-routine-working-directory-checklist.md](2026-09-08-routine-working-directory-checklist.md),
which is gate G2 — *"does a routine actually read and write inside its folder, on both kinds?"* The
walkthrough it executes is §6 of [2026-09-08-routine-working-directory.md](2026-09-08-routine-working-directory.md).

All five §6 steps pass. Three observations below, one of them user-visible; all three have since
been acted on.

`dotnet test` never launches the app and never fires a routine, so the runtime half of this feature had
no coverage at all. This is the live pass that closes it, plus the two other things on
`feature/updates` reachable the same way: the unattended framing a fired routine runs under, and the
persona model-type picker.

## How to reproduce

```powershell
dotnet build
Remove-Item -Recurse -Force $env:TEMP\pia-routines   # the seed leaves local\history.db alone
node tests/ui-scripts/agent-run-e2e/setup-profile.mjs $env:TEMP\pia-routines routines DeepSeek
# ww_launch src/Pia.Wpf/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.exe with env
#   PIA_DATA_DIR       = $env:TEMP\pia-routines\roaming
#   PIA_LOCAL_DATA_DIR = $env:TEMP\pia-routines\local
node tests/ui-scripts/agent-run-e2e/setup-profile.mjs $env:TEMP\pia-routines verify   # afterwards
```

`routines` is a new mode on the existing seed script: `seed`'s defaults (auto-approve on,
`assistantDefaultWorkingDirectory: Playground`) with the named BYOK provider pinned into **both**
`modeProviderDefaults.Assistant` and `.Optimize`. Pinning Assistant alone is not enough —
`useSameProviderForAllModes` is on in a real profile, so the resolver reads the Optimize default for
every mode and the run silently goes to Pia Cloud, which cannot authenticate with `syncEnabled:false`.
Do **not** use `park` here: it turns auto-approve off, and a fired routine has nobody to approve its
writes, so the run hangs instead of parking into a scenario.

The same change pins `uiLanguage: 0` for **all** seed modes. The script copies the real
`settings.json`, and a German install turns every name-based selector and every `optionText` into a
German string.

Provider: DeepSeek (`deepseek/deepseek-v4-flash` via OpenRouter). Debug build — the `AiClientService`
tool-args/result log lines quoted below are erased in Release. `verify` printed
`real profile untouched (incl. vault: 36 files 94e3e437…)` at the end.

The seed lays down eight decoy folders (`Absence`, `Config`, `Docs`, `Finance`, `Inventory`,
`Playground`, `ReleaseNotes`, `Support`) beside the empty `Playground`. That is what makes the
narrowing falsifiable: the same goal, narrowed and un-narrowed, produces two lists that cannot be
confused.

## The discriminator

One goal, run three times: *"List every file and folder at the root of your working folder, using the
file tools only. Then write that exact list to a file named `<name>.md`, one entry per line."*

| Run | Kind | Routine's folder | File landed at | Model saw |
|---|---|---|---|---|
| 1 | Research | `Playground/E2E` | `files/Playground/E2E/seen.md` | `marker-e2e.txt` |
| 2 | Agent run | `Playground/E2E` | `files/Playground/E2E/seen-agent.md` | `marker-e2e.txt`, `seen.md` |
| 3 | Research | *(null — legacy)* | `files/seen-legacy.md` | 37 files across all eight folders |

Run 3 is run 1 with the column nulled and nothing else changed. It names `Inventory`, `Support`,
`Finance`, `Docs`, `Config`, `Absence`, `ReleaseNotes` and the vault; runs 1 and 2 name none of them. A
pass that only checked "a file appeared" would not have told those apart.

## §6 step by step

**1. A new routine opens on `\Playground`.** Both create paths: the blueprint catalog
(`Routines_Blueprint_news-briefing`) and `Routines_StartBlank`. `Routines_Field_WorkingDir`'s text child
read `\Playground` in each, under the label *"Files this routine reads and writes stay in this folder.
Stored on this device only."*

**2. Drill, create inline, save, reopen.** `Routines_WorkingDir_AddFolder` → `_NewFolderName` →
`_NewFolderConfirm` created `E2E` under `Playground`, and it appeared on disk. Creating does **not**
select it — `ExecuteConfirmCreateFolder` deliberately raises no `WorkingDirectoryChosen` — so the row
still has to be entered, and that row activates on `PreviewMouseLeftButtonUp`, so it needs a physical
click rather than `ww_invoke`. The button then read `\Playground\E2E`, the row persisted as
`ScheduledJobs.WorkingDirectory = 'Playground/E2E'` (normalized to forward slashes),
`Routines_Detail_WorkingDir` showed `\Playground\E2E`, and reopening the editor read it back unchanged.

**3. The Research leg runs in the folder.** Run 1. `list_files` was called with `args: {}` and answered
`Found 1 file(s)` — the narrowed root, not the sandbox root. `write_file path: seen.md` resolved inside
`Playground/E2E`. The produced chat carries `WorkingDirectory = Playground/E2E` in `AssistantChats`, and
resuming it in the Assistant view showed `ChatChip_WorkingDir` = `\Playground\E2E`.

**4. The AgentTask leg runs in the folder.** Run 2, after switching `Routines_Field_Kind` to *Agent run*
on the same routine — the folder survived the kind switch, in the editor and in the row. The workspace
lines are the proof:

```
RunWorkspaceService  Run b63652ad workspace copied in 2 file(s), 61 bytes
RunWorkspaceService  Run b63652ad promoted 1 file(s), skipped 0, 0 conflict(s)
```

61 bytes is `marker-e2e.txt` (45) + `seen.md` (16) — the narrowed folder and nothing else. Seeded from
the sandbox root it would have been 37 files. The one promoted file is `seen-agent.md`, back into the
narrowed root. Note the leg narrows differently from the Research one: `HeadlessTurnExecutor` pins
`ctx.WorkingSubpath = null` on purpose, and the scoping comes from the workspace being seeded from and
promoted back to the folder.

**5. A routine from before the change still reads `\`.** The app was closed, the column set to `NULL`
directly in `history.db`, and the app relaunched. `Routines_Detail_WorkingDir` was absent — the line is
hidden when a routine has no folder — while `Routines_Detail_NextRun` was present, so the pane was
loaded and the line was genuinely hidden rather than the pane being empty. The editor button read `\`,
and run 3 worked the whole sandbox. Nothing moved.

## Also verified in the same pass

**A fired routine does the work instead of asking about itself.** The reply to run 1 was *"Done. The
root of the working folder contains a single file, marker-e2e.txt, and that exact list (one entry) is
now written to seen.md."* — work product, no question. The mechanism is visible too: `GetAllTools`
returns 46 and the unattended turn was sent 40. The six withheld are exactly the routine-management
tools (`create_scheduled_research`, `query_`, `update_`, `delete_`, `list_routine_blueprints`,
`create_routine_from_blueprint`). The agent run's step turn was sent 42 — the same 40 plus the step's
own two — and likewise names none of them, so `StepPersonaResolver` carries the framing into a step
rather than re-composing it attended.

**The persona model-type picker offers "private".** `PersonaEdit_ModelType` lists
`general, fast, code, private`. Selecting `private` renders *"Only has an effect if your cloud provider
offers a private model."*; selecting `fast` removes it, so the hint is bound to the value and not
statically present.

**Run history.** After a relaunch, all three firings are listed under `Routines_RunHistory`, each
`Completed` with its own `Routines_OpenRunChat_<chatId>`. Within a session the list does not pick up a
second *Run now* until the view is revisited.

## Observations

All three were acted on the same day; each paragraph below records the observation and what closed it.

**An agent routine's produced chat carries no working directory — FIXED.** Run 2's chat row had
`WorkingDirectory = null` while run 1's had `Playground/E2E`, and opening it showed the pill as `\` —
next to a run whose files went to `\Playground\E2E`. `HeadlessRunLauncher` wrote the stub chat with no
`WorkingDirectory`, and `HeadlessTurnExecutor` only carries forward what the row already has. It was a
scope cut rather than a regression: plan task 3 promised chat stamping on the Research leg only, and
step 4 asks about promotion, which passed. The launcher now stamps `req.WorkingSubpath` onto the stub,
which is the only place it can be stamped — the executor pins `ctx.WorkingSubpath = null` and reads the
row only to write it back.

The stamp is **display-only**, and that is a fact under test rather than an intention. Three readers
were checked before choosing it. `HeadlessTurnExecutor` pins `ctx.WorkingSubpath = null` in
`BeginRunAsync` regardless of what the row carries, which
`HeadlessTurnExecutorTests.BeginRunAsync_DoesNotInheritTheChatsWorkingSubpath_ParityWithLive` locks by
seeding a row with `projects/alpha` and asserting the context comes back null. A resume never consults
the row either: `ResolveResumeWorkspaceRootAsync` calls `ProvisionAsync(run.Id, workingSubpath: null)`
and relies on the persisted workspace metadata being idempotent. And the verifier's artifact probe
resolves against `ctx.WorkspaceRoot` with `ctx.WorkingSubpath` — both set by the executor, neither from
the chat. So where an agent run reads and writes is unchanged; only the pill moved.

The sibling case is the composer's **Run in background**, not a background assignment —
`HeadlessAssignmentLauncher` and `AssignmentRunOrchestrator` never touch `IHeadlessRunLauncher` and
have no working-subpath input at all. *Run in background* does go through the same launcher
(`AssistantViewModel` → `StartBackgroundRunAsync(userText, ActiveSession?.WorkingDirectory)`), had the
identical symptom, and gets the identical fix from the one stamp. One edge is left deliberately: a
delegated CHILD run's request carries no subpath, so a parked child's visible stub chat still shows
`\`. Widening that would mean inventing a value the request does not carry.

**Both re-checked live on 2026-09-09**, because the fix itself was only ever under unit test and the
symptom is a pill. A fresh throwaway profile, an agent routine pinned to `\Playground\E2E` and fired
with *Run now*: `Routines_OpenRunChat_<chatId>` resumed the produced chat with `ChatChip_WorkingDir`
reading `\Playground\E2E` — folder glyph, not the root home glyph — the row still read
`WorkingDirectory = Playground/E2E` after the run had finished and rewritten it, and the one promoted
file landed in the narrowed folder rather than at the sandbox root. *Run in background* from that chat
produced a second chat reading the same pill, with its file in the same place. Read off the UIA tree,
both times after the run reached `Completed` — the launcher test asserts the stamp at launch, and the
finished chat is where the symptom was seen.

**`list_files` emits native separators, `find_files` emits forward slashes — FIXED.** Run 3's
`list_files` result read `Support\tickets\T-2001.txt` while `find_files` in the same round returned
`Absence/Fehlzeitenübersicht-2026.csv`. `list_files` now normalizes at its reporting site, with the same
`NormalizeSeparators` helper `find_files` already applies as it collects and the `@Files` picker applies
to the same collector's output. The shared `CollectRelativeFiles` is untouched and still documents
native separators, which stays true — both of its callers normalize. Scoped to the in-round
disagreement on purpose: the broader "forward slashes are the tool contract" change lives on another
line of work, and no branch had settled `list_files` either way.

**The screenshot channel goes stale once the window is not foreground — DOCUMENTED.** `ww_screenshot`
kept returning the last composed frame while the UIA tree moved on: it showed the Routines view after
navigation to the Assistant view had already happened (`InputTextBox` visible, `Routines_JobList`
gone). Read state off the tree, not off a capture, and never read a stale screenshot as evidence that a
navigation failed. Now a Known-gaps entry in
[`../ui_automation/ui-automation-playbook.md`](../ui_automation/ui-automation-playbook.md), next to the
GPU-stall bullet it is easy to confuse with — that one returns a blank or torn frame, this one a
plausible stale one.

## Not covered, and why

- **A managed persona's model type over the pull channel.** The client half is here
  (`SyncManagedPersona.ModelType` plus the `SyncMapper` line, both unit-tested), but the server has
  nowhere to set it: `ManagedPersona` has no `ModelType` column and `SaveManagedPersonaRequest` no
  field, so the wire value is always null. Not testable against the local server until the server side
  lands.
- **Built-in personas asking Pia Cloud for a fast or coding model.** Needs a Pia Cloud provider; this
  profile runs `syncEnabled:false` on BYOK. `PersonaServiceTests` pins the catalog values.
- **The Teams join name and the silent-when-visible fix.** Needs a live meeting; no autonomous path.
