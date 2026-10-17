# Sandboxed local MCP servers: checklist

**Status:** Open
**Owner:** Marco Altmann
**Written:** 2026-10-17
**Origin:** [2026-10-17-mcp-sandbox.md](2026-10-17-mcp-sandbox.md)

Effort: `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.
Value: `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little
standalone value, unblocks a High.

Branches: integration `feature/mxc-sandboxed-mcp`. worker1 works on `feature/mxc-sandbox-core`,
worker2 on `feature/mxc-sandbox-ui`, both branched from it, each in its own `.worktrees/` checkout.
The coordinator merges into the integration branch. A step is done when its landing commit ticks the
box, the rebuild reports `0 Warning(s)` / `0 Error(s)` in Debug **and** Release, and the test exe
reports `Failed: 0`.

## Decision gates

| Gate | Step | Question |
|---|---|---|
| G1 | S1 | Non-elevated MXC spawn works and enforces. Command dir under Program Files. Profile leak. |
| G2 | S2 | SDK loads in WPF, publish lays out native files, CI runner can spawn. |
| G3 | (deferred) | Shim servers sandboxable. Out of the first shot. |

If G1 fails, stop and do not tick C1 or anything below it. Report to the coordinator.

## Steps

- [x] **S1 Non-elevated spike (worker1).** A throwaway console spike (outside the repo, scratch dir) launched at medium IL spawns Mxc's `artifacts/servers/files` exe under MXC and checks the four denials, a Program-Files command without its dir granted, and `%LOCALAPPDATA%\Packages` profile growth across three spawns.
  *Deps:* none · *Effort:* XS · *Value:* Enabler (gate G1)
- [x] **S2 Native-outside-app-dir check (worker1).** Add `Microsoft.Mxc.Sdk` 1.0.1 to `Pia.Wpf` with `ExcludeAssets="native"`. Confirm `dotnet publish -r win-x64 --self-contained` then carries **no** `mxc_ffi.dll`. With the four native files placed in a separate dir and a `NativeLibrary.SetDllImportResolver`, confirm a Tier 3 spawn works from the app process, and record which files it actually needs and where it looks for `plm.exe`.
  *Deps:* S1 · *Effort:* XS · *Value:* Enabler (gate G2)
- [x] **D1 Runtime provisioner (worker1).** `RuntimeAssetCatalog.MxcRuntime` (mirror key `runtimes/microsoft.mxc.sdk.1.0.1.nupkg`, nuget.org upstream, pinned SHA-256), `PiaPaths.MxcRuntimeDirectory`, and `MxcRuntimeProvisioner`: download through `IAssetDownloader`, pin check, `.tmp` extract of `runtimes/win-x64/native/*`, WinTrust plus trusted-publisher check, resolver registration, progress. `IMxcAvailability` states `NotInstalled` / `Downloading` / `Unsupported` / `Ready`. Unit tests for every failure path.
  *Deps:* S2, C0 · *Effort:* S · *Value:* High
- [x] **D2 Catalogue scripts (worker1).** `Mxc` group in `scripts/RuntimeAssetCatalogue.ps1`, so `Publish-RuntimeAssets.ps1 -Include Mxc` and `Save-RuntimeAssets.ps1 -Include Mxc` work, and `RuntimeAssetCatalogTests` holds both sides. `-ListOnly` only: the real upload to `storage.pia-ai.de` is the owner's step.
  *Deps:* D1 · *Effort:* XS · *Value:* Enabler
- [x] **C0 Contract commit (worker1).** `McpSandboxPolicy`, `LocalMcpDefinition.Sandbox`, the `sandbox` ConfigJson round-trip with tests, the `IMxcAvailability` interface, `PiaPaths.McpSandboxRoot`. No behaviour yet. worker2 merges it next.
  *Deps:* S1 · *Effort:* XS · *Value:* Enabler
- [x] **F1 Test MCP server (worker2).** `tests/Pia.TestMcpServer`, ported from Mxc's `DemoMcp.FileServer` (list/read/write/delete file and dir, server info) plus `export` (HTTP POST, the egress probe). Logs to stderr only, returns `ERROR <Type>: <msg>` as `isError`, never validates paths. Wired into the test project with `ReferenceOutputAssembly=false` so its output sits under the test bin.
  *Deps:* none · *Effort:* S · *Value:* Enabler
