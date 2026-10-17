# Sandboxed local MCP servers (MXC)

**Status:** Planned, first shot. Defaults marked *owner to confirm* were chosen while the owner was away.
**Owner:** Marco Altmann
**Written:** 2026-10-17
**Origin:** Owner request: "in mxc we made a poc for sandboxed gated mcp calling. please create a first
shot plan to integrate into pia". The PoC is `G:\Git\Mxc` (commit `15fa885`), mainly its `README.md` and
`docs/CONTRACTS.md`. The local-MCP surface it builds on is
[../local_mcp_servers/2026-09-17-local-mcp-servers.md](../local_mcp_servers/2026-09-17-local-mcp-servers.md).
Tracking: [2026-10-17-mcp-sandbox-checklist.md](2026-10-17-mcp-sandbox-checklist.md).

## Goal

A user-added local MCP server can be marked **Run in sandbox**. Pia then starts its process inside an
MXC AppContainer with a filesystem and network policy the user sees and edits. A tool call that tries to
leave that boundary fails at the OS level, even after the user approved it on the action card. Pia
already has the "gated" half: every MCP call is a deferred `PluginToolCall` behind the approval card or
the grant gate. This work adds the "sandboxed" half underneath it.

## What the PoC proved, and what carries over

The PoC runs a separate ASP.NET gateway that contains each stdio server with `Microsoft.Mxc.Sdk` 1.0.1
(Tier 3, `AppContainerDacl`, on this Windows Server 2025 host) and re-exposes the tools over HTTP.
Under MXC, its 8 scenarios (delete a protected file, read a secret, delete or purge CRM data,
exfiltrate over HTTP) are denied, and the same binaries uncontained do the damage.

**Pia does not take the gateway.** Pia already is the gateway: it prefixes (`<slug>__<tool>`), applies
an allowlist, gates every call and has a probe and a settings UI. So the integration is **in-process**.
Pia references `Microsoft.Mxc.Sdk` and replaces `StdioClientTransport` with an MXC spawn wired through
`StreamClientTransport`, exactly as the PoC's `BackendConnection` does. Ported from the PoC:

| PoC file | Becomes |
|---|---|
| `Launching/MxcBackendLauncher.cs` | `Services/Plugins/Sandbox/MxcMcpLauncher.cs` (spawn recipe, graceful shutdown, warnings) |
| `Launching/CommandLine.cs` | `Services/Plugins/Sandbox/CommandLine.cs` (verbatim, MSVCRT quoting) |
| `Security/AceSweeper.cs` | `Services/Plugins/Sandbox/AceSweeper.cs` (shallow-first, see below) |
| `Backends/BackendConnection.cs` stderr pump + tail | inside the MXC transport, so a failed start shows the child's stderr tail like it does today |
| `src/DemoMcp.FileServer` + CRM `export_customers` | `tests/Pia.TestMcpServer` (test fixture, never shipped) |

MXC facts on this tier that set the rules. Each comes from the PoC's limitations table:

- `DeniedPaths` is **not enforced**. A path that must stay protected is never granted and never nested
  inside a granted path. Read-only and denied dirs are siblings of read-write dirs.
- Network is **binary**, `Allow` or `Deny`. No per-host rules. No host loopback.
- .NET children need `Ui.Disable = false` and their own exe dir in `ReadonlyPaths`.
- Tier 3 adds temporary `S-1-15-2-*` ACEs to every policy path. A hard kill leaks them, so the host
  must sweep at startup.
- A `ContainerName` must be unique among running containers.

## Decisions

