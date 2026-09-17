# Local MCP servers

**Status:** In progress
**Owner:** Marco Altmann
**Written:** 2026-09-17
**Origin:** Owner request — "make it possible to allow classic local addition of MCPs to Pia:
adding a json, test connection, select allowed tools", followed by "adding mcps should go into
settings / assistant, UI like personas or routines".

## What exists today

Pia already speaks MCP, but only for servers an admin pushes down the sync channel.

- `SyncPlugin` (`src/Pia.Shared/Models/SyncPlugin.cs`) is the catalogue row. `Kind` is
  `mcp_server` | `builtin_tool_pack` | `rest_api`; `ConfigJson` carries the kind-specific manifest.
- `PluginService.ApplyServerPluginsAsync` upserts those rows, persists non-preloaded ones to the
  `Plugins` SQLite table, and calls `HandleNewServerPluginAsync` to activate them.
- `HandleNewServerPluginAsync` parses `transport` / `command` / `args` / `systemPromptAddition`,
  runs preflight (command on PATH, Node version, cab extraction) and builds an
  `McpPluginToolHandler`.
- `McpPluginToolHandler` opens a `StdioClientTransport`, lists tools, and returns every call as a
  **deferred** `PluginToolCall` so MCP goes through the same approval gate as a built-in write.
- `PluginService.GetToolCatalog` feeds Settings → Assistant → Tool permissions, so any registered
  MCP tool is already grantable.

Four gaps block user-added servers:

1. **No local write path.** Every code path that creates a plugin row starts from a server push.
2. **No env or working directory.** `StdioClientTransportOptions` has `EnvironmentVariables` and
   `WorkingDirectory`; the handler sets neither. Nearly every real MCP server (GitHub, Slack,
   Linear, Notion) authenticates through an env var, so without this the feature is a toy.
3. **Tool-name collisions are silent.** `PluginService._toolNameRoutes` is keyed by the bare tool
   name. `@modelcontextprotocol/server-filesystem` exposes `read_file` / `write_file` — the same
   names as Pia's built-in files pack — and whichever registers last wins.
4. **No probe.** `McpPluginToolHandler.InitializeAsync` swallows its exception and logs it. A
   "Test connection" button needs the failure text, not a log line.

`sse` is a fifth, pre-existing gap: the transport is preflighted with `PingUrlAsync` and then a
**stdio** handler is built with `command ?? ""` regardless, so a remote server has never worked.

## Decisions

| Question | Decision | Why |
|---|---|---|
| Transports | **stdio only** | Owner call. A `url` in pasted JSON is rejected at parse time with a clear message rather than half-working as it does today. |
| Name collisions | **Auto-prefix**: a local server's tools are exposed as `<slug>__<tool>` | Owner call. Collisions become impossible instead of detectable, the model sees which server a tool came from, and a standing grant keyed on the name can never change meaning later. |
| Prefix scope | **Local servers only** | Prefixing server-pushed MCPs would rename tools in existing deployments and silently void their standing grants. Local slugs are unique among themselves, so local servers cannot collide with anything. |
| Config entry | **Paste JSON + form fields** | Owner call. Paste a Claude-Desktop-shaped blob to prefill, or type the fields. No second source of truth on disk. |
| Storage | The existing `Plugins` table, `Kind = "mcp_server"`, marked `"source":"local"` in `ConfigJson` | Catalogue, routing, the approval gate and the tool-permission surface then work unchanged. `SyncPlugin` is a wire DTO in `Pia.Shared` and must not grow a client-only column. |
| Env values | **DPAPI-encrypted** in `ConfigJson` | Matches `ProviderService`, which encrypts API keys with `DpapiHelper` before they reach the DB. |
| Allowed tools | A per-server `allowedTools` list, **separate** from the grant tiers | The allowlist decides *exposure* (does the model see this tool at all); session/Always grants decide *auto-approval*. Conflating them would make un-ticking a tool look like a permission when it is a visibility choice. |

## Shape

### ConfigJson for a local server

```json
{
  "source": "local",
  "transport": "stdio",
  "command": "npx",
  "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\data"],
  "env": { "GITHUB_TOKEN": "<dpapi-base64>" },
  "cwd": "C:\some\dir",
  "toolPrefix": "filesystem",
  "allowedTools": ["read_file", "list_directory"],
  "defaultEnabled": true
}
```

`allowedTools` holds **unprefixed** names — the names the server itself reports, so a re-probe can
match them. `null` means "every tool"; an empty array means "none, server is inert".

### Accepted paste formats

All three, normalised to one definition:

- the Claude Desktop envelope `{"mcpServers": {"github": {"command": ..., "args": [...]}}}`
- a single named entry `{"github": {"command": ...}}`
- a bare server object `{"command": "npx", "args": [...], "env": {...}}`

