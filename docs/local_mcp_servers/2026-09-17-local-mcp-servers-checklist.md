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

- [x] **C1 — Tests.** Parser shapes and rejections, ConfigJson round-trip, allowlist/prefix
      filtering in the handler, local-vs-server locality, preference-push exclusion.
      *Deps:* A4, B1 · *Effort:* S · *Value:* High

- [ ] **C2 — Live verification.** Add `npx -y @modelcontextprotocol/server-everything` through the
      real UI: test connection, restrict the allowlist, call a tool in a chat, restart and confirm
      it re-activates.
      *Deps:* B2, C1 · *Effort:* XS · *Value:* High

- [x] **D1 — Release notes.** A bullet in `docs/release_notes/RELEASE.md`.
      *Deps:* C2 · *Effort:* XS · *Value:* Med

## Decision gates

| Gate | Question it answers | Blocks |
|---|---|---|
| A3 on `npx` | **Answered:** the SDK wraps the command in `cmd.exe /c` on Windows, so a `.cmd` shim launches and no resolver is needed. | — |

## Suggested order

A1 → A2 → A3 (the gate) → A4 → C1 for the service half → B1 → B2 → C1 for the view half → C2 → D1.
