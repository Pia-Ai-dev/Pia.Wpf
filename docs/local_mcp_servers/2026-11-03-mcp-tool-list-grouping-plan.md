# MCP Tool List Grouping Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status:** Proposed · **Owner:** man · **Written:** 2026-11-03 · **Size:** S (no checklist file)
**Origin:** Owner brainstorm, 2026-11-03. Settings → MCP servers → a server's detail pane. It was "hard to
understand which tools are enabled and which are disabled". The owner approved the description below,
including every guess in it.

**Goal:** Make the read-only tool list on an MCP server's detail pane show at a glance what the assistant
can use. Enabled tools are listed first, disabled tools are folded away below.

**Architecture:** The view model splits the detail list into two sorted collections and exposes the few
flags the view needs, including the fold state. The XAML renders an always-open "offered" group and a
collapsed `Expander` for the withheld group. The edit form keeps its checkbox list and only gains the same
A–Z order and the same quieter "may delete" chip.

**Tech Stack:** WPF on net10.0-windows, WPF-UI 4.3.0, CommunityToolkit.Mvvm, xunit v3, NSubstitute.

**Spec:** this document. The approved description follows.

### Approved description

Today a tool counts as "on" only because it lacks a "gesperrt" chip. The tools come in server order, so on
and off are mixed. The chips sit wherever each name ends.

Read-only list on the detail pane, top to bottom:

- **Freigegeben · N.** Always open. Row: green check icon · tool name · `?` help hint · right-aligned
  "kann löschen" chip if the server declares the tool destructive.
- **Gesperrt · N · K davon können löschen.** Starts **collapsed**; one click opens it. Rows have a grey
  dash icon and a greyed name. When K = 0, "· K davon können löschen" is left out.
- Both groups are sorted A–Z.
- "kann löschen" becomes a quiet outlined chip, not the filled orange badge.
- The header line "3 von 13 Tool(s) freigegeben" is unchanged.
- Picking another server folds the withheld group again. A reload or a status tick does not.

Edge cases:

- Every tool enabled (no allowlist, or all ticked): no Gesperrt group.
- No tool enabled: the offered group shows one line, "Kein Tool freigegeben – über Bearbeiten ändern".
  The Gesperrt group stays folded.
- Server stopped: only the saved allowlist is known. It shows as the offered group, sorted, with no
  Gesperrt group, and the existing "not running" hint stays.

Non-goals:

- Toggling tools in place. Editing stays behind **Bearbeiten**.
- Grouping by name prefix.
- Showing descriptions inline.
- Changing the edit form, apart from A–Z order and the chip style.

## Global Constraints

- Branch `feature/mcp-tool-list-grouping` from `develop` via the `release-flow` skill. Do not commit on
  `feature/installer-vcredist-and-recall`.
- Every new string goes into all three of `ViewStrings.resx`, `ViewStrings.de.resx` and
  `ViewStrings.fr.resx`, or `LocalizationTests.AllTranslations_MustBeComplete` fails.
- Colours come only from `DynamicResource` theme brushes (`PiaSuccessBrush`, `TextMutedBrush`,
  `TextSubtleBrush`, `WarnBrush`, `WarnSoftBrush`, `WarnBorderBrush`). No hex values, because dark theme
  must work.
- Sort with `OrderBy(name, StringComparer.OrdinalIgnoreCase)`, which is stable. Allowlist membership stays
  `StringComparer.Ordinal`.
- Comment discipline (CLAUDE.md):
  - At most one short WHY line.
  - No history and no task IDs.
  - Bring touched members' existing summaries into line.
- Zero warnings: `dotnet build -t:Rebuild -v:n` in Debug **and** `-c Release` must both report
  `0 Warning(s)` and `0 Error(s)`.
- Test gate: the built exe, not `dotnet test`:
  - Run `tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe > test.log 2>&1`.
  - It passes on `Failed: 0`.
  - Use one filter kind per run.
- Do not touch `docs/release_notes/RELEASE.md` in this feature PR.

## Review Focus

1. **Long tool names in a narrow window.** The name must trim with an ellipsis rather than push the chip
   out of view. Pinned by Task 3 Step 2.