A `url` / `type: "http"|"sse"` key is a hard parse error naming stdio-only support.

### New/changed types

| File | Change |
|---|---|
| `Services/Plugins/LocalMcpDefinition.cs` | new — the parsed definition, `ToConfigJson` / `FromConfigJson`, slug derivation |
| `Services/Plugins/LocalMcpJsonParser.cs` | new — the three paste shapes → definition, with a localisable error |
| `Services/Plugins/McpServerProbe.cs` | new — `ProbeAsync` → tools or an error string, on a timeout, disposing the child process |
| `Services/Plugins/McpPluginToolHandler.cs` | env + cwd + tool prefix + allowlist |
| `Services/Plugins/PluginService.cs` | `ProbeLocalMcpAsync` / `SaveLocalMcpAsync` / `RemoveLocalMcpAsync` / `GetLocalMcpDefinition`; local ids excluded from preference push; `HandleNewServerPluginAsync` renamed and taught the new keys |
| `ViewModels/McpServersSettingsViewModel.cs`, `ViewModels/Models/McpServerRow.cs`, `McpToolRow.cs` | new — the master/detail surface |
| `Views/SettingsViews/McpServersView.xaml` | new — list + detail/edit pane, mirroring `PersonasView` |
| `Views/SettingsViews/AssistantView.xaml` | new inner `TabItem` |

### Why the prefix is applied in the handler

`RegisterHandler` and `GetToolCatalog` both read `IPluginToolHandler.GetTools()`. Renaming and
filtering there means a hidden tool is unroutable *and* absent from the grant catalogue for free,
with no second filter to keep in sync. `McpClientTool.WithName` keeps the underlying
`ProtocolTool`, so the wire call still carries the server's own name.

## Risks

- **`npx` is a `.cmd`.** `CheckCommandOnPathAsync` and `StdioClientTransport` must both cope with a
  shim rather than an exe. Verify against a real `npx -y @modelcontextprotocol/server-everything`.
- **A wedged child process.** The probe must have a timeout and must dispose the client, or a
  mistyped command leaves an orphan process per Test click.
- **Env values in logs.** Log env **names** only. Not even `SensitiveDebug` should carry a value.
- **Startup cost.** Each enabled local server spawns a process at launch inside
  `InitializePersistedPluginsAsync`. Activation already runs per plugin sequentially.

## What review caught, and the answers

Four defects found by reading the finished code against the UI flows, all fixed:

- **A stopped server lost its allowlist on edit.** The editor's tool list comes from the *running*
  handler, so a disabled or failed server opens with nothing to tick — and "nothing ticked" read as
  "no restriction", so saving a name change reopened every tool. The view-model now carries the
  saved allowlist through the edit session and falls back to it whenever nothing has been probed.
- **That fallback list was handed out live.** `CurrentAllowedTools` returned the field itself, which
  `CloseEditor` then clears. Production survived on call ordering; the copy makes it not depend on
  that.
- **Test connection and Save took different launch paths.** Activation ran `where.exe <command>`
  first, and `where.exe` reads any rooted path as its own `directory:pattern` syntax and errors —
  so an absolute command (the norm in a pasted config) probed fine and then refused to start with
  "not found on PATH". A local server now skips the preflight entirely, and
  `CheckCommandOnPathAsync` answers a rooted path off the filesystem for the server-pushed kind.
- **A server that failed to start reported "Running · 0 tools".** `InitializeAsync` swallows its
  exception and the handler was registered regardless. The handler now keeps `LastError`, pre-handler
  failures land in `PluginService._startFailures`, and `LocalMcpStatus.IsRunning` is the absence of
  an error rather than the presence of a handler.

Measured rather than assumed:

- **`npx` launches.** The SDK wraps the command in `cmd.exe /c` on Windows, so a `.cmd` shim works
  and no resolver is needed.
- **A timed-out probe leaves no orphan.** Probed a command that starts and never speaks MCP, with a
  5 s timeout: the process count returned to its starting value.
- The probe timeout is **90 s**, because the first `npx -y <package>` downloads the server before it
  answers.

## Known gaps

- Removing a server leaves any standing grant keyed on its plugin id behind. Harmless — ids are
  never reused — but the rows accumulate in settings.
- `ApplyServerPluginsAsync` would delete a local row if the server ever pushed a tombstone for its
  id. It cannot: local ids are minted client-side and the server has never seen them.

## Live verification (2026-09-17)

Driven through the real app on a throwaway profile, against
`npx -y @modelcontextprotocol/server-everything`:

- Pasting the `mcpServers` envelope filled Name, Command, Arguments (one per line) and
  `DEMO_TOKEN=s3cret-value`.
