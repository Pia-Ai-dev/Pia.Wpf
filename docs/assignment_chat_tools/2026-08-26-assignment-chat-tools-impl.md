# Assignment chat tools — implementation plan

**Status:** implemented, uncommitted
**Owner:** Marco Altmann
**Written:** 2026-08-26
**Origin:** [2026-08-25-assignment-chat-tools.md](2026-08-25-assignment-chat-tools.md), plus the owner decisions
recorded below, which close that doc's D1/D2 gates and reverse its B2 step.

This doc is the tracking surface for the work: tick the boxes in the commit that lands each slice. Effort is
sized `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface. Value is
`High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` unblocks a High.
The whole is `M`.

---

## What this builds

A new built-in tool pack, `assignments` (plugin GUID `10000000-0000-0000-0000-00000000000A`), giving the
assistant three tools over the Mesh task plane:

| Tool | Shape | Job |
|---|---|---|
| `query_assignments` | inline read | list this user's runs, newest first |
| `get_assignment` | inline read | one run's **progress** — status, events, and where the answer landed |
| `start_assignment` | pending action | propose a skill + prompt; the human affirms in the existing consent dialog |

Plus `@Assignment` at-commands, and the rename of the vestigial `@Research` domain to `@Routine`.

## Settled owner decisions — do not relitigate

- **D1 = YES. `start_assignment` ships.** The honest case is conversational continuity ("research that in the
  background"), not saved clicks, and the owner wants it.
- **D2 = YES. The consent dialog accepts a tool-supplied prefilled prompt.**
  `AssignmentConsentViewModel.InitializeAsync` already takes `string? prefillPrompt`, so the prompt half is free.
  (The *skill* half is not — see S3.)
- **B2 reversed: `start_assignment` is NOT excluded from headless runs.** It stays routable and grantable in
  routines and background turns. When a granted headless run calls it, a receipt **is** minted in-process and
  `AssignmentRunOrchestrator.StartAsync` **is** called. The owner chose this with the consent-boundary warning
  in front of them; it is deliberate, and it supersedes step B2 of the origin doc.
- **The "no item parameter" rule stands and is now load-bearing.** The model may propose the skill and the
  prompt, never the record selection. A headless start therefore sends skill + prompt with an **empty** item
  list, so zero decrypted records leave via the model's choice. Pinned by a schema test, not a comment.
- **Audit compensation for headless minting.** The JSONL consent entry gains `grantedBy` (`"user"` vs
  `"routine:<jobId>"` / `"background:<runId>"`) and the prompt's **character count**. Metadata only — never the
  prompt text — consistent with that file's existing rule.
- **At-commands: add `Assignment`, rename `Research` → `Routine`.** The research view was removed long ago; the
  domain maps to the scheduled-job tools under a stale name. `"Research"` survives as a **hidden alias** in
  `AtCommandParser`'s keyword map so old muscle memory and pre-existing chat text still parse; only `"Routine"`
  appears in `AutocompleteService`'s tier-1 list.

## Slice order and why it is fixed

Five slices, implemented **sequentially by separate agents in one working tree**. The order is not preference —
it is collision avoidance on shared files. Each slice's "leaves for later slices" note says what state the next
agent expects to find.

- [x] **S1 — read-only handler + surface cache.** `IAssignmentToolHandler`, `AssignmentSurfaceCache`,
      `query_assignments`, `get_assignment`. *Deps:* — · *Effort:* `S` · *Value:* `High`
- [x] **S2 — pack registration, catalog, classification.** GUID `…00A`, the `FromAssignmentHandler` adapter,
      Bootstrapper, the route-rebuild subscription, `ToolClass`/`ActionCardCategory`, resx.
      *Deps:* S1 · *Effort:* `S` · *Value:* `Enabler`
- [x] **S3 — `start_assignment` as a pending action.** The consent-prompt abstraction; confirming opens the
      existing dialog prefilled with skill + prompt. *Deps:* S2 · *Effort:* `S` · *Value:* `High`
- [x] **S4 — headless minting + audit.** The headless prompt implementation, `grantedBy` + prompt char count,
      the amended architecture test, the schema pin. *Deps:* S3 · *Effort:* `S` · *Value:* `High`
- [x] **S5 — at-commands.** `@Assignment`, `Research` → `Routine` with the hidden alias.
      *Deps:* S1 (for the cache) · *Effort:* `S` · *Value:* `Med`

**Suggested order:** exactly S1 → S2 → S3 → S4 → S5. S5 is the only slice that could move earlier, but it
consumes `IAssignmentSurfaceCache` from S1 and is otherwise self-contained, so leaving it last keeps the
consent-boundary work uninterrupted.

---

## S1 — `IAssignmentToolHandler` + the two read-only tools

**Goal:** the assistant can list the user's background runs and ask how one is going, with a transport failure
never dressed up as "you have no runs".

### Files

| Path | Change |
|---|---|
| `src/Pia.Wpf/Services/Interfaces/IAssignmentToolHandler.cs` | **NEW** — the interface + pending record |
| `src/Pia.Wpf/Services/AssignmentToolHandler.cs` | **NEW** — the handler |
| `src/Pia.Wpf/Services/Operators/AssignmentSurfaceCache.cs` | **NEW** — the shared surface cache |
| `tests/Pia.Wpf.Tests/Services/AssignmentToolHandlerTests.cs` | **NEW** |
| `tests/Pia.Wpf.Tests/Services/AssignmentSurfaceCacheTests.cs` | **NEW** |

`src/Pia.Wpf/Services/AssignmentToolHandler.cs` — the placement is deliberate, not incidental. Every other tool
handler lives directly under `Services/`, and `Services/Operators/` is inside the file glob that
`AssignmentConsentNotRememberedTests.NoSourceFileGrowsARememberedConsent` sweeps, where an innocuous identifier
can trip a regex. S4 widens that glob to cover this file by name; do not move it afterwards.

### The interface — write the FINAL shape now

S1 has no pending action, but S3 does, and S2's adapter is written against whatever S1 declares. Declaring the
inline-only `Task<object?> HandleToolCallAsync` shape here would force S3 to rewrite the interface, the adapter
**and** `PluginService` — three files S2 already owns. So ship the tuple shape on day one and have S1's read
arms return `(result, null)`.

```csharp
namespace Pia.Services.Interfaces;

public record AssignmentToolCall(
    string ToolName,
    string Description,
    string? Details,
    Func<Task<object?>> Execute);

