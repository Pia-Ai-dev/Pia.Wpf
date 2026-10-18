# Sandboxed npx, uvx and dnx: spike results

**Status:** Measured. Answers the builder-checks of the design; the owner decided the four open results (end of
file). [2026-10-18-sandbox-runtimes-plan.md](2026-10-18-sandbox-runtimes-plan.md) builds on these results.
**Owner:** Marco Altmann
**Written:** 2026-10-18
**Origin:** [2026-10-18-sandbox-runtimes-design.md](2026-10-18-sandbox-runtimes-design.md), the "left to the
builder" list and the *(Builder-check call.)* markers. Prior MXC findings: `G:\Git\Mxc\README.md`.

All runs were on this host (Windows Server 2025, German locale, MXC `Microsoft.Mxc.Sdk` 1.0.1, Tier 3
`AppContainerDacl`) at **medium IL**, launched de-elevated through `explorer.exe`; every result records
`S-1-16-8192`. The few elevated runs are named as such. Raw specs and result JSON live under
`%LOCALAPPDATA%\PiaRuntimeSpike\<topic>\` (`specs\`, `results\`), the merged results in
`spike-round1-result.json` and `spike-round2-containment.json` there. The spike runner is
`PiaRuntimeSpike\harness\bin\MxcSpike.exe` with `run-spike.ps1` (same `ContainerRequest` shape as
`MxcMcpLauncher.BuildRequest`, plus an MCP handshake mode and ACL watches).

## 1. The container is a regular AppContainer, not an LPAC

A folder carrying only `S-1-15-2-1:(OI)(CI)(RX)` was readable and executable with no policy grant; so was
one carrying only `S-1-15-2-2`; one with neither was denied. The token is Low IL, `isAppContainer=1`, no
`WIN://NOALLAPPPKG`, one capability (`AgenticAppContainer`), plus `internetClient` under network Allow.

**Decision:** "readable already" = an allow ACE for `S-1-15-2-1` **or** `S-1-15-2-2` with RX on the
toolchain root and its key files, no matching deny. The step-2 button grants `S-1-15-2-1`, as designed.
`C:\Program Files\dotnet` inherits both from `C:\Program Files` across all 47,677 entries.

## 2. What the container sees and what it may touch

- **Environment.** The container's block is rebuilt from the registry (machine + `HKCU\Environment`), not
  copied from Pia's process. `PATH` is the registry `PATH`. Overrides in `spec.Environment`:
  `APPDATA`, `USERPROFILE`, `HOMEDRIVE`, `HOMEPATH` stick as given. `LOCALAPPDATA` gets
  `\Packages\<containerName>\AC` **appended**. `TEMP`/`TMP` overrides are **ignored**: they become
  `<LOCALAPPDATA>\Packages\<containerName>\AC\Temp`. So the state folder holds `TEMP` only when
  `LOCALAPPDATA` points into it **and** `<state>\AppData\Local\Packages\<containerName>\AC\Temp` exists
  before the spawn. It depends on the container name, so probe and real start need one each.
- **Writable without a grant:** only the container's own `%LOCALAPPDATA%\Packages\<name>\AC` (and `AC\Temp`).
  Not `%USERPROFILE%`, `%APPDATA%`, `C:\Users\Public`, `C:\ProgramData`.
- **Grant shapes.** A single **file** in `ReadonlyPaths` works (sibling files and the folder listing stay
  denied). A deep folder works without granting its parents (bypass-traverse). A **missing** readwrite or
  readonly path fails the spawn with `MxcException BackendUnavailable` (`os error 2`); MXC creates nothing.
- **Admin-owned policy root.** Listing `C:\Program Files\nodejs` in `ReadonlyPaths` fails the spawn in
  ~40 ms with `BackendUnavailable` ("requires write-DAC permission"). No process, empty `Warnings`, no
  ACL change. MXC checks the root; it skips only *children* it cannot rewrite.
- **Secrets in the user's registry environment** reach every container: on this host `HKCU\Environment`
  holds two PAT variables, and both appear inside a sandboxed server.

