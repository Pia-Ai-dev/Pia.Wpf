# Tool access: one control surface for tool authority

**Date:** 2026-08-09
**Branch:** `feature/agent-run-spine` (or a fresh `feature/tool-access-surface`)
**Status:** plan, not started

## The problem

Four independent mechanisms can approve a tool call, they all render the same grey
"auto-approved" card, and exactly one of them has a UI:

| Mechanism | Stored | Visible in UI |
|---|---|---|
| Allow once | nowhere | n/a |
| Allow this session | RAM (`SessionToolGrantStore`) | **no** |
| Always allow | `settings.json` → `alwaysAllowedTools` | yes — the whole Tool access tab |
| Autonomy switch (`agentRunAutoApproveBuiltInWrites`) | `settings.json` | a checkbox on a *different* tab |

Observed: with the Autonomy switch on and zero standing grants, an agent run silently runs
memory/todo/reminder/scheduling/files tools, renders a card reading **"you always allow X"**
— which is false — and its **Manage** link lands on a page that lists a different mechanism
and is therefore blank.

Three concrete defects behind it:

1. `ActionCardBuilder.cs:163-167` picks the resolved-status string by branching only on
   `AutoApprovedSessionGrant`. Every other tier falls through to `ActionCard_AutoApproved`
   ("you always allow {0}"), so a policy auto-approval makes a false claim and points at the
   wrong page.
2. `ActionCardControl.xaml:269-271` comments *"only shown for auto-approved (standing-grant)
   cards"* but binds `Visibility` to `IsAutoApproved`, true for all three interactively
   reachable tiers. Intent and binding drifted.
3. `ChatSessionManager.cs:824-828` asserts *"every write_file goes through an action card the
   user clicks"*. With the switch on, `ToolClass.Files` is policy-covered and the policy arm
   (`ToolAutonomy.cs:202`) runs first — so that comment is wrong in exactly the configuration
   it describes.

## Goal

The Tool access page answers one question: **what can run without asking me, right now?** —
across all three live sources, with the ability to revoke and to pre-arm.

---

## Decision: which tier can pre-approval actually target?

This is the one place the requested feature collides with the existing security model, and it
must be settled before Phase 3 is built.

`ToolAutonomy.IsStandingGrantOfferable` = `isAllowlisted || (External && !IsDeleteLike)`, and
`isAllowlisted` is membership in `ToolPermissionService.AutoApproveAllowlist` — a **four-name**
set: `create_object`, `create_todo`, `create_reminder`, `append_to_list`.

So a pre-approval catalogue built against the **standing** tier can offer exactly those four
built-in tools plus non-destructive MCP tools. It cannot pre-approve `write_file`, any `git_*`
tool, `update_todo`, or anything else that currently prompts. Built as literally asked, the
feature would not cover a single tool that prompts today.

`ToolAutonomy.IsSessionGrantOfferable` is name-only —
`!IsDeleteLike && !IsWorkDiscarding && !IsAuthorityAuthoring` — and admits `write_file`, most
git writes, and most MCP tools.

**Decision: the catalogue offers both tiers per tool, with the session tier as the primary
control.**

- "Until Pia closes" ⇒ `GrantForSession(pluginId, toolName)` — offered wherever
  `IsSessionGrantOfferable` is true.
- "Always" ⇒ `GrantAsync(pluginId, toolName)` — offered only where `IsStandingGrantOfferable`
  is true (the four names + non-destructive MCP).

Why this is safe: pre-arming the session tier from settings produces *byte-identical state* to
pressing the card button, so `ToolAutonomy.Resolve` is untouched. It is strictly narrower than
the authority already switched on — the Autonomy toggle covers all of `ToolClass.Files`
including unattended runs, whereas a session grant dies with the process and is refused
unattended for `ToolClass.External` (`ToolAutonomy.cs:239-245`).

Rows where neither tier is offerable (`delete_*`, `git_switch/restore/stash`,
`create_scheduled_research`) are **shown disabled with a stated reason**, not hidden — the page
should be honest about what it deliberately cannot do.

---

## Phase 1 — make the card truthful and the page explain itself

Answers the reported complaint on its own. Nothing below depends on Phases 2-3.

### 1.1 Card names the tier that actually approved

`src/Pia.Wpf/Services/ActionCardBuilder.cs:163-167` — replace the two-way branch with a total
switch over `autoApprovedAs`:

| `ToolGateDecision` | Resource key |
|---|---|
| `AutoApprovedStandingGrant` | `ActionCard_AutoApproved` (existing, "you always allow {0}") |
| `AutoApprovedSessionGrant` | `ActionCard_AutoApprovedForSession` (existing) |
| `AutoApprovedPolicy` | **new** `ActionCard_AutoApprovedByAutonomy` |
| `GrantedByName` | **new** `ActionCard_AutoApprovedByRunGrant` |
| anything else | `ActionCard_AutoApproved` (default arm) |

