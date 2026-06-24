# N-option Tool Permissions — Design Spec (Spec 2 of 2)

- **Date:** 2026-06-23
- **Status:** Approved (brainstorm) — ready for implementation planning
- **Branch:** `feature/snackbar_rework`
- **Author:** Marco Altmann (with Claude Code)
- **Depends on:** `2026-06-23-multi-action-cards-design.md` (Spec 1) — consumes `CardDecisionBar`.
- **⚠ Security gate:** This spec changes the app's **security boundary** (tool calls can execute without a per-call prompt). It MUST pass the `/security-review` skill before merge. §3 is the threat model; §11 is the review checklist.

## 1. Problem & goal

Every **built-in** write tool is gated by a per-call confirmation: a built-in handler returns a `pendingAction`, and the run loop awaits `card.WaitForUserDecisionAsync()` (a `Task<bool>`) before calling `pendingAction.Execute()` (`ChatSession.cs:435-484`). For tools the user trusts and uses constantly (e.g. `create_todo`), confirming every single call is fatigue with no added safety.

**Important scope fact (verified):** this gate covers **built-in tools only**. `McpPluginToolHandler.HandleToolCallAsync` always returns `(resultText, null)` (`McpPluginToolHandler.cs:96/118/124`) — it never yields a `pendingAction`, so MCP tools **never reach the gate and already auto-execute with no confirmation today**. REST plugins are not implemented (`PluginService.cs` route is a TODO). Therefore everything in this spec — triad, grants, bypass — applies to **built-in write tools only**. The pre-existing ungated MCP path is a separate, larger concern called out in §3/§11, not solved here.

**Goal:** turn the binary Accept/Decline into the standard permission **triad — Allow once / Always allow / Decline** — and let "Always allow" persist a standing grant so future calls to that specific tool skip the prompt, **without** weakening the boundary for destructive operations and **without** silent, untraceable execution.

## 2. Decisions captured during brainstorming

| # | Decision | Choice |
|---|----------|--------|
| Option set | What buttons | **Triad: Allow once / Always allow / Decline.** No "Never allow" denial-list in v1. |
| Grant granularity | What a grant covers | **Per tool**, keyed by `(PluginId, ToolName)`. No per-plugin/category scope in v1. |
| Grant key | Identity correctness | **`(PluginId Guid, ToolName)`** — *not* `ToolName` alone. Not because of simultaneous collisions (`_toolNameRoutes` is `Dictionary<string,handler>` — last registration wins, so two active handlers cannot share a name), but for a **temporal** reason: a tool name can rebind to a different plugin across installs/updates, and a name-only grant would then wrongly apply to the new owner. |
| Auto-approve eligibility | Which tools can be "always allowed"? | **Deny-by-default.** A tool is auto-approvable only if explicitly classified **safe (additive, non-overwriting, non-deleting)** — e.g. `create_todo`, `create_reminder`, `create_object`, `append_to_list`. **`delete_*` and overwrite-class tools (`write_file`) are NOT eligible** and always prompt. The `ToolName.Contains("delete")` heuristic (`ActionCardBuilder.cs:35`) is **insufficient** — it misclassifies `write_file` as safe. Enforced **at the gate**, not just hidden in UI. |
| Bypass visibility | Silent execution? | **No.** A granted tool still renders an Action Card in a resolved **"auto-approved"** state — the conversation keeps an audit trace. |
| Persistence | Where grants live | **Global**, in `AppSettings` JSON via `ISettingsService` (mirrors `AllowedSyncProviders`). |
| Revocation | Can the user undo? | **Mandatory** settings surface listing grants with Revoke. A grant you can't see or revoke is a trap. |

## 3. Threat model (the security boundary change)

**Scope:** the gate (and therefore this whole feature) covers **built-in write tools only** — MCP tools bypass the gate entirely today (§1). So "Before/After" below is about built-in tools.

**Before:** no built-in write tool executes without an explicit per-call user click.
**After:** a built-in write tool the user has granted (and that is auto-approve-eligible) executes without a per-call click.

Risks and mitigations:

1. **Auto-approving a data-loss tool.** The existing destructive heuristic is `ToolName.Contains("delete")` (`ActionCardBuilder.cs:35`). `write_file` is a built-in that returns a `pendingAction` (`FilesToolHandler`) yet contains no "delete" → it would be classified safe, become always-allowable, and a granted `write_file` would auto-execute with **model-chosen path and content, silently overwriting** an existing file. → **Deny-by-default eligibility (§2):** only an explicit safe/additive set is auto-approvable; `delete_*` and overwrite-class tools (`write_file`) always prompt. Enforced **at the gate**. The substring heuristic is replaced/augmented (§5).
2. **Silent execution / no audit.** A bypassed call leaving no trace. → Always render a resolved **auto-approved** card in the conversation *before* awaiting `Execute()`; log the bypass (privacy-safe: tool name + plugin id only — never arguments; CLAUDE.md).
3. **Standing grant abused via prompt-injection.** A granted tool can be invoked by the model with **model-chosen arguments** — always-allow grants the *tool*, not the *arguments*. Principal residual risk. → Bounded by: only the user grants; destructive/overwrite tools are ineligible; eligible tools are additive (e.g. `create_todo` cannot delete or overwrite); the user can revoke any time. Documented for the security review; not eliminated.
4. **Grant mis-applies to a re-owned tool name (temporal).** A tool name rebinds to a different plugin across installs/updates. → Key grants by `(PluginId, ToolName)`, never by name alone (§2). (Simultaneous same-name collision is structurally impossible: `_toolNameRoutes` is a name-keyed dictionary.)
5. **Pre-existing: MCP tools auto-execute ungated (NOT introduced here, but adjacent).** MCP write tools already run with no card and no confirmation today (§1). This spec does **not** change that, but the security review should see it — it is a larger gap than the built-in grants this spec adds, and any future "always allow" for MCP would first require gating MCP at all. Out of scope; flagged.
6. **Revoke vs in-flight bypass (TOCTOU).** `IsGranted` is a synchronous read of an in-memory cache; a revoke landing *after* the check but during the same turn can still permit one already-decided bypass. Benign (one extra additive call, user-initiated revoke), but named so the reviewer sees it was considered.
7. **Loss of grant integrity** (settings file tampering). → Same trust model as all other `AppSettings` (local file); out of scope to harden beyond existing settings.

## 4. The decision type & gate change

- **`ToolDecision`** (enum, new) — `AllowOnce · AlwaysAllow · Decline`. (Cancel remains a separate cancellation path.)
- `ActionCardInfo._tcs` changes `TaskCompletionSource<bool>` → `TaskCompletionSource<ToolDecision>`; `WaitForUserDecisionAsync()` returns `Task<ToolDecision>`.
- **Gate logic** (`ChatSession.HandleToolCall`, replacing `ChatSession.cs:435-484`):

  ```
  if (pendingAction is not null):
      eligible = IsAutoApproveEligible(pendingAction)      // deny-by-default safe set; see §5
      if (eligible && permissions.IsGranted(pluginId, toolName)):
          card = builder.Build(..., autoApproved: true)   // resolved "auto-approved" state
          message.ActionCards.Add(card)                   // audit trace BEFORE execute
          log bypass (toolName, pluginId)                  // no args
          return await Execute-and-report(pendingAction)   // same success path as AllowOnce
      card = builder.Build(...)                            // triad if eligible, else Decline/Allow-once pair
      message.ActionCards.Add(card)
      decision = await card.WaitForUserDecisionAsync()     // ToolDecision
      switch decision:
          AllowOnce    -> return Execute-and-report(pendingAction)
          AlwaysAllow  -> if (!eligible) treat as AllowOnce  // defensive: never grant an ineligible tool
                          permissions.Grant(pluginId, toolName); return Execute-and-report(pendingAction)
          Decline      -> return "User declined the {toolName} operation. Do not retry..."  // unchanged string
  ```
  An ineligible tool never offers "Always allow" in the UI **and** the gate refuses to grant or auto-bypass it even if a grant somehow exists — eligibility is checked at the gate, not trusted from the card.

- **`Execute-and-report`** is the existing accepted branch (`ChatSession.cs:461-479`): `Execute()`, fire `ToolSucceeded`, re-init token map for memory writes, return the result. AllowOnce, AlwaysAllow, and auto-approved all share it.
- `WaitingForTool` state, the `Cancel`/`TaskCanceledException` path, and the decline return string are **unchanged**.

## 5. Tool identity & PluginId threading

- `PluginToolCall` (`IPluginToolHandler.cs`) carries `ToolName`, `PluginName`, ... but **not** `PluginId`. Add **`Guid PluginId`**. Each handler/route knows its plugin Guid (built-ins have fixed Guids in `BuiltInPluginDefaults`; `PluginService.RouteToolCallAsync` resolves name→plugin). Thread `PluginId` from the route into `PluginToolCall` and into `ActionCardInfo` (which already carries `ToolName`).
- **Auto-approve eligibility (`IsAutoApproveEligible`).** `ActionCardInfo.IsDestructive` exists but is only `ToolName.Contains("delete")` (`ActionCardBuilder.cs:35`) — too weak (misses `write_file`). Introduce an explicit, **deny-by-default** classification computed at the gate (not trusted from the card): a tool is auto-approve-eligible only if it is in a curated **safe/additive** set (preferred: a capability declared by the handler, e.g. an `IsAdditive`/`IsAutoApprovable` flag on `PluginToolCall`; minimum acceptable: a curated allowlist of safe built-in tool names). `delete_*` and overwrite-class tools (`write_file`) are excluded. The exact final set is confirmed during `/security-review`. The existing `IsDestructive` flag stays for the red warning UI; eligibility is the separate, stricter gate-level property.

