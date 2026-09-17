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
