# Sandboxed MCP servers on npx, uvx and dnx: checklist

**Status:** In progress. A1–A3 done; every decision gate is answered.
**Owner:** Marco Altmann
**Written:** 2026-10-18
**Origin:** [2026-10-18-sandbox-runtimes-plan.md](2026-10-18-sandbox-runtimes-plan.md)

Effort: `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.
Value: `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little
standalone value, unblocks a High.

A step is done when its landing commit ticks the box and meets the plan's *Done means*: a rebuild
(`dotnet build -t:Rebuild -v:n`, then again with `-c Release`) reports `0 Warning(s)` and `0 Error(s)`, the
built test exe (run with `commit.gpgsign` off, redirected, never two full runs at once) reports `Failed: 0`,
and every real-container class the step touches reports Passed, not Skipped.
Groups: A contract and gates · B detection · C launch · D runtime state · E guard, verifier and errors ·
G step 2 and guides · U editor · T real-container tests · X end to end · W docs.

## Decision gates

| Gate | Step | Question | Answer and effect |
|---|---|---|---|
| G-ENV | A2 | Does `""` in `spec.Environment` blank a variable the container gets from the registry (empty or absent), while the rest of the block still arrives? | **Answered 2026-10-18: yes.** Build host (Windows Server 2025, 26100), test process elevated, `MxcSandboxGateTests`: with `OS=""` the test server reads `OS` as empty, `ComSpec` still arrives and `inAppContainer` is true. Without the entry `OS` is set and a variable set only in the test process (`PIA_TEST_SESSION_ONLY_<guid>`) is absent. Effect: C3 blanks with `""`; no complete block. |
| G-LINK | A2 | Can a contained server create a junction or symlink in its read-write folder that points at a folder it cannot write, and does the next spawn's ACL rewrite follow it to the target? | **Answered 2026-10-18: creation refused.** Build host, test process elevated, `MxcSandboxGateTests`: from inside the container a junction to an ungranted folder fails at `FSCTL_SET_REPARSE_POINT` with Win32 error 5 (opening the new empty folder succeeds) and a directory symlink fails with 1314; the same junction uncontained is created, so the refusal is the container's. A junction to a folder the container can write is created (stored as `\??\Global\C:\…`); with that grant revoked, the next spawn over the same read-write folder starts, puts no ACE on the target or its file, and can neither read nor write through the link. Effect: no hole in the shipped sandbox; the reparse rules in C2 and D1 stay, since a container can still plant a junction to any folder it can write and Pia's host-side walks must not follow it. |
| G-UVCACHE | A3 | Does a uv tool venv still start after Pia deletes `uv-cache` and `python-cache`? | **Answered 2026-10-18: yes.** Build host, uv 0.13.1, host side, the plan's uv environment: after `uv tool install mcp-server-time` (5.0 s cold, CPython 3.15.0), deleting `uv-cache` (2,396 files, 49 MiB) and `python-cache` (23.3 MiB) left 4,556 files and 103 MiB, and `tools\mcp-server-time\Scripts\python.exe -c <entry point>` answered `initialize` and `tools/list` in 1.9 s. Effect: C2 prunes both caches as planned, after every successful install (a recheck recreates `uv-cache`). |
| G-UVSPEC | A3 | Does `uv tool install` replace an installed tool when the spec changes? | **Closed by construction.** C2 deletes `tools` and `tool-bin` whenever Pia's spec record differs. A3 records uv's own behaviour for the docs only. Observed 2026-10-18, uv 0.13.1: `uv tool install --with tzdata mcp-server-time` over the installed tool exits 0 in 0.7 s and rewrites the receipt's `requirements` to `mcp-server-time` plus `tzdata` in place ("Checked 33 packages"; tzdata was already a dependency), so uv does not keep the old spec. `--python <full path to python.exe>` is accepted under `only-managed`: the receipt records `python = '<path>'`, the venv's `home` points at that interpreter in another state folder, and nothing is downloaded (the parser refuses a path either way). |
| S1 | spike §1 | Is the container a regular AppContainer, and what makes a folder readable? | Answered: regular AppContainer; an allow RX ACE for `S-1-15-2-1` or `S-1-15-2-2`. |
| S2 | spike §2 | Where do env and TEMP come from? | Answered: rebuilt from the registry; TEMP follows `LOCALAPPDATA` + a pre-created `Packages\<cn>\AC\Temp`. |
| S3 | spike §3, §5 | Can Node run with read grants alone? | Answered: no; flags plus the fs preload. Owner: ship the preload, granted read-only. |
| S4 | spike §4 | Per-start grant or persistent ACE? | Answered: per start, full toolchain folder (owner). |
| S5 | spike §5 | Exact Node recipe? | Answered: `node.exe npx-cli.js`, `NODE_OPTIONS`, npm cache, `LOCALAPPDATA`, `PATH` prepend; Deny adds `prefer_offline` and `fetch_retries=0`. |
| S6 | spike §6 | Can uvx run in the container? | Answered: no; host-side prepare, sandboxed `python.exe -c` (owner). Entry point from `uv-receipt.toml` plus the tool's own dist-info. |
| S7 | spike §7 | Exact dnx recipe, and Deny? | Answered: `dotnet exec <sdk>\dotnet.dll dnx <pkg> --yes`, SDK ≥ 10; Deny on SDK 10 needs a local-only `NuGet.Config`. |
| S8 | spike §8 | Step 2 mechanics and classifier? | Answered: .NET grant works and propagates; pre-check existing ACE; grantability through the linked token when elevated. |
| S9 | spike §9 | Do the guide commands work without admin? | Answered: yes for Node (winget user scope, zip), uv, .NET 10 SDK. |
| S10 | spike §2 | Do user registry secrets reach containers? | Answered: yes; blank `HKCU\Environment` except an allowlist, in every sandboxed server (owner). |
| S11 | spike §3 | Does any MXC containment option fix the ancestor, NUL or pipe denials? | Answered: no; keep the workarounds. |
| S12 | spike §3 | Can `cmd /c` stay in the launch? | Answered: no; cmd cannot start an unquoted drive-qualified path in the container, so it is unwrapped. |