## 3. Three things the container is denied, and no SDK option changes

1. **`lstat` on every ancestor of a grant** (`C:\`, `C:\Users`, `C:\Users\<user>`, `AppData`, `AppData\Local`)
   fails with `EPERM`. `C:\` grants only `isolatedWin32-volumeRootMinimal`, `C:\Users`
   `isolatedWin32-profilesRootMinimal`, `C:\Users\<user>` `isolatedWin32-userProfileMinimal`; `AppData`
   and `AppData\Local` grant no capability at all.
2. **`\Device\Null` and `\Device\MountPointManager`** open with `ERROR_ACCESS_DENIED`. Their DACLs hold no
   AppContainer or capability ACE. `GetFinalPathNameByHandle` with DOS/GUID names therefore fails for
   every path, and so does Rust's `fs::canonicalize`.
3. **Global pipe names** (`\\.\pipe\x`, libuv's `\\?\pipe\uv\…`) are denied; only `\\.\pipe\LOCAL\…`
   works. A Node child process with piped stdio spins at 100 % CPU forever; `stdio: 'ignore'` fails with
   `EPERM` (it opens NUL). `stdio: 'inherit'` works.

Round 2 tried every `ContainerRequest.Containment` option the SDK exposes. This host offers only the
`ProcessContainer` backend at `AppContainerDacl` with **no** backend capabilities.
- `Containment.ProcessContainer` with nothing set behaves exactly like today.
- `EnumeratePaths` throws `BackendUnavailable` ("not supported by this version of Windows").
- Any `isolatedWin32-*` capability makes `CreateProcessW` fail with `0x80070057`, elevated too.
- Other capabilities (`lpacCom`, `registryRead`) land in the token and change nothing.
- `LearningMode` logs and still blocks. `CaptureDenials` needs an elevated `plm.exe` from an admin-only
  tree. `LeastPrivilege` is not in the V1 API.
- No option changed the ACL of `C:\`, `C:\Users` or the profile, before, during or after a run.

`MxcContainer.Probe` is the only API that reports 1 and 2 (`MxcProcess.Warnings` stays empty); it names an
elevated `wxc-host-prep prepare-system-drive` / `prepare-null-device` that is not in the NuGet package,
and says the NUL fix resets at every boot.

**`cmd.exe` inside the container** cannot start a program from an *unquoted, drive-qualified* path
("Zugriff verweigert", `call` included). Quoted paths, `start /b /wait`, rooted paths without a drive and
PATH search all work. npm's `.cmd` shims quote their targets.

## 4. Per-start grant cost

MXC rewrites the ACL of every entry below each policy root at spawn and again at dispose. Linear, about
**51–77 ms per 1,000 entries each way** on a quiet host, two to five times that under load.

| Root | Entries | Spawn | Dispose |
|---|---|---|---|
| none (baseline) | — | 52–90 ms | 0–5 ms |
| typical Node install (`Program Files\nodejs` shape) | ~3,000 | ~0.2–0.3 s | ~0.2–0.3 s |
| this host's `pi-node\current` (carries a global package) | 16,350 | 1.0–2.2 s | 1.0–2.1 s |
| `node.exe` file + `node_modules\npm` only | 2,465 | 0.37–0.53 s | 0.35–0.50 s |
| user-scope .NET 10 SDK root (`dotnet-install.ps1`) | 6,390 | +0.41 s | +0.42 s |
| npx state after one package (`_cacache`, `_npx`) | 5,800–6,200 | 0.5–1.2 s | ~0.4 s |
| npx state for `@tiberriver256/mcp-server-azure-devops` | 15,980 | ~1.2 s | ~1.2 s |
| uv state after `uv tool install` (own CPython + venv) | 10,900–17,100 | 1.3–1.5 s | 1.3–2.1 s |
| a copy of `Python312` read-only | 26,099 | 2.0–4.8 s | 2.4–4.5 s |

The **state folder** is a per-container read-write grant: it pays this on every start whatever is decided
for toolchains. Both Test connection and the real start pay it. `SandboxGrantVerifier`'s recursive
WRITE_DAC probe over the 16k Node tree took 0.25–0.54 s warm and 2.0 s cold; its 5,000-entry cap checks
about 30 % of that tree. `SandboxPathGuard.MaxEntriesPerRoot` (20,000) refuses `Python312` (26,099).

## 5. Node (`npx`)

Works at medium IL for `@modelcontextprotocol/server-everything`, `server-memory`, and
`@tiberriver256/mcp-server-azure-devops` (installs, then stops on "Organization URL is required": a config
error, not a sandbox one). npm 10.9 and 11.2.

- **Launch:** `<node dir>\node.exe <node dir>\node_modules\npm\bin\npx-cli.js <user's npx args>`. Never
  `cmd.exe`, never `npx.cmd`. Use the npm bundled next to the chosen `node.exe`, not the global-prefix
  npm under `%APPDATA%\npm` that a terminal's `npx.cmd` would prefer; that folder is never granted.
- **Required env:** `NODE_OPTIONS=--preserve-symlinks --preserve-symlinks-main --require "<preload>"`
  (forward slashes: a backslash inside the quotes is an escape), the preload's list of granted roots,
  `npm_config_cache=<state>\npm-cache`, `LOCALAPPDATA=<state>\AppData\Local` with the `AC\Temp` folder
  pre-created, `PATH=<node dir>;<registry PATH>`. Without the `PATH` prepend the package's `.bin` shim
  finds `Program Files\nodejs` first, which the container cannot list ("Der Befehl "node" … nicht
  gefunden"). Optional: `npm_config_update_notifier=false`, `npm_config_audit=false`.
  Not needed: `npm_config_prefix`, `npm_config_userconfig` (the read of `~\.npmrc` fails silently).
- **The preload** (~90 lines, `PiaRuntimeSpike\node\fixtures\pia-node-sandbox.cjs`) answers `lstat`/`stat`
  of an *ancestor of a granted root* as a directory and lets `realpath` fall back to `path.resolve` on
  `EPERM`. Without it Node dies resolving its main script (`EPERM lstat 'C:\'`), npm's arborist dies
  walking the cache's ancestors, and servers that compare `realpathSync(argv[1])` with their own path
  (`server-everything`, `server-memory`) **exit 0 silently**. Both flags are needed as well.
- **Timings (warm, quiet):** init 0.6–0.7 s, total 2.4 s with the narrow grant, 4.1–4.3 s with the 16k
  toolchain folder. Cold start (empty state, Allow): init 6.7 s, total 10.8 s.
- **Network Deny:** a warm `npx -y` waits 70 s for registry retries; `npm_config_prefer_offline=true` makes
  it 0.6 s. An empty cache with `npm_config_fetch_retries=0` fails in 3.6 s with `ENOTFOUND`. A pinned
  version needs its own cached install.
- **Narrow grant** (`node.exe` file + `node_modules\npm`) needs a Pia-written `<state>\bin\node.cmd`
  (`@"<node dir>\node.exe" %*`) first on `PATH`, because cmd must list the `PATH` folder to find `node`.
- `server-memory` writes `memory.jsonl` inside the npx cache by default, so dropping the state folder
  drops that data unless `MEMORY_FILE_PATH` points at the server folder.
- `Program Files\nodejs\node.exe` **as the MXC-launched command** runs a script in a granted folder; it fails
  only when started from inside the container or when npm's JS lies under Program Files.

## 6. uv (`uvx`)

**`uvx` cannot run inside the container on Tier 3.** It canonicalizes paths (mount manager denied) to
unpack a managed Python and to start its trampoline exes, and opens NUL as stdin to query any interpreter
(fully granted system Python and warm cache included). Grants, env vars and network do not change that.

What works (`mcp-server-time`, `mcp-server-fetch`, both under Deny for `time`):
1. **Host-side prepare** at medium IL, uncontained: `uv tool install <pkg>` with `UV_NO_BUILD=1` (wheels
   only, so no package code runs on the host; sdist-only packages are refused), `UV_NO_CONFIG=1`,
   `UV_PYTHON_PREFERENCE=only-managed`, `UV_PYTHON_INSTALL_REGISTRY=0`, `UV_PYTHON_INSTALL_BIN=0`, and
   `UV_CACHE_DIR`, `UV_TOOL_DIR`, `UV_TOOL_BIN_DIR`, `UV_PYTHON_INSTALL_DIR` in the state folder.
   6.2 s cold for `time` (incl. a 23 MiB CPython download), 2.2 s for `fetch` (Python reused), 50 ms recheck.
2. **Sandboxed run:** `<state>\tools\<pkg>\Scripts\python.exe -c "import sys; from <mod> import <func>;
   sys.exit(<func>())"`, entry point from `uv-receipt.toml` + `<dist>.dist-info\entry_points.txt`, state
   folder **read-only**, no env. Init 0.6–0.8 s warm, 2.0–2.3 s on the first start (writes `.pyc`). Never the
   trampoline exes. One benign stderr line: "Failed to find real location of …python.exe".
- Each server carries its own CPython (~62 MB, 2,269 files). The system `Python312` is unusable to uv in
  the container.
- uv derives its folders from `LOCALAPPDATA` (cache), `APPDATA` (python, tools) and `USERPROFILE` (bins),
  so every `UV_*` dir must be explicit. No write reached `%LOCALAPPDATA%\uv`, `%APPDATA%\uv`,
  `~\.local\bin` or `HKCU\Software\Python`.
- Granting only the files `uv.exe` and `uvx.exe` works. A WinGet `Links` symlink must be resolved
  (`File.ResolveLinkTarget(path, true)`): uvx looks for `uv.exe` beside the path it was started by.

## 7. .NET (`dnx`)

Works: `"<root>\dotnet.exe" exec "<root>\sdk\<last line of --list-sdks>\dotnet.dll" dnx <args>` (what
`dnx.cmd` does, without cmd). `dnx` needs SDK ≥ 10.0.100: 10.0.100, 10.0.402 and 11.0.100-rc.2 work, 8.0
and 9.0 do not. `dnx.cmd` ignores `global.json`. `Program Files\dotnet` needs no grant.

- **Required env:** `DOTNET_CLI_TELEMETRY_OPTOUT=1` (otherwise a registry `SecurityException` crash),
  `DOTNET_CLI_HOME=<state>\home`, `APPDATA=<state>\home\AppData\Roaming` (NuGet.Config),
  `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0` (keeps a non-JSON line off stdout, and on the host keeps the SDK's
  first run from appending to the user's `PATH` — the spike's own probe did exactly that once).
  Recommended: `NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH`, `NUGET_SCRATCH`, `NUGET_PLUGINS_CACHE_PATH` under
  `<state>\nuget`, `DOTNET_NOLOGO=1`, `DOTNET_GENERATE_ASPNET_CERTIFICATE=0`, `DOTNET_CLI_UI_LANGUAGE=en-US`,
  `LOCALAPPDATA` + `AC\Temp` as for Node. `DOTNET_SKIP_FIRST_TIME_EXPERIENCE` has no effect.
- **Timings:** cold init 2.0–3.1 s, warm 0.56–0.58 s; state after a cold start 100 files, 22 MB.
- **Network Deny, warm:** works on SDK 11. On SDK 10 the feed is contacted on every start and fails (6.4 s)
  unless the state's `NuGet.Config` holds `<clear/>` plus the local packages folder. On SDK 11 a floating
  `@1.*` never updates once cached; on SDK 10 it updates on each Allow start.
- **Bare `dotnet <dll>`:** grant only the dll's folder read-only. A missing grant surfaces as an SDK
  telemetry crash (`0xE0434352`), not as "file not readable".

## 8. Admin-owned install (step 2)

On a scratch copy given the Node MSI's ACL (owner SYSTEM, protected, Users/Authenticated Users RX):
- Unreadable: `node.exe -e` still exits 0 (MXC launches it); a script from the folder fails with exit 1
  and `EPERM open` or `MODULE_NOT_FOUND`, never an NTSTATUS. In `ReadonlyPaths` the spawn throws.
- The elevated .NET grant of `S-1-15-2-1` RX with `(OI)(CI)` took 1.4 s for 16,350 entries, reached all
  of them and kept the DACL protected. A medium-IL container then ran the full npx recipe with **no** MXC
  grant. `icacls <dir> /remove:g *S-1-15-2-1` restored the exact pre-grant SDDL.
- **Pre-check:** if the folder already carries an `S-1-15-2-1` ACE, the new grant merges into it and any
  removal also takes the old access away. The button must check first.
- **Classifier:** readability from the ACL (section 1). Grantability = `AccessCheck(WRITE_DAC)` with the
  process token, or with `TokenLinkedToken` when Pia is elevated: an elevated Pia otherwise judges
  Program Files grantable. The owner SID is useless (user toolchains here are owned by Administrators
  and still grantable at medium IL).
- **Elevation:** `IsInRole(Administrator)`, `TokenIsElevated` and `TokenElevationType` agree
  (True/1/2 elevated, False/0/3 at medium).

## 9. User-scope installs for the step-3 guide (each verified at medium IL, no admin)

- **Node:** `winget install --id OpenJS.NodeJS.LTS --exact --scope user` picks the portable zip (without
  `--scope user` winget picks the machine MSI and UAC). Without winget: the LTS zip extracted to
  `%LOCALAPPDATA%\Programs\nodejs` (`npx -y cowsay` ran). The zip route changes no `PATH`.
- **uv:** `powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"` installs
  to `%USERPROFILE%\.local\bin` and prepends it to `HKCU` `Path`. `winget install --id astral-sh.uv -e`
  is user-scope too.
- **.NET 10 SDK:** `dotnet-install.ps1 -Channel 10.0` (20 s, 770 MB, 6,390 entries) installs to
  `%LOCALAPPDATA%\Microsoft\dotnet` and changes no `PATH`. winget has no user-scope SDK.
- Detection must probe those folders directly, because Pia's `PATH` is stale after an install.
  Caveats for the guide text: proxies, a GPO execution policy beats `-ExecutionPolicy ByPass`,
  AppLocker/WDAC may block exes in user folders. A real standard (non-admin) account was not available;
  a UAC-filtered admin token is equivalent for file writes.

## Back to the owner: decided 2026-10-18

1. **Node fs preload** (section 3.1, 5): **ship it.** Pia owns the preload, grants it read-only and sets
   `NODE_OPTIONS` for every sandboxed Node start. Piped-stdio child processes stay a known limit.
2. **uv** (section 6): **host-side prepare.** An uncontained `uv tool install` with `UV_NO_BUILD=1` into
   `<slug>.runtime`, then a sandboxed `python.exe -c <entry point>` with that folder read-only. Scope is
   `uvx <pkg>`; `uv run` stays refused.
3. **Grant cost** (section 4): **per start, full toolchain folder**, as designed. Keep the uv state lean by
   pruning uv's cache after the prepare.
4. **User registry env vars** (section 2): **blank every `HKCU\Environment` variable** in sandboxed servers
   except an allowlist (`Path`, `TEMP`/`TMP`, `OneDrive*`). The server's own env entries still win.

## Left on the host

About 200 `pia-spike-*` AppContainer profiles (`HKCU\…\AppContainer\Mappings` plus
`%LOCALAPPDATA%\Packages\pia-spike-*`), an empty `C:\PiaRuntimeSpikeAdmin`, and about 1.5 GB under
`%LOCALAPPDATA%\PiaRuntimeSpike` (the harness there is kept for the live e2e). A stray `PATH` entry the
.NET probe added to `HKCU` was removed by hand.