2. **Dark theme.** Check the icons, the greyed names and the chip in dark theme. Pinned by Task 3 Step 2.
3. **A status tick while the group is open.** `PluginsChanged` re-selects the same server through a null
   write from the ListBox, and that must not fold the group. Pinned by
   `AReload_LeavesAnOpenedWithheldGroupOpen` (Task 1).
4. **A re-probe from the detail pane** (`TestSelectedCommand`) refills the lists and must keep the fold
   state. Pinned by `TestingFromTheDetailPane_LeavesTheWithheldGroupAsItWas` (Task 1).
5. **Screen readers.** The Expander must read as "Gesperrt" (its `AutomationProperties.Name`), not as the
   whole header text, and keep a stable `AutomationId`. Pinned by `ViewAutomationIdTests` (Task 2) and
   Task 3 Step 2.

---

### Task 0: Branch

- [ ] **Step 1:** Invoke `release-flow` → `start mcp-tool-list-grouping`. This runs
  `git switch -c feature/mcp-tool-list-grouping origin/develop`.
  - `git switch` carries uncommitted changes over. Check `git status` first.
  - If tracked files belonging to another branch are modified, do not switch there. Use a worktree
    instead (`superpowers:using-git-worktrees`) and copy this plan doc into it.
- [ ] **Step 2:** Commit this plan doc as the first commit:
  `git add docs/local_mcp_servers/2026-11-03-mcp-tool-list-grouping-plan.md && git commit -m "docs: plan MCP tool list grouping"`.

### Task 1: View model: split, sort, summarise, fold state

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/McpServersSettingsViewModel.cs`. Touches `SelectedTools` (l. 50–52),
  `OnSelectedServerChanged` (l. 183–194), `FillSelectedTools` (l. 265–297) and `FillTools` (l. 486–495).
- Modify: `src/Pia.Wpf/ViewModels/Models/McpServerRow.cs`. Delete `McpServerToolInfo.IsWithheld` (l. 64)
  once nothing reads it.
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings{,.de,.fr}.resx`. Add the two summary keys.
- Test: `tests/Pia.Wpf.Tests/ViewModels/McpServersSettingsViewModelTests.cs`.

**Interfaces (Produces):**
- `ObservableCollection<McpServerToolInfo> SelectedAllowedTools { get; }`, sorted A–Z. It **replaces**
  `SelectedTools`.
- `ObservableCollection<McpServerToolInfo> SelectedWithheldTools { get; }`, sorted A–Z.
- `bool HasSelectedTools`. Already exists; true when either collection is non-empty.
- `[ObservableProperty] bool HasWithheldTools`.
- `[ObservableProperty] bool ShowsNoToolAllowed`. True only in the discovered branch, when the allowlist
  is non-null and nothing is allowed.
- `[ObservableProperty] string? WithheldSummary`. Null when there is no withheld tool.
- `[ObservableProperty] bool IsWithheldExpanded`. Defaults to `false`, bound two-way.
- Resource keys:
  - `McpServers_Detail_WithheldSummary`, `{0}` = withheld count.
  - `McpServers_Detail_WithheldSummaryDestructive`, `{0}` = withheld count, `{1}` = how many of those are
    destructive.

- [ ] **Step 1: Point the existing tests at the new collections.** Ordering in the current fixtures is
  already A–Z and allowed-first, so only the property names change:
  - l. 167–168 and 226–227: `Assert.Equal(["create_issue"], vm.SelectedAllowedTools.Select(t => t.Name)); Assert.Equal(["delete_issue"], vm.SelectedWithheldTools.Select(t => t.Name));`
  - l. 179, 242, 292: `vm.SelectedAllowedTools`, and add `Assert.Empty(vm.SelectedWithheldTools)` at
    l. 179.
  - l. 188: `Assert.Empty(vm.SelectedAllowedTools); Assert.Empty(vm.SelectedWithheldTools);`.

- [ ] **Step 2: Write the new failing tests.** Each uses `Create(...)` / `CreateTwo()` and the echoing
  localizer. The `Format` echo is `"key:arg0,arg1"`.