public interface IAssignmentToolHandler
{
    bool IsAvailable { get; }
    IList<AITool> GetTools();
    Task<(object? Result, AssignmentToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall, CancellationToken cancellationToken = default);
    Task<object?> ExecutePendingActionAsync(AssignmentToolCall pendingAction);
}
```

In S1, `ExecutePendingActionAsync` is reached by nothing. Implement it as the real try/catch wrapper copied from
`ScheduledJobToolHandler.ExecutePendingActionAsync` (`src/Pia.Wpf/Services/ScheduledJobToolHandler.cs:98-115`),
not as a throw — a throw is what `FromChatHistoryHandler` does and it is the trap S2/S3 must avoid.

Declare the tools with `AIFunctionFactory.Create(<SchemaMethod>, "name")` plus private `[Description]`-annotated
no-op schema methods, the way `ChatHistoryToolHandler` does (`:76-81` and `:270-283`): the parameter signature
and its attributes **are** the tool metadata, and the body is never invoked because dispatch is by tool name.

### The surface cache — load-bearing, not an optimisation

`AssignmentSurface` is an async HTTP probe (`IAssignmentApiClient.GetSurfaceAsync`). The handler's `IsAvailable`
must be a **non-blocking bool read off a cached field**: `PluginService`'s constructor eagerly calls
`InitializeBuiltInPlugins()` then `RegisterHandler` then `GetTools()` on every handler, so an awaited probe in
the ctor or in `GetTools()` blocks or deadlocks app launch. `ChatHistoryToolHandler.cs:50-60` comments that even
its `GetAwaiter().GetResult()` is only safe because settings are pre-cached; an HTTP call has no such excuse.

Today three view models each keep a private copy of the surface — `AssistantViewModel.cs:78` /
`RefreshAssignmentSurfaceAsync` at `:1392-1406`, `MainWindowViewModel.cs:29` / `:191-200`, and
`AssignmentsViewModel.cs:143-158`. One singleton removes the need for a fourth and serves five consumers:
the handler's `IsAvailable` (S1), the skill-to-`Mode` lookup a headless mint needs (S4), `PluginService`'s route
rebuild (S2), the `@Assignment` tier-1 gate and the tier-2 run list (S5).

```csharp
namespace Pia.Services.Operators;

public interface IAssignmentSurfaceCache
{
    /// <summary>Last known surface; AssignmentSurface.Hidden until the first refresh.</summary>
    AssignmentSurface Surface { get; }

    /// <summary>Raised when Surface flips between hidden and available.</summary>
    event EventHandler? Changed;

    Task<AssignmentSurface> RefreshAsync(CancellationToken ct = default);

    /// <summary>Ordinal match against Surface.Skills; null when the surface is hidden or the name is unknown.</summary>
    AssignmentSkill? FindSkill(string skillName);

