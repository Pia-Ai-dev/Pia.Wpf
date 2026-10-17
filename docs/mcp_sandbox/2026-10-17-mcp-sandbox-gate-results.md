# Sandboxed local MCP servers: gate results

**Status:** G1 passed with caveats (below). G2 pending.
**Owner:** Marco Altmann
**Written:** 2026-10-17
**Origin:** Gates G1 and G2 of [2026-10-17-mcp-sandbox.md](2026-10-17-mcp-sandbox.md), checklist steps S1
and S2 in [2026-10-17-mcp-sandbox-checklist.md](2026-10-17-mcp-sandbox-checklist.md).

## G1: MXC spawn from a non-elevated process (S1)

**Method.** A throwaway console app (outside the repo) with `Microsoft.Mxc.Sdk` 1.0.1 and
`ModelContextProtocol` 2.2.0, started with `explorer.exe` as the parent so it ran at medium integrity
(`S-1-16-8192`, `IsInRole(Administrator)` false) on Windows Server 2025 build 26100. It creates its own
fixture under the user profile, so the user owns every file, and drives the PoC's `files` and `crm` servers
over MCP. A Tier 3 (`AppContainerDacl`) spawn is the only tier tried.

| Check | Result |
|---|---|
| `files` server, exe dir and rw/ro dirs under the user profile | Spawns in 30-330 ms. List, read, write and delete in the rw dir work. A read in the ro dir works |
| Write outside rw, write and delete in ro, read and list of an ungranted sibling | All five fail with `UnauthorizedAccessException`. `protected/important.txt` SHA-256 unchanged |
| Egress, network `Deny` | `HttpRequestException` / `SocketException` "access to a socket was forbidden" for IP targets. A hostname fails at DNS first |
| Egress, network `Allow` | Reaches the host (`HTTP 200`, `HTTP 405`) |
| `dotnet.exe --version`, `node.exe -e` under `Program Files`, dir **not** in `ReadonlyPaths` | Both work. So does `dotnet.exe <server.dll>` as a stdio MCP server with only the dll dir granted |
| `dotnet.exe` with `Ui.Disable = true` | Exit `0x80008089`: needs `Ui.Disable = false`, as in the PoC |
| `%LOCALAPPDATA%\Packages` profiles across 3 spawns | A **per-pid or per-spawn name leaves one profile and one `AppContainer\Mappings` registry key per name, and dispose does not remove them**. A stable name stays at +1 in total |
| Hard kill of the host while a container runs | The child dies with the host (no orphan). The 3 policy-path ACEs leak |
| Respawn with the same name after that kill | Spawns and works. The leaked ACEs are reused, not removed, by a clean dispose. A sweep is still required |
| ACE hygiene after a clean dispose | 0 `S-1-15-2-*` ACEs left on any fixture path |

**Verdict: pass.** The plan survives. Decisions that follow:

- **Container name.** Use a stable `pia-mcp-<prefix>`, not a per-pid name. Per-pid names leak a profile
  per start. Guard the name in-process against concurrent use (a probe of a running server gets a
  `-probe` suffix). A stable name also reuses the SID after a kill, so leaked ACEs do not pile up.
- **Command dir rule.** Confirmed: a command under `Program Files` needs no grant. Keep the rule of
  adding the command dir only when it is outside `%WINDIR%` and `Program Files*`.
- **Spawn does not prove the server can run (caveat).** Tier 3 adds its ACE to every file below a policy
  path, and it silently skips files the caller cannot `WRITE_DAC`. The spawn still reports success with
  no warning, and the server then dies on load with "access denied". Observed with the PoC's published
  `files` dir on `G:`: `DemoMcp.FileServer.dll` and `.exe` are owned by `Administrators` (created by an
  elevated build) and the user only has `Users:(RX)` on them. The same server works elevated, and works
  from a copy the user owns. So a server that an elevated installer or build put outside `Program Files`
  cannot be sandboxed by a non-elevated Pia. The launcher must surface the child's stderr tail, and a
  pre-check that the command's directory and files are owned by the user is worth adding.
- **SDK order rule.** Take `StandardInput`, `StandardOutput` and `StandardError` before the first
  `WaitAsync`. Reading `StandardOutput` after `WaitAsync` has started throws `InvalidOperationException`.
