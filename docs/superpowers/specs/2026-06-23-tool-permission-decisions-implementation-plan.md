# N-option Tool Permissions — Implementation Plan (Spec 2)

> **For agentic workers:** REQUIRED: use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax for tracking.
> **⚠ Security gate:** changes the security boundary (tool calls can auto-execute). Run `/security-review` before merge using the checklist in `2026-06-23-tool-permission-decisions-design.md` §11.

**Goal:** Replace the binary tool-confirmation gate with the triad Allow once / Always allow / Decline; persist per-`(PluginId,ToolName)` grants; auto-bypass *only* deny-by-default-eligible tools, always rendering an auto-approved card; add a revocation UI.

**Architecture:** A singleton `IToolPermissionService` owns grants (in `AppSettings`) and the eligibility allowlist. `PluginToolCall` gains `PluginId`. `ActionCardInfo`'s gate goes `bool → ToolDecision`. `ChatSession.HandleToolCall` checks eligibility+grant for a pre-gate bypass, else awaits the triad. A settings VM lists/revokes grants.

**Tech Stack:** WPF / net10.0-windows, CommunityToolkit.Mvvm, `ISettingsService` (JSON), xunit.v3 + NSubstitute.

Derived from `2026-06-23-tool-permission-decisions-design.md` + ground-truthed recon. **Depends on Spec 1** (`CardDecisionBar`, `ActionCardInfo.Decisions`). Branch: `feature/snackbar_rework`.

## Key recon findings (ground-truthed)

1. **ChatSession** ctor (`ViewModels/Models/ChatSession.cs:82-99`): `ITokenMapService, IAiClientService, IPluginService, IActionCardBuilder, ILocalizationService, ILogger, Func<ChatSession,bool> isActive`. Built in `ChatSessionManager.CreateSession` (`ChatSessionManager.cs:100-125`); both **scoped**. Gate `HandleToolCall` (404-487): `_actionCardBuilder.Build(pendingAction, tokenizationEnabled)` (437); `confirmed = await card.WaitForUserDecisionAsync()` (445); accepted branch `Execute()` + `ToolSucceeded` + memory token re-init + `return actionResult` (459-479); decline string (483).
2. **ActionCardInfo** (`Models/ActionCardInfo.cs`): `TaskCompletionSource<bool> _tcs` (55), `WaitForUserDecisionAsync()` (64), `Accept`/`Decline`/`Cancel` `[RelayCommand]` (66-91), `IsDestructive`, `ToolName`, `IsResolved`, `ResolvedStatusText`.
3. **ActionCardBuilder** (`Services/ActionCardBuilder.cs`): `Build(PluginToolCall pendingAction, bool detokenize)` (24); `isDelete = pendingAction.ToolName.Contains("delete")` (35); `IActionCardBuilder` has `Build`, `ResolveStatusText`, `ResolveSuccessTitle`. Tool name groups (105-113): create_object/create_todo/create_reminder; update_object/append_to_list/update_todo/update_reminder; delete_*; complete_todo; write_file.
4. **PluginToolCall** (`Services/Interfaces/IPluginToolHandler.cs:6-11`): `(ToolName, PluginName, Description, Details, Execute)` — **no `PluginId`**; the handler interface exposes `PluginId` (Guid). Constructed at **6 sites** in `BuiltInPluginHandler.cs` (88-89 memory, 109-110 todo, 130-131 reminder, 151-152 job, 172-173 research-history, 196-197 files), each passing `config.Name`; `config.Id` (Guid) is available. `PluginService.RouteToolCallAsync` (265-284) has the handler in hand (PluginId known, already logged at 282); `_toolNameRoutes` = `Dictionary<string, IPluginToolHandler>` (29) → simultaneous name collisions impossible.
5. **FilesToolHandler** (`Services/FilesToolHandler.cs`): `write_file`→`PrepareWriteFile` returns a `FilesToolCall` (own record, `IFilesToolHandler.cs`), adapted to `PluginToolCall` at `BuiltInPluginHandler.cs:196-197`; `delete_file`→`PrepareDeleteFile`. **`write_file` is overwrite-class → ineligible** (no "delete" in name, so the existing heuristic misses it).
6. **Settings**: `ISettingsService` = `GetSettingsAsync`/`SaveSettingsAsync`/`SettingsChanged` (+draft). `SettingsService : JsonPersistenceService<AppSettings>` writes camelCase JSON to `%AppData%/Pia/settings.json`, raises `SettingsChanged` after save. `AppSettings.AllowedSyncProviders` (95) is the `List<>` precedent.
7. **Settings UI**: `SettingsViewModel` composes child VMs (`AssistantVm`, `AccountVm`, `PluginsVm`, …); `AssistantSettingsViewModel` has an inner-tab pattern. `ObservableCollection<T>` + `[RelayCommand]` list pattern used in `AccountSettingsViewModel` (device list).
8. **DI** (`Bootstrapper.cs`): `AddSingleton<ISettingsService, SettingsService>` (258), `AddScoped<IActionCardBuilder, ActionCardBuilder>` (267), `AddScoped<IChatSessionManager, ChatSessionManager>` (336). DEBUG `ValidateScopes=true` → a singleton may not inject a scoped dep. `IToolPermissionService` → **Singleton** injecting only `ISettingsService` (singleton) + `IPluginService` (singleton, for display names) — safe; scoped `ChatSession` injecting the singleton is fine.
9. **Tests**: `ChatSessionStateMachineTests` builds `new ChatSession(_tokenMap,_ai,_plugins,_cards,_loc,NullLogger.Instance,_=>true)` (34-39); `_cards.Build(Arg.Any<PluginToolCall>(),Arg.Any<bool>()).Returns(card)` (191-198); drives `card.AcceptCommand.Execute(null)` (235); asserts `StateChanged` sequence. NSubstitute + xunit.v3.