    /// <summary>The run list behind a short TTL, so a per-keystroke caller does not become a per-keystroke
    /// HTTP request. Null propagates: the server could not answer.</summary>
    Task<IReadOnlyList<AssignmentDto>?> GetRunsAsync(CancellationToken ct = default);
}
```

It lives in `Services/Operators/` (it wraps `IAssignmentApiClient`, and a `Services` type may not name a
ViewModel). **Naming caution:** that folder is inside the `NoSourceFileGrowsARememberedConsent` glob. The type
name `AssignmentSurfaceCache` does **not** match its regex (which wants the literal word `cached`), but a field
named `_assignmentSurfaceCached` **would**. Name the field `_surface`.

Raise `Changed` only on an availability **flip**, not on every refresh — S2 hangs a route rebuild off it.

`AssignmentSkill` is `(Name, DisplayName, Mode, DeclaredInputTypes)` and `GetSurfaceAsync` is its **only**
producer, which is why S4's headless mint has to come through this cache to obtain `skill.Mode`.

### `query_assignments`

Wraps `IAssignmentApiClient.ListAsync(skip, limit, ct)`, which returns `IReadOnlyList<AssignmentDto>?`.
Returns per run: id, skill, status, step count, tokens spent, created/completed timestamps. **Never the
artifact** — the server's list projection omits `ArtifactText`, and the tool must not undo that by fetching each
row.

The three-way outcome must be visible in the **result text**, in these words or equivalents:

| `ListAsync` result | Tool result |
|---|---|
| `null` | "Your Pia server could not be reached, so this is not an answer about your runs — try again." |
| `[]` | "You have no background assignments." |
| rows | the formatted list |

Hardcoded English, not resx — model-facing result strings follow `ChatHistoryToolHandler`'s precedent
(`SearchNote`, `UnknownChatId`, `CurrentChatRefusal` are `const string` literals). Only card-facing
`Description`/`Details` are localized, and S1 produces none. **S1 owes zero resx.**

### `get_assignment`

Wraps `IAssignmentApiClient.GetAsync(Guid)`. Its honest job is **progress, not results**: the drain pass writes
the artifact to a local chat and then collects, after which the server copy is gone.

Resolution order:

1. `GetAsync(id)` returning `null` means the server could not answer *or* does not know the run. Say the server
   could not answer for that id; do not claim the run does not exist.
2. If `dto.PlaintextDroppedAt` is set **and** the run is not outstanding, the result must say the outcome lives
   in the user's chat history rather than returning nothing.
3. "Not outstanding" is resolved against `IAssignmentPendingStore`. **Use `GetJournalAsync()`, not
   `GetAllAsync()`.** `GetAllAsync` returns only runs still awaiting collection, so keying off it would report
   "no local record" for every run that already completed — the exact inverse of the requirement.
   `GetJournalAsync` keeps the collected entry, stamped, carrying the `ChatId` that names the chat.
4. Otherwise render status, step count, spend, timestamps and the event log (`dto.Events`).

`ArtifactText`, `PlaintextDroppedAt` and `Events` exist **only** on the `GetAsync` projection, never on the list
one. Include `ArtifactText` only while the run has not been dropped and has no local chat; once a chat exists,
point at the chat.

### Tests

`tests/Pia.Wpf.Tests/Services/AssignmentToolHandlerTests.cs` (new):

- `QueryAssignments_TransportFailure_DoesNotClaimTheUserHasNoRuns` — `ListAsync` returns `null`; assert the
  result text does **not** contain the empty-list phrasing and does say the server could not be reached.
- `QueryAssignments_EmptyList_SaysThereAreNone` — `ListAsync` returns `[]`; the mirror assertion.
- `QueryAssignments_ListsRunsWithoutArtifactText` — rows come back; assert no artifact text is in the result.
- `GetAssignment_DroppedAndCollected_PointsAtTheChat` — `PlaintextDroppedAt` set, journal has the entry with a
  `ChatId`; assert the result names chat history.
- `GetAssignment_DroppedAndNotInJournal_SaysSo` — the same run absent from the journal.
- `GetAssignment_ServerCannotAnswer_DoesNotClaimTheRunIsGone`.
- `Tools_AreEmpty_WhenTheSurfaceIsHidden`.

`tests/Pia.Wpf.Tests/Services/AssignmentSurfaceCacheTests.cs` (new): `Changed` fires on a flip and not on a
same-value refresh; `FindSkill` is ordinal and returns null for an unknown name; `GetRunsAsync` propagates
`null` rather than substituting `[]`; a second call inside the TTL makes no second HTTP call.

### Acceptance

`dotnet test` reports `failed: 0`. `dotnet build -t:Rebuild -v:n` and the same with `-c Release` both report
`0 Warning(s)` and `0 Error(s)`. The handler is not registered anywhere yet, so nothing user-visible changes.

### Leaves for later slices

- The **final** `IAssignmentToolHandler` shape, so S2's adapter and S3's new arm are additive.
- `IAssignmentSurfaceCache` with a `Changed` event, unregistered in DI. **S2 registers it.**
- `ExecutePendingActionAsync` implemented for real, so S3's first confirmed card does not throw.

---

## S2 — pack registration, localization, catalog test

**Goal:** the pack exists, is preloaded and default-enabled, contributes its tools only while the surface is
available, and its confirmation card is not titled "External tool".

### Files

| Path | Change | On-disk EOL |
|---|---|---|
| `src/Pia.Wpf/Services/Plugins/BuiltInPluginDefaults.cs` | GUID, `PreloadedPluginIds`, `Defaults` entry, class doc | CRLF |
| `src/Pia.Wpf/Services/Plugins/BuiltInPluginHandler.cs` | `FromAssignmentHandler` | LF |
| `src/Pia.Wpf/Services/Plugins/PluginService.cs` | field, ctor param, switch arm, `Changed` subscription | LF |
| `src/Pia.Wpf/Bootstrapper.cs` | `IAssignmentSurfaceCache` + `IAssignmentToolHandler` registrations | CRLF |
| `src/Pia.Wpf/Models/ToolGateEnums.cs` | `ToolClass.Assignment = 9` | LF |
| `src/Pia.Wpf/Models/ActionCardInfo.cs` | `ActionCardCategory.Assignment` | CRLF |
| `src/Pia.Wpf/Services/ToolClassifier.cs` | `"assignments" => ToolClass.Assignment` | LF |
| `src/Pia.Wpf/Services/ActionCardBuilder.cs` | category row, title key, status strings | CRLF |
| `src/Pia.Wpf/ViewModels/PluginItemViewModel.cs` | fallback-icon row (optional, has a default) | CRLF |
| `src/Pia.Wpf/Resources/Strings/MessageStrings.resx` + `.de.resx` + `.fr.resx` | new keys, all three | CRLF |
| `tests/Pia.Wpf.Tests/Services/AssignmentPluginRegistrationTests.cs` | **NEW** | — |
| `tests/Pia.Wpf.Tests/Services/PluginServiceToolCatalogTests.cs` | the one direct `new PluginService(...)` | — |

### Registration, in order

1. **`BuiltInPluginDefaults.cs`** — add
   `public static readonly Guid AssignmentsPluginId = new("10000000-0000-0000-0000-00000000000A");`
   beside `ChatHistoryPluginId` (`…009`, line 26). Add it to `PreloadedPluginIds` and add a `Defaults` entry:
   `Kind = "builtin_tool_pack"`, `Name = "assignments"`, `IsPreloaded = true`, `IsActive = true`,
   `ConfigJson` carrying `"handlerId":"assignments"`, `"defaultEnabled":true` and a `systemPromptAddition`.
   Extend the class doc comment at lines 7-11, which enumerates the client-only ids that have no server plugin
   row — `…00A` is another one.
   The system prompt must name every registered tool exactly (`query_assignments`, `get_assignment`, and from
   S3 `start_assignment`), and must state the two things the model cannot infer: a finished run's answer
   arrives as a chat, and the model may propose a prompt but never chooses which records are sent.
   *S3 edits this same string to add `start_assignment` — write it so the sentence is easy to extend.*
2. **`BuiltInPluginHandler.cs`** — add `FromAssignmentHandler`. **Clone `FromGitHandler` (lines 192-210), NOT
   `FromChatHistoryHandler` (lines 231-242).** The chat-history factory hardcodes
   `_ => throw new InvalidOperationException(...)` as `executePending` and collapses `handleCall` to
   `(result, null)`; a pack built from it compiles clean, passes every registration test, and throws the first
   time a user confirms an S3 card. The git shape adapts the tuple and passes
   `isAvailable: () => handler.IsAvailable`. `AssignmentToolCall` has no `TargetPath`, so pass
   `DiffPreview: null, TargetPath: null`.
3. **`PluginService.cs`** — field in the `:17-24` block, ctor param in `:44-56`, and
   `"assignments" => BuiltInPluginHandler.FromAssignmentHandler(_assignmentToolHandler, config),` in the
   `GetHandlerId(config.ConfigJson) switch` at `:85-96`. That switch's `_ =>` arm **throws**, and
   `InitializeBuiltInPlugins()` runs from the constructor, so a `Defaults` entry without a matching switch arm
   surfaces as a DI resolution failure at startup, not a plugin warning.
4. **The route-rebuild subscription — do not skip this.** `RegisterHandler` (`:204-215`) *snapshots* tool names
   into `_toolNameRoutes` at construction, while `GetAllTools` calls `handler.GetTools()` **live** (`:244`).
   `RebuildToolNameRoutes()` (`:684-695`) fires from only three places: `SettingsChanged` (`:76`),
   `InitializePersistedPluginsAsync` (`:138`) and `ApplyServerPluginsAsync` (`:376`). Every existing
   availability-gated pack flips on a *setting*, which is why line 76 exists. The assignment surface is an
   async HTTP probe and is covered by none of them. Without a subscription, the first probe that turns the
   surface on hands the model the pack's tools with no route, and `RouteToolCallAsync` returns `null` and logs
   "No plugin handler found for tool" — while every unit test that builds a handler directly passes.
   Subscribe to `IAssignmentSurfaceCache.Changed` next to line 76 and call `RebuildToolNameRoutes()`.
5. **`Bootstrapper.cs`** — register `IAssignmentSurfaceCache` in the assignment block at `:845-861`, and
   `services.AddSingleton<IAssignmentToolHandler, AssignmentToolHandler>();` at `:537`, right after the
   `IChatHistoryToolHandler` line and **before** `IPluginService` at `:542`.
   Do not touch the existing assignment lifetimes: every one of them is a singleton, and that is precisely what
   makes S4's in-process receipt and the drain pickup work. `AssignmentConsentViewModel` stays `Transient`
   (`:860`, pinned by `AssignmentConsentNotRememberedTests.TheConsentViewModelIsRegisteredFresh`).
6. **Someone must call `RefreshAsync`.** Wire it where the existing surface probes already fire — the
   navigation hooks in `AssistantViewModel` (`:1337`) and `MainWindowViewModel` (`:176`, `:238`) — and let
   those view models read the cache instead of keeping private copies. Migrating the three existing copies is
   optional in this slice; adding a fourth is not acceptable.

### Classification — the silent "External tool" defect

Without a `ToolClassifier` row, `ActionCardBuilder`'s `_ => ActionCardCategory.Mcp` default titles S3's
confirmation card "External tool" and parses its key/value details as JSON. `ActionCardInfo.cs:27-31` records
this exact regression already shipping once for scheduled-research. There is no compiler or test signal; only
launching the app catches it. So:

- `ToolGateEnums.cs`: append `Assignment = 9` after `Ingest = 8`. That enum is **persisted**
  (`AgentRuns.PolicyJson` stores names, the timeline stores ordinals) and append-only — never renumber, never
  insert, never rename.
- `ActionCardInfo.cs`: append `Assignment` to `ActionCardCategory`.
- `ToolClassifier.MapBuiltInName`: `"assignments" => ToolClass.Assignment,`.
- `ActionCardBuilder`: `ToolClass.Assignment => ActionCardCategory.Assignment` in the switch at `:37-49`,
  `ActionCardCategory.Assignment => "ActionCard_Category_Assignment"` in `FormatToolTitle` (`:192-201`), and
  status strings for the tool names beside `"search_chats"`/`"read_chat"` at `:169-170`.

**Consequence to know before writing it:** classifying the pack also makes `RunAutonomyPolicy.Covers` able to
return true for the class, which is what lets an autonomy preset blanket-approve it. `PresetClasses`
(`src/Pia.Wpf/Models/RunAutonomyPolicy.cs`) is an explicit list — Memory, Todo, Reminder, Scheduling, Files —
so **do not add `Assignment` to it.** Leaving it out keeps the only unattended route to `start_assignment` the
named grant B2 authorised, and nothing wider.

### Localization

New `MessageStrings` keys, in all three of `MessageStrings.resx` / `.de.resx` / `.fr.resx`.
`AllTranslations_MustBeComplete` (`tests/Pia.Wpf.Tests/Architecture/LocalizationTests.cs:124-165`) asserts
**both** directions — a missing translation and an orphaned one both fail:

- `ActionCard_Category_Assignment`
- `Msg_Assistant_StatusCheckingAssignments` (`query_assignments`, `get_assignment`)
- `Msg_Assistant_StatusStartingAssignment` (`start_assignment`, consumed from S3)

Never hand-edit `MessageStrings.Designer.cs`. The pack's `Name`/`Description` in `BuiltInPluginDefaults` are
English literals like every other pack's — they are not resx keys, so do not invent any.

### Tests

`tests/Pia.Wpf.Tests/Services/AssignmentPluginRegistrationTests.cs` (new) — clone the shape of
`tests/Pia.Wpf.Tests/Services/ChatHistoryPluginRegistrationTests.cs`:

- `AssignmentsPlugin_IsPreloadedAndDefaultEnabled` — id in `PreloadedPluginIds`, `IsPreloaded`, `IsActive`,
  `Name == "assignments"`, `"handlerId":"assignments"`, `"defaultEnabled":true`.
- `AssignmentsPlugin_SystemPrompt_NamesEveryTool` — the prompt contains each registered tool name.
- `FromAssignmentHandler_ExposesToolsAndPrompt_WhenTheSurfaceIsAvailable`.
- `FromAssignmentHandler_SuppressesToolsAndPrompt_WhenTheSurfaceIsHidden` — `Assert.Empty(adapter.GetTools())`
  and `Assert.Null(adapter.GetSystemPromptAddition())`. This is the origin doc's "the surface hides itself, so
  the tools must too". `PluginServiceToolCatalogTests.ADisabledPlugin_ContributesNoGrantableRows` (`:88-105`)
  already pins that an empty `GetTools()` also removes the pack from the grant offers.
- `FromAssignmentHandler_ForwardsAPendingActionToExecute` — the guard against the `FromChatHistoryHandler`
  mis-copy. There is no real pending action until S3, so assert it with a stub `IAssignmentToolHandler` whose
  `ExecutePendingActionAsync` records the call, driven through `adapter.ExecutePendingActionAsync(...)`.

`tests/Pia.Wpf.Tests/Services/PluginServiceToolCatalogTests.cs` — the **only** file constructing `PluginService`
directly (`CreateService()`, around `:41`). Add the `Substitute.For<IAssignmentToolHandler>()` argument in the
new parameter position. The ctor ripple stops here.

### Acceptance

`dotnet test` reports `failed: 0`. Rebuild is `0 Warning(s)` in Debug and Release. Launch the app against a
profile with no Pia server: the pack row exists in Settings while its tools are absent from the tool catalogue
and from the grant offers. Point it at a server with skills and confirm both appear.

### Leaves for later slices

- The `Defaults` system-prompt string, **written to be extended** — S3 adds the `start_assignment` sentence.
- `ToolClass.Assignment` / `ActionCardCategory.Assignment` already wired, so S3's card is correct on its first run.
- `Msg_Assistant_StatusStartingAssignment` already present in all three resx files.
- `IAssignmentSurfaceCache` registered and refreshed, so S5 can read it synchronously.

---

## S3 — `start_assignment` as a pending action

**Goal:** the model can propose "run that as a background assignment"; confirming the card opens the existing
consent dialog with the proposed skill and prompt already filled in, and the human still picks the records,
ticks the affirmation and presses Send.

### Files

| Path | Change | On-disk EOL |
|---|---|---|
| `src/Pia.Wpf/Services/Interfaces/IAssignmentConsentPrompt.cs` | **NEW** — the VM-free prompt abstraction | — |
| `src/Pia.Wpf/ViewModels/AssignmentConsentPrompt.cs` | **NEW** — its UI implementation | — |
| `src/Pia.Wpf/Services/AssignmentToolHandler.cs` | the `start_assignment` tool + dispatch arm | — |
| `src/Pia.Wpf/ViewModels/AssignmentConsentViewModel.cs` | `prefillSkillName` on `InitializeAsync` | LF |
| `src/Pia.Wpf/ViewModels/AssistantViewModel.cs` | unchanged call site, verified positionally | CRLF |
| `src/Pia.Wpf/Services/Plugins/BuiltInPluginDefaults.cs` | extend the pack's system prompt | CRLF |
| `src/Pia.Wpf/Bootstrapper.cs` | register `IAssignmentConsentPrompt` | CRLF |
| `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` + `.de.resx` + `.fr.resx` | card `Description`/`Details` | CRLF |
| `tests/Pia.Wpf.Tests/Services/AssignmentToolHandlerTests.cs` | amend | — |
| `tests/Pia.Wpf.Tests/ViewModels/AssignmentConsentViewModelTests.cs` | amend | — |

### The layer trap — read before choosing a shape

`LayerDependencyTests.Services_ShouldNot_DependOn_ViewModels`
(`tests/Pia.Wpf.Tests/Architecture/LayerDependencyTests.cs:22-35`) forbids any `Pia.Services*` type from
depending on `Pia.ViewModels`. `IDialogService.ShowAssignmentConsentDialogAsync(AssignmentConsentViewModel)`
slips through only because the exclusion list uses **unanchored** regexes — `DoNotHaveNameMatching("DialogService")`
also swallows `IDialogService`. A new `Pia.Services.AssignmentToolHandler` that names
`AssignmentConsentViewModel` in any field or signature fails that test immediately.

So: declare a VM-free interface in `Pia.Services.Interfaces` and implement it in `Pia.ViewModels`, which is
allowed to depend on Services.

```csharp
namespace Pia.Services.Interfaces;

