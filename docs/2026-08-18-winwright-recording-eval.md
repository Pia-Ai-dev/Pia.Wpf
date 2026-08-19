# WinWright record/replay, evaluated against Pia

Ran WinWright 3.1.0's recording feature (`ww_record`) end to end on Pia's Settings area: recorded
two real settings changes as a two-test-case script, exported it, probed it with `ww_heal_script`,
then reverted the app to its pre-run state and replayed the script step by step from that clean
baseline.

**Verdict.** Record → export → replay works today, end to end, and it is genuinely deterministic:
the MCP surface has no replay tool, but the same binary has a CLI runner —
`Civyk.WinWright.Mcp.exe run <script.json> [--format junit]` — which launched Pia, replayed all six
recorded steps in 1.6 s, checked both embedded assertions and closed the app again, exit code 0.
The blockers are on our side, not the tool's: **Pia's settings views carry no AutomationIds**, so
recorded selectors fall back to localized control names and some controls cannot be targeted at all,
and a recorded script captures **actions but no preconditions** — this one passes exactly once, then
fails on the second run because the setting it flips changes the app's own start state.

## What was recorded

| Test case | Change | Selector used |
|---|---|---|
| TC-001 | General → Application: **Start minimized to system tray** off → on | `type=CheckBox[name='Start minimized to system tray']` |
| TC-002 | General → Application: **Auto-paste selected text on hotkey** on → off | `type=CheckBox[name='Auto-paste selected text on hotkey']` |

Both persist immediately (`GeneralSettingsViewModel.OnStartMinimizedChanged` → `SaveSettingsAsync`),
confirmed out-of-band in `%APPDATA%\Pia\settings.json`. Both were reverted after the run by
restoring a byte-identical snapshot of that file taken before the run — worth stating because these
two fields also travel to the server through `SyncMapper`.

TC-002 was *meant* to be "set Speech-to-Text language to German". That change turned out not to be
automatable at all — see *Pia-side blockers*.

## How the recorder actually behaves

- **It records your tool calls, not user gestures.** Selector quality in the artifact is entirely
  the operator's authorship; the recorder faithfully persists whatever selector you passed. Every
  action tool takes `record: false` for exploratory calls, which is the right default for discovery
  steps (`ww_snapshot` / `ww_query` / `ww_inspect` are never recorded).
- **Calls that returned success land in the buffer — not calls that did something.** A `ww_select`
  that failed with `no_match` was correctly not recorded (verified by popping afterwards and reading
  `remaining`), but a deliberate no-op — `ww_click` on a static `type=Text[name='Application
  Behavior']` — returned `{"success": true}` and was recorded as a legitimate step. Combined with the
  playbook's documented `ww_click` false-success, that means a careless recording bakes in steps that
  do nothing and still pass on replay. `pop` is the cure, and you have to notice you need it.
- **`stop` is a one-way door.** There is no peek and no pause/resume: `stop` returns the buffer and
  ends the session, `pop` then refuses with *"Recording is not active"*, and `start` **clears** the
  buffer. Mid-run, the only safe read is `pop`'s `remaining` count. Do not call `stop` to inspect —
  that cost one full re-record here.
- **`export` still works after `stop`** and returns the retained buffer, so a premature `stop` costs
  editability but not the recording.
- **Assertions must be `ww_assert_value`.** It embeds into the *last recorded step* rather than
  adding one (TC-001 = 4 steps, 1 assertion). `ww_assert` is not recorded. `ww_assert_value` has no
  checked/unchecked property, but it does not need one: `ww_get_value` on a WPF `CheckBox` resolves
  through TogglePattern and returns `On`/`Off`, so `property=value, expected=On` works.
- **Test-case titles are frozen at `test_start`.** No rename action exists; TC-002's stale title had
  to be corrected by hand in the exported JSON.
- **`mode` is inferred**: `rpa` with a flat `steps[]` when no test case is open, `test` with
  `testCases[]` when one is. `launchPath` / `attachTitle` are export-time parameters, not recorded
  facts.

## The artifact

`version: "1"`, a `runConfig` block that clearly anticipates a runner (`stepTimeoutMs`,
`continueOnFailure`, `maxFailures`, `captureScreenshots`), and per-step
`{timestamp, tool, selector, extra, assertion, testCaseId}`.

Two schema weaknesses for anyone writing that runner: the payload field is a single untyped `extra`
whose meaning depends on the tool (option text for `ww_select`, the string `"check"`/`"uncheck"` for
`ww_set_checked`), and the recorded `appId` is a dead session id a runner must ignore. There are no
wait/settle steps — timestamps are the only timing information.