## Design decisions (refine the spec)

- **`ToolDecision`** (enum, `Models/`): `AllowOnce, AlwaysAllow, Decline`. Cancel remains a separate `TrySetCanceled` path.
- **`ToolGrant`** (record, `Models/`): `(Guid PluginId, string ToolName, DateTimeOffset GrantedAt)`.
- **`AppSettings.AlwaysAllowedTools : List<ToolGrant> = new()`** (camelCase JSON via existing pipeline).
- **`IToolPermissionService` / `ToolPermissionService` (Singleton)**:
  - `bool IsAutoApproveEligible(string toolName)` — **deny-by-default allowlist** `{ create_object, create_todo, create_reminder, append_to_list }` (additive only; `update_*`, `complete_todo`, `write_file`, `delete_*` excluded). *Final set signed off at `/security-review`.* (Lives here, not a separate `*Policy` type, to satisfy the Services naming-suffix rule.)
  - `bool IsGranted(Guid pluginId, string toolName)` — cached `HashSet<(Guid,string)>` loaded from `AppSettings`.
  - `Task GrantAsync(...)` / `Task RevokeAsync(...)` — mutate `AppSettings.AlwaysAllowedTools` and `SaveSettingsAsync`; refresh cache on `SettingsChanged`.
  - `IReadOnlyList<ToolGrant> List()` + `event EventHandler? Changed`.
- **`PluginToolCall` += `Guid PluginId`**; all 6 `BuiltInPluginHandler` sites pass `config.Id` (the Files adapter too).
- **`ActionCardInfo`**: `_tcs` → `TaskCompletionSource<ToolDecision>`; `WaitForUserDecisionAsync() : Task<ToolDecision>`; replace `Accept` with `AllowOnce` (`TrySetResult(AllowOnce)`) and `AlwaysAllow` (`TrySetResult(AlwaysAllow)`); `Decline`→`TrySetResult(Decline)`; `Cancel` unchanged. Add `PluginId`, `IsAutoApprovable` (eligibility), `IsAutoApproved` (resolved bypass), and an auto-approved variant of `ResolvedStatusText`. **`Decisions`** (from Spec 1) now derives from `IsAutoApprovable`: eligible → `[Decline(Default), AllowOnce(Primary), AlwaysAllow(Default)]`; ineligible → `[Decline(Default), AllowOnce(IsDestructive?Danger:Primary)]`. **Never key the button set off `IsDestructive`** (write_file is ineligible yet not "destructive").
- **`ActionCardBuilder.Build(PluginToolCall, bool detokenize, bool autoApproved = false)`**: carry `PluginId`; set `IsAutoApprovable = permissions.IsAutoApproveEligible(toolName)`; when `autoApproved`, return the card pre-resolved (`State=Accepted`, `IsAutoApproved=true`). Inject `IToolPermissionService` into the builder for eligibility. (`IsDestructive` stays = delete heuristic, for the red warning only.)
- **Gate rewrite** (`ChatSession.HandleToolCall`, per design §4): extract `Task<object?> ExecuteAndReport(pendingAction)` (the current 459-479 body). Then:
  - `eligible = _permissions.IsAutoApproveEligible(tool)`; if `eligible && _permissions.IsGranted(pluginId, tool)` → `Build(...,autoApproved:true)`, add card, log bypass (tool + pluginId, **no args**), `return await ExecuteAndReport(...)`.
  - else add card, `decision = await WaitForUserDecisionAsync()`; `AllowOnce`→`ExecuteAndReport`; `AlwaysAllow`→ if `!eligible` treat as AllowOnce (defensive: never grant ineligible) else `await _permissions.GrantAsync(pluginId, tool)` then `ExecuteAndReport`; `Decline`→ unchanged decline string. `TaskCanceledException`→ decline path (unchanged).
