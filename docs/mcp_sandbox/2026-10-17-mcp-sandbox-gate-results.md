# Sandboxed local MCP servers: gate results

**Status:** G1 and G2 passed (caveats below). The CI runner's ability to spawn a container is still open.
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
  pre-check that the command's directory and files are owned by the user is worth adding. An
  admin-installed **runtime** (Node, .NET) is the exception: when Pia runs elevated, step 2 of
  [2026-10-18-sandbox-runtimes-plan.md](2026-10-18-sandbox-runtimes-plan.md) lets every sandboxed app read
  the runtime's folder. It does not cover a server's own folder.
- **SDK order rule.** Take `StandardInput`, `StandardOutput` and `StandardError` before the first
  `WaitAsync`. Reading `StandardOutput` after `WaitAsync` has started throws `InvalidOperationException`.

## G2: native runtime outside the app dir (S2)

**Method.** `Pia.Wpf` references `Microsoft.Mxc.Sdk` 1.0.1 with `ExcludeAssets="native"`. A throwaway console
with the same reference, started at medium integrity through `explorer.exe`, spawns `cmd /c echo` under
Tier 3 with different subsets of the four native files in a separate directory.

| Check | Result |
|---|---|
| `dotnet build` of `Pia.Wpf` and of `Pia.Wpf.Tests` | `Microsoft.Mxc.Sdk.dll` is copied, no native file is. The exclusion flows to the test project |
| CI-style publish (`-c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false`) | No `mxc_ffi.dll`, `plm.exe`, `wslcsdk.dll` or `wxc-wslc-daemon.exe` in the output. Other packages' natives do appear, so the exclusion is what removed them |
| Spawn with only `mxc_ffi.dll` (6.1 MB) in a separate dir | Works: `PlatformSupport.IsSupported`, backend `ProcessContainer` at tier `AppContainerDacl`, spawn in 80-130 ms, stdout read back |
| Same with `plm.exe`, `wslcsdk.dll` or both added | No difference. `wslcsdk.dll` is only loaded if it sits beside `mxc_ffi.dll`. `plm.exe` and `wxc-wslc-daemon.exe` never start |
| No native file anywhere | `DllNotFoundException` (`0x8007007E`) from the **first SDK call**, including `MxcPlatform.GetPlatformSupport()` |
| Signatures | All four files are `Valid`, signer `CN=Microsoft Corporation`, issuer `Microsoft Code Signing PCA 2024` |

**Verdict: pass.** Decisions that follow:

- **Only `mxc_ffi.dll` is extracted.** The other three are for the guarded WPR capture and the WSLC
  backend, which Pia does not use. `mxc_ffi.dll` looks for `plm.exe`, `wxc-wslc-daemon.exe` and
  `wslcsdk.dll` **beside itself**, not beside the host exe. The mirrored asset stays the whole nupkg
  (same bytes upstream and on the mirror, one pin).
- **No custom resolver is needed.** The SDK registers its own resolver for `mxc_ffi` and probes, in
  order: the `MXC_FFI_DIR` environment variable, `AppContext.BaseDirectory`,
  `<base>\runtimes\<rid>\native`. A custom `SetDllImportResolver` also works, but only when it is
  registered before the SDK's own, which registers on the first SDK call and then fails quietly. Setting
  `MXC_FFI_DIR` after verification has no ordering constraint: a call that failed earlier is retried.
- **`NotInstalled` is decided by file existence**, not by calling the SDK, because every SDK call throws
  without the native.
- **Open: the CI runner.** Whether `windows-latest` can create an AppContainer is only known from a CI run.
  The integration tests skip at runtime when a spawn is not possible, so the gate works either way.

## Environment: why the test gate runs with TEMP redirected

On the development machine, Windows Defender's real-time scan opens a file right after it is written, so
`File.Replace` straight after a write fails with "Die Datei, die ersetzt werden soll, kann nicht entfernt
werden" (`ERROR_UNABLE_TO_REMOVE_REPLACED`). Measured on 2026-10-17 with a plain `pwsh` loop and no Pia
code (write a file, then `[IO.File]::Replace(tmp, target, [NullString]::Value)`): **235 of 400 failed** under
`%TEMP%`. The atomic-write tests (`FilesToolHandlerWrite/Edit/DocxXlsx*Tests`, `WriteFileHelpersTests`,
`HistoryDatabaseEncryptionTests`, `VaultWikiTests`, `ConfirmedApproverStoreTests`) then fail 1 to 5 at a time
in a full run, and they fail the same way on a build with none of this branch's changes. A bare `$null` for
the backup argument fails every time in PowerShell, so the probe must pass `[NullString]::Value`.

The gate runs with `TEMP` and `TMP` pointed at `<worktree>\artifacts\tmp` for the test process only. No
Defender setting is changed. The runner's own hangs (the log freezes right after the `MeetingAttendee` skip
line) are a separate, older stall; kill the process and rerun.