```json
{
  "version": "1",
  "appId": "24e428a8-e7ec-437a-b51a-e5db42fbfe33",
  "mode": "test",
  "launchPath": "C:\\projects\\Pia.Wpf\\src\\Pia.Wpf\\bin\\Debug\\net10.0-windows10.0.17763.0\\Pia.Wpf.exe",
  "runConfig": {
    "captureScreenshots": false,
    "screenshotFormat": "png",
    "screenshotOnFailureOnly": false,
    "continueOnFailure": false,
    "stepTimeoutMs": 10000,
    "maxFailures": 0
  },
  "testCases": [
    {
      "id": "TC-001",
      "title": "Settings General: enable Start minimized to system tray",
      "steps": [
        {
          "timestamp": "2026-08-18T19:44:04.5652913+00:00",
          "tool": "ww_invoke",
          "selector": "automationId=NavItem_Settings",
          "testCaseId": "TC-001"
        },
        {
          "timestamp": "2026-08-18T19:44:10.0613472+00:00",
          "tool": "ww_select",
          "selector": "automationId=Settings_CategoryList",
          "extra": "General",
          "testCaseId": "TC-001"
        },
        {
          "timestamp": "2026-08-18T19:44:15.5661155+00:00",
          "tool": "ww_select",
          "selector": "type=TabControl",
          "extra": "Application",
          "testCaseId": "TC-001"
        },
        {
          "timestamp": "2026-08-18T19:44:34.2719959+00:00",
          "tool": "ww_set_checked",
          "selector": "type=CheckBox[name='Start minimized to system tray']",
          "extra": "check",
          "assertion": {
            "type": "assert",
            "selector": "type=CheckBox[name='Start minimized to system tray']",
            "property": "value",
            "op": "eq",
            "expected": "On",
            "message": "Start-minimized checkbox should be checked after set_checked"
          },
          "testCaseId": "TC-001"
        }
      ]
    },
    {
      "id": "TC-002",
      "title": "Settings General: disable Auto-paste selected text on hotkey",
      "steps": [
        {
          "timestamp": "2026-08-18T19:47:56.8362946+00:00",
          "tool": "ww_select",
          "selector": "type=TabControl",
          "extra": "Application",
          "testCaseId": "TC-002"
        },
        {
          "timestamp": "2026-08-18T19:48:02.8260929+00:00",
          "tool": "ww_set_checked",
          "selector": "type=CheckBox[name='Auto-paste selected text on hotkey']",
          "extra": "uncheck",
          "assertion": {
            "type": "assert",
            "selector": "type=CheckBox[name='Auto-paste selected text on hotkey']",
            "property": "value",
            "op": "eq",
            "expected": "Off",
            "message": "Auto-paste checkbox should be unchecked"
          },
          "testCaseId": "TC-002"
        }
      ]
    }
  ]
}
```

## Replay

Two ways, both tried after reverting the app to its pre-run state (close → restore the
`settings.json` snapshot).

**By hand through MCP** — re-issued the six steps against a fresh session. All selectors resolved,
`set_checked` reported `previousState: unchecked → newState: checked` for both, both assertions
passed, `settings.json` came back to `startMinimized: true` / `autoCaptureSelectedText: false`. This
only proves the artifact is *sufficient*; a model issuing the calls is not a deterministic runner.

**By the CLI runner** — the real answer. `Civyk.WinWright.Mcp.exe --help` lists verbs the MCP surface
does not expose:

```
run <script.json> [options]  Replay a recorded automation script
heal <script.json> [options] Probe and repair broken selectors in a script
tools [--json|<name>]        List the tool surface for CLI use (no MCP client needed)
call <tool> [--param v .]    Invoke one tool via the local daemon
daemon <start|stop|status>   Control the background host that owns CLI sessions
```

`run` honours the script's `launchPath`: it started Pia, replayed both test cases, evaluated the
embedded assertions and closed the app again, leaving no process behind.

```
[PASS] TC-001: Settings General: enable Start minimized to system tray  (1182 ms)
  [pass] #1 ww_invoke  [automationId=NavItem_Settings]
  ...
Result: PASSED  (6 passed, 0 failed, 0 errors, 6 total)
```

Exit codes are CI-shaped: `run` returns 0 on pass, 1 on failure, `heal` returns 2 when it found
something to report. `--format junit --output <file>` emits one `<testsuite>` per test case with
`<error>` / `<skipped>` children. A rotted selector reports precisely
(`[ERR ] #1 ww_invoke [automationId=NavItem_Setings] -- resolved 0 elements`) and, because
`runConfig.maxFailures` is 0 and `continueOnFailure` false, the first failure aborts the run — note
that later test cases are then **dropped from the junit report entirely** rather than marked skipped,
so a CI consumer sees 4 total tests instead of 6.

### The script poisons its own precondition

The second `run` of the *same* passing script failed at step 1 with **"No main window found"**. Cause:
TC-001 sets *Start minimized to system tray*, so the next launch starts hidden in the tray and there
is no main window to drive. Restoring the baseline `settings.json` made it pass again — verified both
ways.

That is the general shape of the problem, not a quirk of this setting: a recording captures actions
and **no preconditions**, while Pia restores `lastActiveView`, window geometry and sidebar-collapsed
state from `settings.json`. TC-001 self-navigates via `NavItem_Settings` and so survives a changed
start view, but nothing in the artifact asserts or normalizes the start state. Any recorded script
that mutates persisted app state has to be paired with a seeded `settings.json` (or a first step that
restores the state it depends on) before it can run twice.

## `ww_heal_script`