`GrantedByName` is unreachable interactively today (`ChatSession.cs:1293` passes
`IsNamedGrant: false`) but is included so the switch is total and stays correct if a named
grant ever reaches this surface.

Proposed copy (en):

- `ActionCard_AutoApprovedByAutonomy` → `Auto-approved · agent autonomy is on for this kind of tool`
- `ActionCard_AutoApprovedByRunGrant` → `Auto-approved · this run was granted {0}`

### 1.2 Autonomy state on the Tool access page

`src/Pia.Wpf/Views/SettingsViews/AssistantView.xaml:166-230` — new first section above the
existing grant list:

- Mirrored checkbox for the Autonomy switch.
- The five covered classes spelled out (notes/memory, todos, reminders, scheduled jobs, files).
- The exclusions spelled out (deletes, git, external/MCP) — the existing
  `Settings_Agent_AutoApproveBuiltInWrites_Description` copy already says this and can be reused.

**Binding trap:** the tab's `ScrollViewer` sets `DataContext="{Binding ToolPermissionsVm}"`,
but `AgentRunAutoApproveBuiltInWrites` lives on the parent `AssistantSettingsViewModel` (with
`OnAgentRunAutoApproveBuiltInWritesChanged` at `AssistantSettingsViewModel.cs:373` driving
persistence). Do **not** add a second `[ObservableProperty]` to `ToolPermissionsVm` — that
yields two properties over one settings key with a save path on only one of them. Bind up to
the parent instead:

```xml
IsChecked="{Binding DataContext.AgentRunAutoApproveBuiltInWrites,
            RelativeSource={RelativeSource AncestorType=UserControl}, Mode=TwoWay}"
```

One property, one save path, both checkboxes in sync for free. The Agent runs tab keeps its
copy unchanged.

### 1.3 Page framing

`Settings_ToolPermissions_Title` ("Always-allowed tools") becomes a *section* header inside the
page; the page gains a new top-level title and description covering all three sources. Existing
`ToolPermissions_Empty` copy stays accurate once it sits under the "Always allowed" section
rather than standing in for the whole page.

### 1.4 Comment corrections (no behaviour change)

- `ActionCardControl.xaml:269-271` — drop "(standing-grant)"; the link is correct for every
  tier once the page covers them.
- `ChatSessionManager.cs:824-828` — the `write_file`-always-cards claim is false when the
  Autonomy switch is on.
- Per CLAUDE.md comment discipline: one short line each, no `<para>`, no task IDs.

---

## Phase 2 — session grants: listed and revocable

### 2.1 `ISessionToolGrantStore` gains list / revoke / change notification

`src/Pia.Wpf/Services/Interfaces/ISessionToolGrantStore.cs`

```csharp
IReadOnlyList<ToolGrant> List();
void Revoke(Guid pluginId, string toolName);
event EventHandler? Changed;
```

Reusing the existing `ToolGrant(PluginId, ToolName, GrantedAt)` record means the UI row type is
shared with the standing tier. That requires the store to hold a timestamp, so
`HashSet<(Guid, string)>` becomes `Dictionary<(Guid, string), DateTimeOffset>` in
`SessionToolGrantStore.cs:23`. The comparer stays the default ordinal, case-sensitive tuple
comparer — that property is load-bearing (this tier must never match a name the standing tier
would not) and must not change.

Raise `Changed` **outside** the lock. The store is written from the UI thread and read from run
threads, so this event genuinely can be observed off-thread.

**This reverses two documented decisions and the docs must move with the code:**

- `ISessionToolGrantStore.Grant` doc: *"there is deliberately no revoke: the only way out is
  closing the app, which is exactly the promise the button makes."* The promise changes to
  "gone when Pia closes, or when you forget it here" — strictly narrowing, so no gate impact.
- `ToolPermissionService.GrantForSession` (`:184-189`): *"NO `Changed` event: … raising Changed
  would tell the settings grant list to refresh for a grant it can neither show nor revoke."*
  That reasoning inverts the moment the page can do both.

Leaving either comment behind is worse than not doing the work.

### 2.2 `IToolPermissionService` pass-throughs

Add `IReadOnlyList<ToolGrant> ListSessionGrants()` and
`void RevokeSessionGrant(Guid, string)`, mirroring the existing `IsGrantedForSession` /
`GrantForSession` shape — one owner for all tiers, so the VM keeps a single dependency.

Subscribe `ToolPermissionService` to the store's `Changed` and re-raise its own `Changed`, so
`ToolPermissionsSettingsViewModel` keeps its single subscription and existing `PostOrRun`
marshalling (`ToolPermissionsSettingsViewModel.cs:45`) covers the off-thread path.

### 2.3 ViewModel + view

`ToolPermissionsSettingsViewModel` gains `SessionGrants` (`ObservableCollection<ToolGrantRow>`),
`HasSessionGrants`, and `ForgetSessionCommand`. `RefreshGrants` rebuilds both collections from
one `_pluginService.GetAllPluginConfigs()` read.