## 6. Grant store & persistence

- **`IToolPermissionService`** / `ToolPermissionService` (singleton):
  - `bool IsGranted(Guid pluginId, string toolName)`
  - `Task GrantAsync(Guid pluginId, string toolName)`
  - `Task RevokeAsync(Guid pluginId, string toolName)`
  - `IReadOnlyList<ToolGrant> List()` + a change event for the settings UI.
- **`ToolGrant`** — `{ Guid PluginId, string ToolName, DateTimeOffset GrantedAt }`.
- **Storage:** `AppSettings.AlwaysAllowedTools : List<ToolGrant>` (camelCase JSON via `ISettingsService`, global) — mirrors the existing `AllowedSyncProviders` list pattern. In-memory cached; reload via `SettingsChanged`. (SQLite is available if a per-grant audit trail/scale is wanted later — deferred; §12.)
- The service is injected into `ChatSession` (or its manager) for the gate check and into the settings VM for management.

## 7. UI

- **Buttons on the Action Card** via Spec 1's `CardDecisionBar`, by gate-computed eligibility:
  - **Eligible** (safe/additive): `[ Decline (Default), Allow once (Primary), Always allow (Default) ]`.
  - **Ineligible** (`delete_*`, `write_file`, anything not in the safe set): `[ Decline (Default), Allow once ]` — **no Always allow**. For destructive (`delete_*`) the Allow once button is styled `Danger` and the existing warning text shows; overwrite tools (`write_file`) likewise warrant the warning.
- **Auto-approved card:** built in a resolved state (`State = Accepted`) with a distinct indicator — e.g. "Auto-approved · you always allow {tool}" — and an inline affordance to **Manage** (deep-link to the permissions settings). Visible, never silent.
- **Revocation UI (mandatory):** a settings section (new "Permissions"/"Tool access" group, or within Privacy/Account settings) listing grants grouped by plugin: tool name, plugin, granted date, **Revoke**. Backed by `IToolPermissionService.List()/RevokeAsync`.

## 8. `ActionCardInfo` changes

- `_tcs`: `bool → ToolDecision`; `WaitForUserDecisionAsync() : Task<ToolDecision>`.
- Replace the single `Accept()` command with `AllowOnce()` and `AlwaysAllow()` (each sets `State = Accepted`, `TrySetResult(AllowOnce|AlwaysAllow)`); keep `Decline()` (`TrySetResult(Decline)`) and `Cancel()` (`TrySetCanceled`) as-is.
- Expose `Decisions : IReadOnlyList<DecisionButton>` built from **`IsAutoApproveEligible`** (eligible → triad; ineligible → `[Decline, Allow once]` pair) for `CardDecisionBar`. **Not** from `IsDestructive` — `write_file` is ineligible yet `IsDestructive == false`, so keying the buttons off `IsDestructive` would wrongly show "Always allow" for it. `IsDestructive` drives only the red `Danger` warning styling.
- Add `IsAutoApproved : bool` for the resolved auto-approved render; resolved-status text gains the auto-approved variant.

## 9. Unit decomposition

- **Models** — `ToolDecision` enum; `ToolGrant`; `AppSettings.AlwaysAllowedTools`.
- **Services** — `IToolPermissionService`/`ToolPermissionService`; `PluginToolCall` gains `Guid PluginId` (+ eligibility flag if handler-declared) — **every construction site updated**, notably `BuiltInPluginHandler`'s factories (e.g. `BuiltInPluginHandler.cs:88-89`) which must pass `config.Id`; `ActionCardBuilder` (`autoApproved` build path, carry `PluginId`, compute eligibility); the `IsAutoApproveEligible` classifier.
- **ViewModels** — `ChatSession.HandleToolCall` gate rewrite; settings/permissions management VM.
- **Views** — `ActionCardControl.xaml` (triad/pair + auto-approved state); permissions settings view.
- **Wiring** — DI registration of `IToolPermissionService`; inject into `ChatSession(Manager)` and settings VM; localization strings (triad labels, auto-approved text, settings strings).

## 10. Testing