```csharp
// Discovered in server order; allowed = ["me_get_profile", "mail_get_message"]
// tools: mail_move_to_deleted (destructive), me_get_profile, calendar_list_calendars, mail_get_message
[Fact] TheDetailPane_PutsOfferedToolsFirst_AndSortsEachGroup
    Assert.Equal(["mail_get_message", "me_get_profile"], vm.SelectedAllowedTools.Select(t => t.Name));
    Assert.Equal(["calendar_list_calendars", "mail_move_to_deleted"], vm.SelectedWithheldTools.Select(t => t.Name));
    Assert.True(vm.HasWithheldTools);
    Assert.False(vm.ShowsNoToolAllowed);
    Assert.Equal("McpServers_Detail_WithheldSummaryDestructive:2,1", vm.WithheldSummary);

// allowed = ["a"], tools a, b (neither destructive)
[Fact] TheWithheldSummary_LeavesOutTheDeleteCount_WhenNoneCanDelete
    Assert.Equal("McpServers_Detail_WithheldSummary:1", vm.WithheldSummary);

// allowed = null, two discovered tools
[Fact] AnUnrestrictedServer_HasNoWithheldGroup
    Assert.Empty(vm.SelectedWithheldTools); Assert.False(vm.HasWithheldTools); Assert.Null(vm.WithheldSummary);

// allowed = [] (empty list), two discovered tools
[Fact] AServerWithEveryToolWithheld_SaysNoneIsOffered
    Assert.Empty(vm.SelectedAllowedTools); Assert.True(vm.ShowsNoToolAllowed); Assert.True(vm.HasSelectedTools);

// stopped server, allowed = ["zeta", "alpha"]
[Fact] AStoppedServer_ListsItsSavedAllowlist_AToZ
    Assert.Equal(["alpha", "zeta"], vm.SelectedAllowedTools.Select(t => t.Name));
    Assert.False(vm.HasWithheldTools); Assert.False(vm.ShowsNoToolAllowed);

[Fact] PickingAnotherServer_FoldsTheWithheldGroupAgain      // CreateTwo
    vm.IsWithheldExpanded = true; vm.SelectedServer = vm.Servers.Single(s => s.Id == OtherId);
    Assert.False(vm.IsWithheldExpanded);

[Fact] AReload_LeavesAnOpenedWithheldGroupOpen
    // running server with one withheld tool; reproduce the ListBox null write exactly as
    // AReloadWhileTheEditorIsOpen_DoesNotDiscardWhatIsTyped does (Reset → SelectedServer = null)
    vm.IsWithheldExpanded = true; plugins.PluginsChanged += Raise.Event<EventHandler>(plugins, EventArgs.Empty);
    Assert.True(vm.IsWithheldExpanded);

[Fact] TestingFromTheDetailPane_LeavesTheWithheldGroupAsItWas
    vm.IsWithheldExpanded = true; await vm.TestSelectedCommand.ExecuteAsync(null);
    Assert.True(vm.IsWithheldExpanded);

// running server, discovered [zeta, alpha], allowed null
[Fact] TheEditorListsToolsAToZ
    vm.EditSelectedCommand.Execute(null);
    Assert.Equal(["alpha", "zeta"], vm.Tools.Select(t => t.Name));
```

- [ ] **Step 3: Run them and see them fail** (compile errors count):
  - `dotnet build tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj`
  - `tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe -class "Pia.Tests.ViewModels.McpServersSettingsViewModelTests" > test.log 2>&1`

  Expected: build errors on the new members, or failures.

