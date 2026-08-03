# Trust model — what this app contains, and what it does not

**Cross-batch reference doc (T2-G4), not a batch.** Every claim cites `file:line`, so a reader can check it rather
than believe it. The headline, stated once: **MCP stdio subprocesses run with the user's full privileges entirely
outside `SafeFolderPath`, and containment in this app is per-chokepoint, not per-process.**

## 1. What is contained, and by what mechanism

Two mechanisms, used **together, in order** — resolve, then guard.

`Infrastructure/SafeFolderPath.cs` answers *is this path inside the sandbox?* `TryResolveInside` (`:30`) rejects
rooted paths; `TryResolveInsideAllowingAbsolute` (`:59`) accepts an absolute path and canonicalizes it through the
OS via `GetFinalPathNameByHandleW` (`:177`, `:214`) before the lexical check in `TryContain` (`:127`). Because the
check runs on what the OS resolves to and not the caller's string, `..\` traversal and **reparse-point / junction
escapes**, intermediate ones included, are defeated.

`Infrastructure/SensitivePathGuard.cs` answers what containment cannot: *must this path never be touched even
though it **is** inside the sandbox?* Its header (`:5-10`) states why it is load-bearing — the resolver accepts
in-base absolute paths, so a permissive sandbox root can otherwise reach Pia's own data, config and DB under
`%LOCALAPPDATA%\Pia`, and true system / credential directories. `IsBlocked` (`:31`) tests a denylist of well-known
roots (`:24`); two carve-outs are checked first and win (`:40-49`, built `:117-124`) —
`AssistantWorkspace.LegacyWorkdir` and `RunsRoot`, each its exact subtree only, with the DB / config / logs
siblings still blocked.

The call sites below are the containment surface. Every one checks an individual **operation's path argument**;
none is a process boundary.

| File | Operations checked | Anchors |
|---|---|---|
| `Services/FilesToolHandler.cs` | read, write, delete, list/search, preview, dir suggestion, root narrowing | `:196`, `:285`/`:293`, `:383`–`:462`, `:644`, `:667`, `:789`, `:968`/`:1036`, `:1213`/`:1239` |
| `Services/GitToolHandler.cs` | every git path argument, plus root narrowing | `:681`, `:729` |
| `Services/MemoryService.cs` | vault topic / source reads by reference | `:671`, `:724` |
| `Services/WorkingDirectoryService.cs` | listing and creating working subfolders | `:34`/`:51`, `:79` |
| `Services/AgentVerifier.cs` | the probe root, and each declared-artifact candidate | `:349`, `:429` |
| `Services/RunWorkspaceService.cs` | copy-mode source enumeration, and the source root | `:866`/`:872`, `:1085` |
| `Services/MarkdownExportService.cs` | the export destination folder | `:363` |

`SensitivePathGuard.IsBlocked` is consulted at these sites, always on the path `SafeFolderPath` has **already**
resolved and never on the caller's string: `FilesToolHandler.cs:296`, `:465`, `:677`, `:791`, `:972`, `:1038`,
`:1218`, `:1241`; `MemoryService.cs:740`; `WorkingDirectoryService.cs:53`, `:85`; `RunWorkspaceService.cs:873`;
`Vault/AssistantFolderValidator.cs:45`. `Helpers/RunWorkspaceRedirects.cs:71`/`:112` reach it through
`CanonicalizeAllowedIsland`, resolving a carve-out.

**Coverage is not uniform, which is why both lists are here.** `GitToolHandler`, `AgentVerifier` and
`MarkdownExportService` never consult the guard, nor do `MemoryService`' two *read* sites. §5 states the pairing as
the norm for a **new** handler, not as a description of every existing one.

## 2. What is NOT contained

`Services/Plugins/McpPluginToolHandler.cs:49-56` builds `StdioClientTransportOptions` carrying **only** `Name`,
`Command` and `Arguments`; `PluginService.cs:510-514` constructs the handler with no options object at all. At
both anchors: no working-directory restriction, no environment restriction, no job object, no restricted token, no
AppContainer — and **`_command`/`_args` are themselves never path-checked** (neither guard class appears in either
file). The child inherits Pia.Wpf's ambient working directory, environment and user token.

**Configuring an MCP server is equivalent to running that program yourself.**

That reaches further than "bypasses the check". `SafeFolderPath.cs:14-19` documents its own containment
*assumption* — that the file toolset "exposes no capability to create reparse points (junctions/symlinks) inside
the sandbox", with the residual dangling-leaf and directory-swap TOCTOU windows resting on it — and scopes it to
"these tools". An MCP subprocess is not one of them, so a server can invalidate the premise §1 states for itself.