- Test connection listed **13 tools**, each rendering its own `McpServers_Tool_<name>` id.
- Saved with only `echo` ticked. The log line is the evidence:
  `MCP plugin everything initialized with 1 of 13 tools: everything__echo` — allowlist and prefix
  both applied.
- Reopening the editor on the running server showed all 13 with the stored ticks, so a restriction
  can be widened again.
- Restart re-activated it unprompted: `Plugin everything (stdio) activated with 1 tools`, which also
  proves the DPAPI round-trip — the server only starts with its env decrypted.
- The row read `Running · 1 tool(s)`; the switch flipped it to `Disabled` and the log confirmed
  `enabled=False`. (`ww_set_checked` is a no-op on that switch — physical click, then read the log.)
- `s3cret-value` appears **nowhere** in the log or the database; `history.db-wal` holds
  `"env":{"DEMO_TOKEN":"AQAAANCMnd8BFdER…"}`, the DPAPI blob.

Isolation held: the run wrote its own `Workspace/Vault`, and the real vault was untouched.

### Three public servers, end to end (2026-09-17)

The everything server is a demo. This round used three servers people actually install, none of
which needs a credential, added through the real profile's UI and driven from real chats on **both**
providers — the Anthropic one (Sonnet) and Pia Cloud, whose tool calls take the server-side path.
Each server was handshaken from a shell first, so a slow first `npx`/`uvx` download could not be
mistaken for a broken server.

| Server | Command | Tools |
|---|---|---|
| `filesystem` | `npx -y @modelcontextprotocol/server-filesystem <dir>` | 14 of 14 |
| `memory` | `npx -y @modelcontextprotocol/server-memory`, `MEMORY_FILE_PATH` in env | 9 of 9 |
| `time` | an **absolute** path to `uvx.exe`, `mcp-server-time --local-timezone Europe/Berlin` | 1 of 2 |

What each one was chosen to prove:

- **The prefix earns its keep.** One turn made both calls: `filesystem__read_file` on the server's
  own directory and the built-in `read_file` on a workspace file. The log shows two different
  handlers answering the same underlying tool name — `MCP tool filesystem__read_file invocation on
  plugin filesystem` returning the server's `{"content":[…]}` envelope, against the built-in's
  `total_lines=2`. Without the prefix one of them is unreachable.
- **Env reaches the child.** `memory` was given `MEMORY_FILE_PATH`, and `knowledge-graph.json`
  appeared at exactly that path on the first `create_entities` call — disk as the witness, not a log
  line. After a restart, `memory__search_nodes` read the same entity back, so the DPAPI round-trip
  feeds the same file across sessions.
- **A rooted command starts.** `time` was entered as a full path to `uvx.exe`, the shape that used
  to probe fine and then fail activation with "not found on PATH". It probes, saves, starts, and
  re-starts after a restart.
- **A withheld tool is invisible, not just unrouted.** `convert_time` was left unticked; the row
  reads `1 Tool(s)`, the log says `initialized with 1 of 2 tools`, and Settings → Tool permissions
  lists `time__get_current_time` and no `convert_time` at all.

Restart re-activated all three sequentially in **2.4 s** total (13:04:21.3 → 13:04:23.7), allowlist
intact. Closing Pia left no orphan child process.

Two negative paths, both landing where they should:

- A pasted `{"type":"http","url":"https://mcp.context7.com/mcp"}` is refused at parse time, naming
  the URL: "Remote servers are not supported — only servers that Pia starts on this machine."
- A server whose command does not exist reads `Not running: MCP server process exited unexpectedly
  (exit code: 1)` with the child's stderr tail underneath, in both the editor message and the list
  row — never `Running · 0 tool(s)`. Its editor reopens with the fields intact and no tool list.

Two automation notes for the next run:

- The row's enable switch did not respond to a WinWright mouse click **or** to a raw `SendInput`
  click at its centre (100 % DPI, window foreground, a click on the row itself worked from the same
  coordinates). `ww_set_checked` flips `IsChecked` but never runs `ToggleServerCommand`, so the row
  reads disabled while the subprocess keeps running. Since `IsChecked` is a `TwoWay` binding and the
  persistence hangs off `Command`, the two can disagree — worth a human click to see whether this is
  only an automation artefact.
- The delete confirmation is a WPF-UI `ContentDialog` whose buttons carry the framework's own
  `PrimaryButton` / `CloseButton` ids, not Pia ones.

### Detail pane and the enable switch (2026-09-17)

Driven through a throwaway profile with two servers: a deliberately broken one
(`no-such-mcp-binary`, a working directory and one env var) and `everything` with `echo` unticked.

- The failed server reads `Not running` as a badge, the child's stderr tail in the notice block,
  and `DEMO_TOKEN` under Environment variables — the name, never the value.