- [x] **C1 Launcher and transport (worker1).** `CommandLine`, `MxcMcpLauncher` (recipe, command-dir rule, container naming per S1, graceful shutdown), `MxcClientTransport` (stderr tail), `McpTransportFactory` used by both `McpPluginToolHandler` and `McpServerProbe`.
  *Deps:* C0, S2 · *Effort:* M · *Value:* High
- [x] **C2 Path guard (worker1).** `SandboxPathGuard` with every rule from the plan, localisable messages, unit tests per rule.
  *Deps:* C0 · *Effort:* S · *Value:* High
- [ ] **C3 Fail closed and availability (worker1).** `MxcAvailability` (cached, reason, the D1 states). `PluginService` refuses to start a sandboxed server uncontained, reports a specific `Not running` reason, and starts a missing runtime's download at startup. Same for the probe.
  *Deps:* C1, D1 · *Effort:* S · *Value:* High
- [ ] **C4 ACE sweep (worker1).** Port `AceSweeper`, shallow-first, refuse system paths, run at startup before the first activation, count in Information and paths in `SensitiveDebug`.
  *Deps:* C1 · *Effort:* S · *Value:* High
- [ ] **T1 MXC integration tests (worker1).** Spawn `Pia.TestMcpServer` through the real factory on a temp fixture: allowed, denied and egress cases, the uncontained control, and ACE hygiene including a simulated leak. Runtime-skipped with a reason when MXC cannot spawn.
  *Deps:* C1, C4, F1 · *Effort:* S · *Value:* High
- [x] **U1 Editor sandbox section (worker2).** Toggle, rw/ro lists (add/remove, folder picker, "create under sandbox root"), network switch with its warning, guard errors inline, the "why disabled" line (MXC unsupported, or shim command), and the runtime-download prompt with progress when the runtime is not installed. Default on for a new server. AutomationIds `McpServers_Sandbox*` and their `ViewAutomationIdTests` rows. Strings localised like the rest of the view.
  *Deps:* C0 (C2 for live errors) · *Effort:* M · *Value:* High
- [x] **U2 Status and detail pane (worker2).** The list row and detail pane show `Sandboxed · rw=N ro=M net=off` or `Not sandboxed`, and a fail-closed reason in the existing notice block.
  *Deps:* C3, U1 · *Effort:* S · *Value:* Med
- [x] **U3 Action card line (worker2).** `ActionCardBuilder` adds the one-line sandbox summary for calls from a sandboxed server. No gate change.
  *Deps:* C1 · *Effort:* XS · *Value:* Med
- [x] **E1 E2E harness (worker2).** A script (under `tests/ui-scripts/` or `scripts/`, per that README) that seeds the fixture tree (workspace/protected/secret), records SHA-256s, launches the Debug exe **de-elevated** with the throwaway profile and `PIA_CLOUD_SERVER_URL=https://localhost:18081`, and prints the IL, ACE state and orphan processes on demand.
  *Deps:* F1 · *Effort:* S · *Value:* Enabler
- [ ] **E2 Live run (worker2, coordinator reviews).** Scenarios 0–12 from the plan through WinWright on the e2e stack, with results written into the plan's *Live verification* section.
  *Deps:* E1, U1, U2, U3, C3, C4, D1 · *Effort:* S · *Value:* High
- [ ] **O1 Mirror upload (owner).** `Publish-RuntimeAssets.ps1 -Include Mxc` against `storage.pia-ai.de`. Until it runs, clients fetch from nuget.org with the same pin. Not blocking the PR, but due before the release that ships this.
  *Deps:* D2 · *Effort:* XS · *Value:* Med

## Suggested order

1. S1, then S2. These are the cheapest and most decisive. In parallel, F1 and E1 (worker2), which
   survive any gate result.
2. C0, then worker2 merges it. U1 starts against the contract.
3. C1 → D1 → D2 → C2 → C3 → C4 → T1 (worker1). In parallel: U1 → U3 → U2 (worker2), merging core as it lands.
4. Coordinator merges both into `feature/mxc-sandboxed-mcp` and runs the full gate.
5. E2 on the merged branch, then the coordinator's review and the PR into `develop`. Merging is the
   owner's.

## Not yet planned

Server-pushed plugin sandboxing · `RequireMcpSandbox` policy key · gate relaxation for sandboxed tools ·
shim servers (G3) · cloud-synced and UNC paths · per-call containers · Tier 1 egress rules. Details are
in the plan.