## Steps

### A Contract and gates

- [x] **A1 Contract and parser.** Every new enum, record and interface of the plan's *Contract*, the optional
  trailing `SandboxLaunchSpec` fields, the three `PiaPaths` additions, `CommandLine.Split`,
  `SandboxContainerLease.IsActive`, `RuntimeStateLocks`, the frozen `FakeRuntimeHost` and `PathFreeAssert`
  test helpers, and `RunnerCommandParser` with its table tests, uvx value validation included.
  *Deps:* none · *Effort:* M · *Value:* Enabler
- [x] **A2 Container gate probes (G-ENV, G-LINK).** `get_environment` and `create_link` tools in
  `Pia.TestMcpServer`, a per-process probe name in `MxcTestHost`, and real-container tests that record whether
  `""` blanks a variable while the rest arrives, and whether a planted link is created and followed by the next
  spawn.
  *Deps:* none · *Effort:* S · *Value:* Enabler (gates G-ENV, G-LINK)
- [x] **A3 uv host gates (G-UVCACHE, G-UVSPEC).** A host-side measurement with a user-scope uv that never
  touches the user's `PATH`: install a tool with the plan's `UV_*` set, prune the caches, start the venv, change
  the spec, and record the receipt layout and uv's answers to `--python <path>` and a changed spec.
  *Deps:* none · *Effort:* XS · *Value:* Enabler (gate G-UVCACHE)

### B Detection

- [ ] **B1 Windows host facts.** `RuntimeHostService` with registry `PATH` and `HKCU\Environment` reading, the
  static name reader, the registry-built default environment block, the DEBUG-only candidate override,
  final-path resolution, the container-readability ACL check and the linked-token WRITE_DAC check, with tests
  on real temp folders.
  *Deps:* A1 · *Effort:* M · *Value:* Enabler
- [x] **B2 Runtime detector.** `RuntimeDetector`: candidates in registry-`PATH` order plus the user-scope
  locations, link resolution, per-kind validity (npm beside every Node, runtimeconfig frameworks for a dll),
  readable/grantable/admin-owned classification, program folders and versions from file metadata, cancellable,
  tested against the fake host.
  *Deps:* A1 · *Effort:* M · *Value:* High

### C Launch

