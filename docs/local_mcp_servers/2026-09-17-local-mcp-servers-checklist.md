# Local MCP servers — checklist

**Status:** In progress
**Owner:** Marco Altmann
**Written:** 2026-09-17
**Origin:** [2026-09-17-local-mcp-servers.md](2026-09-17-local-mcp-servers.md)

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new
surface · `L` a week or more, a new subsystem.

**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler`
little standalone value, unblocks a High.

## Steps

- [x] **A1 — Definition + JSON parser.** `LocalMcpDefinition` (command, args, env, cwd, prefix,
      allowlist) with `ConfigJson` round-tripping, and a parser accepting the `mcpServers`
      envelope, a single named entry and a bare server object, rejecting `url`/`type`.
      *Deps:* — · *Effort:* S · *Value:* Enabler

- [x] **A2 — Handler: env, cwd, prefix, allowlist.** `McpPluginToolHandler` passes
      `EnvironmentVariables` and `WorkingDirectory`, filters `GetTools()` by the allowlist, and
      renames through `McpClientTool.WithName`. Env names logged, never values.
      *Deps:* A1 · *Effort:* S · *Value:* High

- [x] **A3 — Probe.** `McpServerProbe.ProbeAsync` connects, lists tools, disposes, on a timeout,
      returning tools or a human-readable error.
      *Deps:* A1 · *Effort:* S · *Value:* High

- [x] **A4 — PluginService local CRUD.** `ProbeLocalMcpAsync` / `SaveLocalMcpAsync` /
      `RemoveLocalMcpAsync` / `GetLocalMcpDefinition`; local ids excluded from the preference push;
      activation path taught the new ConfigJson keys and made stdio-only for local rows.
      *Deps:* A2, A3 · *Effort:* M · *Value:* High

- [x] **B1 — Settings surface.** `McpServersSettingsViewModel` + `McpServersView`, master/detail
      like `PersonasView`, hosted as a new inner tab under Settings → Assistant. Paste-JSON box,
      form fields, Test connection, tool checkboxes, enable toggle, Delete.
      *Deps:* A4 · *Effort:* M · *Value:* High

- [x] **B2 — AutomationIds + localisation.** `McpServers_*` ids on every interactive control, a
      `ViewAutomationIdTests` row, and en/de/fr resx parity.
      *Deps:* B1 · *Effort:* S · *Value:* Med

- [x] **B3 — Detail pane + toggle feedback.** Owner round: the detail pane mirrors `PersonasView`
      (card, centred placeholder, status badge, failure notice, working directory and environment
      key names) and lists the server's tools with the withheld ones marked, with Test connection
      alongside Edit and Delete so a stopped server can still be probed; toggling one server no
      longer greys out every other switch, and the row that is moving shows a spinner reading
      Starting… or Stopping….
      *Deps:* B2 · *Effort:* S · *Value:* High

- [x] **C1 — Tests.** Parser shapes and rejections, ConfigJson round-trip, allowlist/prefix
      filtering in the handler, local-vs-server locality, preference-push exclusion.
      *Deps:* A4, B1 · *Effort:* S · *Value:* High

- [x] **C2 — Live verification.** Add `npx -y @modelcontextprotocol/server-everything` through the
      real UI: test connection, restrict the allowlist, call a tool in a chat, restart and confirm
      it re-activates.
      *Deps:* B2, C1 · *Effort:* XS · *Value:* High

- [x] **D1 — Release notes.** A bullet in `docs/release_notes/RELEASE.md`.
      *Deps:* C2 · *Effort:* XS · *Value:* Med

- [x] **C3 — Live check of the detail pane and the toggle.** In the real app: the detail pane
      against a running server and a failed one, and two switches flipped back to back.
      *Deps:* B3 · *Effort:* XS · *Value:* High

- [x] **B4 — Status refresh + tool-description hints.** Owner round: `InitializePersistedPluginsAsync`
      raises `PluginsChanged` per server, so a Settings page built mid-startup stops freezing its rows
      at "not running"; and a tool's description moved into a `PiaHelpHint` glyph in both tool lists.
      *Deps:* B3 · *Effort:* XS · *Value:* High

- [x] **B5 — Save feedback + German status wording.** Owner round: saving the editor restarts the
      subprocess, so the Save button now spins and says so instead of greying out silently; and the German
      running/failed states read "Verbunden" / "Nicht verbunden" rather than the literal "Läuft".
      *Deps:* B4 · *Effort:* XS · *Value:* Med

## Decision gates

| Gate | Question it answers | Blocks |
|---|---|---|
| A3 on `npx` | **Answered:** the SDK wraps the command in `cmd.exe /c` on Windows, so a `.cmd` shim launches and no resolver is needed. | — |

## Suggested order

A1 → A2 → A3 (the gate) → A4 → C1 for the service half → B1 → B2 → C1 for the view half → C2 → D1,
then the owner round B3 → C3.