- **Revocation UI**: `ViewModels/ToolPermissionsSettingsViewModel.cs` (injected deps in `readonly` fields). `ToolGrantRow` is a **plain `record (Guid PluginId, string PluginName, string ToolName, DateTimeOffset GrantedAt)`** (not an `ObservableObject` — keeps it clear of `ViewModels_MustEndWith_ViewModel`); `RevokeCommand` lives on the VM and takes the row as parameter → `RevokeAsync(row.PluginId, row.ToolName)`. The VM exposes `ObservableCollection<ToolGrantRow>` (PluginName via `IPluginService`, ToolName, GrantedAt) and refreshes on `Changed`. Surface as an inner section of `AssistantSettingsViewModel` (or a new "Security" tab in `SettingsViewModel`) — UX placement is an open question. View: a simple list with a Revoke button per row + an empty-state.

## File plan

**Create** — `Models/ToolDecision.cs`, `Models/ToolGrant.cs`, `Services/Interfaces/IToolPermissionService.cs`, `Services/ToolPermissionService.cs`, `ViewModels/ToolPermissionsSettingsViewModel.cs` (+ view section), tests: `Services/ToolPermissionServiceTests.cs`, extend `ViewModels/ChatSessionStateMachineTests.cs`, `Services/ActionCardBuilderTests.cs`.

**Modify** — `Services/Interfaces/IPluginToolHandler.cs` (PluginId), `Services/Plugins/BuiltInPluginHandler.cs` (6 sites), `Models/ActionCardInfo.cs`, `Services/ActionCardBuilder.cs` + `IActionCardBuilder.cs`, `ViewModels/Models/ChatSession.cs` + `ChatSessionManager.cs`, `Models/AppSettings.cs`, `ViewModels/SettingsViewModel.cs` (+ `AssistantSettingsViewModel.cs`), `Controls/ActionCardControl.xaml` (auto-approved resolved visuals), `Bootstrapper.cs`, `Resources/Strings/ViewStrings{,.de,.fr}.resx` (`ActionCard_AllowOnce`, `ActionCard_AlwaysAllow`, `ActionCard_AutoApproved`, permissions-settings strings).

## Task sequence

### Chunk 1 — Grant store + eligibility (TDD, no UI/gate yet)
- [ ] Add `ToolDecision`, `ToolGrant`, `AppSettings.AlwaysAllowedTools`.
- [ ] **Test** (`ToolPermissionServiceTests`, fake `ISettingsService`): `IsAutoApproveEligible` true for `create_object/create_todo/create_reminder/append_to_list`, false for `update_object/complete_todo/write_file/delete_object/delete_file`. Run → fails.
- [ ] **Test**: `GrantAsync` persists; `IsGranted` reads back; `RevokeAsync` removes; `(PluginIdA,"X")` vs `(PluginIdB,"X")` independent; reload from `AppSettings` on construct/`SettingsChanged`.
- [ ] Implement `ToolPermissionService`; register Singleton in `Bootstrapper`. Run → passes. `dotnet build`. Commit.

### Chunk 2 — PluginId threading
- [ ] Add `Guid PluginId` to `PluginToolCall`; update all 6 `BuiltInPluginHandler` sites (`config.Id`). `dotnet build` (compile-driven; fix every call site). Commit.