- [ ] **C1 Launch planner and Node preload.** `RuntimeLaunchPlanner` with the Node, dnx, dotnet-dll and uvx
  recipes and their exact environments, the preload in Pia's read-only support folder, both `NuGet.Config`
  bodies, and the `--yes` insertion for dnx.
  *Deps:* A1 · *Effort:* S · *Value:* High
- [ ] **C2 State provisioner and uv install.** `RuntimeStateProvisioner`, `UvToolRunner` and
  `UvEntryPointResolver`: support files, state folders with the per-container `AC\Temp`, reparse-safe writes,
  the grant record, the spec record and reinstall, the host-side `uv tool install`, the receipt-scoped entry
  point, the cache prune and path-free errors, called from an off-thread `MxcClientTransport.ConnectAsync` that
  holds the state-folder lock until the launch returned.
  *Deps:* A1, A2, A3 · *Effort:* M · *Value:* High
- [ ] **C3 Builder, factory, blanking and DI.** `SandboxLaunchSpecBuilder.Build` switches to the parser,
  detector and planner, `SandboxEnvironmentComposer` blanks user registry variables for every sandboxed server
  (per G-ENV), `McpTransportFactory` refuses runners without all three runtime services, both callers run
  `Create` off the UI thread, `Bootstrapper` registers everything, and two DI tests prove the registrations
  reach the factory and `PluginService`.
  *Deps:* A1, A2, B1, B2, C1, C2, D1, E1, E2, G1 · *Effort:* M (L if G-ENV is answered no) · *Value:* High

### D Runtime state

- [ ] **D1 Runtime state store.** `RuntimeStateStore`: grant records outside the state folder, sweep roots,
  `TryDelete` that skips a prefix whose lock or lease is held, reparse-safe robust deletion and orphan
  detection.
  *Deps:* A1 · *Effort:* S · *Value:* Enabler
- [ ] **D2 Server lifecycle and one prefix.** `PluginService` sweeps the state and recorded roots, deletes the
  state folder on remove and on a prefix change, cleans orphans at startup off the UI thread and never under a
  starting or running probe, and offers `PreviewLocalMcpToolPrefix` so the probe and the saved start share one
  prefix.
  *Deps:* A1 · *Effort:* S · *Value:* High

### E Guard, verifier and errors

- [ ] **E1 Guard rules for runtime folders.** The `ManagedRuntimeFolder` policy refusal, tests that the grants
  and support folders are refused as Pia data, and `RuntimeRootVerifier` (too wide, too large, semicolon) for
  toolchain roots and program folders.
  *Deps:* A1 · *Effort:* S · *Value:* High
- [ ] **E2 Verifier, launcher request and serial spawns.** `SandboxGrantVerifier` walks toolchain roots fully
  with a cached positive result and checks Pia's own folders with their own clause; `MxcMcpLauncher` grants the
  toolchain, support and state folders, honours `ReplaceEnvironmentBlock`, and serializes spawns and disposals
  through `MxcAclGate`.
  *Deps:* A1 · *Effort:* S · *Value:* High
- [ ] **E3 Path-free start errors.** `MxcSpawnErrors` turns `MxcException` texts, which carry full paths, into
  path-free messages without an inner exception, with the raw text in `SensitiveDebug` only.
  *Deps:* none · *Effort:* XS · *Value:* High

### G Step 2 and guides

- [ ] **G1 Elevated fix, editor facade and guides.** `ToolchainAclService` (pre-check, `S-1-15-2-1` RX grant
  through .NET), `SandboxRuntimeService` (async detection, exact `icacls` preview, elevation gate) and
  `RuntimeInstallGuides` with the verified commands.
  *Deps:* A1 · *Effort:* S · *Value:* Med

### U Editor

- [ ] **U0 Editor strings.** Every key of the plan's *Appendix C* in en, de and fr, `SandboxRuntimeKeys`, the
  three enum→key tests and the placeholder-parity theory.
  *Deps:* A1 · *Effort:* S · *Value:* Enabler
- [ ] **U1 Editor view model.** Parser-based toggle and reasons, guard parity with the factory, debounced
  detection while the sandbox is on for a runner, the runner network default that survives a folder edit and
  respects a touched switch, step 2 and step 3 state and commands, and the preview prefix.
  *Deps:* C3, D2, G1, U0 · *Effort:* M · *Value:* High