| Question | Decision | Why |
|---|---|---|
| Architecture | **In-process** MXC spawn in `Pia.Wpf`, no separate gateway | Pia's prefix, allowlist, gate, probe and UI already do the gateway's job. A second process would add a transport (HTTP) that local MCPs deliberately do not support. |
| Which servers | **User-added local servers only** | Server-pushed org plugins are listed under *Not yet planned*. |
| Default for a new server | Sandbox **on** when available and the command is sandboxable. *Owner to confirm* | Opt-out makes the safe path the easy one. Existing saved servers are not migrated; they stay uncontained until edited. |
| Fail mode | **Fail closed** | A server saved as sandboxed never starts uncontained. If MXC is unavailable or the spawn throws, the row reads `Not running` with the reason, the same way the policy block does today. |
| One launch path | Probe and activation both go through `McpTransportFactory` | The local-MCP doc records the bug where Test connection and Save took different launch paths. |
| Default policy | rw = a per-server dir `PiaPaths.McpSandboxRoot/<slug>` (a **property**), no ro, egress **Deny**, cwd = first rw path | Least privilege. The user widens it in the editor. |
| Network | `Deny` default, a single `Allow` switch with a warning that it allows every host | That is all MXC offers on Tier 3. |
| Gate | **Unchanged.** Sandboxed tools are still approved per call | Relaxing the gate for sandboxed tools is a separate decision (*Not yet planned*). The card gains a one-line sandbox summary. |
| Shims (`npx`, `uvx`, `.cmd`, `.bat`) | **Out of the first shot.** Toggle disabled with a one-line reason | See gate G3. Their real process downloads into `%LOCALAPPDATA%` caches, which are redirected inside the AppContainer, and needs egress. |
| CI | MXC integration tests use a **runtime skip** when MXC cannot spawn, not `Explicit` | The local gate on this host must prove containment. The GitHub runner may not be able to create AppContainers (G2). |
| How MXC ships | **Not in the installer.** The native runtime is a **runtime asset on Pia's own storage**, downloaded the first time a sandboxed server is needed. Owner request | Keeps ~15 MB of native code, most users never need, out of every install and update. It reuses the `RuntimeAssetCatalog` / `AssetDownloader` / `Assets:MirrorBaseUrl` path the models already use, including the upstream fallback and the air-gapped pre-fetch. See *Shipping the MXC runtime*. |

## Shape

### ConfigJson

`LocalMcpDefinition` gains `McpSandboxPolicy? Sandbox` (null = uncontained, the current behaviour).
The definition is the contract both workers code against. It lands first (checklist C0).

```json
{
  "source": "local",
  "transport": "stdio",
  "command": "C:\\Tools\\fs-mcp\\FsMcp.exe",
  "args": [],
  "toolPrefix": "fs",
  "sandbox": {
    "readwrite": ["C:\\Users\\me\\AppData\\Local\\Pia\\McpSandbox\\fs"],
    "readonly":  ["C:\\Users\\me\\Documents\\Reference"],
    "network": "deny"
  }
}
```

```csharp
public sealed record McpSandboxPolicy(
    IReadOnlyList<string> ReadwritePaths,
    IReadOnlyList<string> ReadonlyPaths,
    bool AllowNetwork);
```

A missing `sandbox` key reads as null, so every existing row round-trips unchanged. A key that is present
but unreadable (not an object, `null`, `{}`) reads as `McpSandboxPolicy.Empty`: contained, nothing
granted, network off. Dropping it would start the server uncontained. Network is `allow` only for the
exact string `allow`; anything else is deny.

### New and changed types