/// <summary>How a proposed assignment reaches a human. The tool handler never sees a dialog or a view model.</summary>
public interface IAssignmentConsentPrompt
{
    Task<AssignmentStartStatus?> PromptAsync(
        string? skillName, string prompt, CancellationToken ct = default);
}
```

`null` means the human dismissed the dialog without sending. Every other value is an `AssignmentStartStatus` —
the same enum `AssignmentConsentViewModel.SendAsync` returns — so the tool result can reuse the existing
localized strings through `AssignmentConsentViewModel.StartResultKey`, which `LocalizationTests.cs:413` already
sweeps for every status. The return type is `Pia.Services.Operators`/`Pia.Shared` only, so nothing ViewModel-shaped
crosses the boundary.

`src/Pia.Wpf/ViewModels/AssignmentConsentPrompt.cs` implements it by doing what
`AssistantViewModel.RunAssignmentAsync` (`:1412-1440`) already does: resolve a fresh view model from the
registered `Func<AssignmentConsentViewModel>` factory, `InitializeAsync(surface, prompt, skillName)`,
`ShowAssignmentConsentDialogAsync`, and on `Primary` only, `SendAsync()`. The surface comes from
`IAssignmentSurfaceCache`.

**It must marshal to the UI thread.** `DialogService` does not marshal —
`IContentDialogService.GetDialogHostEx()` / `ShowAsync()` throw off the UI thread, and
`ExecutePendingActionAsync` runs on the tool-dispatch thread. Wrap the whole dialog interaction in
`IUiDispatcher.PostAsync` (`src/Pia.Wpf/Services/Interfaces/IUiDispatcher.cs`).

### Prefilling the skill is NOT free

D2 covers the *prompt*. `InitializeAsync` ends with `SelectedSkill = Skills.FirstOrDefault();` — it has no skill
parameter and would silently ignore the model's proposal. Extend it:

```csharp
public async Task InitializeAsync(
    AssignmentSurface surface,
    string? prefillPrompt = null,
    string? prefillSkillName = null,
    CancellationToken ct = default)