- [ ] **U2 Editor view.** The runtime block, the step-2 button, the guide with copy buttons, the deny and
  environment hints and the detail line in `McpServersView.xaml`, every interactive control with its
  AutomationId, the `ViewAutomationIdTests` floor raised to 38, and the playbook's id list.
  *Deps:* U1 · *Effort:* S · *Value:* High

### T Real-container tests

- [ ] **T1 Runner starts in a real container.** Gate tests: `dotnet <dll>` with the test server through the
  real factory and detector, and a dependency-free Node fixture server on a temp `node.exe` copy whose realpath
  check proves the preload, with ACE hygiene after dispose.
  *Deps:* C3 · *Effort:* S · *Value:* High
- [ ] **T2 Live downloads.** `[LiveApiFact]` tests for npx (cold, warm, warm Deny), dnx without `--yes` in the
  user's arguments, and uvx under Deny after an Allow start.
  *Deps:* C3 · *Effort:* S · *Value:* Med

### X End to end

- [ ] **X1 Harness for runtimes.** `StageToolchain` (with the `icacls /setowner` admin copy and a junction),
  `Launch -RuntimeRoots`, `Launch -Elevated` with its own profile, `UndoToolchainGrant`, toolchain, support and
  `.runtime` roots in `Status` and `EndSession`, real-profile runtime folders in `Verify`, and the README section.
  *Deps:* C3 · *Effort:* S · *Value:* Enabler
- [ ] **X2 Live run.** Scenarios 1–15 of the plan through WinWright on the e2e stack, results written into the
  plan's *Live verification*.
  *Deps:* X1, U2, T1, D2 · *Effort:* M · *Value:* High

### W Docs

- [ ] **W1 Cross-references.** The design's and spike doc's Status lines, and the 2026-10-17 plan, checklist and
  gate-results passages that still say launchers are refused.
  *Deps:* U1 · *Effort:* XS · *Value:* Med

## Suggested order

1. **A2 and A3** beside **A1**: the gate probes are the cheapest decisive steps, and the contract unblocks
   everything else.
2. In parallel, each in its own worktree: **E1+E2+E3**, **B1**, **B2**, **C1**, **C2+D1** (after A2 and A3),
   **G1**, **D2**, **U0**. They touch disjoint files. Their full test gates run one after another, never at
   once.
3. **C3**, after reading the G-ENV answer. It is the only step that edits `Bootstrapper.cs`.
4. In parallel: **T1+T2** and **U1**.
5. In parallel: **U2** and **W1**.
6. **X1** then **X2**, last, on the merged branch. Then review and the PR through the `release-flow` skill.

## Not yet planned

- A narrow Node grant (`node.exe` plus `node_modules\npm` plus a Pia-written `node.cmd`), measured at 2.4 s
  instead of 4.2 s for a warm start.
- A preload shim that gives async Node child processes `\\.\pipe\LOCAL\` pipes (never covers `spawnSync`).
- Bytecode compilation for uv tools, to speed the first sandboxed start. It must run inside the container,
  never on the host: `UV_COMPILE_BYTECODE` during the host-side install would run the venv's Python there and
  execute any `.pth` file a wheel ships.
- Pruning npm and NuGet caches in the state folder, or a warning once a state folder passes a size that makes
  starts slow.
- Upgrading an installed uvx tool on an Allow start.
- `pnpm dlx`, `yarn dlx`, `bunx`, Deno, Docker, `pipx`; `uv run`, `dotnet run`, `dotnet tool run`.
- Bare `python` through detection, for example on a uv-managed Python.
- One shared runtime cache across servers.
- Passing private registry or proxy configuration (`.npmrc`, `uv.toml`, `NuGet.Config`) into the sandbox, and
  proxy or CA variables on the blanking allowlist (an owner question).
- Starting `Program Files\nodejs\node.exe` for a bare script without step 2 (the spike shows MXC can launch it).
- `FilesystemEnumeratePaths` containment once a Windows build offers it, which could retire the preload.
- Logging `MxcContainer.Probe` warnings at startup.
- The grant verifier using the linked token when Pia runs elevated.
- The sandbox line on the action card naming the runtime.
- The Pia.Docs desktop guide page and the help-corpus refresh at the next `release/*`.