| File | Change |
|---|---|
| `Services/Plugins/LocalMcpDefinition.cs` | `Sandbox` property, `sandbox` ConfigJson read/write |
| `Services/Plugins/Sandbox/McpSandboxPolicy.cs` | new: the record above |
| `Services/Plugins/Sandbox/SandboxPathGuard.cs` | new: validates a policy, returns localisable errors (rules below) |
| `Services/Plugins/Sandbox/MxcAvailability.cs` | new: one cached answer, "can this host spawn an MXC container" plus a reason. Behind an interface for tests |
| `Services/Plugins/Sandbox/MxcMcpLauncher.cs` | new: `ContainerRequest` builder plus spawn. Returns stdin/stdout/stderr and an async shutdown |
| `Services/Plugins/Sandbox/MxcClientTransport.cs` | new: `IClientTransport` that spawns on connect, wraps `StreamClientTransport`, pumps stderr into a tail, shuts the container down on dispose |
| `Services/Plugins/Sandbox/AceSweeper.cs` | new: port, shallow-first |
| `Services/Plugins/McpTransportFactory.cs` | new: `Create(definition-ish inputs) → IClientTransport`, either stdio or MXC. Used by **both** handler and probe |
| `Services/Plugins/McpPluginToolHandler.cs` | builds its transport through the factory, exposes `IsSandboxed` / sandbox summary |
| `Services/Plugins/McpServerProbe.cs` | same factory |
| `Services/Plugins/PluginService.cs` | fail-closed start reasons, startup ACE sweep before the first activation |
| `Paths/PiaPaths.cs` | `McpSandboxRoot` property (under local data) and `MxcRuntimeDirectory` (real local root, versioned) |
| `Services/Assets/RuntimeAsset.cs` | `RuntimeAssetCatalog.MxcRuntime` entry, plus its pinned SHA-256 |
| `Services/Plugins/Sandbox/MxcRuntimeProvisioner.cs` | new: download through `IAssetDownloader`, pin check, extract `runtimes/win-x64/native/*` via `.tmp`, signature check, DllImport resolver registration, progress for the editor |
| `scripts/RuntimeAssetCatalogue.ps1` | `Mxc` group (publish and pre-fetch) |
| `ViewModels/McpServersSettingsViewModel.cs` + view | Sandbox section: toggle, rw list, ro list, network switch, guard errors, "why disabled" line |
| `Services/ActionCardBuilder.cs` | one-line "Runs sandboxed: N read-write, M read-only folder(s), network off" |
| `tests/Pia.TestMcpServer/` | new project: test fixture server |

### Sandbox path guard

`SandboxPathGuard.Validate(policy, command)` returns issues, each `(Path, Kind, Message)`. The message is
plain user-language text that never repeats the path (the issue carries it, so a UI can mark the matching
entry). The factory runs the same guard before every start and every probe, so a policy edited by hand
cannot get past it. Refused:

- A path that is not fully qualified, or that does not exist. A missing **read-write** folder strictly
  under `McpSandboxRoot` is fine: Pia creates it at start. A missing read-only one is refused.
- `%WINDIR%`, `Program Files`, `Program Files (x86)`, or anything below them.
- The user-profile root, anything above it (a drive root, `C:\Users`), or a `PiaPaths` data root (Pia's
  own data dirs, real and routed), anything below one, or anything above one (`AppData\Local`). The
  exception is a folder strictly below `McpSandboxRoot`. `McpSandboxRoot` itself is refused: it holds
  every sandboxed server's data.
- A readonly path equal to or nested inside a readwrite path. Nested, it would be read-write, because
  `DeniedPaths`-style carving does not work.
- A readwrite path nested inside, or equal to, another readwrite path (collapse instead).
- A UNC path. Not tested under MXC, so refused for now.
- **A documents-style root as a policy path:** `Desktop`, `Documents`, `Downloads` and the `OneDrive`,
  `OneDriveConsumer` and `OneDriveCommercial` roots when set. Tier 3 changes the ACL of every entry below
  a policy path on every start, and a hard kill leaves them all to be swept, so these roots mean slow
  starts, ACL churn on thousands of user files and sync noise. Subfolders stay allowed: "Share a
  subfolder instead: Pia would have to change the permissions of everything in <folder>."
- **A folder with more than 20,000 entries** (counted up to one past the cap, cached for 20 s because an
  editor asks on every change): "This folder holds too many files to share with a sandboxed server. Pick a
  smaller subfolder." Folders under `McpSandboxRoot` are exempt. The program's own folder is not counted.
- **The program's own folder, when it is granted and too wide.** The command dir is granted read-only
  (see below), so a program in `Downloads`, `Desktop` or `Documents` themselves, the profile root, a
  drive root or a Pia data root would share all of that folder: "Put the server in its own folder: Pia
  would have to share all of <folder> with it." The issue's `Path` is that folder, so a UI can show
  "Also readable: the server's own folder".
- **A program's folder with more than 20,000 entries**, for the same reason as a policy folder: "The
  server's folder holds too many files to share with a sandboxed server. Put the server in a smaller
  folder." It is not counted when a policy folder already covers it.
- A command that resolves to a reparse point (the Microsoft Store `python.exe` alias): its folder is not
  the program's own. Refused by the launch-spec builder with a plain reason.