- The running one reads `Running`, `everything · 12 of 13 tool(s) offered`, and all 13 tools with
  their descriptions, `echo` marked `withheld`.
- Toggling it off put `Stopping…` and a ring on that row alone; the other row's switch
  stayed live throughout. Toggling back on returned `Running · 12 tool(s)`, allowlist intact.
- Test connection on the detail pane probed a server that was switched **off** and filled the pane
  with all 13 tools, `13 of 13 tool(s) offered` and `Connected. 13 tool(s) available.` — the state
  that previously had nothing to show.

Two corrections to the automation notes above:

- The enable switch **is** drivable: `ww_focus` on it plus `Space` runs the command and
  persists, because `ButtonBase.OnKeyDown` routes through `OnClick`. Each toggle rebuilds the
  list, so focus is lost and the next `Space` goes nowhere — re-focus between toggles.
- The blank-screenshot stall cleared here right after a `ww_window action=resize` (whose response
  still fails to serialize). The blank captures were the window's background gradient with nothing
  painted over it; every shot after that one call rendered. One data point, so retry a resize
  before giving up on screenshots rather than counting on it.

### The stale-status bug (2026-09-17)

Owner report: after every restart only one of three servers read `Läuft`, and `Test connection` on
one of the others answered `Verbunden. 2 Tool(s) verfügbar.` — a flat contradiction.

The servers were all up. The real log has `Plugin filesystem (stdio) activated with 14 tools`,
then `memory`, then `time`, all successful — and `Settings page initialized` timestamped between
the first and the second. `InitializePersistedPluginsAsync` activated each server without raising
`PluginsChanged`, so a Settings page built during that loop froze its rows at whatever happened to
be true at that instant and nothing ever corrected them. Every other mutating path already raised it,
which is why a toggle looked fine and only a restart showed the bug.

The fix raises it (and rebuilds the tool routes) **per server** inside the loop, not once at the end,
so a page built mid-startup fills in as each one comes up. Two tests hold the halves:
`StartupActivation_AnnouncesEveryServerItBringsUp` on the service, and
`AServerThatComesUpAfterTheViewIsBuilt_StopsSayingNotRunning` on the view model. A live restart did
**not** reproduce the race — a throwaway profile boots so much faster than the real one that activation
finished ~5 s before the Settings page was built — so the tests are the evidence, not the walkthrough.

### The open editor was collateral (2026-09-17)

Raising `PluginsChanged` per server made an existing defect reachable, and it is observable: open
**Add server**, type a name, toggle another row, and the editor is gone with everything in it.
`Reload()` clears `Servers`, the ListBox writes **null** back through the two-way `SelectedItem` binding,
and `OnSelectedServerChanged` reads that as a different server and closes the editor. Comparing ids
cannot catch it — the transient value is null, not another id. A `_reloading` flag around the whole
reload now limits the close to a selection the user actually changed.

The test for it has to push the null itself (`Servers.CollectionChanged` → `Reset` → set `SelectedServer`
to null), because there is no ListBox in the harness; it was mutation-checked — it fails with the flag
removed and passes with it.

### Saving looked like nothing happened (2026-09-17)

Owner report: tick or untick a tool in the editor, press **Save**, and the button greys out for
several seconds with no indicator at all.

The grey is the `AsyncRelayCommand` doing its job — it reports `CanExecute` false for as long as
`SaveAsync` runs. What runs that long is `SaveLocalMcpAsync`: it shuts the subprocess down, persists,
and activates the server again, and bringing an `npx` server back up is seconds of work. Nothing in
the view said so. `IsSaving` now drives a `ProgressRing` and a line naming the restart, mirroring the
`IsTesting` pattern a few rows up in the same editor.

`CancelEdit` is gated on `!IsSaving` in the same change: leaving the editor mid-save let the save's
own `CloseEditor()` land on whatever had been opened after it.

Two things this does **not** cover. A row's switch stays clickable while a save restarts that same
server — `_toggleGate` serialises toggles against each other, not against a save. And the editor's
fields stay editable during the window; what gets written is the snapshot taken when Save was pressed.

### "Läuft" was the wrong word (2026-09-17)

Owner call: the German status read `Läuft` / `Läuft nicht`, a literal rendering of Running that does
not fit a server the user connects to. The running/failed pair is now `Verbunden` / `Nicht verbunden`
(badge, status line, and the with-reason variant), which matches what `Verbindung testen` already
answers; `Deaktiviert` still marks the switch being off, and `Wird gestartet…` / `Wird beendet…` still
mark the transitions. The German `Detail_ToolsStopped` sentence moved with them. The `Läuft` hits
under `ChatState_*`, `Run_State_*` and `Assignments_*` belong to other features and were left alone.