Also unprotected: **`%LOCALAPPDATA%\Pia\history.db`**, the only database (`SqliteContext.cs:37-42`), unencrypted —
its connection string carries no key (`:27`). What protects it today is the guard, not encryption: it sits in a
blocked root (`SensitivePathGuard.cs:77`) outside both carve-outs, so no built-in tool reaches it however the
sandbox is configured. Anything that skips the guard reads it as an ordinary file. Likewise **anything the user's
configured plugin command does** (never validated), and **logs the user attaches to support** —
`%LOCALAPPDATA%\Pia\Logs\pia-*.log` is expected to be attached (CLAUDE.md, *Privacy-First Logging*), so release
redaction is only as good as the `Sensitive*` discipline at each call site.

## 3. Why the tool-permission gates are not a substitute

Destructive-tool detection is a name match on `DestructiveStems` (`ToolPermissionService.cs:60-61`) in
`IsDeleteLike` (`:98-101`); its own doc comment (`:89-95`) calls it "a NAME HEURISTIC, not a boundary". And
classification is route-based, not capability-based: `PluginService.IsMcpTool:290-294` is a route/type check,
`ToolClassifier.Classify:31-37` short-circuits to `External` on it.

MCP's own `ToolAnnotations.DestructiveHint` / `ReadOnlyHint` exist on `McpClientTool.ProtocolTool`, but nothing
plumbs them out of `McpPluginToolHandler` (it reads `tool.Name` `:90`, calls `tool.InvokeAsync` `:126`). Consuming
them is **T2-7b**, and it may only ever narrow, never widen, **because a server must not be able to declare itself
safe** — which is why the hint will not be a trust boundary either.

## 4. The boundaries that do hold

- **The gate runs before a write executes**, on all three surfaces through one resolver `ToolAutonomy.Resolve`:
  `ChatSession.cs:1049` (card), `AssistantViewModel.cs:1639` (voice), `BackgroundAssistantTurnRunner.cs:467`
  (unattended); a run may also park `WaitingForInput` for a human decision (`AgentRunOrchestrator.cs:1297`).
- **A destructive external tool hits a floor before any policy branch** (`ToolAutonomy.cs:142-150`) — but
  interactively it only suppresses auto-approval and still prompts; it refuses outright only unattended and in voice.
- **The run's policy is its authority of record** (`AgentRuns.PolicyJson`; `AgentRun.cs:45`, column
  `SqliteContext.cs:333`), additive-only, so a document cannot shrink the floor (`RunAutonomyPolicy.cs:3-12`) and
  an unrecognised class name never becomes authority (`:31-35`).
- **User content leaves release logs by compilation, not by log level.** Tool *result* content is logged only
  through `SensitiveDebug` (`[Conditional("DEBUG")]` — call *and* argument evaluation erased from Release IL):
  `AiClientService.cs:404-405`, `McpPluginToolHandler.cs:129-130`, `ChatSession.cs:1019`, `GitToolHandler.cs:749`,
  plus the plugin command line (`:45-46`) and MCP arguments (`:114`). Release keeps metadata only
  (`GitToolHandler.cs:747`, `MemoryToolHandler.cs:94`).
- **Single-flight, scoped to a trigger and to that alone**: `AnyExecutingRunForTriggerAsync`
  (`AgentRunService.cs:765`, SQL `:761-763`) counts non-terminal runs by `TriggerRef`; child runs carry null. See
  [`16-event-trigger-design-note.md`](16-event-trigger-design-note.md) §3b for what it does not bound.
- **Workspace isolation is best-effort by design, not a boundary** (`RunWorkspaceService.cs:20-22`): nothing
  throws, every fault degrades worktree → copy → *no isolation at all*, and over the caps it provisions nothing
  (`:34-37`).

## 5. If you are adding a new tool or handler

1. **Resolve every caller-supplied path through `SafeFolderPath` immediately before the syscall**, in the handler
   itself — never trust a path a caller says it validated; there is no process boundary behind you. If your tool
   can create a reparse point inside a sandbox root, **say so here**: `SafeFolderPath.cs:14-19` names that as the
   assumption its residual TOCTOU windows rest on.
2. **Then pass that resolved path through `SensitivePathGuard.IsBlocked`.** Resolve first, guard second, act third
   — the denylist and its carve-outs are defined over *canonical* paths, so the order is not cosmetic.
3. **Route the tool through `ToolAutonomy.Resolve` with a real `ToolClass`** — do not lean on the name heuristic
   to notice your tool is destructive.
4. **Never widen authority from data a server or a persisted document supplies.** Narrowing is fine; widening is
   the bug this page is about.
5. **Log payloads, user-named items and URLs through `SensitiveDebug` / `SafeUrl`** — the log ships to support.