```

Match `prefillSkillName` ordinally against `Skills`, falling back to `Skills.FirstOrDefault()` for an unknown or
absent name. Setting `SelectedSkill` retriggers `LoadRecordsAsync` through `OnSelectedSkillChanged`, so the
`await PendingRecordLoad.WaitAsync(ct)` must stay **after** the assignment. The existing call site at
`AssistantViewModel.cs:1420` passes `(surface, InputText)` positionally and keeps working unchanged — verify
that, do not edit it.

### The tool

```csharp
[Description("Propose running the user's request as a background assignment on their Pia server. This does NOT start it: the user is shown a confirmation and chooses which of their own records, if any, are sent.")]
private static string StartAssignmentSchema(
    [Description("The skill to run, from the names listed in the assignments system prompt. Omit to let the user pick.")] string? skill = null,
    [Description("What the assignment should do, self-contained — the run cannot ask a follow-up question.")] string prompt = "") => "";
```

**No item / record / entity parameter, in any spelling.** The absence of the parameter is the mechanism; S4
pins it with a schema test.

The handler's dispatch arm returns `((object?)null, new AssignmentToolCall(...))` whose `Execute` calls
`_prompt.PromptAsync(skill, prompt, ct)` and maps the result to a model-facing string. `Description` and
`Details` are **localized** (`ViewStrings`: `Tool_Assignment_Desc_Start`, `Tool_Assignment_Detail_Skill`,
`Tool_Assignment_Detail_PromptLength`), matching `ScheduledJobToolHandler`'s card strings — all three resx files.
Put only the skill name and the prompt's **length** in `Details`: the prompt itself is shown by the dialog the
user is about to see, and a card detail is a different surface.

Refuse **before** minting a card when the surface is hidden, the prompt is blank, or the prompt is over
`AssignmentInput.MaxPromptChars` (4000). Return `(errorString, null)` so no card is shown — the same shortcut
`ScheduledJobToolHandler.HandleToolCallAsync:87-93` takes for its error arms.

Extend the S2 system prompt in `BuiltInPluginDefaults` to name `start_assignment` and to state plainly that the
model proposes a prompt and never chooses which records are sent.

### Tests

`AssignmentToolHandlerTests.cs` (amend):

- `StartAssignment_ReturnsAPendingAction_AndDoesNotPromptUntilExecuted` — assert the prompt substitute received
  nothing until `pendingAction.Execute()` is awaited.
- `StartAssignment_Executed_PassesTheModelsSkillAndPromptThrough`.
- `StartAssignment_Dismissed_ReportsNothingWasSent` — `PromptAsync` returns `null`.
- `StartAssignment_SurfaceHidden_ReturnsAnErrorAndNoCard`.
- `StartAssignment_PromptOverTheCap_ReturnsAnErrorAndNoCard`.

`AssignmentConsentViewModelTests.cs` (amend): `InitializeAsync_WithASkillName_SelectsThatSkill` and
`InitializeAsync_WithAnUnknownSkillName_FallsBackToTheFirst`; both assert `PendingRecordLoad` completed for the
**selected** skill, not the first one.

### Acceptance

`dotnet test` is `failed: 0`; rebuild is clean in both configurations. Manually, with a server configured: ask
the assistant to "run that as a background assignment", confirm the card is titled from
`ActionCard_Category_Assignment` and not "External tool", confirm the dialog opens with the model's skill
selected and its prompt in the box, and confirm the affirmation checkbox starts **unticked** with Send disabled
until it is ticked.

### Leaves for later slices

- `IAssignmentConsentPrompt` with exactly one implementation. **S4 adds a second, headless one**, so keep the
  interface free of anything UI-shaped and keep the DI registration in one place S4 can branch on.
- The `start_assignment` schema, which S4 pins with a test.

---

## S4 — headless minting and the audit line

**Goal:** a routine or background run that was granted `start_assignment` can actually start one — minting the
receipt in-process with an **empty** item list — and the audit file says who granted it.

### What B2 now means

The origin doc's step B2 said to exclude `start_assignment` from headless grant sets. **The owner reversed
that.** `BackgroundAssistantTurnRunner` keeps routing the tool, keeps resolving the unattended gate
(`ResolveToolGate`, `:498-534`), and keeps calling `pending.Execute()` when the verdict allows — which, for this
tool, can only be the named-grant arm, because `Assignment` is deliberately absent from
`RunAutonomyPolicy.PresetClasses` (S2).

The headless `IAssignmentConsentPrompt` implementation does what the dialog would have done, minus the human:

```
IAssignmentSurfaceCache.RefreshAsync()      // refuse if !Available
  -> cache.FindSkill(skillName)             // ordinal; fall back to the single skill, else refuse
  -> IAssignmentConsentStore.RecordAsync(skill.Name, skill.Mode, items: [], grantedBy, promptChars, ct)
  -> IAssignmentRunOrchestrator.StartAsync(new AssignmentRequest(skill.Name, prompt.Trim(), []), receipt, ct)
  -> map AssignmentStartOutcome.Status to the tool result
```

This is mechanically sound with **no orchestrator change**. `SelectionMatches`
(`src/Pia.Wpf/Services/Operators/AssignmentRunOrchestrator.cs:261-265`) with `Items = []`: the skill names are
equal, `0 == 0`, and `All` over an empty sequence is true. The cap block (`:96-100`) passes for zero items; only
`Prompt` must be non-whitespace and within `AssignmentInput.MaxPromptChars`. The read loop (`:108-123`) is a
no-op, so `IAssignmentScopeResolver.ReadTextAsync` is never called and **zero decrypted records leave**.
`_consent.WasRecorded` (`:89`) reads the singleton store's session `HashSet`, so an in-process mint satisfies it.
A run started this way is picked up by `AssignmentDrainService` on its next 20-second tick with no restart —
same singletons, same in-memory `_cached` field in `JsonPersistenceService` — and gets the same completion toast
and the same artifact chat as a user-started run.

### Files

| Path | Change | On-disk EOL |
|---|---|---|
| `src/Pia.Wpf/Services/HeadlessAssignmentConsentPrompt.cs` | **NEW** — the headless implementation | — |
| `src/Pia.Wpf/Services/Operators/JsonlAssignmentConsentStore.cs` | `grantedBy` + prompt chars on `RecordAsync` and the JSONL entry | LF |
| `src/Pia.Wpf/ViewModels/AssignmentConsentViewModel.cs` | pass `"user"` at its `RecordAsync` call site (`:143`) | LF |
| `src/Pia.Wpf/Services/TaskAmbient.cs` | one optional trailing member on `TaskContext` (`:52-58`) | LF |
| `src/Pia.Wpf/Services/BackgroundAssistantTurnRunner.cs` | set it at `:152` | LF |
| `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs` | set it at `:490` | LF |
| `src/Pia.Wpf/Bootstrapper.cs` | register the headless prompt | CRLF |
| `tests/Pia.Wpf.Tests/Architecture/AssignmentConsentNotRememberedTests.cs` | **amended** — summary, glob, companion assertions | LF |
| `tests/Pia.Wpf.Tests/Services/AssignmentToolHandlerTests.cs` | schema pin | — |
| `tests/Pia.Wpf.Tests/Services/HeadlessAssignmentStartTests.cs` | **NEW** | — |

Both prompt implementations are registered; the headless one is selected where the run is headless. Whichever
selection mechanism is used, keep it in **one** place so a reader can see which surface gets which.

### `RecordAsync` — extend the signature, never overload

`AssignmentConsentNotRememberedTests.TheConsentStoreOffersNoWayToReuseAReceipt` (`:77-88`) asserts the **exact
sorted method-name array** of `IAssignmentConsentStore` equals `{ RecordAsync, WasRecorded }`. An overload makes
it `[RecordAsync, RecordAsync, WasRecorded]` and fails. So extend the one method:

```csharp
Task<AssignmentConsentReceipt> RecordAsync(
    string skillName,
    string mode,
    IReadOnlyList<AssignmentScopeItem> items,
    string grantedBy,
    int promptChars,
    CancellationToken ct = default);