Its doc string names a non-existent `ww_export_script`, but the round trip works: the export feeds
straight in. Three probes:

1. **Healthy script, app parked on the target tab** — 6/6 `ok`. This is the shippable use: a cheap
   guard that a renamed AutomationId broke a walkthrough.
2. **Deliberately rotted script** — the one-character typo `NavItem_Setings` scored **0.562** and
   came back `suggested`, not healed, because the default `minConfidence` is 0.70. Worse, both
   damaged *name-based* selectors (`'Start minimised…'` against the live `'Start minimized…'`, ~83 %
   token overlap) came back **`unresolvable` — "No similar elements found"**. Candidate scoring
   appears to consider only elements that *have* an AutomationId, so healing is useless for exactly
   the controls Pia's settings views expose (name only). Heal also never inspects
   `assertion.selector`, so a rotted assertion is invisible to it.
3. **Healthy script, app parked on the Assistant view** — 3 of the 4 good steps reported
   `unresolvable`. Heal probes *the UI as it is right now*, so it can only validate steps whose
   targets are currently on screen. For any multi-screen script a single heal pass necessarily
   produces false alarms; healing per step during playback needs — again — a runner.

Healed selectors are emitted in `#Id` shorthand rather than the `automationId=Id` form the recorder
writes. Both parse; the inconsistency is cosmetic.

The CLI `heal` has the same blind spot, and it is more visible there: run on the rotted script it
launched Pia, probed from the app's *start* screen and reported `ok=0 healed=0 suggested=1
unresolvable=5` — the four undamaged selectors were flagged too, simply because their targets were not
on screen yet. `run` has no `--heal` option, so per-step healing during playback is not available.

## Pia-side blockers

- **Every settings view has zero AutomationIds** — `GeneralView.xaml`, `AssistantView.xaml` and
  friends. Only the category list, the inner tabs and the edit dialogs got ids in the earlier
  automation pass. Checkboxes survive on their `Content`-derived `Name`, which makes recorded scripts
  locale-fragile (they break the moment the UI language changes) and duplicate-prone
  (`Settings_Agent_AutoApproveBuiltInWrites` renders twice in `AssistantView.xaml`).
- **The Speech-to-Text engine and language ComboBoxes cannot be targeted at all.** Both have an empty
  `Name` *and* an empty `AutomationId`, two of them sit on the same tab, and the selector grammar has
  no ordinal/index attribute (supported keys: `automationId, name, class/className, helpText,
  frameworkId, type, isEnabled, isOffscreen, pid, visible, accessible_name, accessible_role,
  localizedType, value`). `ww_inspect find_by_description` scores names only, so it returns the label
  `Text` elements and never the combo. Recording that change is impossible until the XAML carries
  ids — the one concrete product fix this evaluation asks for.
- **`ww_snapshot`'s `label` is not a selectable `name`.** The snapshot shows the language combo as
  `label: "Speech-to-Text Language"` (inferred from the neighbouring TextBlock), but
  `type=ComboBox[name='Speech-to-Text Language']` resolves 0 elements. Do not build selectors from
  snapshot labels.
- **`ww_get_tree_path` output is not a usable selector**, despite claiming to be "suitable for use as
  a selector": it emits ordinals (`type=Custom[1] >> … >> type=ComboBox[0]`) that the selector parser
  rejects with `Expected attribute key inside '[...]'`. WinWright bug, not a Pia one.
- **`ww_inspect label_map` mis-associates the settings checkboxes.** Its `SpatialAbove` heuristic
  pairs each checkbox with the *previous* setting's description TextBlock, so the map reads
  plausibly and is wrong by one row.
- Re-confirmed the playbook's value-filter trap: `type=ComboBox[value='Auto']` resolves 0 while the
  snapshot reports `value: "Auto"`.

## Recommendations

1. **Add AutomationIds to the settings views** (start with `GeneralView.xaml`'s ComboBoxes and
   CheckBoxes, `Settings_Field_*`-style). This is the precondition for recording anything in
   Settings, and it also unblocks `ww_heal_script`, which ignores name-only elements.
2. **Record with replay in mind**: `test_start` / `test_end` around each scenario, `ww_assert_value`
   for every check worth keeping, `record: false` on discovery calls, never `stop` to peek, and `pop`
   any step whose success you did not verify.
3. **Give each script a known start state.** Seed `%APPDATA%\Pia\settings.json` from a checked-in
   fixture before `run`, or have the script's first steps restore what it changes. Without that, a
   script that touches persisted state passes once.
4. **`winwright run --format junit` is CI-ready today** — exit 0/1 and a junit file per run — so a UI
   regression suite is a matter of committing scripts plus a settings fixture, not of building a
   runner. Remember that an aborted run omits the remaining test cases from the report; one script per
   scenario keeps that honest. Both recordings from this evaluation now live in `tests/ui-scripts/`
   with that fixture and a replay harness (`Invoke-UiScripts.ps1`); see its README.
5. **Treat heal as a per-state selector check**, run while the app sits on the screen the steps target,
   with `minConfidence` near 0.55 if typo-level ids should auto-heal. It cannot help name-only
   controls, which is another reason for recommendation 1.