- [ ] **Step 4: Implement.**
  - `FillSelectedTools`:
    1. Clear both collections.
    2. Order the input with `OrderBy(…, OrdinalIgnoreCase)`.
    3. Route each tool by `IsAllowed`.
    4. Set `HasWithheldTools`, `ShowsNoToolAllowed` and `WithheldSummary`. The summary picks the
       `…Destructive` key only when the destructive count among withheld tools is > 0.
    5. The stopped-server branch fills only `SelectedAllowedTools`, sorted.
    6. The unknown branch clears every flag.
  - `OnSelectedServerChanged`: reset `IsWithheldExpanded = false` under the same guard the editor close
    uses, `!_reloading && oldValue?.Id != newValue?.Id`, with one WHY line. Do **not** reset it in
    `RefreshDetail` or `FillSelectedTools`.
  - `FillTools`: iterate `discovered.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)`.
  - Delete `SelectedTools` and its summary, and delete `McpServerToolInfo.IsWithheld`.
  - Add the resx keys:

    | key | en | de | fr |
    |---|---|---|---|
    | `McpServers_Detail_WithheldSummary` | `{0} tool(s)` | `{0} Tool(s)` | `{0} outil(s)` |
    | `McpServers_Detail_WithheldSummaryDestructive` | `{0} tool(s) · {1} of them may delete` | `{0} Tool(s) · {1} davon können löschen` | `{0} outil(s) · {1} peuvent supprimer` |

- [ ] **Step 5: Run the class again.** Expected: `Failed: 0`. Then also run
  `-class "Pia.Tests.Architecture.LocalizationTests"` and expect `Failed: 0`.
- [ ] **Step 6: Commit.** `feat: split the MCP detail tool list into offered and withheld, sorted A–Z`.

### Task 2: View: grouped detail list, folded withheld group, quiet chip