```

Make the two new parameters **required** and place them before `ct`. `AssignmentConsentViewModel.cs:143` calls
`RecordAsync(skill.Name, skill.Mode, items, ct)` positionally, so required parameters break it at compile time —
which is the point: no future caller can mint a receipt without naming the granter. Production call sites are
one (the view model, passing `"user"`) plus the new headless one; everything else is tests.

`grantedBy` values: `"user"`, `"routine:<jobId>"`, `"background:<runId>"`.

The JSONL entry — the anonymous object at `JsonlAssignmentConsentStore.cs:67-76` — gains `grantedBy` and
`promptChars` beside `itemCount`/`totalChars`. **Metadata only, never the prompt text.** The file's rule is
stated in the comment at `:64-66`; keep that comment true. The append stays awaited under `_writeGate` with
`_written.Add(recordId)` only after it succeeds — that ordering is what makes a receipt evidence the record
reached disk.

### Where `grantedBy` comes from

At the `pending.Execute()` call site (`BackgroundAssistantTurnRunner.cs:558`, inside `DispatchGateVerdictAsync`
declared at `:539`) the scheduled-**job** id is not in scope: it lives in `request.TriggerRef` and never leaves
`RunAsync`. `AgentTimelineScope.RunId` is also unusable — `RunExchangeAsync` is called positionally at `:174`
with six arguments, so `timeline` is **null** on exactly the single-turn routine path B2 is about. What *is*
reachable is `TaskAmbient.Current?.TaskId` (`= run?.Id ?? chatId`, set at `:152`), giving `"background:<runId>"`.

To distinguish a routine, add one optional trailing member to the `readonly record struct TaskContext`
(`src/Pia.Wpf/Services/TaskAmbient.cs:52-58`) — trailing optional parameters are additive — and set it at
**both** `BackgroundAssistantTurnRunner.cs:152` and `HeadlessTurnExecutor.cs:490`. Both sites already hold the
value. Updating only the first leaves agent-run starts reporting a null job id.

**Capture the value when the pending record is built, not inside `Execute()`.** `TaskAmbient` is restored in the
`finally` at `BackgroundAssistantTurnRunner.cs:180`, and `FilesToolHandler.cs:51` already documents that a
deferred `Execute` closure cannot rely on ambient flow — reading it late yields null.

### The amended architecture test — owner-mandated

`tests/Pia.Wpf.Tests/Architecture/AssignmentConsentNotRememberedTests.cs`. Its class summary today reads
"Pins that no later 'convenience' change quietly adds a remembered blanket consent." After this slice a
background run mints a receipt with no human, so that sentence is no longer the whole rule and the file must be
amended rather than named around. Three edits:

1. **Rewrite the class summary** to state the rule as it now is: a consent is never *remembered* — every send
   still mints its own receipt for its own selection — and the only caller that may mint one without a human is
   a background run explicitly granted `start_assignment`, which sends an empty item list and names the granting
   job in the audit line.
2. **Widen the glob** in `NoSourceFileGrowsARememberedConsent` (`:52-72`) with a third `Concat` for
   `Services/Assignment*.cs`, `TopDirectoryOnly` — mirroring the existing `ViewModels/Assignment*.cs` arm, and
   picking up `AssignmentToolHandler.cs` by name. Do **not** widen to all of `Services/*.cs`: that is 100+
   unrelated files and would trip the regex on prose with nothing to do with consent. Update the
   `files.Count >= 3` non-vacuity guard to the new count so it stays honest.
3. **Add two companion assertions**, so the rewritten summary is enforced and not merely asserted:
   - `AHeadlessMintAlwaysNamesItsGranter` — reflect over `IAssignmentConsentStore.RecordAsync` and assert a
     `grantedBy` parameter exists and is **required** (`HasDefaultValue == false`), so no call site can mint
     anonymously.
   - `AHeadlessStartSendsNoRecords` — drive `HeadlessAssignmentConsentPrompt` with a recording consent store and
     assert the `items` argument is empty.

Also note briefly in the file that `TheConsentStoreOffersNoWayToReuseAReceipt` is what forbids a `RecordAsync`
overload — the next person to extend the signature needs to know why it stays one method.

### The schema pin

Owner-mandated: the "no item parameter" rule is pinned on the **schema**, not a comment.
`AIFunctionFactory.Create(<SchemaDelegate>, name, description)` derives the JSON schema from the delegate's
parameters, so asserting on the C# signature alone would not catch a schema added another way. Copy the pattern
from `tests/Pia.Wpf.Tests/Services/ScheduledJobBlueprintToolTests.cs:51-63`, whose own doc comment says "the
absence of the parameter is the mechanism, so pin it".

**Trap:** `AIFunction.JsonSchema.ToString()` includes the `[Description]` text, so a naive
`Assert.DoesNotContain("item", schema)` fails against a description that says "the user chooses which records
are sent". Assert on the schema's **`properties` keys**, not the serialized string:

```
StartAssignment_ExposesNoItemOrRecordParameter
  -> parse tool.JsonSchema, enumerate the properties object's keys
  -> Assert.Equal(["skill", "prompt"], keys)
  -> and assert no key contains "item" / "record" / "entity" / "id", case-insensitively
```

### Other tests

`tests/Pia.Wpf.Tests/Services/HeadlessAssignmentStartTests.cs` (new):

- `AGrantedHeadlessStart_MintsAReceiptAndStartsTheRun`.
- `AHeadlessStart_SendsAnEmptyItemList` — also asserted from the architecture file, deliberately: one is the
  behaviour, the other is the rule.
- `AHeadlessStart_WithAHiddenSurface_RefusesAndMintsNothing`.
- `AHeadlessStart_WithAnUnknownSkill_RefusesAndMintsNothing`.
- `TheAuditEntryCarriesGrantedByAndPromptCharsButNotThePrompt` — read the JSONL line back; assert the prompt text
  is absent and its length is present.
- `AnUngrantedHeadlessCall_NeverReachesExecute` — the unattended gate still denies a tool nobody granted.

### Acceptance

`dotnet test` is `failed: 0`; rebuild is clean in Debug and Release. Manually: create a routine granting
`start_assignment`, fire it, and confirm one line appears in the consent-audit `assignments.jsonl` with
`"grantedBy":"routine:<id>"`, `"itemCount":0` and a `promptChars` number — and that no prompt text appears
anywhere in the file.

---

## S5 — at-commands: `@Assignment`, and `Research` to `Routine`

**Goal:** `@Assignment` scopes a turn to the assignment tools and lists the user's actual runs; the stale
`@Research` domain is renamed to `@Routine` without breaking anyone's muscle memory.

### Files

| Path | Change | On-disk EOL |
|---|---|---|
| `src/Pia.Wpf/Models/AtCommand.cs` | `Research` to `Routine`, add `Assignment` | LF |
| `src/Pia.Wpf/Services/AtCommandParser.cs` | rename the row, add `Assignment`, add the alias table | LF |
| `src/Pia.Wpf/Services/AutocompleteService.cs` | tier-1 rows, tier-2 arms, one new ctor dependency | LF |
| `src/Pia.Wpf/Services/AssistantPromptComposer.cs` | two `GetAtCommandToolMapping` rows | CRLF |
| `tests/Pia.Wpf.Tests/Services/AtCommandParserTests.cs` | rename + alias tests | LF |
| `tests/Pia.Wpf.Tests/Services/AutocompleteServiceTests.cs` | rename + new domain tests + the ctor ripple | LF |
| `tests/Pia.Wpf.Tests/ViewModels/PersonaPromptCompositionTests.cs` | add an `Assignment` mapping assertion | — |

### The static-init trap — read this before touching the parser

`AtCommandParser` builds two dictionaries from one array (`:16-29`):

```csharp
DomainMap  = Domains.ToDictionary(d => d.Keyword, d => d.Domain, StringComparer.OrdinalIgnoreCase);
KeywordMap = Domains.ToDictionary(d => d.Domain,  d => d.Keyword);
```

`KeywordMap` keys on the **domain**. Adding `(AtCommandDomain.Routine, "Research")` as a second row in `Domains`
throws `ArgumentException: An item with the same key has already been added` from the static initializer,
surfacing as a `TypeInitializationException` on the first keystroke in the composer — with **no compile error
and no CI signal**.

So: keep `Domains` strictly one row per enum value, add a separate `Aliases` array, and build `DomainMap` from
`Domains.Concat(Aliases)` while `KeywordMap` stays built from `Domains` alone. That keeps `GetKeyword` honest
and keeps both `Enum.GetValues` round-trip tests (`AtCommandParserTests.cs:208-216` and `:218-227`) meaningful —
they only ever ask for the canonical keyword, and `ParseTriggerFragment` resolves through `DomainMap`, which the
alias only widens.

### The rename, in full

- `src/Pia.Wpf/Models/AtCommand.cs:8` — `Research,` becomes `Routine,`; add `Assignment,`. The enum is
  **transient**: `ChatSessionManager` re-extracts commands from message text on every send
  (`AtCommandParser.ExtractAllCommands`), it is not persisted and it is not in `Pia.Shared`, so there is no
  migration and no compatibility window.
- `AtCommandParser.cs:21` — `(AtCommandDomain.Routine, "Routine")`, plus `(AtCommandDomain.Assignment, "Assignment")`.
  `Aliases` gets `(AtCommandDomain.Routine, "Research")`.
- `AutocompleteService.cs:18` — the tier-1 row becomes `DisplayText = "Routine"`. If you change the icon,
  **verify the `SymbolRegular` member is a BMP code point before shipping**: 2863 of the 9235 members are above
  U+FFFF and render a garbage letter with zero compiler warnings, so only launching the app catches a bad one.
  `SymbolRegular.Search24` is already proven; keep it if in doubt.
- `AutocompleteService.cs:82` and `:142-158` — rename the tier-2 switch arm and `GetResearchSuggestionsAsync`
  (including its `Domain = AtCommandDomain.Research` at `:153`).
- `AssistantPromptComposer.cs:195-198` — rekey the mapping row to `AtCommandDomain.Routine`. The tool names and
  the `"scheduled research job"` category label stay as they are: they name the tools, which are not renamed.

### The new `Assignment` domain

- **Tier 1 is gated**, exactly like Files. `GetTier1Suggestions` (`AutocompleteService.cs:62-72`) is
  **synchronous** and gates Files on `_filesToolHandler.IsAvailable`, a plain bool. Do the same with
  `IAssignmentSurfaceCache.Surface.Available`. Never await a probe here.
- **Tier 2 lists actual runs**, off `IAssignmentSurfaceCache.GetRunsAsync()` — the TTL'd snapshot from S1, not
  `IAssignmentApiClient.ListAsync` directly. See the keystroke trap below. Suggest `DisplayText` = the run's
  skill name plus its status, `ItemId` = the assignment id. A `null` run list must yield **no suggestions**, not
  an empty-looking success: the popup showing nothing is the correct rendering of "could not answer" here,
  because there is no text surface to explain it in.
- `AssistantPromptComposer.GetAtCommandToolMapping` needs an `Assignment` row —
  `("background assignment", "query_assignments", ["query_assignments", "get_assignment", "start_assignment"])`.
  That switch's `_ =>` arm throws `ArgumentOutOfRangeException`, and
  `PersonaPromptCompositionTests.GetAtCommandToolMapping_EveryEnumValue_HasNonEmptyMapping` (`:115-127`) sweeps
  every enum value, so a missing row fails in CI rather than at runtime.
- `BuildAtCommandHint` (`:218-250`) special-cases only Files. Its generic wording — "call `query_assignments`
  first to obtain its ID" — is correct for assignments, so no new branch is needed.

### Localization

**This slice owes zero resx.** The tier-1 labels in `AutocompleteService.BaseTier1Suggestions` are hardcoded
English literals by design: they are the exact keyword the user types. No resx key names an at-command, and no
resx value, XAML string or placeholder mentions `@Research` (`Assistant_InputPlaceholder` is just
"Type a message..."). The only prose mention anywhere in the repo is
`docs/user_questions/2026-08-16-first-run-user-questions.md:146`; update that line in the same commit.

### Tests

`AtCommandParserTests.cs`:

- Rename the three Research tests (`:181-186`, `:191-195`, `:197-204`). Note that
  `ParseTriggerFragment_ResearchPartial` asserts `"Res"` yields a null domain with a tier-1 filter — still true
  for `Routine`, but `"Rou"` is the new meaningful partial, so rewrite it around that.
- **New:** `ExtractAllCommands_ResearchAlias_StillParsesAsRoutine` — `"@Research check the news"` yields
  `AtCommandDomain.Routine`.
- **New:** `ExtractAllCommands_AssignmentDomain_ReturnsOne`.
- The two `Enum.GetValues` sweeps need no change and must keep passing — they are what proves the alias did not
  leak into `Domains`.

`AutocompleteServiceTests.cs`:

- **Ctor ripple.** Line 18 is
  `private AutocompleteService CreateService() => new(_memory, _todo, _reminder, _scheduledJobs, _files);` —
  a target-typed `new(...)`, so `grep "new AutocompleteService("` finds **nothing**. Add the
  `IAssignmentSurfaceCache` substitute there.
- Rename the five Research tests (`:20-86`). `Tier1_FilterRes_ReturnsResearchOnly` (`:20-31`) asserts
  `Assert.Single` on the prefix `"Res"`; with `Research` demoted to a hidden alias that prefix now matches
  **nothing**, so move it to `"Rou"` and keep the `Assert.Single`.
- **New:** `Tier1_OmitsAssignment_WhenTheSurfaceIsHidden` and `Tier1_IncludesAssignment_WhenAvailable`.
- **New:** `Tier1_NeverOffersTheResearchAlias` — filter `null`, assert no suggestion has `DisplayText == "Research"`.
- **New:** `Tier2_Assignment_ListsRunsFromTheCache` and `Tier2_Assignment_TransportFailure_ReturnsEmpty`.
- **New:** `Tier2_Assignment_RepeatedFragments_MakeNoExtraApiCalls` — the keystroke-hot-path guard: assert the
  underlying `IAssignmentApiClient` substitute received exactly one `ListAsync` across several fragments.

### Acceptance

`dotnet test` is `failed: 0`; rebuild clean in both configurations. Manually: type `@Rou` and see Routine; type
`@Research` in full and confirm it still parses (the popup will not offer it, which is intended); type
`@Assignment:` on a machine with a server and see real runs; confirm `@Assignment` is absent entirely on a
local-only profile.

---

## Traps carried forward

**The `AutocompleteService` ctor ripple is exactly one test file.**
`tests/Pia.Wpf.Tests/Services/AutocompleteServiceTests.cs:18` is the only place the concrete type is
constructed, and it uses a target-typed `new(...)`, so grepping for `new AutocompleteService(` returns **zero
hits** — grep the type name instead. Nine other test files inject `Substitute.For<IAutocompleteService>()` into
`AssistantViewModel` and are untouched by a **ctor** change; they would all ripple if the **interface** changed,
so leave `IAutocompleteService` alone. Ctor ripple: **1 file**. Interface ripple: **10 files**
(`AgentRunOrchestratorUserPauseLiveTests`, `AssistantViewModelAssignmentTests`, `AssistantViewModelChipDeleteTests`,
`AssistantViewModelGoalPreflightTests`, `AssistantViewModelLeverTests`, `AssistantViewModelManagedPersonaTests`,
`AssistantViewModelOverlayHostingTests`, `AssistantViewModelVoiceGateTests`, `AssistantViewParseTests`, plus the
concrete one).

**The keystroke hot path, and the chosen caching seam.** `AtCommandAutocompleteBehavior.OnTextChanged`
(`src/Pia.Wpf/Behaviors/AtCommandAutocompleteBehavior.cs:115-132`) restarts a 100 ms `DispatcherTimer` per
keystroke and dedupes only on a byte-identical fragment with the popup already open (`:150-151`). Every distinct
fragment is one fetch. All existing tier-2 lookups are local — SQLite reads or a synchronous filesystem walk —
and nothing hits the network. `AssignmentApiClient` has **no cache**: `GetSurfaceAsync` (`:72-104`) and
`ListAsync` (`:140-170`) each issue a live `Http.GetAsync`, so wiring either directly into
`GetTier2SuggestionsAsync` means one HTTP round trip per typed character — `"@Assignment:proj"` is four of them.
The chosen seam is `IAssignmentSurfaceCache` from S1: a synchronous `Surface` for the tier-1 gate and a TTL'd
`GetRunsAsync()` for tier-2. A `Substitute.For<IAssignmentApiClient>()` in a unit test answers instantly and
hides this completely, which is why S5 owes an explicit call-count test.

**The route snapshot.** Repeated because it is the one defect no existing test would catch:
`PluginService.RegisterHandler` captures tool names at construction while `GetAllTools` calls `GetTools()` live.
An availability flip that fires no `RebuildToolNameRoutes()` offers the model tools it cannot route, and
`RouteToolCallAsync` returns null. S2 owns the subscription; do not ship the pack without it.

**Mixed line endings, per file.** Every file is LF in the git index, but the working tree is mixed and you must
match the file you are editing rather than normalize it. Check with `git ls-files --eol <path>`. S2 has both in
one slice: `src/Pia.Wpf/Models/ActionCardInfo.cs` is **CRLF** on disk while `src/Pia.Wpf/Models/ToolGateEnums.cs`
is **LF**. Also CRLF: `Bootstrapper.cs`, `BuiltInPluginDefaults.cs`, `AssistantPromptComposer.cs`,
`ActionCardBuilder.cs`, `AssistantViewModel.cs`, and all six resx files. Also LF: `AtCommand.cs`,
`AtCommandParser.cs`, `AutocompleteService.cs`, `BuiltInPluginHandler.cs`, `PluginService.cs`,
`ToolClassifier.cs`, `ChatHistoryToolHandler.cs`, `DialogService.cs`, `IDialogService.cs`,
`JsonlAssignmentConsentStore.cs`, `AssignmentConsentViewModel.cs`, `AssignmentConsentNotRememberedTests.cs`,
`AtCommandParserTests.cs`, `AutocompleteServiceTests.cs`.

**A headless start still sends the model's prompt string.** The empty item list stops the model from *selecting
records to decrypt*. It does not stop the model from *writing what it already read* into the prompt. A routine
that called `recall`, `read_topic` or `read_file` earlier in the same turn can put that content into
`start_assignment`'s `prompt` parameter, and it will leave the end-to-end-encrypted plane in plain text with no
human in the loop. This is a known and accepted consequence of the owner's B2 decision. It is not mitigated
here, and nothing in this plan should be read as closing it.

**The `ToolClass` ordinal is persisted.** `AgentRuns.PolicyJson` stores class member *names* and the timeline
stores the *ordinal*. Append `Assignment = 9`; never renumber, never insert, never rename an existing member.

**`null` is not empty, in three places.** `IAssignmentApiClient.ListAsync` returns `null` when the server could
not answer and `[]` when the user has no runs; `GetSurfaceAsync` returns `AssignmentSurface.Hidden` for no
server, no token, `401`/`403`/`404` **and** for an empty skill list. `query_assignments` (S1), the tier-2 popup
(S5) and the cache's own `GetRunsAsync` (S1) are three independent chances to write `?? []` and turn a transport
failure into a confident wrong answer about the user's own data.

**Do not reorder the drain.** `AssignmentRunOrchestrator.DrainAsync` writes the artifact chat **before** calling
collect, because collect is irreversible (`:177-188`). A headless-started run flows through the identical path,
so nothing new is needed — but no "return the artifact to the model synchronously" convenience may reorder those
two, and no tool may call `CollectAsync` itself.

**Session-scoped receipts depend on singleton lifetimes.** `WasRecorded` reads an in-memory `HashSet`
(`JsonlAssignmentConsentStore.cs:41`). A headless mint satisfies the orchestrator's check only because
`IAssignmentConsentStore` and `IAssignmentRunOrchestrator` resolve the same singletons (`Bootstrapper.cs:845`,
`:853-854`). Scoping either registration turns every headless start into a silent `ConsentMissing`.

**Zero-warning gate.** Verify with a **rebuild**, not an incremental build, in both configurations:
`dotnet build -t:Rebuild -v:n` and again with `-c Release`. Read the count off MSBuild's `N Warning(s)` summary
line; at `-v:n` each warning is printed twice, so grepping the log double-counts. WPF re-reports `src/` warnings
under a generated `Pia.Wpf_<hash>_wpftmp.csproj` — fixing the source clears both.

## Gates that stay closed

Unchanged from the origin doc, and none of the five slices may open them:

- **No `cancel_assignment`.** Cancelling is one click in a view the user is already looking at, and a
  model-initiated cancel of work the user paid tokens for is a bad trade.
- **No `collect`.** It is irreversible and the drain pass owns it.
- **No item selection from the model**, in any tool, on any surface.
- **No artifact in `query_assignments`.** The list projection omits it server-side; the tool must not undo that.