### Chunk 3 — ActionCardInfo + builder (bool→ToolDecision)
- [ ] Change `_tcs`→`ToolDecision`; split commands (`AllowOnce`/`AlwaysAllow`/`Decline`/`Cancel`); add `PluginId`/`IsAutoApprovable`/`IsAutoApproved` + auto-approved status text; `Decisions` keyed off `IsAutoApprovable`.
- [ ] Add the triad/auto-approved resx strings **now** (not Chunk 5): `ActionCard_AllowOnce`, `ActionCard_AlwaysAllow`, `ActionCard_AutoApproved` in all three locales — the builder resolves these labels here (`ActionCardInfo` is a Model and cannot inject `ILocalizationService` — `LayerDependencyTests`; labels are passed in by the builder). Omitting them now red-flags `AllCodeLocalizationKeys_MustExistInResources` at the next full-suite run.
- [ ] `ActionCardBuilder`: inject `IToolPermissionService` + `ILocalizationService`; carry `PluginId`; set `IsAutoApprovable`; resolve triad labels; add `autoApproved` param (pre-resolved card).
- [ ] **Test** (`ActionCardBuilderTests`): eligible tool → Decisions = triad; `write_file`/`delete_file` → `[Decline, AllowOnce]` (no AlwaysAllow); `autoApproved:true` → `State==Accepted && IsAutoApproved`. Implement; run; `dotnet build`. Commit.

### Chunk 4 — Gate rewrite (the security-critical step)
- [ ] Inject `IToolPermissionService` into `ChatSession` + pass from `ChatSessionManager.CreateSession`.
- [ ] Extract `ExecuteAndReport`; implement the eligibility+grant bypass and the `ToolDecision` switch (design §4). Bypass logs tool+pluginId only (privacy).
- [ ] **Tests** (extend `ChatSessionStateMachineTests`, NSubstitute `IToolPermissionService`):
  - `AllowOnce` → `Execute()` called, result returned (existing accept test adapted to `AllowOnceCommand`).
  - `AlwaysAllow` → `GrantAsync(pluginId,tool)` `.Received()` + `Execute()`.
  - `Decline` → decline string, `Execute()` `.DidNotReceive()`.
  - **Granted + eligible** → no `WaitingForTool`/await; an auto-approved card is added **before** `Execute()`; result returned. *Prove the ordering, not just presence:* the test owns the `PluginToolCall`, so its `Execute` lambda asserts the card is already in `message.ActionCards` **and** in `Accepted`/`IsAutoApproved` state at the moment Execute runs (a post-call `Assert.Contains` would pass vacuously).
  - **Forged grant on ineligible** (`IsGranted` returns true for `write_file`) → **not** auto-bypassed; user is still prompted; `AlwaysAllow` on it degrades to AllowOnce (no `GrantAsync`).
- [ ] Run; `dotnet build`. Commit.

### Chunk 5 — UI (cards + revocation)
- [ ] `ActionCardControl.xaml`: auto-approved resolved visuals ("Auto-approved · you always allow {tool}" + Manage link). Triad already renders via `CardDecisionBar` (Spec 1) bound to `Decisions`.
- [ ] `ToolPermissionsSettingsViewModel` + view section (list + Revoke + empty state); wire into Assistant settings (or new Security tab); resolve plugin display names via `IPluginService`.
- [ ] Add the **permissions-settings** resx strings (×3) (the `ActionCard_*` triad strings were added in Chunk 3). `dotnet build` + full suite. Manual smoke: grant a `create_todo` → next call auto-approves with a visible card; revoke in settings → it prompts again; `write_file` never shows "Always allow". Commit.

## Tests (summary)
ToolPermissionService: eligibility allowlist, grant/revoke/persist/reload, `(PluginId,ToolName)` keying. Gate: each `ToolDecision` outcome, grant-bypass renders auto-approved card before Execute, ineligible never auto-bypassed/granted even with a forged grant, decline string unchanged. Builder: triad vs pair by eligibility, autoApproved pre-resolve. Privacy: bypass log carries no arguments.

## Open questions / for `/security-review`
1. **Final auto-approve safe set** sign-off (start: create-only + append). Include `update_*`/`complete_todo`? Default: no.
2. **Permissions UI placement** — Assistant inner tab vs a new top-level "Security" tab.
3. **Pre-existing finding (not changed here):** MCP write tools auto-execute ungated today (`McpPluginToolHandler` returns null pendingAction) — flag to the reviewer; gating MCP is a separate effort.
4. **Revoke-vs-in-flight TOCTOU** — one already-decided bypass may still run after a mid-turn revoke; accepted.