**Files:**
- Modify: `src/Pia.Wpf/Views/SettingsViews/McpServersView.xaml`. Covers the `UserControl.Resources`
  (l. 7–9), the detail tool list (l. 230–256) and the badge in the edit row (l. 469–473).
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings{,.de,.fr}.resx`.
- Test: `tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs:65`.

**Interfaces (Consumes):** everything in Task 1's Produces block. This task only binds; it adds no code.

- [ ] **Step 1: Raise the AutomationId floor.** Change the McpServersView row's `minimumInspected` from
  `18` to `19`, because the Expander is a new interactive control. The value is a floor, not a count.
  Run `-class "Pia.Tests.Views.ViewAutomationIdTests"`: the McpServersView case fails if the view
  measures exactly 18 today and passes if it measures more. Either way it must pass after Step 3.
- [ ] **Step 2: Add the shared resources to `UserControl.Resources`.**
  - Style `McpDestructiveChip`:
    - `TargetType="Border"`, `BasedOn="{StaticResource PiaLabelChipStyle}"`.
    - `BorderBrush` = `WarnBorderBrush`, `BorderThickness` = `1`, `Background` = `WarnSoftBrush`.
    - Its child TextBlock: `Text="{loc:Str McpServers_Destructive}"`, `FontSize="11"`,
      `Foreground` = `WarnBrush`.
  - DataTemplate `McpDetailToolRow` for `McpServerToolInfo`:
    - A `Grid` with columns `Auto` (icon) · `*` (name and help hint) · `Auto` (chip).
    - Icon: `ui:SymbolIcon`, `FontSize="14"`.
      - `IsAllowed`: `CheckmarkCircle16`, `Filled="True"`, `PiaSuccessBrush`.
      - Otherwise: `SubtractCircle16`, `TextSubtleBrush`.
    - Name: Consolas 12, `TextTrimming="CharacterEllipsis"`.
      - `IsAllowed`: `TextDefaultBrush`. Otherwise: `TextMutedBrush`.
    - Put the name and `shared:PiaHelpHint` in a left-aligned inner Grid with columns `*` (name) and
      `Auto` (hint). That way the name trims instead of overflowing, and the hint stays right after it.
      A StackPanel would never trim.
    - The chip goes in the last column, visible on `ServerDeclaredDestructive`.
    - Switch on `IsAllowed` with DataTriggers.
- [ ] **Step 3: Replace the detail `ItemsControl` (l. 230–256)** with, in order:
  1. Group header row: `{loc:Str McpServers_Detail_AllowedGroup}` (SemiBold 12) plus
     `{Binding SelectedAllowedTools.Count}` (`TextMutedBrush`). Visible on `HasSelectedTools`.
  2. `ItemsControl ItemsSource="{Binding SelectedAllowedTools}"`, `ItemTemplate` = `McpDetailToolRow`,
     `AutomationId="McpServers_Detail_AllowedTools"`.
  3. `TextBlock {loc:Str McpServers_Detail_NoneAllowed}`, `PiaMetaTextStyle`, visible on
     `ShowsNoToolAllowed`.
  4. A plain `Expander`, following the `RoutinesView.xaml:345` pattern:
     - `IsExpanded="{Binding IsWithheldExpanded, Mode=TwoWay}"`
     - `AutomationProperties.AutomationId="McpServers_Detail_WithheldGroup"`
     - `AutomationProperties.Name="{loc:Str McpServers_Detail_WithheldGroup}"`
     - Visible on `HasWithheldTools`.
     - Header: `{loc:Str McpServers_Detail_WithheldGroup}` (SemiBold 12) plus
       `{Binding WithheldSummary}` (`TextMutedBrush`, left margin 8).
     - Content: an `ItemsControl` bound to `SelectedWithheldTools` with `McpDetailToolRow`,
       `AutomationId="McpServers_Detail_WithheldTools"`.
- [ ] **Step 4: Swap the edit row's badge (l. 469–473)** for a `Border Style="{StaticResource McpDestructiveChip}"`
  with the same Visibility binding. The checkbox row and its ids stay as they are.
- [ ] **Step 5: Update the strings.**
  - Delete `McpServers_Detail_Withheld` from all three resx files; nothing reads it any more.
  - Add:

    | key | en | de | fr |
    |---|---|---|---|
    | `McpServers_Detail_AllowedGroup` | `Offered` | `Freigegeben` | `Proposés` |
    | `McpServers_Detail_WithheldGroup` | `Withheld` | `Gesperrt` | `Bloqués` |
    | `McpServers_Detail_NoneAllowed` | `No tool offered – change this under Edit` | `Kein Tool freigegeben – über Bearbeiten ändern` | `Aucun outil proposé – modifiable via Modifier` |

- [ ] **Step 6: Run the tests.** Run `-class "Pia.Tests.Views.ViewAutomationIdTests"`, then
  `-class "Pia.Tests.Architecture.LocalizationTests"`. Expected: `Failed: 0` for each.
- [ ] **Step 7: Commit.** `feat: fold withheld MCP tools away and give each row a status mark`.

### Task 3: Gate and look

- [ ] **Step 1: Run the zero-warning check.** Run `dotnet build -t:Rebuild -v:n` and then
  `dotnet build -t:Rebuild -v:n -c Release`. Expected: `0 Warning(s)` and `0 Error(s)` on both summary
  lines.
- [ ] **Step 2: Do the visual check in the real app.**
  - **The owner does this check on their own profile**, because it needs real MCP servers. An agent
    must not launch or drive the app against the real profile. If an agent runs it, it uses a
    throwaway profile (`PIA_DATA_DIR` / `PIA_LOCAL_DATA_DIR`, see
    `docs/ui_automation/winwright-setup.md`) with one test server added.
  - Run `dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj`, then open Settings → MCP servers.
  - Check each of these in **light and dark** theme:
    1. A server with some tools withheld: offered first, withheld folded, the chip aligned right.
    2. The same server, expanded, then wait for a status tick: it stays open.
    3. Pick another server and come back: it is folded.
    4. An unrestricted server: no withheld group.
    5. A stopped server.
    6. Narrow the window until a long name trims with `…` and the chip stays visible.
  - Optionally, inspect the Expander with WinWright `ww_inspect`: it should report Name "Gesperrt" and
    id `McpServers_Detail_WithheldGroup`.
  - Take a before/after screenshot for the PR.
- [ ] **Step 3: Run the full gate.**
  - Run `tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe > test.log 2>&1`,
    then `grep -E "TEST EXECUTION SUMMARY" -A 2 test.log`.
  - Expected: `Failed: 0`.
- [ ] **Step 4: Open the PR.** Use `release-flow` → `pr` into `develop`. The PR description must:
  - Say that no client→server request changed.
  - Note that the Pia.Docs desktop guide may still describe the "gesperrt"/"withheld" chip. The
    `help-corpus` skill checks this on the next `release/*` branch.