- Gate returns the right outcome for each `ToolDecision`: AllowOnce/AlwaysAllow → `Execute()` + result; Decline → decline string (unchanged); Cancel → cancellation (unchanged).
- AlwaysAllow persists a grant; a **subsequent** call to the same `(PluginId, ToolName)` **bypasses** the prompt and renders an **auto-approved** card (added before `Execute()` — audit trace present).
- **Ineligible tools never offer Always allow** and are **never auto-bypassed** even if a grant exists — asserted for both `delete_*` **and `write_file`** (the load-bearing eligibility test; the gate ignores a forged grant).
- `IsAutoApproveEligible` classifier: safe set (`create_todo`, `create_reminder`, `create_object`, `append_to_list`) eligible; `delete_*`, `write_file` ineligible.
- **Key (temporal):** a grant for `(PluginIdA, "X")` does not bypass `(PluginIdB, "X")` after the name re-owns to a different plugin.
- Revoke removes the grant → the tool prompts again.
- Privacy: bypass logging records tool name + plugin id only (no arguments) — asserted.

## 11. Security review checklist (`/security-review` — required before merge)

- [ ] Grant key is `(PluginId, ToolName)`; name-only keys impossible. `PluginId` threaded to every `PluginToolCall` construction site (incl. `BuiltInPluginHandler` factories).
- [ ] **Eligibility is deny-by-default and enforced at the gate.** `delete_*` AND `write_file` (overwrite) are ineligible — verified the classifier does not rely on the `Contains("delete")` heuristic alone. Final safe set signed off.
- [ ] Gate refuses to grant or auto-bypass an ineligible tool even with a forged/stale grant.
- [ ] No silent execution: every bypass renders a visible auto-approved card *before* `Execute()`.
- [ ] Revocation works and is discoverable.
- [ ] Residual prompt-injection risk (granted tool, model-chosen args) documented and accepted; eligible set is additive-only so blast radius is bounded.
- [ ] Revoke-vs-in-flight TOCTOU window acknowledged (one already-decided bypass may still run).
- [ ] **Pre-existing finding noted:** MCP write tools auto-execute ungated today (this spec does not change that, but the reviewer should be aware; it is a larger gap than the built-in grants added here).
- [ ] Logging privacy-safe (no tool arguments; CLAUDE.md `SensitiveDebug`/`SafeUrl`).

## 12. Deferred (out of scope for this spec)

- "Never allow" / denial-list; per-plugin or per-category grant scope; per-conversation scope; time-boxed/expiring grants; SQLite-backed grant store with audit trail; restricting always-allow to built-ins (decided during security review).

## Appendix — key integration points (from codebase recon)

- Tool gate: `src/Pia.Wpf/ViewModels/Models/ChatSession.cs:404-487` (`HandleToolCall`; gate block ~435-484; accepted branch ~459-480; decline ~482-483).
- Action card model & commands: `src/Pia.Wpf/Models/ActionCardInfo.cs` (`_tcs`, `Accept`/`Decline`/`Cancel`, `IsDestructive`, resolved state).
- Action card builder: `src/Pia.Wpf/Services/ActionCardBuilder.cs:35` (`isDelete = ToolName.Contains("delete")` — the insufficient heuristic; category/title, detokenization).
- Tool call type & routing: `src/Pia.Wpf/Services/Interfaces/IPluginToolHandler.cs` (`PluginToolCall` — has `PluginName`, **lacks `PluginId`**; handler exposes `PluginId`); `src/Pia.Wpf/Services/Plugins/PluginService.cs` (`_toolNameRoutes` name-keyed dict at :29; `RouteToolCallAsync` returns `(result, pendingAction)` and drops the handler ref); `src/Pia.Wpf/Services/Plugins/McpPluginToolHandler.cs:96/118/124` (always returns `null` pendingAction — MCP ungated); built-in factories `src/Pia.Wpf/Services/Plugins/BuiltInPluginHandler.cs:88-89`; `src/Pia.Wpf/Services/FilesToolHandler.cs` (`write_file` returns a pendingAction via `PrepareWriteFile`, ~line 114); `src/Pia.Shared/Models/SyncPlugin.cs` (`Id` Guid, `Kind`); `BuiltInPluginDefaults` (fixed built-in Guids).
- Settings: `src/Pia.Wpf/Services/Interfaces/ISettingsService.cs`, `src/Pia.Wpf/Services/SettingsService.cs`, `src/Pia.Wpf/Models/AppSettings.cs` (`AllowedSyncProviders` list precedent).
- Decision bar (Spec 1): `src/Pia.Wpf/Controls/Cards/CardDecisionBar`.
- DI root: `Bootstrapper.ConfigureServices`.