XAML: a second section "Allowed until Pia closes", same card template as the standing rows,
button labelled **Forget** rather than Revoke, plus one line stating these disappear on restart.

---

## Phase 3 — pre-approval catalogue

### 3.1 Expose the server destructive hint

`McpPluginToolHandler.IsServerDeclaredDestructive` (`:177`) is `internal static` and only
reachable while building a `PluginToolCall`. The catalogue needs it per tool **before** any
call, otherwise the page offers "Always" on a server-declared-destructive tool and persists a
grant the floor ignores forever — the exact "button that does nothing" failure
`ToolAutonomy.cs:103-107` exists to prevent.

Add a default interface method to `IPluginToolHandler`:

```csharp
bool DeclaresDestructive(string toolName) => false;
```

Only `McpPluginToolHandler` overrides it. Every other handler is untouched — "no hint available"
is the honest answer for a built-in, and the name heuristic remains the whole rule there.

### 3.2 `IPluginService.GetToolCatalog()`

```csharp
public sealed record ToolCatalogEntry(
    Guid PluginId, string PluginName, string ToolName,
    string? Description, bool IsExternalRoute, bool ServerDeclaredDestructive);
```

Built in `PluginService` by walking `_handlers`, honouring the same `IsPluginEnabled` skip
`GetAllTools()` uses (`PluginService.cs:233-239`) so a disabled plugin's tools are not
grantable, with `IsExternalRoute` from the `_toolNameRoutes` MCP check.

### 3.3 ViewModel rows

Each row carries, computed from the same functions the gate uses:

- `CanGrantForSession` = `ToolAutonomy.IsSessionGrantOfferable(name, serverDestructive)`
- `CanGrantAlways` = `ToolAutonomy.IsStandingGrantOfferable(class, name, isAllowlisted, serverDestructive)`
- live state from `IsGrantedForSession` / `IsGranted`

`ToolClass` comes from `ToolClassifier.Classify(pluginName, isExternalRoute)` — the route-first
overload, never `ClassifyPresumedExternal`, which is documented "never call this from a gate"
and would let a renamed built-in become grantable-as-external by name.

### 3.4 View

Third section, collapsed `Expander` "All tools", grouped by plugin. Per row: tool name, plugin,
two toggles ("Until Pia closes" / "Always"), each hidden or disabled per the offerability flags
above with a one-line reason on the disabled ones.

### 3.5 DI threading

`ToolPermissionsSettingsViewModel` is **hand-constructed** at `SettingsViewModel.cs:70`, not
resolved from the container. Any new constructor dependency must be threaded through that line
and through `SettingsViewModel`'s own ctor.

---

## Tests

| Area | Test |
|---|---|
| `ActionCardBuilderTests` | one case per `ToolGateDecision` → expected resource key; specifically that `AutoApprovedPolicy` does **not** produce "you always allow" |
| `SessionToolGrantStoreTests` (new) | revoke removes; `Changed` fires on grant and revoke; timestamps recorded; comparer still ordinal/case-sensitive |
| `ToolPermissionsSettingsViewModelTests` | session rows appear on grant and vanish on forget; refresh marshals off-thread `Changed` |
| Catalogue | a row offering "Always" implies `IsStandingGrantOfferable` — the button-that-does-nothing guard |
| Catalogue | a disabled plugin contributes no grantable rows |
| `ToolAutonomyTests` | unchanged and must stay green — Phase 3 mints through the existing `GrantForSession`/`GrantAsync`, so the resolver sees no new input |
| Localization | existing en/de/fr parity tests cover the new keys automatically |

## Gates

- `dotnet test` with **no filter**, `failed: 0`.
- `dotnet build -t:Rebuild -v:n` and again `-c Release` → **0 Warning(s), 0 Error(s)** in both.
- New `loc:Str` keys go in all three resx files (`ViewStrings.resx`, `.de.resx`, `.fr.resx`).
  Do not hand-edit `ViewStrings.Designer.cs`.
- New `.cs` files must be CRLF.
- Comment discipline: one short line, no `<para>`, no task/spec IDs.

## Out of scope

- **Permanent block list** ("never allow this tool"). Declined — it would need a new persisted
  set and a new branch in `ToolAutonomy.Resolve` above the grant tiers, i.e. surgery on the
  security-critical path.
- Voice-mode allowlist surfacing (`AutoApprovedAllowlist` authorizes on voice only; no card
  exists there).
- Scheduled-job / headless run grant envelopes — a separate per-run concept with its own
  lifecycle.
- Changing which tools are standing-grantable. The four-name allowlist stays as-is; Phase 3
  works within it rather than widening it.

## Sequencing note

Phase 1 alone closes the reported complaint: the card stops lying and the Manage link lands
somewhere that explains what happened. Phases 2 and 3 are additive and independently shippable.