A policy with **no** folders at all is valid: it is the most restrictive case, for a server that needs no
files (a time server).

### Command dir rule

The PoC always adds the command's own dir to `ReadonlyPaths`, because .NET needs it. Tier 3 rewrites
the DACL of every policy path. A non-admin cannot do that on `Program Files`, and the PoC says never
to put it in a policy. So the dir is added **only when it is not** under `%WINDIR%` or
`Program Files*`. Those are readable inside the AppContainer anyway, through the inherited
`ALL APPLICATION PACKAGES:(RX)` ACE. Gate G1 confirms this. The dir is also skipped when a read-write or
read-only path already covers it. The command must resolve to an `.exe` (a bare name is looked up on
`PATH`); `.cmd`, `.bat`, `.ps1` and shims such as `npx` are refused at start with a reason.

### Grant preflight

Tier 3 grants access by rewriting ACLs, and it skips without any warning the files it may not rewrite.
The spawn then succeeds and the server dies on load with "access denied" (gate G1: files an elevated
build left owned by `Administrators`). So before every start, and for the probe too, Pia opens each
target with `WRITE_DAC` (`CreateFileW`, `FILE_FLAG_BACKUP_SEMANTICS` for folders) and counts the ones it
cannot open. The access right is the test, not ownership: owner equals user is both too strict and too
loose.

- The command dir is walked **recursively**, only when it is granted at all (outside `%WINDIR%` and
  `Program Files*`), capped at 5000 entries. Past the cap Pia trusts the rest and the stderr tail speaks.
- A policy path is checked **shallow**: the folder and its direct files. A file deeper down that the
  sandbox misses only means less access, which is the safe direction.
- Any blocked item means the server does not start. The reason names counts, never paths: "Pia can't
  sandbox this server: N file(s) in its folder belong to an administrator or another account. Install
  or copy the server into a folder you own." The count goes to the Information log.
- It runs inside the transport, behind `ISandboxGrantVerifier`, so one unit test covers the refusal
  without an administrator-owned file.

### Container name

A stable `pia-mcp-<prefix>`, guarded in-process against concurrent use, so a probe of a running server
gets `pia-mcp-<prefix>-probe`. Gate G1 showed that every distinct name leaves one
`%LOCALAPPDATA%\Packages` profile and one `AppContainer\Mappings` key, and that dispose removes
neither. A per-pid name would therefore leak one per start. A stable name also keeps the same SID
after a hard kill, so leaked ACEs do not pile up. Removing the server deletes its profile.

### ACE sweep

`AceSweepService` runs at startup, before the first activation, over every sandboxed server's policy
roots, its working directory and its granted command folder (disabled servers too, because their leak is
still there), and when a server is removed, before its profile is deleted. It refuses `%WINDIR%` and
`Program Files*`.

- **Only Pia's own SIDs.** The PoC removes every explicit `S-1-15-2-*` ACE. A policy path here is a user
  folder, where other packaged apps may hold container ACEs of their own, so only the SIDs of containers
  named `pia-mcp-*` go. `PiaContainerSidResolver` reads them from the profile mappings
  (`HKCU\...\AppContainer\Mappings\<SID>`, value `Moniker`), adds the SIDs derived from the names in use
  (`DeriveAppContainerSidFromAppContainerName`), and never touches the well-known `S-1-15-2-1` and
  `S-1-15-2-2`. A leaked ACE of a removed server can only be recognised while its profile exists, which is
  why removal sweeps first.
- **Shallow first.** A root that carries none of our ACEs is trusted: one ACL read, no walk, so granting a
  big folder does not make starts crawl. A root that does is walked.
- **Bottom up, root last.** Files, then their folder, deepest folders first, and the root only when
  nothing below it failed. A sweep cut off half way (a fault, a kill) therefore leaves the root ACE in place,
  which is what makes the next sweep look below it again. Links are not followed.
- The log carries counts (`N folder(s) checked, M ACE(s) removed, K entry(ies) failed`); paths only through
  `SensitiveDebug`. A failing sweep never keeps a server from starting.
- **A normal close does not leak, but only because Pia shuts its servers down on exit** (a clean dispose
  removes the container's ACEs itself): `IPluginService.ShutdownAllAsync` has to be called from the app's
  exit and session-ending paths, which belongs to the app shell, not to this sweep. The sweep is what
  remains for a crash or a kill. `ShutdownAllAsync` closes the servers in parallel and off the caller's
  context, so a caller that blocks the UI thread on it cannot deadlock and N servers cost the slowest one.

## Shipping the MXC runtime via Pia's storage

`Microsoft.Mxc.Sdk` 1.0.1 has two parts: a 430 KB managed `Microsoft.Mxc.Sdk.dll` and, for win-x64,
four native files (`mxc_ffi.dll`, `plm.exe`, `wslcsdk.dll`, `wxc-wslc-daemon.exe`, about 15 MB). All
of them are Microsoft-signed (checked 2026-10-17).

- **The managed assembly stays a normal `PackageReference`.** The code compiles against it, it is
  small and signed, and loading it costs nothing until a sandboxed server starts. The **native**
  assets are excluded from the build and the publish (`ExcludeAssets="native"`, confirmed in S2), so
  the installer carries no MXC native code.
- **The native payload is a `RuntimeAsset`.** Mirror key `runtimes/microsoft.mxc.sdk.1.0.1.nupkg`,
  upstream `https://api.nuget.org/v3-flatcontainer/microsoft.mxc.sdk/1.0.1/microsoft.mxc.sdk.1.0.1.nupkg`
  (14.6 MB). The archive is mirrored verbatim, as the sherpa bundles are, so the extract step is
  identical on the mirror and upstream paths. The client downloads it through `AssetDownloader`
  (mirror first, then upstream) and extracts **only** `runtimes/win-x64/native/*` into
  `PiaPaths.MxcRuntimeDirectory` (`<real local root>\Runtimes\Mxc\1.0.1\`). That is a **property**,
  and like `ModelsDirectory` it sits on the real profile, because downloaded artifacts deliberately
  ignore `PIA_LOCAL_DATA_DIR`.
- **This is executable code, so it is verified, not just size-checked.** The nupkg's SHA-256 is
  **pinned in code** next to the catalog entry (`RuntimeAssetCatalog.MxcRuntimeSha256`, and a test pins
  it against the package NuGet restored). A mismatch deletes the download and fails closed. Only
  `runtimes/win-x64/native/mxc_ffi.dll` is extracted (G2: the other three files are not needed), into a
  `.tmp` dir that is renamed into place only after the extracted file passes `WinTrust.HasValidSignature`
  with the signer `Microsoft Corporation`. An interrupted extract leaves only `.tmp`, which the next run
  discards. A package pre-fetched to `PiaPaths.MxcRuntimeArchivePath` (the air-gapped path) is checked
  against the same pin and consumed instead of downloading.
- **Loading.** The SDK probes the `MXC_FFI_DIR` environment variable first, so Pia owns it (G2 found no
  need for a custom `SetDllImportResolver`, which cannot be registered once the SDK has touched its
  native). Constructing the provisioner removes any inherited value, before any SDK call. `IMxcRuntime.TryLoad`
  sets it to `MxcRuntimeDirectory` only after `mxc_ffi.dll` passes the signature and publisher check **in
  this process**, once per process, and otherwise removes it. The nupkg hash is not re-checked on each
  start; it was pinned when the file was extracted. `MxcMcpLauncher`, the only SDK caller, refuses to
  run until `TryLoad` succeeds, and the availability service must not call the SDK (not even
  `GetPlatformSupport`) before that either: with no native present every SDK call throws. `MXC_FFI_DIR`
  is stripped from the environment of every MCP child, sandboxed or not, and a contained child does not
  inherit it (tested with a real container).
- **When the download happens.** The first time the user turns **Run in sandbox** on, the editor says
  "Pia needs to download the sandbox runtime (15 MB) from Pia's storage" and shows progress. A saved,
  **enabled** sandboxed server whose runtime is missing triggers the same download when it is activated,
  at startup or on save: that is the user's consent. A bare start, a disabled server, a probe and the
  editor never start one by themselves. The row reads `Not running: downloading sandbox runtime` and
  starts by itself when the runtime turns `Ready`. Fail closed applies throughout: no runtime means no
  start, and never an uncontained one. A local row whose saved settings cannot be read is not started
  either (its sandbox policy is unknown), and a damaged `sandbox` block reads as an empty, contained
  policy. `IMxcAvailability` (`MxcAvailabilityService`) reports `NotInstalled`, `Downloading`,
  `Unsupported` (the OS cannot spawn) or `Ready`, and asks the SDK (even `GetPlatformSupport`) only once
  the runtime is installed and verified. Removing a server deletes the AppContainer profiles of its
  container and its probe container.
- **Publishing.** `scripts/RuntimeAssetCatalogue.ps1` gains the `Mxc` group, so
  `Publish-RuntimeAssets.ps1 -Include Mxc` mirrors it and `Save-RuntimeAssets.ps1 -Include Mxc`
  pre-fetches it for air-gapped machines. `RuntimeAssetCatalogTests` pins the key on both sides. The
  upload to `storage.pia-ai.de` needs `PIA_STORAGE_UPLOAD_SECRET` and is a public write, so it is the
  **owner's** step and is run by hand. Until it runs, clients fall back to nuget.org with the same
  hash pin.
- **Self-hosted deployments.** They point `Assets:MirrorBaseUrl` at their own storage, as they do for
  models. No new setting.
- **Updating MXC.** A version bump changes the key, the pin and the directory name together, so
  versions never mix. The old directory is removed after the new one is verified.

## Logging (privacy)

- Paths in a policy are user-named: `SensitiveDebug` only. Release logs carry counts (`rw=2 ro=1
  net=deny`) and the container name. The prefix is the user's slug, which is fine at the same level as
  today's tool-name logging.
- MXC warnings and the stderr tail go to the log with the same handling the stdio path uses today.

## Gates

| Gate | Question | Cancels / changes |
|---|---|---|
| **G1** | Does an MXC spawn work from a **non-elevated** process (medium IL) with policy paths under the user profile? Are the four denials enforced (write outside rw, delete in ro, read an ungranted sibling, egress)? Does a command under `Program Files` work without adding its dir? Do per-pid container names leak `%LOCALAPPDATA%\Packages` profiles? | If the spawn fails non-elevated, the whole plan stops and goes back to the owner (an elevated broker is a different project). The other answers set the command-dir rule and the naming. |
| **G2** | Can MXC run with its native files **outside** the app dir? Native assets excluded from publish, `mxc_ffi` resolved from `MxcRuntimeDirectory` through a DllImport resolver, and a Tier 3 spawn that works (does it need `plm.exe` / the WSL files beside `mxc_ffi.dll`, or next to the exe?). Can the `windows-latest` runner spawn a container? | If the native runtime cannot live outside the app dir, the storage-shipping decision goes back to the owner (bundling instead). CI result: whether the integration tests run there or skip. `plm.exe`, `wslcsdk.dll` and `wxc-wslc-daemon.exe` are WSL-side. Drop them from the extract only if the Tier 3 spawn works without them. |
| **G3** | Can a shim-launched server (`npx -y …`) run sandboxed with a reasonable policy? | First shot: no, the toggle is disabled for shims. A positive spike becomes a follow-up, not scope creep. |

## Verification

### Unit tests (in the gate)

ConfigJson round-trip with and without `sandbox`. Every guard rule. The command-dir rule.
`ContainerRequest` built from a policy (rw/ro/egress/ui/env/cwd). The fail-closed matrix: sandboxed
with MXC unavailable means `Not running` with a reason and no process start. Probe and activation call
the same factory. Provisioner: a wrong hash deletes the download and leaves no extracted dir. An
unsigned or wrongly signed extracted file is refused. An interrupted extract leaves only `.tmp`,
which the next run discards. An existing verified dir is not re-downloaded. The catalog key matches
the `.ps1` catalogue (`RuntimeAssetCatalogTests`).

### MXC integration tests (in the gate, runtime-skipped when MXC cannot spawn)

Spawn `Pia.TestMcpServer` through the real `McpTransportFactory` under a temp fixture tree
(`workspace` rw, `protected` ro, `secret` sibling not granted):

- list and read in the workspace: allowed. Write and delete in the workspace: allowed.
- delete `protected/important.txt`: `isError`, SHA-256 unchanged. Read `secret/passwords.txt`:
  `isError`.
- egress `export` to `http://example.com`: `isError` (socket forbidden).
- Uncontained control: the same calls against a fresh copy succeed. This proves the test detects
  damage.
- ACE hygiene: after dispose, no explicit `S-1-15-2-*` ACE on any fixture path. A simulated leak
  (kill without dispose) is removed by the sweeper.

`Pia.TestMcpServer` is a `ProjectReference` of the test project with
`ReferenceOutputAssembly=false`, and its output is copied under the test bin. The gate is the built
exe, so the fixture must be on disk wherever the tests are built.

### End-to-end through the real app (WinWright plus the podman e2e stack)

Read `docs/ui_automation/ui-automation-playbook.md` first.

- **Stack:** `https://localhost:18081` (`pia-e2e`). **Never** use 8081, which holds the owner's real
  data. Get a test account from the `PIASERVER` session. Do not reset the stack.
- **Launch de-elevated.** These sessions are elevated, and a Pia started from WinWright would be too,
  which would prove nothing for G1. Start the Debug exe at medium IL with `explorer.exe` as the
  parent. Auto mode refuses a scheduled task as persistence. Set `PIA_CLOUD_SERVER_URL`, `PIA_DATA_DIR` and
  `PIA_LOCAL_DATA_DIR` set to a throwaway profile. Check the IL from the log or with
  `whoami /groups` in the same launch path, then `ww_attach`. Elevated UIA can drive a medium-IL
  window.
- **Pacing:** the stack's `ai` limit is 30 requests per 60 s. Space the prompts.
- **Never** restore `roaming\settings.json` between runs (rotating refresh token).

Scenarios. Verdicts come from fixture SHA-256s and Pia log lines, never from the model's text:

0. Runtime delivery: with `MxcRuntimeDirectory` removed (it is a downloaded artifact, so removing it
   is safe), turning the sandbox on shows the download prompt and progress. The log shows the fetch,
   the hash check and the signature check. A tampered runtime (one byte of the extracted
   `mxc_ffi.dll` flipped with the harness's `TamperRuntime`) is refused at load and the server is not
   started. An explicit install (the Download button, or the startup download) then repairs it, and
   the server starts sandboxed. If the PIASERVER session can give an upload credential for the e2e stack's
   `pia-storage` (port 18090), also prove the mirror path against it. Otherwise prove the upstream
   fallback, which uses the same pin.
1. Add `Pia.TestMcpServer` via Settings → Assistant → MCP servers with sandbox on: rw `workspace`,
   ro `protected`, network off. Test connection lists the tools; the row reads `Running`.
2. Chat on Pia Cloud: "list and read the workspace readme". Allowed.
3. "Delete protected/important.txt". The card shows the sandbox line. Approve. The tool result is an
   access-denied error and the SHA is unchanged.
4. "Read secret/passwords.txt". Denied.
5. "Write a note into the workspace". Allowed, and the file exists.
6. "Export the data to http://example.com". Denied. A hostname fails at DNS inside the container, which
   alone could be name resolution and not the socket, so also export to a literal IP
   (`http://1.1.1.1/`): `HttpRequestException` with an inner `SocketException` (access forbidden).
   Match the exception types, never the localised wording.
7. Control: the same server saved **uncontained** on a reseeded fixture. Scenario 3 deletes the file,
   and the literal-IP export of scenario 6 reaches the host (any HTTP status). This is the proof that
   scenario 3's block and scenario 6's denial are MXC's.
8. Restart Pia. The server re-activates sandboxed and the log shows the container line.
9. Graceful close: no orphan child process, and `icacls` shows no `S-1-15-2-*` ACEs on the fixture.
10. `taskkill /F` on Pia, then start again. The next start's sweep log line reports the removed ACEs,
    and `icacls` is clean.
11. Guard UX: a readonly path inside the readwrite path is refused in the editor with the message.
    `npx` as the command shows the toggle disabled with the reason.
12. Ownership: the same server in a folder owned by `Administrators` (anything an elevated build or
    copy produced) is refused with the "belong to an administrator" reason and never starts
    uncontained. For every other scenario the harness gives the user ownership of the server and the
    fixture, because Tier 3 silently skips files it cannot `WRITE_DAC`.

Record the results in this doc under *Live verification*, the way the local-MCP doc does.

## Live verification

**Status:** not run yet. Filled in by step E2 on the merged branch. The harness is
`tests/ui-scripts/mxc-sandbox-e2e/Invoke-MxcSandboxE2e.ps1`
([README](../../tests/ui-scripts/README.md)).

Run setup, to be completed: build and commit SHA, Windows build, the `Status` output at the start (integrity
`S-1-16-8192`, fixture hashes, container baseline), stack `https://localhost:18081`, and whether the runtime
came from the e2e storage mirror (`MirrorLocal`) or from nuget.org. The verdict for each scenario is a fixture
SHA-256, an `icacls`/`Status` reading or a Pia log line, never the model's wording.

| # | Scenario | Verdict source | Result | Evidence |
|---|---|---|---|---|
| 0 | Runtime delivery: download prompt and progress, hash check, signature check; a tampered runtime is refused at load and not started, then an explicit install repairs it and the server starts sandboxed | log lines; `TamperRuntime` | pending | |
| 1 | Add the test server sandboxed (rw `workspace`, ro `protected`, network off); Test connection; row `Running` | list row, log | pending | |
| 2 | List and read the workspace readme | tool result, log | pending | |
| 3 | Delete `protected/important.txt` after approving the card (the card shows the sandbox line) | SHA-256 unchanged; access-denied result | pending | |
| 4 | Read `secret/passwords.txt` | access-denied result | pending | |
| 5 | Write a note into the workspace | file exists, `Status` | pending | |
| 6 | Export to `http://example.com`, and to a literal IP (`http://1.1.1.1/`) | `HttpRequestException` with inner `SocketException` (by type, not wording) | pending | |
| 7 | Control: same server saved uncontained on a reseeded fixture; scenario 3 deletes the file, the literal-IP export reaches the host | SHA-256 changed; any HTTP status | pending | |
| 8 | Restart Pia; the server re-activates sandboxed | container log line | pending | |
| 9 | Graceful exit through `TrayMenu_Exit` | no orphan process, no `S-1-15-2-*` ACE | pending | |
| 10 | `Stop -Kill`, start again | sweep log line, `icacls` clean | pending | |
| 11 | Guard UX: a read-only path inside the read-write path is refused; `npx` shows the toggle disabled with its reason | editor message, toggle state | pending | |
| 12 | Ownership: the same server in the Administrators-owned `server-admin` folder is refused and never started uncontained | `Not running` reason, no process | pending | |

First-use cost, from a de-elevated dry run of the provisioner and launcher on this host (nuget.org
fallback, a console outside the repo): downloading, pinning, extracting and signature-checking the
14.6 MB runtime took about 0.5 s, and loading it 7 ms. The first sandboxed start took about 1.7 s, because
it also created the AppContainer profile; later starts took 0.3-0.4 s and a dispose 25-50 ms. A tampered
`mxc_ffi.dll` was refused at load in 18 ms and repaired by the next install in about 0.5 s. To be
confirmed by E2 through the real app.

## Not yet planned

- Sandboxing **server-pushed** (org) MCP plugins, through a `sandbox` block in their ConfigJson.
- A `RequireMcpSandbox` org policy key in client policy, which would refuse uncontained local servers.
- Relaxing the approval gate (session/Always tiers) for sandboxed tools.
- Shim servers (`npx`/`uvx`) sandboxed, depending on G3.
- OneDrive or other cloud-synced folders and UNC paths as policy paths.
- Per-call or per-approval narrower containers for destructive tools (the PoC's "stronger model").
- Tier 1 (BaseContainer) per-host egress rules, once available on the target Windows builds.
