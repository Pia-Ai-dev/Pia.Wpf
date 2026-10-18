# Sandboxed MCP servers on npx, uvx and dnx: plan

**Status:** In progress on `feature/mxc-sandboxed-mcp`. All gates are answered (see the checklist): an empty
value blanks a variable (G-ENV), a container cannot plant a link to a folder it cannot write (G-LINK), a uv
tool starts after its caches are pruned (G-UVCACHE), and Pia reinstalls on a spec change (G-UVSPEC).
**Owner:** Marco Altmann
**Written:** 2026-10-18
**Origin:** The owner-approved concept [2026-10-18-sandbox-runtimes-design.md](2026-10-18-sandbox-runtimes-design.md)
and the measurements plus the owner's four decisions in
[2026-10-18-sandbox-runtimes-spike-results.md](2026-10-18-sandbox-runtimes-spike-results.md). It builds on the
shipped MXC sandbox of [2026-10-17-mcp-sandbox.md](2026-10-17-mcp-sandbox.md) (branch `feature/mxc-sandboxed-mcp`).
Tracking: [2026-10-18-sandbox-runtimes-checklist.md](2026-10-18-sandbox-runtimes-checklist.md).

## Goal

A local MCP server whose command is `npx …`, `npm exec …`, `uvx …`, `dnx …`, `node <script>`,
`dotnet <app>.dll`, or `cmd /c` wrapping any of them, runs inside the existing MXC sandbox. Pia finds the
user's own runtime, shares it read-only, and moves every cache, config and home lookup into a folder Pia
owns per server. When no runtime is usable, the editor says why and what to do before the user saves.
None of this blocks the window: detection, preparation and the grant check run off the UI thread.

The promise of the sandbox does not change: the server writes only to its sandbox folders and reads only
those plus the toolchain. It also gains one thing: secrets in the user's registry environment no longer
reach any sandboxed server.

## Decisions that bind this plan

From the owner (spike results, end of file), fixed:

1. **Node fs preload: ship it.** Pia owns the preload, grants it read-only and sets `NODE_OPTIONS` for every
   sandboxed Node start. Piped-stdio child processes stay a known limit.
2. **uv: host-side prepare.** An uncontained `uv tool install` with `UV_NO_BUILD=1` into the server's
   runtime state folder, then a sandboxed `python.exe -c <entry point>` with that folder read-only. Scope is
   `uvx <pkg>` (and its alias `uv tool run`). `uv run` stays refused.
3. **Grant cost: per start, the full toolchain folder.** No persistent ACE on user folders. Keep the uv
   state lean by pruning uv's cache after the prepare.
4. **Blank every `HKCU\Environment` variable** in every sandboxed server, runner or plain exe, except `Path`,
   `TEMP`, `TMP` and names starting with `OneDrive`. The server's own env entries still win.

From the design, also fixed: three steps (use what exists, elevated-only fix button, user-scope guide);
a new package-runner server (npx, npm exec, uvx, dnx, or `cmd /c` wrapping one) defaults to network Allow, a
saved server's setting never changes; the Pia-managed
runtime state folder is `PiaPaths.McpSandboxRoot\<prefix>.runtime`; Test connection and Save share one
launch path through `McpTransportFactory`.

Calls made in this plan (the builder's to make, now recorded):

- `cmd /c` is **unwrapped**, never run. `cmd.exe` inside the container cannot start a program from an
  unquoted drive-qualified path (spike section 3), and re-parsing cmd syntax would be an injection risk.
  A cmd line that uses cmd features (`& | < > ^ %`, or quotes in a separate argument) is refused.
- Node always runs as `<node dir>\node.exe <node dir>\node_modules\npm\bin\npx-cli.js …`: the npm bundled
  with the chosen `node.exe`, never `npx.cmd`, never the global npm under `%APPDATA%\npm`.
- A Node install counts only when `node_modules\npm\package.json` sits beside `node.exe`. That excludes the
  scoop and Chocolatey shim folders, whose `node.exe` only forwards to a folder Pia never granted.
- `node <script>` and `dotnet <app>.dll` go through the same detection and steps as the runners. Bare
  `python` stays on today's exe path.
- A full path to a launcher (`C:\Program Files\nodejs\npx.cmd`) is a preference: that install is tried
  first, then the usual order. The editor shows which install was picked.
- Every runner kind gets a runtime state folder, also `node <script>` and `dotnet <dll>` (it holds a
  writable home).
- **The preload lives in Pia's support folder**, `PiaPaths.McpSandboxSupportDirectory`
  (`<local data>\McpSandboxSupport\node-sandbox.cjs`), outside every state folder. Every Node start grants
  that folder read-only; no container can write it, and it holds nothing else.
- The probe and the saved start use **the same prefix**, so they share one state folder (each with its
  own container name and its own `AC\Temp`). `IPluginService` gains a prefix preview for that.
- The list of roots granted per start is persisted **outside** the state folder (which the server can
  write), so a hard kill is swept even when detection later picks another install.
- **Every `SandboxStartException` the new code throws is path-free and has no inner exception.**
  `McpPluginToolHandler` logs the exception with `LogError(ex, …)` and `McpServerProbe` appends inner
  messages, so a path in either reaches the release log. Raw texts go to `SensitiveDebug` only.
- **No sandboxed start skips the blanking.** The variable names come from the host service when DI
  provides one and from a direct registry read otherwise; no code path composes a sandboxed environment
  without them.
- **Runners need the detector, the host service and the provisioner together.** A factory missing any of
  them refuses runners with a clear reason; it never starts one half-prepared.
- **Nothing in the start path runs on the UI thread.** `McpTransportFactory.Create` and the preparation in
  `MxcClientTransport.ConnectAsync` run on the thread pool (see *Off the UI thread*).
- **The host-side uv install only gets validated tokens:** package names from the index with an optional
  version, and a version number for `--python`. A path, URL, UNC name or a token starting with `-` is
  refused before anything runs.
- Pia **reinstalls a uvx tool when its spec changes**, keyed on a spec record in the read-only state folder.
- dnx gets `--yes` right after the package when the user's arguments carry neither `--yes` nor `-y`: a
  sandbox has no console to answer a prompt, and the spike always passed it in that position.
- Pia **serializes MXC spawns and disposals process-wide**: two containers rewriting ACLs over one tree at
  the same moment was never measured.
- **One lock per state folder** covers preparation until the container's lease exists, so nothing deletes
  or rewrites a state folder that a start is still filling.
- **Host-side walks inside a server-writable state folder never follow reparse points.** Measured on this
  host: `Directory.EnumerateFiles(…, AllDirectories)` returned a file behind a junction, and
  `Directory.Delete(dir, true)` threw `IOException` ("Falscher Parameter") on it; `AttributesToSkip =
  ReparsePoint` skipped it.
- The e2e harness and the gate tests control detection through a **DEBUG-only** environment override, like
  the other `PIA_DEBUG_*` hooks. A Release build ignores it.

## The start path after this change

```
McpServerProbe.ProbeAsync / McpPluginToolHandler.InitializeAsync
  await Task.Run(() => factory.Create(request))               off the UI thread

McpTransportFactory.Create(request)
  SandboxLaunchSpecBuilder.Build(request, runtime context)
    RunnerCommandParser.Parse(command, args, workingDir)      refusal -> SandboxStartException
    Kind None -> today's exe rules on the effective command (cmd /c <exe> unwrapped)
    runner    -> detector + host + provisioner present, else refused
              -> IRuntimeDetector.Detect(runner, token)       not Ready -> SandboxStartException
              -> RuntimeLaunchPlanner.Plan(...)               command, args, env, toolchain roots,
                                                              state and support folders, preparation
    SandboxEnvironmentComposer.Compose(...)                   every spec, exe servers included
  SandboxPathGuard.Validate(policy, effective command)        user folders; managed-folder rule
  new MxcClientTransport(spec, launcher, verifier, provisioner, ...)

MxcClientTransport.ConnectAsync                               async
  await Task.Run(...)                                         off the caller's thread
    RuntimeStateLocks.AcquireAsync(state folder)              runners only, held until the lease exists
    CreateOwnedFolders()                                      user rw folders under McpSandboxRoot (today)
    IRuntimeStateProvisioner.PrepareAsync(spec)               support folder, state dirs, AC\Temp, files,
                                                              grant record, uv install
    ISandboxGrantVerifier.Verify(final spec)                  toolchain roots walked fully
    IMxcProcessLauncher.LaunchAsync(final spec)               lease taken; spawn and dispose serialized
                                                              process-wide
```

Today's anchors (all on `feature/mxc-sandboxed-mcp` at `3ae5998e`):
`McpTransportFactory.cs:47-66` (Create), `McpServerProbe.cs:37-45` and `McpPluginToolHandler.cs:84-85`
(the two callers), `SandboxLaunchSpecBuilder.cs:24-66` (Build), `:144-162` (WorkingDirectoryFor), `:164-167`
(CopyEnvironment), `SandboxPathGuard.cs:27-68` (SandboxGuardEnvironment), `:172-224` (Check),
`SandboxGrantVerifier.cs:20,30-51` (cap and Verify), `MxcClientTransport.cs:56-92` (ConnectAsync,
CreateOwnedFolders), `MxcMcpLauncher.cs:13-40` (BuildRequest), `:83-99` (SpawnAsync), `SandboxContainerLease.cs`,
`PluginService.cs:247-275` (startup), `:508-648` (probe, save, remove, prefix), `:912-946` (sweep),
`McpServersSettingsViewModel.cs:671-875` (definition, sandbox editor; `ApplySandboxDefaults` at about 840),
`App.xaml.cs:291` (startup activation, on the dispatcher).

## New and changed types

New code lives in `src/Pia.Wpf/Services/Plugins/Sandbox/Runtimes/`, namespace
`Pia.Services.Plugins.Sandbox.Runtimes`. Non-static service classes need a suffix from
`NamingConventionTests.ServiceClasses_MustFollowNamingConvention` (Service, Detector, Verifier, Store,
Runner, Provisioner, Composer, Planner, Resolver, …); static classes and records are exempt.

### Contract (A1, lands first)

`Runtimes/RunnerModels.cs`

```csharp
public enum RunnerKind { None, Npx, NpmExec, NodeScript, Dnx, DotnetDll, Uvx }

public enum RunnerRefusal
{
    None, NonGoalLauncher, ScriptFile, CmdNotSystem, CmdWithoutC, CmdMetacharacters,
    UnsupportedForm, UvxOption, UvxValue, RelativePath
}

/// <param name="Package">What uv installs: the --from value, else the first token.</param>
/// <param name="CommandName">The console script to run.</param>
public sealed record UvxSpec(string Package, string CommandName, IReadOnlyList<string> With, string? Python);

/// <param name="LauncherPathHint">The full path the user named, tried before the usual order; null for a bare name.</param>
/// <param name="Arguments">What follows the launcher: npx args, script args, dnx args, server args.</param>
/// <param name="RuntimeOptions">node flags before the script.</param>
public sealed record RunnerRequest(
    RunnerKind Kind,
    string? LauncherPathHint,
    IReadOnlyList<string> Arguments,
    string? ScriptOrDll,
    IReadOnlyList<string> RuntimeOptions,
    UvxSpec? Uvx);

/// <param name="Subject">A launcher or option name, never a value.</param>
/// <param name="EffectiveCommand">The program after cmd /c unwrapping; for Kind None this is what Build launches.</param>
public sealed record RunnerParseResult(
    RunnerKind Kind,
    RunnerRequest? Runner,
    RunnerRefusal Refusal,
    string? Subject,
    string EffectiveCommand,
    IReadOnlyList<string> EffectiveArgs);
```

`Runtimes/RuntimeModels.cs`

```csharp
public enum RuntimeFamily { Node, Dotnet, Uv }

public enum RuntimeOutcome { Ready, NeedsAdminFix, NotFound, Refused }

public enum RuntimeIssue
{
    None, NodeNotFound, NodeAdminOwned, DotnetSdkMissing, DotnetRuntimeMissing, DotnetAdminOwned,
    UvNotFound, ToolchainTooLarge, ToolchainTooWide, ToolchainPathUnsupported,
    ProgramFolderTooLarge, ProgramFolderTooWide, ScriptMissing, DllMissing, UvInWritableFolder
}

/// <param name="Root">Final path (links resolved) of the folder holding node.exe, dotnet.exe or uv.exe.</param>
/// <param name="EntryScript">npx-cli.js, npm-cli.js or sdk\<v>\dotnet.dll; null when the kind needs none.</param>
public sealed record RuntimeInstall(
    RuntimeFamily Family,
    string Root,
    string Executable,
    string? Version,
    string? NpmVersion,
    string? EntryScript,
    bool ReadableAlready,
    bool AdminOwned);

/// <param name="Install">The chosen install when Ready, the first admin-owned one when NeedsAdminFix, the refused one when Refused.</param>
/// <param name="ToolchainRoots">Read-only grants per start; empty when readable already and for uv.</param>
/// <param name="ProgramFolder">The script's package root or the dll's folder; null for the package runners.</param>
public sealed record RuntimeResolution(
    RunnerKind Kind,
    RuntimeOutcome Outcome,
    RuntimeIssue Issue,
    RuntimeInstall? Install,
    IReadOnlyList<string> ToolchainRoots,
    string? ProgramFolder);

public sealed record PreparedFile(string Path, string Content);

/// <param name="StateDirectory">Holds tools, tool-bin, the caches and the spec record.</param>
public sealed record UvPrepareRequest(
    string ContainerName,
    string UvExecutable,
    UvxSpec Spec,
    IReadOnlyDictionary<string, string> Environment,
    bool Offline,
    string StateDirectory,
    IReadOnlyList<string> ServerArgs,
    IReadOnlyList<string> CacheDirectoriesToPrune);

public sealed record RuntimePreparation(
    IReadOnlyList<string> Directories,
    IReadOnlyList<PreparedFile> Files,
    UvPrepareRequest? Uv);

public sealed record RuntimePlanInput(
    RunnerRequest Runner,
    RuntimeResolution Resolution,
    string ContainerName,
    string StateDirectory,
    string SupportDirectory,
    bool AllowNetwork,
    IReadOnlyList<string> SandboxRoots,
    string RegistryPath,
    IReadOnlyDictionary<string, string>? UserEnvironment);

public sealed record RuntimeLaunchPlan(
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> ToolchainRoots,
    string StateDirectory,
    bool StateReadOnly,
    string? SupportDirectory,
    RuntimePreparation Preparation);

public sealed record RuntimeDetection(RunnerParseResult Parse, RuntimeResolution? Resolution, string? StateDirectory, bool StateReadOnly);

public sealed record MachineWideGrantPreview(string Root, string GrantCommand, string RemoveCommand, bool HasExistingAce);

public sealed record MachineWideGrantResult(bool Ok, string? Error);

public sealed record RuntimeGuide(RuntimeFamily Family, string Command, string? AlternativeCommand);
```

Interfaces, one file each under `Runtimes/`:

```csharp
/// <summary>The machine facts detection and planning read; a seam so they test without a real profile.</summary>
public interface IRuntimeHostService
{
    IReadOnlyList<string> RegistryPathEntries();     // HKLM then HKCU Path, expanded, in order
    string RegistryPath();                           // the same, joined with ';'
    IReadOnlyList<string> UserEnvironmentNames();    // value names under HKCU\Environment
    string? RegistryEnvironmentValue(string name);   // HKCU over HKLM, expanded
    IReadOnlyList<string>? CandidateOverride { get; } // PIA_DEBUG_SANDBOX_RUNTIME_ROOTS, DEBUG builds only
    string LocalAppData { get; }
    string RoamingAppData { get; }
    string UserProfile { get; }
    string WindowsDirectory { get; }
    IReadOnlyList<string> ProgramFilesDirectories { get; }
    bool FileExists(string path);
    bool DirectoryExists(string path);
    IReadOnlyList<string> EnumerateDirectories(string path, string searchPattern);
    string? ReadAllText(string path);
    string? ResolveFinalPath(string path);           // symlinks and junctions anywhere in the path; null when missing
    string? FileVersion(string path);                // ProductVersion
    bool IsContainerReadable(string path);           // allow RX for S-1-15-2-1 or S-1-15-2-2, no deny
    bool CanUserWriteDacl(string path);              // as the medium-IL user, also when Pia runs elevated
}

public interface IRuntimeRootVerifier
{
    /// <returns>Why Pia will not grant this folder per start; null when it may.</returns>
    RuntimeIssue? Check(string root, bool isProgramFolder);
}

public interface IRuntimeDetector
{
    RuntimeResolution Detect(RunnerRequest runner, CancellationToken cancellationToken);
}

public interface IRuntimeStateProvisioner
{
    /// <summary>Writes Pia's folders and files and runs the uv install; returns the spec to launch.</summary>
    Task<SandboxLaunchSpec> PrepareAsync(SandboxLaunchSpec spec, Action<string> stderrLine, CancellationToken cancellationToken);
}

public interface IRuntimeStateStore
{
    void WriteGrantRecord(string containerName, IReadOnlyList<string> roots, bool stateReadOnly = false);
    bool? StateGrantedReadOnly(string containerName);
    IReadOnlyList<string> SweepRoots(string prefix);
    /// <returns>False when a start of this prefix is preparing or running; nothing was touched then.</returns>
    bool TryDelete(string prefix, Action? beforeDelete = null);
    IReadOnlyList<string> OrphanPrefixes(IReadOnlySet<string> knownPrefixes);
}

public interface IToolchainAclService
{
    bool HasAllAppPackagesAce(string root);
    void GrantAllAppPackagesRead(string root);
}

/// <summary>What the server editor asks about runtimes; detection runs off the UI thread.</summary>
public interface ISandboxRuntimeService
{
    Task<RuntimeDetection> DetectAsync(string command, IReadOnlyList<string> args, string? workingDirectory,
        string toolPrefix, CancellationToken cancellationToken);
    bool CanGrantMachineWide { get; }
    MachineWideGrantPreview PreviewMachineWideGrant(string root);
    Task<MachineWideGrantResult> GrantMachineWideReadAsync(string root, CancellationToken cancellationToken);
    RuntimeGuide GuideFor(RuntimeFamily family);
}
```

Every caller that needs a state folder path calls `PiaPaths.McpRuntimeStateDirectory(prefix)` directly.

Changed in the contract slice:

- `SandboxLaunchSpec` (`SandboxLaunchSpec.cs:5-14`) gains **optional trailing** parameters, so every
  positional `new SandboxLaunchSpec(` in the tests keeps compiling:
  `IReadOnlyList<string>? ToolchainDirectories = null, string? RuntimeStateDirectory = null,
  bool RuntimeStateReadOnly = false, string? SupportDirectory = null, RunnerKind RunnerKind = RunnerKind.None,
  RuntimePreparation? Preparation = null`, plus a computed
  `IReadOnlyList<string> Toolchain => ToolchainDirectories ?? []`.
- `PiaPaths` gains the method `McpRuntimeStateDirectory(string prefix) => Path.Combine(McpSandboxRoot,
  prefix + ".runtime")` and the properties `McpSandboxGrantsDirectory` (`<LocalDataDirectory>\McpSandboxGrants`)
  and `McpSandboxSupportDirectory` (`<LocalDataDirectory>\McpSandboxSupport`). All routed by
  `PIA_LOCAL_DATA_DIR`. The last two sit outside `McpSandboxRoot` and under `LocalDataDirectory`, so the guard
  already refuses them as Pia data if a user lists one as a policy folder.
- `CommandLine` (`CommandLine.cs`) gains `Split(string)`: MSVCRT/`CommandLineToArgvW` rules for every token
  (2n backslashes + quote → n backslashes and a quote toggle; 2n+1 → n and a literal quote; `""` inside
  quotes → literal quote).
- `SandboxContainerLease` gains `public static bool IsActive(string name)`, read under the same lock.
- `Runtimes/RuntimeStateLocks.cs` (static): one `SemaphoreSlim` per state folder (keyed by the normalized
  path, case-insensitive), `Task<IDisposable> AcquireAsync(string stateDirectory, CancellationToken)` and
  `IDisposable? TryAcquire(string stateDirectory)` (null when held). The transport holds it from before
  `CreateOwnedFolders` until `LaunchAsync` returns, when the container's lease has taken over;
  `RuntimeStateStore.TryDelete` takes it and then checks both leases. That closes the window in which a
  starting probe holds neither.
- Test infrastructure, frozen after A1 so parallel slices never edit it: `FakeRuntimeHost` (an in-memory
  `IRuntimeHostService`) and `PathFreeAssert.IsPathFree(Exception)` (no inner exception, and no
  drive-qualified or UNC path in the message).

### Parser: `RunnerCommandParser.Parse(command, args, workingDirectory, systemDirectory = null, fileExists = null)`

Static and pure apart from the `fileExists` seam (default `File.Exists`, called only in rule 2's
single-item case). The editor calls it on every keystroke; Build calls it on every start.

1. Trim the command and strip one pair of surrounding quotes.
2. **cmd unwrapping.** When the file name without extension is `cmd`:
   - A rooted path must equal `<systemDirectory ?? Environment.SystemDirectory>\cmd.exe` after
     `Path.GetFullPath`, case-insensitive. Otherwise `CmdNotSystem`.
   - Leading switches `/d`, `/s`, `/q` (any case, any order) are skipped. The next item must be `/c` (any
     case). `/k`, anything else, or nothing → `CmdWithoutC`.
   - What follows `/c` is the inner command line. Empty → `CmdWithoutC`.
   - **Exactly one item:** if it is fully qualified and `fileExists` says it names a file, it is the program
     as a whole (cmd keeps such a quoted path whole: `["/c", "C:\Users\Jane Doe\mcp\server.exe"]`).
     Otherwise it must not contain `& | < > ^ %`, and is split with `CommandLine.Split`.
   - **Several items:** each is taken as is, and none may contain `& | < > ^ % "`.
   - A metacharacter hit → `CmdMetacharacters`. The first inner item is the new command, the rest its args.
     A second `cmd` → `CmdWithoutC`.
3. **Classify** by the file name without extension, lowercase. A launcher name with an extension other
   than none, `.exe` or `.cmd` → `ScriptFile`.
   - `npx` → `Npx`, arguments unchanged.
   - `npm` with first arg `exec` or `x` → `NpmExec`, arguments after it. Other npm forms →
     `UnsupportedForm`, subject `npm`.
   - `uvx`, or `uv` followed by `tool run` → `Uvx`. Other `uv` forms → `UnsupportedForm`, subject `uv`.
     The uvx options before the package: `--from <v>` / `--from=<v>`, `--with <v>` (repeatable, also `=`),
     `--python <v>` / `-p <v>` / `--python=<v>`, `-q` / `--quiet` and `--isolated` (ignored). Any other
     leading option → `UvxOption`, subject **the option name only**, cut at `=` (a value can carry
     credentials: `--index-url=https://user:token@host`). The first non-option token is the command (with
     `--from`) or the package spec (without). The rest are server args.
   - **uvx value checks** (anything else → `UvxValue`, no subject): the package token and every `--from` and
     `--with` value must match
     `^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?(\[[A-Za-z0-9._,-]+\])?(@[A-Za-z0-9.*+!_-]+|(===|==|>=|<=|~=|!=|<|>)[A-Za-z0-9.*+!_-]+(,(===|==|>=|<=|~=|!=|<|>)[A-Za-z0-9.*+!_-]+)*)?$`
     (an index name with optional extras and a version, no spaces). That refuses a leading `-`, paths,
     `file:`, URLs, git sources and UNC names, which uv would otherwise read or contact on the host, uncontained.
     An archive file name (`.whl`, `.zip`, `.tar`, `.tar.gz`, `.tgz` and the other sdist suffixes) is refused
     like a path: uv reads it as a local file.
     `--python` must match `^(cpython-?|python)?[0-9]+(\.[0-9]+){0,2}$`: uv also accepts an interpreter path
     there and would run it on the host to query it.
   - `CommandName` is the command token without `@…`, `[…]` and any version specifier; it must match
     `^[A-Za-z0-9][A-Za-z0-9._-]*$`, else `UnsupportedForm`, subject `uv`.
   - `dnx`, or `dotnet` followed by `dnx` → `Dnx`, arguments after it.
   - `dotnet <x>.dll …` or `dotnet exec <x>.dll …` → `DotnetDll`. A relative dll resolves against the working
     directory; relative with no working directory → `RelativePath`. Other dotnet forms → `UnsupportedForm`,
     subject `dotnet`.
   - `node` → `NodeScript`. Leading options are kept as `RuntimeOptions`: `--flag` and `--flag=value`;
     `-r`, `--require`, `--import`, `--loader`, `--experimental-loader` take the next item too. `-e`, `-p`,
     `--eval`, `--print`, `-i`, `--interactive`, `--test`, `--run` and a bare `-` → `UnsupportedForm`,
     subject `node`. The first non-option is the script; a relative script resolves against the working
     directory; relative with no working directory → `RelativePath`. No script → `UnsupportedForm`.
   - `pnpm`, `pnpx`, `yarn`, `bunx`, `bun`, `deno`, `pipx`, `docker` → `NonGoalLauncher`, subject the name.
   - Any other `.cmd`, `.bat` or `.ps1` → `ScriptFile`.
   - Everything else, `python` included → `Kind None` with the (possibly unwrapped) command and args.
4. `LauncherPathHint` is the rooted path when the user named one.

### Detection: `RuntimeDetector(IRuntimeHostService host, IRuntimeRootVerifier roots)`

Never executes a toolchain. Reads the registry, the file system and ACLs. Checks the cancellation token
between candidates.

**Candidates**, de-duplicated by final path (links resolved through `ResolveFinalPath`):

1. The folder of `LauncherPathHint`, when given.
2. When `CandidateOverride` is set (DEBUG only): exactly those folders, in order, and nothing else.
3. Otherwise the registry `PATH` entries in order (HKLM, then HKCU; Pia's own `PATH` is stale after a guide
   install). Skipped: `%LOCALAPPDATA%\Microsoft\WindowsApps` (Store aliases) and any folder holding
   `volta-shim.exe` (Volta shims need Volta's state).
4. Then the user-scope locations from the guide research:
   - Node: `%LOCALAPPDATA%\Programs\nodejs`; the newest `%LOCALAPPDATA%\Microsoft\WinGet\Packages\OpenJS.NodeJS*\node-v*`;
     the target of the registry variable `NVM_SYMLINK`; `%FNM_DIR%\aliases\default` (default
     `%APPDATA%\fnm\aliases\default`); Volta's `%VOLTA_HOME%\tools\image\node\<ver>` (default
     `%LOCALAPPDATA%\Volta`, `<ver>` from `tools\user\platform.json` `node.runtime`); scoop's
     `%SCOOP%\apps\nodejs-lts\current` and `\apps\nodejs\current` (default `%USERPROFILE%\scoop`).
   - .NET: `DOTNET_ROOT` (registry), `%LOCALAPPDATA%\Microsoft\dotnet`, `%ProgramFiles%\dotnet`.
   - uv: `%USERPROFILE%\.local\bin`, `%USERPROFILE%\.cargo\bin`, `%LOCALAPPDATA%\Microsoft\WinGet\Links`
     (the link target), `%SCOOP%\shims` (the `path = "…"` line of `uv.shim`).

   The Volta, fnm, nvm and scoop shapes come from documentation and source reading; none was installed on
   the build host. Scenario 15 of the live run checks one link-resolved install.

**A candidate is valid** when it holds what the kind needs:
- Every Node kind: `node.exe` and `node_modules\npm\package.json`. `Npx` also `node_modules\npm\bin\npx-cli.js`,
  `NpmExec` also `…\npm-cli.js`.
- `Dnx`: `dotnet.exe` and an `sdk\<v>\dotnet.dll` whose highest SemVer `<v>` has major ≥ 10 (the folder
  `dnx.cmd` would pick: the last line of `--list-sdks` is the highest version; a prerelease sorts below its
  release). A root with only SDK 8 or 9, or only `dnx.cmd`, is not valid.
- `DotnetDll`: `dotnet.exe` and, for every framework in `<dll name>.runtimeconfig.json`
  (`runtimeOptions.framework` or `runtimeOptions.frameworks[]`), a `shared\<name>\<v>` with the same major
  and `v` ≥ the requested version. No runtimeconfig: at least one `shared\Microsoft.NETCore.App\<v>`.
- `Uvx`: `uv.exe` (final path). `uvx.exe` is not needed: Pia runs `uv` itself.

**Classification**, first valid candidate in order:
- `Uvx`: Ready at once. uv runs on the host, so nothing is granted. A `uv.exe` in a folder this server can
  write, or under `McpSandboxRoot`, is skipped at start (`UvInWritableFolder`): a server could have planted it.
- Readable already when the root, the executable and the entry script all pass `IsContainerReadable`:
  Ready, no toolchain roots. (`C:\Program Files\dotnet` inherits `S-1-15-2-1` and `S-1-15-2-2` RX.)
- Under `%WINDIR%`: skipped. Under `Program Files*` and not readable: remembered as admin-owned, next.
- `CanUserWriteDacl(root)` true: `roots.Check(root, false)`; an issue remembers the candidate as refused,
  next; no issue → Ready with `ToolchainRoots = [root]`.
- Otherwise remembered as admin-owned, next.

No Ready candidate → the first admin-owned (`NeedsAdminFix`, issue `NodeAdminOwned` / `DotnetAdminOwned`),
else the first refused (`Refused`, its issue), else `NotFound` (`NodeNotFound`, `DotnetSdkMissing` for dnx,
`DotnetRuntimeMissing` for a dll, `UvNotFound`).

The root is always the folder that holds `node.exe` or `dotnet.exe` after link resolution, never a parent.
A version manager's link therefore resolves to one version folder, and neither a per-start grant nor the
step-2 button ever covers a whole version store.

**Program folder** for `NodeScript` and `DotnetDll`: the script or dll must exist (`ScriptMissing` /
`DllMissing`, outcome `Refused`). For a script it is the nearest folder upward, starting with the script's
own, that holds `package.json`, never the drive root or the profile root; else the script's folder. For a
dll it is the dll's folder. `roots.Check(folder, true)` refuses a too-wide or too-large one
(`ProgramFolderTooWide` / `ProgramFolderTooLarge`). Under `%WINDIR%` or `Program Files*` it is not granted.

**Versions** come from file metadata only: `FileVersion(node.exe)`; npm from `node_modules\npm\package.json`
`"version"`; the chosen SDK folder name; the highest `shared\Microsoft.NETCore.App` folder name;
`FileVersion(uv.exe)` (may be null).

`RuntimeHostService(IElevationService)` (the real `IRuntimeHostService`):
- Registry through `Microsoft.Win32.Registry`: `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment`
  and `HKCU\Environment`, read with `RegistryValueOptions.DoNotExpandEnvironmentNames`, `%NAME%` expanded
  against the registry environment (HKCU over HKLM), then Pia's process environment.
- `public static IReadOnlyList<string> ReadUserEnvironmentNames()`: the value names under `HKCU\Environment`,
  empty on any error. The instance member returns the same; the factory uses the static one when DI gave it
  no host, so blanking never depends on DI.
- Profile roots from `PiaPaths.LocalProfileRoot`, `RoamingProfileRoot`, `UserProfileRoot`: the real roots,
  never the routed data roots. `DataDirectoryRoutingTests` forbids `SpecialFolder.(Local)ApplicationData`
  elsewhere; update the comment on those three properties to say they also serve toolchain discovery.
- `CandidateOverride`: `PIA_DEBUG_SANDBOX_RUNTIME_ROOTS`, `;`-separated, read only inside `#if DEBUG`.
  Release returns null. A Release override would change which folders get granted.
- `ResolveFinalPath`: new `Native/FinalPathProbe.cs`, `CreateFileW` (access 0, `FILE_SHARE_ALL`,
  `FILE_FLAG_BACKUP_SEMANTICS`) plus `GetFinalPathNameByHandleW(FILE_NAME_NORMALIZED | VOLUME_NAME_DOS)`,
  `\\?\` stripped. Host-side, outside any container, so the mount manager is reachable.
- `IsContainerReadable`: `FileSystemAclExtensions.GetAccessControl` on the file or folder,
  `GetAccessRules(true, true, typeof(SecurityIdentifier))`. Readable when an Allow rule for `S-1-15-2-1` or
  `S-1-15-2-2` covers `ReadAndExecute` (generic bits mapped first) and applies to the object itself (not
  `InheritOnly`), and no Deny rule for those SIDs exists.
- `CanUserWriteDacl`: not elevated → `FileSecurityProbe.CanWriteDacl`. Elevated → new
  `Native/TokenAccessProbe.cs`: `OpenProcessToken(TOKEN_QUERY)`, `GetTokenInformation(TokenLinkedToken)`,
  the file's security descriptor with owner, group and DACL (`GetSecurityDescriptorBinaryForm()`), then
  `AccessCheck(sd, linkedToken, WRITE_DAC, file GENERIC_MAPPING)`. No linked token (UAC off) → the process
  token. An elevated Pia would otherwise judge `Program Files` grantable (spike section 8).

### Launch recipes: `RuntimeLaunchPlanner.Plan(RuntimePlanInput)`

Static and pure. `<state>` is `PiaPaths.McpRuntimeStateDirectory(prefix)`, `<support>` is
`PiaPaths.McpSandboxSupportDirectory`, `<root>` the install root, `<cn>` the container name
(`pia-mcp-<prefix>` or `pia-mcp-<prefix>-probe`).

**Home set**, every kind except `Uvx`:
- `USERPROFILE=<state>\home`, `HOME=<state>\home`, `HOMEDRIVE=<drive of state>`, `HOMEPATH=<rest of <state>\home>`
- `APPDATA=<state>\home\AppData\Roaming`
- `LOCALAPPDATA=<state>\home\AppData\Local`. MXC appends `\Packages\<cn>\AC` and points `TEMP`/`TMP` at
  `<that>\Temp`, so `<state>\home\AppData\Local\Packages\<cn>\AC\Temp` must exist before the spawn.
  `TEMP`/`TMP` are not set (MXC ignores them).
- Directories: `<state>`, `<state>\home`, `<state>\home\AppData\Roaming`,
  `<state>\home\AppData\Local\Packages\<cn>\AC\Temp`.

**Node** (`Npx`, `NpmExec`, `NodeScript`):
- Command `<root>\node.exe`. Args: `Npx` → `<root>\node_modules\npm\bin\npx-cli.js` + the user's npx args;
  `NpmExec` → `<root>\node_modules\npm\bin\npm-cli.js`, `exec` + the rest; `NodeScript` → runtime options,
  the script's full path, the script args.
- `NODE_OPTIONS=--preserve-symlinks --preserve-symlinks-main --require "<support>/node-sandbox.cjs"` with
  forward slashes (a backslash inside the quotes is an escape), then the machine-wide (`HKLM`) `NODE_OPTIONS`
  read through `IRuntimeHostService.MachineEnvironmentValue`, then the server's own `NODE_OPTIONS`, each after
  a space. Values are compared whole, never split into options: a machine value the server's own value already
  repeats is dropped. The user-level (`HKCU`) value stays blanked like every user variable.
- `PIA_SANDBOX_ROOTS` = every granted root, joined with `|` (illegal in Windows paths; the spike's `;`
  breaks on a folder name with a semicolon): the user's rw and ro folders, the program folder, the install
  root, `<state>`, `<support>`.
- `PATH=<root>;<user PATH, else the registry PATH>`. Without it the package's `.bin` shim finds
  `Program Files\nodejs` first, which the container cannot list.
- `Npx` and `NpmExec` only: `npm_config_cache=<state>\npm-cache`, `npm_config_prefix=<state>\npm-prefix`,
  `npm_config_userconfig=<state>\home\.npmrc`, `npm_config_update_notifier=false`, `npm_config_audit=false`.
  With network Deny also `npm_config_prefer_offline=true` and `npm_config_fetch_retries=0` (a warm Deny
  start drops from 70 s to 0.6 s; an empty cache fails in 3.6 s with `ENOTFOUND`).
- Plus the home set. Files: `<support>\node-sandbox.cjs`, the embedded preload. Directories: for the npm
  kinds `<state>\npm-cache` and `<state>\npm-prefix`.
- Toolchain roots: none when readable already, else `[<root>]`. State read-write, support folder read-only.

**The preload.** `Runtimes/pia-node-sandbox.cjs`, an `EmbeddedResource` with `LogicalName`
`Pia.Sandbox.NodePreload` in `Pia.Wpf.csproj` (as `Pia.DefaultFileIgnore` is), read through
`NodeSandboxPreload.Content`. Its full text is in *Appendix A*. It answers `lstat`/`stat` of an ancestor of
a granted root that fails with `EPERM`/`EACCES` with the stats of its own folder, `<support>` (a directory,
in the `bigint` form when asked for it). A `realpath*` call that fails with `EPERM`/`EACCES` resolves the
path by hand: the ancestors count as plain folders, every component below them is `lstat`ed, and a link is
read with `readlink`, spliced in (a relative target against the link's parent) and the walk restarted, at
most 32 times (then `ELOOP`). Any other error from a component is rethrown, a missing leaf gives `ENOENT`,
and the `encoding` option is honoured. A junction inside a granted folder therefore resolves to its target,
so a server that checks paths against its allowed folders after `realpath` sees where a link leads. The
provisioner writes it to `<support>` when it is missing or differs; the folder is granted
read-only per start, so no server can change what the next one loads. The spike's runs loaded the preload
from a read-only grant, so `--preserve-symlinks` is enough for Node to load it from there.

**dnx**:
- Command `<root>\dotnet.exe`, args `exec`, `<root>\sdk\<v>\dotnet.dll`, `dnx` + the user's args, with
  `--yes` inserted right after the package token (the first argument that is not an option) when no
  argument before a `--` is `--yes` or `-y`. What `dnx.cmd` does, without cmd.
- `DOTNET_CLI_TELEMETRY_OPTOUT=1` (otherwise a registry `SecurityException` crash), `DOTNET_CLI_HOME=<state>\home`,
  `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0` (keeps a non-JSON line off stdout), `DOTNET_NOLOGO=1`,
  `DOTNET_GENERATE_ASPNET_CERTIFICATE=0`, `DOTNET_CLI_UI_LANGUAGE=en-US`,
  `NUGET_PACKAGES=<state>\nuget\packages`, `NUGET_HTTP_CACHE_PATH=<state>\nuget\http-cache`,
  `NUGET_SCRATCH=<state>\nuget\scratch`, `NUGET_PLUGINS_CACHE_PATH=<state>\nuget\plugins-cache`,
  `PATH=<root>;…`, plus the home set (its `APPDATA` is where NuGet reads `NuGet.Config`).
- File `<state>\home\AppData\Roaming\NuGet\NuGet.Config`, rewritten every start. Allow:
  ```xml
  <?xml version="1.0" encoding="utf-8"?>
  <configuration>
    <packageSources>
      <clear />
      <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    </packageSources>
  </configuration>
  ```
  Deny: the same with only `<add key="pia-local" value="<state>\nuget\packages" />` (XML-escaped). SDK 10
  contacts the feed on every start and fails under Deny without it (6.4 s).
- Directories: `<state>\nuget\packages`. Toolchain roots as for Node (`Program Files\dotnet` is readable
  already).

**dotnet `<app>.dll`**: command `<root>\dotnet.exe`, args the dll's full path + the rest.
`DOTNET_CLI_TELEMETRY_OPTOUT=1`, `DOTNET_CLI_HOME=<state>\home` (the first-run configurer otherwise tries
`%USERPROFILE%\.dotnet` and dies as an opaque `0xE0434352`), `DOTNET_NOLOGO=1`,
`DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0`, `PATH`, the home set. The program folder is the dll's folder, granted
read-only like today's command folder.

**uvx**: the plan carries `UvPrepareRequest`; the final command exists only after the install.
- Host-side install, uncontained at Pia's IL, by `UvToolRunner`: `<uv.exe> tool install <Package>
  [--with <req>]… [--python <v>]`, built with `ArgumentList` from the validated tokens. Environment: Pia's
  process environment minus every `UV_*` except `UV_NATIVE_TLS`, minus `MXC_FFI_DIR` and `PIA_*`, plus
  `UV_CACHE_DIR=<state>\uv-cache`, `UV_PYTHON_INSTALL_DIR=<state>\python`,
  `UV_PYTHON_CACHE_DIR=<state>\python-cache`, `UV_TOOL_DIR=<state>\tools`, `UV_TOOL_BIN_DIR=<state>\tool-bin`,
  `UV_PYTHON_BIN_DIR=<state>\python-bin`, `UV_NO_BUILD=1` (wheels only, so no package code runs on the host;
  sdist-only packages are refused), `UV_NO_CONFIG=1`, `UV_PYTHON_PREFERENCE=only-managed`,
  `UV_PYTHON_INSTALL_REGISTRY=0`, `UV_PYTHON_INSTALL_BIN=0`, `TEMP=<state>\tmp`, `TMP=<state>\tmp`. Proxy
  variables stay.
- **Spec record** `<state>\pia-uv-spec.json` (package, with, python), written after a successful install.
  The state folder is read-only inside the container, so no server can change it.
- **An installed tool is never re-run through uv**, with network Allow or Deny: when the spec record matches
  and the tool resolves (its entry point and `<tool>\Scripts\python.exe` are present), the start goes straight
  to the sandboxed `python.exe`.
- **Every other case deletes `<state>\tools` and `<state>\tool-bin` before uv runs**, so uv never queries a
  venv that holds code from the index, and a changed spec never launches the old package. With network Deny
  that case fails before anything is deleted ("Network access is off and this package is not installed yet.
  Allow network access for the first start."). While another container of the prefix is running it fails
  the same way with "This server's package is in use by the running server. Stop the server, then try again."
- After exit code 0, `<state>\uv-cache` and `<state>\python-cache` are deleted (owner decision 3; gate
  G-UVCACHE confirms the venv still runs).
- **Entry point**, scoped to the installed tool (measured layout in the spike's `uv\state\pre1\tools`):
  the tool folder is `<state>\tools\<normalized package name>` (PEP 503: lowercase, runs of `-_.` → `-`).
  Its `uv-receipt.toml` lists `entrypoints = [{ name = "…", install-path = "…", from = "…" }, …]`; the entry
  whose `name` equals `CommandName` gives `from`. The dist-info folder in `<tool>\Lib\site-packages` whose
  name before `-<version>.dist-info` normalizes to `from` holds `entry_points.txt`; its `[console_scripts]`
  line `<CommandName> = <module>:<attr>` (a trailing `[extra]` dropped) is the entry point. No receipt
  entry, no dist-info or no line → the start fails. `<module>` and `<attr>` must match
  `^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$`.
- Final command `<tool>\Scripts\python.exe`, args `-c`,
  `import sys, importlib, functools; sys.argv[0] = '<CommandName>'; sys.exit(functools.reduce(getattr, '<attr>'.split('.'), importlib.import_module('<module>'))())`,
  then the server args. Never the trampoline exes. State folder **read-only**, no runtime env (the spike ran
  it with an empty env), no toolchain roots. One benign stderr line: "Failed to find real location of …".
- uv's stdout and stderr lines go to the transport's stderr tail (shown by Test connection and the
  settings row) and to `SensitiveDebug`.

### Environment: blanking and composition

`SandboxEnvironmentComposer` (static, C3) builds `spec.Environment` for **every** sandboxed server, exe
servers included, in this order:

1. Every name from the user-environment names except `Path`, `TEMP`, `TMP` and names starting with
   `OneDrive` (case-insensitive) → `""`. The container's block is rebuilt from the registry, so this is what
   keeps `HKCU\Environment` secrets (two PATs on the build host) out.
2. The plan's variables.
3. The user's own env entries, without `MXC_FFI_DIR`. `PATH` and `NODE_OPTIONS` are skipped here when the
   plan set them: the planner already merged the user's value into them.

The names come from `IRuntimeHostService.UserEnvironmentNames()` when DI provided a host and from
`RuntimeHostService.ReadUserEnvironmentNames()` otherwise (a factory seam replaces it in tests). A missing
`HKCU\Environment` key gives no names; any other failed read refuses the start with
`SandboxRuntimeMessages.UserEnvironmentUnreadable` and a warning naming only the exception type, so no
sandboxed start runs unblanked.

Whether `""` blanks a variable is **gate G-ENV**, answered yes by step A2 with a real container: the test
server with `OS=""` (every machine's registry environment carries `OS`) reads `OS` as empty through
`get_environment`, while the rest still arrives (`ComSpec` set, `get_server_info` reports
`inAppContainer`). So the composer always blanks with `""` on top of the registry-built block the container
inherits, and no build ships a sandboxed start without blanking.

### Builder and factory (C3)

`SandboxLaunchSpecBuilder.Build(request, SandboxRuntimeContext runtime, pathVariable, fileExists,
isReparsePoint, logger)`, with

```csharp
public sealed record SandboxRuntimeContext(
    IRuntimeDetector? Detector,
    IRuntimeHostService? Host,
    bool CanProvision,
    IReadOnlyList<string> UserEnvironmentNames,
    CancellationToken CancellationToken = default);
```

G-ENV answered yes, so there is no environment mode: the composer always blanks with `""`.

1. Parse. A refusal → `Fail(<English refusal text>)`.
2. `Kind None`: today's rules (`SandboxLaunchSpecBuilder.cs:36-65`) on `EffectiveCommand` and
   `EffectiveArgs`, so `cmd /c C:\Tools\server.exe` launches the exe directly. `.cmd`, `.bat`, `.ps1` stay
   refused. Environment through the composer.
3. Runner: no detector, no host or `CanProvision` false →
   `Fail("Pia can't sandbox this server: runtime detection is not available.")`. `Detect` and `Plan` run
   inside a `try`; any exception other than `SandboxStartException` →
   `Fail("Pia could not check this server's runtime.")`, the exception type at Warning, the message through
   `SensitiveDebug`. Never a start. Not Ready → `Fail(<English issue text>)`. Program folder through
   `CommandDirectoryToGrant` (skipped when a policy folder covers it). Spec: plan command and args, composed
   environment, `WorkingDirectoryFor(request.WorkingDirectory, rw, ro, programFolder)` (the state, support
   and toolchain folders are never a fallback; with no folders at all it falls back to System32 as today),
   `GrantedCommandDirectory = programFolder`, the toolchain roots, the state folder, `RuntimeStateReadOnly`,
   `SupportDirectory`, `RunnerKind`, `Preparation`.

The old `Build(request, pathVariable, fileExists, isReparsePoint)` overload stays `internal` for the
builder tests and passes a context with no detector and no names; production only reaches `Build` through
the factory, which always passes names.

`McpTransportFactory` gains optional trailing ctor parameters `IRuntimeDetector? detector`,
`IRuntimeHostService? host`, `IRuntimeStateProvisioner? provisioner` (DI fills them), and the internal
ctor a `Func<IReadOnlyList<string>>? userEnvironmentNames` seam. `SandboxPathGuard.Validate` gets the spec's
effective command for `Kind None` (so `cmd /c C:\Tools\s.exe` judges `C:\Tools`) and no command for a runner
(the detector already judged its folders).

DI (`Bootstrapper.cs:631-640`, singletons): `IRuntimeHostService → RuntimeHostService`,
`IRuntimeRootVerifier → RuntimeRootVerifier`, `IRuntimeDetector → RuntimeDetector`,
`IRuntimeStateStore → RuntimeStateStore`, `UvToolRunner`, `IRuntimeStateProvisioner → RuntimeStateProvisioner`,
`IToolchainAclService → ToolchainAclService`, `ISandboxRuntimeService → SandboxRuntimeService`.
Every one arrives as an optional parameter, so a missing registration would still build and pass
`ValidateOnBuild`. Two tests close that: `DiRegistrationTests` also enumerates the `Runtimes` namespace's
interfaces, and a new `BootstrapperGraphValidationTests` case asserts that every constructor parameter of
`McpTransportFactory` and `PluginService` whose type lives in `Pia.Services.Plugins.Sandbox.Runtimes` has a
registered descriptor (descriptor level, so nothing touches the profile or the database). U1 adds
`SettingsViewModel` to that case, whose new `ISandboxRuntimeService` and `IClipboardService` parameters have
the same silent-null shape.

### Off the UI thread

`McpTransportFactory.Create` is synchronous and today runs on the dispatcher from three places: Test
connection (`ProbeLocalMcpAsync` → `McpServerProbe`), Save (`ActivateMcpPluginAsync` →
`McpPluginToolHandler.InitializeAsync`) and startup (`App.xaml.cs:291` fires
`InitializePersistedPluginsAsync` on the dispatcher). Detection adds ACL reads and entry counts there, and
`ConnectAsync` runs `CreateOwnedFolders`, the provisioner and the uncapped toolchain walk (0.25–2.0 s on a
16k tree) before its first real await.

- `McpServerProbe` and `McpPluginToolHandler` call `await Task.Run(() => factory.Create(request), token)`.
  Both already catch every exception from that point.
- `MxcClientTransport.ConnectAsync` runs the state-folder lock, `CreateOwnedFolders`, `PrepareAsync`,
  `Verify` and `LaunchAsync` inside `await Task.Run(…, cancellationToken).ConfigureAwait(false)`.
- The editor detects through `ISandboxRuntimeService.DetectAsync`, which runs `Detect` on the thread pool.
- `PluginService` runs the orphan cleanup through `Task.Run`.

### Concurrency

- `RuntimeStateLocks` (A1) serializes everything that touches one state folder: a Test connection and a
  start never prepare one prefix at once, a uv cache prune never runs under another install, and the startup
  orphan cleanup never deletes a folder a starting probe is filling. The transport holds the lock until
  `LaunchAsync` has returned, so between the lock and the container's `SandboxContainerLease` there is no gap.
- `MxcMcpLauncher` passes `MxcContainer.SpawnAsync` and `process.Dispose()` in `ShutdownAsync` through one
  process-wide gate, `MxcAclGate` (an internal static `SemaphoreSlim(1)` with `EnterAsync` returning an
  `IDisposable`). Each rewrites ACLs below every root, and two containers sharing a toolchain or state root at
  the same moment was never measured. The cost is serial starts: 0.2–2 s each. ACE sweeps run under the same
  gate, and a sweep never strips the SID of a container that holds a live `SandboxContainerLease`.
- The probe and the running server share the npm cache and rely on npm's own locking (a known limit).

### Runtime state folder lifecycle

`PiaPaths.McpRuntimeStateDirectory(prefix)`, a sibling of the server's own `McpSandboxRoot\<prefix>`:
never inside it, so the server's file tools and default working directory never see `node_modules`, and
the nested-read-write rule is untouched. Slugs are `[a-z0-9_]`, so no server's folder can be `x.runtime`.
A definition whose tool prefix holds a character outside `[A-Za-z0-9_]` is unreadable, so a prefix is always
one plain path segment.

- **Created** by `RuntimeStateProvisioner` in `ConnectAsync`, before `Verify`: the support folder and its
  files (written only when missing or different, through a temp file and a replace), the plan's directories
  (with the `AC\Temp` of this container name), the plan's state files, the grant record, then the uv
  install. A spec without a runner kind gets only its grant record.
- **Reparse-safe.** Before writing a state file, every path component between `<state>` and the file is
  checked; a reparse point there is deleted as a link (never followed) and the folder recreated. Every walk
  of a state folder uses `EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip =
  FileAttributes.ReparsePoint }`. Deletion removes reparse points first, without descending into them.
- **Granted** per start: read-write, or read-only for `Uvx`. Verified shallowly, with its own clause.
- **Cleared before every uv install**, which runs outside the sandbox and would read and run what a container
  planted: unless the grant records of both container names (kept outside the folder) show only read-only grants,
  a non-empty state folder is deleted first (a record without its access counts as read-write, no record at all
  deletes too), and the start is refused while a container that could write the folder is running. Once the
  folder is cleared, the other container name's read-write record is rewritten as read-only with its roots
  kept: the folder that container could write is gone, but its roots still need sweeping.
- **Shared** by the probe and the running server: same prefix, different container names and `AC\Temp`.
- **Shown** in the editor as "managed by Pia", not editable, never saved in ConfigJson.
- **Kept** when the sandbox is switched off or the command stops being a runner.
- **Deleted** with the server (`RemoveLocalMcpAsync`), on a prefix-changing save (the old prefix), and at
  startup for orphans (a probe of a server that was never saved), always through `TryDelete`, which skips a
  prefix whose state-folder lock is held or whose container holds a `SandboxContainerLease`. A skipped folder
  belongs to no saved server any more, so the next startup's orphan pass deletes it.

`RuntimeStateStore`:
- `WriteGrantRecord(containerName, roots, stateReadOnly)` writes `<McpSandboxGrantsDirectory>\<containerName>.json`,
  `{"container": "...", "roots": ["..."], "writtenUtc": "...", "stateReadOnly": false}`. The provisioner writes it
  before every sandboxed launch, exe servers included, with the server's read-write and read-only policy folders,
  the toolchain roots, the program folder, the state folder and the support folder. A spec without a state folder
  records the state folder as not writable (`stateReadOnly: true`). The new roots are merged with the previous
  record's roots that still exist on disk, so a grant that never got swept stays in the record.
  `StateGrantedReadOnly(containerName)` reads the access back (null without a record).
- `SweepRoots(prefix)`: the state folder when it exists, plus the roots of both records
  (`pia-mcp-<prefix>` and `-probe`).
- `TryDelete(prefix, beforeDelete)`: `RuntimeStateLocks.TryAcquire` on the state folder, then
  `SandboxContainerLease.IsActive` for both container names; either busy → false, nothing touched. Otherwise
  `beforeDelete` (the orphan pass sweeps ACEs and deletes profiles there), then reparse points, then the state
  folder recursively (on `UnauthorizedAccessException` or `IOException`, clear `ReadOnly` on every file found
  without following links and retry once; .NET handles long paths), then both records. Best effort: logs
  counts and the exception type, never throws.
- `OrphanPrefixes(known)`: `*.runtime` folders and record files whose prefix is not in `known`. The prefix of
  a container name is the name without `pia-mcp-` and an optional `-probe`.

`PluginService` (D2), with an optional trailing ctor parameter `IRuntimeStateStore? runtimeStateStore`:
- `string PreviewLocalMcpToolPrefix(Guid? pluginId, string name)` on `IPluginService`:
  `UniqueToolPrefix(<definition with ToolPrefix = DeriveToolPrefix(name)>, pluginId ?? Guid.Empty)`, the exact
  prefix a save would store.
- `SweepSandboxLeaksAsync` adds `SweepRoots(prefix)` for every sandboxed definition. On a same-prefix save
  whose new definition is still sandboxed they count as still granted; otherwise they are swept. It hands
  `PolicyRoots` the parser's effective command, so the folder of a `cmd /c <exe>` server is swept too.
- `RemoveLocalMcpAsync`: after the sweep and `DeleteServerProfiles`, `await Task.Run(() => store.TryDelete(prefix))`.
- `CleanUpReplacedContainersAsync`, prefix changed: after the sweep and `DeleteServerProfiles(old)`,
  `store.TryDelete(old)`.
- `InitializePersistedPluginsAsync`, after activation, through `Task.Run`: for each orphan prefix not
  claimed by a saved definition meanwhile, `store.TryDelete(orphan, beforeDelete: sweep its SweepRoots with
  both container names, then delete both AppContainer profiles)`. The editor can run a Test connection on an
  unsaved server while activation is still running; `TryDelete` leaves that one alone.

The editor (U1) builds its definition with `ToolPrefix = PreviewLocalMcpToolPrefix(EditingServerId, name)`,
and `DefaultFolderFor` uses the same prefix. A blank preview (an unconfigured NSubstitute returns `""`)
falls back to `DeriveToolPrefix(name)`, so existing editor tests keep their `files` prefix.

### Guard and verifier (E1, E2)

- `SandboxPathGuard.Check`: a policy path strictly under `McpSandboxRoot` whose first segment ends with
  `.runtime` → new kind `ManagedRuntimeFolder`, "Pia manages this folder for a server's runtime. Choose a
  different folder." Today such a path passes (the root exemption at `SandboxPathGuard.cs:210,216`).
  `McpSandboxGrantsDirectory` and `McpSandboxSupportDirectory` sit under `LocalDataDirectory`, which
  `SandboxGuardEnvironment.Current().PiaDataRoots` holds, so `FindProblem` already refuses them as Pia data;
  E1 pins that with a test for each. A read-write policy folder that is, or sits above, a registry `PATH`
  entry (machine or user, expanded) or a fixed uv folder is refused too (`HostProgramFolder`), whether or not
  a program is there yet; read-only stays allowed. The guard asks for those folders only when it judges a
  read-write folder, caches them for 20 s, and never probes a UNC or network-drive entry for uv.exe links.
- `RuntimeRootVerifier : IRuntimeRootVerifier`, against `SandboxGuardEnvironment.Current()`: a path with
  `;` → `ToolchainPathUnsupported`; the profile root or above, a wide folder itself (Desktop, Documents,
  Downloads, OneDrive roots), a Pia data root outside `McpSandboxRoot` → `ToolchainTooWide` /
  `ProgramFolderTooWide`; more than `MaxEntriesPerRoot` (20,000) entries → `ToolchainTooLarge` /
  `ProgramFolderTooLarge`. Under `%WINDIR%` or `Program Files*` → null (never granted; the detector handles
  it). Measured: a zip Node is about 2,000 entries, `pi-node\current` 16,350, a user .NET SDK 6,390.
- `SandboxGrantVerifier.Verify`: `spec.Toolchain` walked recursively **without** the 5,000 cap (that cap
  checked about 30 % of a 16k tree), skipping reparse points; a positive result is cached per root for 10
  minutes in-process (the walk took 0.25–0.54 s warm, 2.0 s cold). New clause: "{n} file(s) in the runtime
  folder belong to an administrator or another account", advice "Install the runtime for your user only; the
  server's settings show how." `RuntimeStateDirectory` and `SupportDirectory` are checked shallowly with
  their own clause: "{n} file(s) in Pia's own folders for this server can't be shared", advice "Remove the
  server and add it again; Pia then starts with fresh folders."
- `MxcMcpLauncher.BuildRequest`: `spec.Toolchain` and `SupportDirectory` added to `ReadonlyPaths`;
  `RuntimeStateDirectory` to `ReadonlyPaths` when `RuntimeStateReadOnly`, else `ReadwritePaths`;
  `InheritDefaultEnvironment` is always true. The Information line gains `runtime={Kind}`
  and the counts include the new paths.

### Start errors and messages

`MxcSpawnErrors.Describe(MxcException)` (static, E3) replaces the raw text in `MxcMcpLauncher.SpawnAsync`.
MXC's message names the path (`…requires write-DAC permission on 'C:\Program Files\nodejs'…`) and it reaches
the release log through `LogError(ex, …)` in `McpPluginToolHandler.InitializeAsync`.
- `Code == BackendUnavailable` and the message contains `(os error 2)` or `(os error 3)`: "The sandbox could
  not start the server: a folder it needs is missing. Open the server's settings and detect runtimes again."
- `BackendUnavailable` and `WRITE_DAC` or `(os error 5)`: "The sandbox could not start the server: Pia may not
  change the permissions of a folder it needs, because it belongs to an administrator."
- Otherwise: "The sandbox could not start the server ({Code})." plus `Remediation` when MXC gives one.
- The raw message goes to `SensitiveDebug` only. The thrown `SandboxStartException` has no inner exception.

English service texts (static `SandboxRuntimeMessages`, C3; never with a path):
- `NonGoalLauncher`: "Pia can't sandbox servers started with {0}. npx, npm exec, uvx and dnx work; otherwise
  point the command at the server program itself."
- `ScriptFile`: "Pia can't sandbox this server: its command is a .cmd, .bat or .ps1 script. npx, npm exec,
  uvx and dnx work; otherwise point the command at the server program itself."
- `CmdNotSystem`: "Pia can't sandbox this server: 'cmd' must be the Windows command processor."
- `CmdWithoutC`: "Pia can't sandbox this server: only 'cmd /c' followed by a program can be unwrapped."
- `CmdMetacharacters`: "Pia can't sandbox this server: its cmd /c line uses cmd features (& | < > ^ % or
  quotes) a sandbox can't reproduce. Put the program in the command field and its arguments one per line."
- `UnsupportedForm`, by subject: npm "Pia can sandbox npm only as npx or npm exec."; uv "Pia can sandbox uv
  only as uvx or uv tool run, with a command name it can read."; dotnet "Pia can sandbox dotnet only as dnx,
  dotnet dnx or dotnet <app>.dll."; node "Pia can sandbox node only as node <script>."
- `UvxOption`: "Pia can't sandbox this uvx call: the option {0} is not supported. Supported are --from,
  --with and --python."
- `UvxValue`: "Pia can't sandbox this uvx call: packages must be names from the package index, with an
  optional version, and --python takes a version number."
- `RelativePath`: "Pia can't sandbox this server: give the full path of the script or .dll, or set a
  working directory."
- `NodeNotFound`: "Node.js was not found. Install it for your user only (the server's settings show how), then
  start the server again."
- `NodeAdminOwned` / `DotnetAdminOwned`: "The only {Node.js|.NET} found is installed for all users, and
  sandboxed apps can't read its folder. Run Pia as administrator once to allow it, or install it for your user
  only (the server's settings show how)."
- `DotnetSdkMissing`: "dnx needs a .NET 10 SDK, and none was found. Install one for your user only (the
  server's settings show how)."
- `DotnetRuntimeMissing`: "The .NET runtime this program needs was not found. Install it for your user only
  (the server's settings show how)."
- `UvNotFound`: "uv was not found. Install it for your user only (the server's settings show how)."
- `ToolchainTooLarge`: "The runtime's folder holds too many files to share with a sandbox. Install a separate
  copy for your user only (the server's settings show how)."
- `ToolchainTooWide`: "The runtime sits directly in a folder Pia won't share (your profile, Desktop,
  Documents or Downloads). Install it into a folder of its own."
- `ToolchainPathUnsupported`: "The runtime's folder path contains a semicolon, which a sandbox can't use."
- `ProgramFolderTooWide`: "Put the program in its own folder: Pia would have to share all of the folder
  around it." · `ProgramFolderTooLarge`: "The program's folder holds too many files to share with a
  sandboxed server. Put the program in a smaller folder."
- `ScriptMissing` / `DllMissing`: "The script was not found." / "The .dll was not found."
- Builder: "Pia can't sandbox this server: runtime detection is not available." · "Pia could not check this
  server's runtime." · for a plain program that is not an `.exe`: "Pia can't sandbox this server: its command is
  not a program (.exe). npx, npm exec, uvx and dnx work; otherwise point the command at the server program itself."
- Provisioner (C2): "uv could not install the package. Only packages published as wheels are supported; the
  server's error output has the details." · "Network access is off and this package is not installed yet.
  Allow network access for the first start." · "The installed package does not provide the command this
  server names." · "This server's package is in use by the running server. Stop the server, then try again."
  · "Pia could not run uv." (a `Win32Exception` from the process start) · "Pia could not prepare the
  server's runtime folder." (any `IOException` or `UnauthorizedAccessException`). Each with the exception
  type at Warning and the message through `SensitiveDebug`.

### Editor (U0 strings, U1 view model, U2 view)

Behaviour:
- `CanEnableSandbox` = the parse has no refusal and MXC is not `Unsupported`. `SandboxDisabledReason` shows
  the refusal's key (formatted with its subject where it takes one), then today's MXC reasons. The toggle is
  enabled for every runner, whatever detection says.
- `IsRunnerCommand` = a runner kind without refusal. The runtime panel shows when the sandbox is on and the
  command is a runner.
- Detection runs debounced (`RuntimeDetectionDebounceMs`, internal, 250 ms) off the UI thread with its own
  `CancellationTokenSource`, like `UpdateSandboxGuardAfterTyping`, but only while `EditSandboxEnabled &&
  IsRunnerCommand`: on changes to the command, the arguments (new `OnEditArgumentsChanged`) and the working
  directory (new `OnEditWorkingDirectoryChanged`), when the toggle goes on, and at once on the Detect runtimes
  and Detect again buttons. A name change recomputes only `RuntimeStateFolderText` from the preview prefix.
  The token reaches `Detect`, which checks it between candidates.
- **Network default**, independent of `_sandboxPristine` (which a folder add or remove clears):
  `ApplyNetworkDefault()` runs on command, argument and working-directory changes whenever
  `EditingServerId is null && !_networkTouched`, and inside `_applyingSandboxDefaults` sets
  `EditSandboxAllowNetwork` to Allow for a package runner only (npx, npm exec, uvx, dnx, or `cmd /c` wrapping
  one). `node <script>` and `dotnet <dll>` keep Deny and get no deny hint: they download nothing at start.
  New `OnEditSandboxAllowNetworkChanged` sets `_networkTouched` outside `_applyingSandboxDefaults`;
  `ResetSandboxEditor` clears it. A user who adds a folder first and then pastes `npx …` still gets Allow.
  Tests 110 and 114 of `McpServersSandboxEditorTests` (Deny for an exe) stay green.
- The editor's guard (`GuardMessageFor` and the debounced `UpdateSandboxGuardAfterTyping`) passes
  `SandboxPathGuard.Validate` the parse's effective command for `Kind None` and no command for a runner, as
  the factory does, so `cmd /c C:\Tools\s.exe` is judged the same way in the editor and at start. The detail
  pane's `SelectedSandboxAlsoReadable` uses the effective command too.
- Step 2 shows when the outcome is `NeedsAdminFix` and `CanGrantMachineWide`. Step 3 (the guide) shows for
  `NotFound`, for the toolchain `Refused` issues, and for `NeedsAdminFix` when not elevated.
- Copy puts the command on the clipboard through `IClipboardService` and shows a snackbar.
- An always-visible hint in the sandbox panel says user environment variables are hidden and how to pass one.
- The detail pane gains one line for a saved sandboxed runner: its kind and its managed folder, from the
  parse alone (no detection in `RefreshDetail`).
- New dependencies are optional trailing ctor parameters on `McpServersSettingsViewModel` and on
  `SettingsViewModel` (`ISandboxRuntimeService? sandboxRuntime = null, IClipboardService? clipboard = null`),
  so the positional constructions in `McpServersSandboxEditorTests` (68-75),
  `McpServersSettingsViewModelTests` (75) and `SettingsPolicyReloadTests` keep compiling.
- `SandboxCommandRules` and `SandboxCommandRulesTests` are deleted; the parser tests cover their cases.

View-model surface (exact names, the view binds to them):
`IsRunnerCommand`, `ShowRuntimePanel`, `IsDetectingRuntime`, `RuntimeOutcome? RuntimeOutcomeValue`,
`RuntimeSummary`, `RuntimeReason`, `RuntimeFolders`, `ShowRuntimeFolders`, `RuntimeReadableAlready`,
`RuntimeStateFolderText`, `ShowRuntimeFix`, `RuntimeFixText`, `ShowRuntimeGuide`, `RuntimeGuideText`,
`RuntimeGuideCommand`, `RuntimeGuideAltCommand`, `ShowRuntimeGuideAlt`, `ShowRunnerDenyHint`,
`SelectedSandboxRuntime`; commands `DetectRuntimesCommand`, `GrantRuntimeReadCommand`,
`CopyRuntimeGuideCommand` (parameter `"primary"` or `"alt"`).

Controls, all inside the sandbox-enabled panel of `McpServersView.xaml`, the runtime block placed after the
download error (line 496) and before "Folders it can read and write"; the deny hint after the network
warning (line 645); the environment hint after the guard message (line 662). Interactive controls with their
AutomationId:

| Control | AutomationId | Binding |
|---|---|---|
| Button "Detect runtimes" | `McpServers_Sandbox_RuntimeDetect` | `DetectRuntimesCommand` |
| Button "Allow sandboxed apps to read this folder" | `McpServers_Sandbox_RuntimeFix` | `GrantRuntimeReadCommand`, visible `ShowRuntimeFix` |
| Read-only TextBox, guide command | `McpServers_Sandbox_RuntimeGuideCommand` | `RuntimeGuideCommand` |
| Button "Copy" | `McpServers_Sandbox_RuntimeGuideCopy` | `CopyRuntimeGuideCommand`, `"primary"` |
| Read-only TextBox, alternative command | `McpServers_Sandbox_RuntimeGuideAltCommand` | `RuntimeGuideAltCommand`, visible `ShowRuntimeGuideAlt` |
| Button "Copy" | `McpServers_Sandbox_RuntimeGuideCopyAlt` | `CopyRuntimeGuideCommand`, `"alt"` |
| Button "Detect again" | `McpServers_Sandbox_RuntimeDetectAgain` | `DetectRuntimesCommand` |

Read-only texts, also with ids for UI scripts: `McpServers_Sandbox_RuntimeResult` (`RuntimeSummary`),
`McpServers_Sandbox_RuntimeReason`, `McpServers_Sandbox_RuntimeFolders` (one path per line, a plain
TextBlock: no new ItemsControl, so `McpServersPeerRefreshTests` keeps its three lists),
`McpServers_Sandbox_RuntimeReadableAlready`, `McpServers_Sandbox_RuntimeStateFolder`,
`McpServers_Sandbox_RuntimeFixText`, `McpServers_Sandbox_RuntimeGuideText`, `McpServers_Sandbox_RunnerDenyHint`,
`McpServers_Sandbox_EnvHint`, `McpServers_Detail_SandboxRuntime`. A `ProgressRing` shows while
`IsDetectingRuntime`. `ViewAutomationIdTests` row 65 goes from `(McpServersView, 31, 4, …)` to
`(McpServersView, 38, 4, …)`.

Localization: every key, with its English original, is in *Appendix C*. All of them go into
`ViewStrings.resx`, `.de.resx` and `.fr.resx` together (U0). German and French are written as carefully as
the existing `McpServers_Sandbox_*` entries; commands are never translated. `McpServers_Sandbox_Reason_Shim`
is reworded and now serves `NonGoalLauncher` and `ScriptFile`.

`ViewModels/Models/SandboxRuntimeKeys.cs` (static) maps `RunnerRefusal` (with the `UnsupportedForm` subject),
`RuntimeIssue` and `RunnerKind` to keys. `LocalizationTests`' literal-key regexes cannot see those, so it gets
`EveryRunnerRefusalKeyResolvesInAllThreeLocales`, `EveryRuntimeIssueKeyResolvesInAllThreeLocales` and
`EveryRunnerKindKeyResolvesInAllThreeLocales`, shaped like `EveryRunStateLabelKeyResolvesInAllThreeLocales`,
and a placeholder-parity theory `ASandboxRuntimeKeyCarriesTheSamePlaceholdersInEveryLocale` shaped like
`ADiagnosticsKeyCarriesTheSamePlaceholdersInEveryLocale`, with one row per key that takes placeholders
(Appendix C marks them). A de or fr text that drops `{2}` from `_FixConfirmMessage` would hide the removal
command, the only undo of step 2.

### Step 2: the elevated fix (G1)

`ToolchainAclService`:
- `HasAllAppPackagesAce(root)`: any explicit or inherited ACE for `S-1-15-2-1` on the folder.
- `GrantAllAppPackagesRead(root)`: `DirectoryInfo.GetAccessControl()`, `AddAccessRule(new FileSystemAccessRule(
  new SecurityIdentifier("S-1-15-2-1"), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit |
  InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow))`, `SetAccessControl`.
  Windows propagates it to the tree (spike: 16,350 entries in 1.4 s, DACL stayed protected).

`SandboxRuntimeService.PreviewMachineWideGrant(root)` returns the exact commands
`icacls "<root>" /grant *S-1-15-2-1:(OI)(CI)(RX)` and `icacls "<root>" /remove:g *S-1-15-2-1`, and whether an
ACE exists already. If one exists, Pia refuses: the new grant would merge into it and a later removal would
take the old access away too. `GrantMachineWideReadAsync` runs the grant through `Task.Run`, only when
`CanGrantMachineWide` (`IElevationService.IsElevated`), never for a folder under `%WINDIR%`, and returns the
exception message on failure. The root is the detector's install root, the folder holding the executable.

The editor's flow: preview → refuse with `_FixExistingAce` if an ACE exists → confirmation dialog
(`IDialogService.ShowConfirmationDialogAsync`) naming the folder, saying it changes the folder for all
sandboxed apps on the PC, and showing both commands → grant → snackbar `_FixDone` → detect again (now
"readable already"). Pia offers no undo. An MSI repair may restore the installer's ACL; the next start then
fails detection with the same reason and offers step 2 or 3 again.

### Step 3: the in-app guides (G1 commands, U0 texts)

`RuntimeInstallGuides` (static), commands verbatim from the guide research, each verified at medium IL
without admin:
- Node: `winget install --id OpenJS.NodeJS.LTS --exact --scope user --accept-package-agreements --accept-source-agreements`
  (`--scope user` is required: without it winget picks the machine MSI and UAC). Alternative, for PowerShell,
  without winget, no `PATH` change (Pia looks in `%LOCALAPPDATA%\Programs\nodejs`):
  ```
  $ProgressPreference='SilentlyContinue';$a=@{ARM64='arm64'}[$env:PROCESSOR_ARCHITECTURE];if(!$a){$a='x64'};$v=((irm https://nodejs.org/dist/index.json)|?{$_.lts})[0].version;$d="$env:LOCALAPPDATA\Programs\nodejs";$z="$env:TEMP\node-$v.zip";iwr "https://nodejs.org/dist/$v/node-$v-win-$a.zip" -OutFile $z -UseBasicParsing;New-Item $d -ItemType Directory -Force|Out-Null;tar -xf $z -C $d --strip-components=1
  ```
- uv: `powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"` (installs to
  `%USERPROFILE%\.local\bin` and prepends it to the user `Path`). Alternative: `winget install --id astral-sh.uv -e`.
- .NET 10 SDK: `powershell -ExecutionPolicy ByPass -c "& ([scriptblock]::Create((irm https://dot.net/v1/dotnet-install.ps1))) -Channel 10.0"`
  (installs to `%LOCALAPPDATA%\Microsoft\dotnet`, no `PATH` change; winget has no user-scope SDK).
  No alternative. This is the guide for dnx.
- .NET runtime, for `dotnet <app>.dll`: built from the dll's runtimeconfig, the same `dotnet-install.ps1` call
  with `-Runtime dotnet|aspnetcore|windowsdesktop -Channel <major>.0` for the framework it names
  (`Microsoft.NETCore.App`, `Microsoft.AspNetCore.App`, `Microsoft.WindowsDesktop.App`). Without a readable
  runtimeconfig it falls back to the .NET 10 SDK command. No alternative.

Detect again needs no restart: detection reads the registry `PATH` and the fixed folders each time.

### Logging

- Information: `Runtime for {Container}: {Kind} {Outcome}, {Roots} toolchain root(s), readable already {Bool}`;
  `uv install for {Container}: exit {Code} in {Ms} ms`; `Blanked {Count} user environment variable(s) for
  {Container}`; the launcher's existing line with `runtime={Kind}`; `Runtime state for prefix {Prefix}
  deleted: {Entries} entries` (the prefix is the slug, logged today at the same level); step 2:
  `Granted all-app-packages read on a runtime folder in {Ms} ms`. Versions are fine at Information.
- `SensitiveDebug` only: toolchain and candidate paths, the state and support folder paths, the program
  folder, the package spec, uv's output lines, the blanked names, MXC's raw exception text, the step-2
  folder, every caught exception message.
- Warning: failures with the exception type only, then a `SensitiveDebug` line with the message.
- Never: an environment value.

## Build slices

The sections above name the step that builds each part. Single owners: `MxcTestHost.cs` and
`Pia.TestMcpServer/FileTools.cs` (R0); `PiaPaths.cs`, `SandboxLaunchSpec.cs`, `CommandLine.cs`,
`SandboxContainerLease.cs`, `RuntimeStateLocks.cs`, `FakeRuntimeHost.cs`, `PathFreeAssert.cs` (R1; R3 touches
one comment in `PiaPaths.cs` after R1 landed); the guard, verifier and launcher (R2); `MxcClientTransport.cs`
(R6); `SandboxLaunchSpecBuilder.cs`, `McpTransportFactory.cs`, `McpServerProbe.cs`, `McpPluginToolHandler.cs`,
`Bootstrapper.cs` and the two DI architecture tests (R8; R11 adds one line to the descriptor case after R8
landed); `PluginService.cs` and `IPluginService.cs` (R9); the three resx files and `LocalizationTests.cs`
(R10); the two editor view models (R11); `McpServersView.xaml` (R12); `Pia.Wpf.Tests.csproj` (R13). Every
slice ticks only its own checklist lines.

- **R0** gate probes → A2, A3. First, beside R1.
- **R1** contract, parser, locks, test infrastructure → A1. First, beside R0.
- **R2** guard rules, verifier, launcher request and serialization, spawn errors → E1, E2, E3. After R1.
- **R3** `RuntimeHostService` and native probes → B1. After R1.
- **R4** `RuntimeDetector` → B2. After R1.
- **R5** `RuntimeLaunchPlanner` and the preload → C1. After R1.
- **R6** provisioner, uv install, state store, transport hook → C2, D1. After R1 and the G-LINK and
  G-UVCACHE answers (R0).
- **R7** step-2 service, editor facade, guides → G1. After R1.
- **R9** `PluginService` lifecycle and prefix preview → D2. After R1.
- **R10** all new strings and their tests → U0. After R1.
- **R8** builder, factory, blanking, DI, off-thread callers → C3. After R1–R7 and the G-ENV answer.
- **R11** editor view model → U1. After R7, R8, R9, R10.
- **R12** editor view → U2. After R11.
- **R13** real-container and live tests → T1, T2. After R8.
- **R14** doc cross-references → W1. After R11.
- **R15** harness and live run → X1, X2. Last.

R2–R7, R9 and R10 touch disjoint files and can run in parallel worktrees; R9 and R10 can also run beside R8;
R13 beside R11, R12 and R14. Parallel worktrees never run their full test gates at the same time (see *Done
means*).

## Done means

Every step, every slice:

1. A rebuild in Debug and in Release (`dotnet build -t:Rebuild -v:n`, then the same with `-c Release`) ends
   with `0 Warning(s)` and `0 Error(s)` on MSBuild's summary line.
2. The built test exe, redirected and never piped, with git signing off for the run (the host's global
   `commit.gpgsign=true` fails `GitToolHandlerRealGitTests` otherwise):
   `GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=commit.gpgsign GIT_CONFIG_VALUE_0=false tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe > test.log 2>&1`
   in bash; in PowerShell set `$env:GIT_CONFIG_COUNT='1'; $env:GIT_CONFIG_KEY_0='commit.gpgsign';
   $env:GIT_CONFIG_VALUE_0='false'` first and redirect the same way. One filter kind per run (`-class` and
   `-namespace` together run 0 tests). The full run with no filter reports `Failed: 0`. A failure in a class
   the slice did not touch is rerun alone before anyone chases it (Defender occasionally fails the
   atomic-write `File.Replace` tests in full runs).
3. Never two full gate runs on the host at once. A second run's container probe can collide with the first,
   and `MxcTestHost.CanSpawn` false turns every container test into `Skipped` with a green run. R0 gives the
   probe a per-process name, which removes the name collision but not the load of two runs.
4. On this host MXC can spawn, so every real-container class a slice adds or changes must report **Passed**,
   not Skipped. A Skipped result counts as a failure unless `MxcTestHost.Reason` explains it.
5. The landing commit ticks the slice's checklist boxes and changes no other line of the checklist.
6. House rules from `CLAUDE.md`: namespaces `Pia.*`, `_camelCase` fields, `[ObservableProperty]` and
   `[RelayCommand]`, comment discipline (one short line at most, no history, no step or gate ids in code),
   privacy-first logging, `PiaPaths` properties for data paths, an AutomationId on every new interactive control.

## Test strategy

Unit tests run in the gate. Real-container tests sit in `[Collection("MxcRuntimeEnvironment")]` with
`[Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = …)]` and skip at
runtime with a reason. Anything that downloads (npm, NuGet, PyPI, CPython) is `[LiveApiFact]` (xunit
`Explicit`, reported as Not Run by default) and uses `Assert.Skip` when its runtime is missing.
`PathFreeAssert.IsPathFree` runs on every `SandboxStartException` the new code produces in tests (R2, R6, R8).

- Gates (R0, real container): `get_environment` without env (`OS` set, `ComSpec` set,
  `PIA_TEST_SESSION_ONLY_<guid>` absent, `inAppContainer` true), then with `OS=""` (empty or absent, the rest
  unchanged). `create_link` for a junction and a symlink to an ungranted temp folder, then a second spawn over
  the same read-write root, then an ACL read of the target by SID. Four outcomes are recorded as they come:
  creation refused; created, the second spawn fails on the link; created, not followed; created and followed.
- `RunnerCommandParser`: table tests for every rule above, including `cmd /c npx -y @x/y`, `cmd /d /s /c "npx
  -y @x/y"`, `["/c", "C:\Users\Jane Doe\mcp\server.exe"]` (existing file, taken whole), `cmd /k npx`, a
  non-System32 `cmd.exe`, `& | < > ^ %` and quotes, `npm exec`/`npm x`/`npm start`, `uv tool run`/`uv run`,
  `uvx --from a b`, `uvx --with`, `uvx --index-url=https://u:t@h/simple` (subject `--index-url`), `uvx -p
  \\host\s\python.exe`, `uvx --from=--index-url=x`, `uvx --with -e .`, `uvx C:\w\x.whl`, `pkg@1.2`,
  `pkg[extra]`, `pkg==1.2`, `dotnet dnx`/`dotnet x.dll`/`dotnet .\bin\x.dll` with and without a working
  directory/`dotnet exec x.dll`/`dotnet run`, `node --require a s.js`, `node -e`, relative script with and
  without a working directory, each non-goal name, `x.bat`, `python`, and `cmd /c C:\t\s.exe`.
  `CommandLine.Split` cases from the MSVCRT rules.
- `RuntimeDetector` with `FakeRuntimeHost`: PATH order wins; admin-owned PF Node then user Node → the user one;
  only admin-owned → `NeedsAdminFix`; a scoop-style `shims\node.exe` without npm skipped; readable PF dotnet →
  no roots; SDK 9 only then user SDK 10 → the user root; SDK `11.0.100-rc.2` beats `10.0.402`; a net10 dll on
  a root with only `shared\Microsoft.NETCore.App\8.0.x` → `DotnetRuntimeMissing`; WindowsApps skipped; Volta
  shim skipped; a WinGet link resolved; the override replaces all sources; a hint tried first then fallback;
  refused root skipped for the next; npm version read; script package root found; missing script →
  `ScriptMissing`; a cancelled token stops between candidates.
- `RuntimeHostService` against real temp folders: a folder given `S-1-15-2-1` RX is readable, one without is
  not, a deny ACE wins; `CanUserWriteDacl` true on a user temp folder; a junction (`cmd /c mklink /J`, no admin
  needed) resolves to its target; when the test runs elevated, a folder owned by SYSTEM (`icacls /setowner
  *S-1-5-18`) with a protected DACL of Users RX, Administrators F and SYSTEM F is not grantable through the
  linked token (runtime-skipped when not elevated).
- `RuntimeLaunchPlanner`: the exact environment per kind and network state, `NODE_OPTIONS` with a path that
  has a space (forward slashes, quoted), user `NODE_OPTIONS` appended, `PATH` merge with and without a user
  `PATH`, `PIA_SANDBOX_ROOTS` separator and members, both `NuGet.Config` bodies, `--yes` insertion and its
  absence when present, `DOTNET_CLI_HOME` for a dll, the `AC\Temp` directory per container name,
  readable-already → no roots, uvx → read-only state and a `UvPrepareRequest`. The preload resource loads and
  splits on `|`; when `node.exe` is on `PATH`, a harness that fails `lstat`/`stat` on ancestors and every
  `realpath` with `EPERM` shows ancestors read as folders while the first root is a file, the `ino` type each
  call asks for in either order, a junction in a granted root resolved to its target, `ENOENT` and `ELOOP`,
  the `encoding` option honoured, and a throwing success callback run once.
- `RuntimeStateProvisioner`, `UvToolRunner`, `UvEntryPointResolver`: support files and state directories
  created before the returned spec, an identical file not rewritten, a planted junction on a state file's path
  replaced and its target untouched, the grant record written, a fake uv process (seam) exit 0 → final python
  command and `-c` text, exit 1 → path-free error and stderr lines in the tail, a `Win32Exception` from the
  start → path-free error without inner exception, a changed spec clears `tools` and `tool-bin`, Deny with and
  without an installed tool, cache dirs pruned only on success; the resolver against temp trees that mirror
  the real tool layout recorded in A3: receipt-scoped match, a dependency declaring the same console script
  ignored, a missing receipt entry, identifier validation.
- `RuntimeStateLocks`: a held lock makes `TryAcquire` return null; disposal frees it; keys ignore case.
- `RuntimeStateStore` (`[Collection("PiaPathsStatic")]`, `RedirectedProfileFixture`): paths routed, record
  round-trip, `SweepRoots`, `TryDelete` with read-only files, a deep tree and a junction to an outside folder
  (the outside file survives), `TryDelete` returning false and touching nothing (and not calling
  `beforeDelete`) while the lock is held or a lease is active, orphan detection.
- `MxcClientTransport`: `ConnectAsync` returns to its caller while a blocking verifier seam still waits (the
  preparation runs off the caller's thread); a second transport on the same state folder waits until the
  first one's `LaunchAsync` returned; the provisioner runs after `CreateOwnedFolders` and before `Verify`; the
  returned spec reaches `Verify` and the launcher.
- `SandboxPathGuard`: `ManagedRuntimeFolder`; the grants and support folders refused as Pia data.
  `RuntimeRootVerifier`: each issue. `SandboxGrantVerifier`: toolchain walked past 5,000 entries, cached
  positive result, state and support folders shallow with their own clause. `MxcMcpLauncher.BuildRequest`:
  toolchain and support read-only, state rw or ro; `MxcAclGate` admits one holder
  at a time. `MxcSpawnErrors`: the two measured messages (German `os error 2` text included) → path-free texts.
- `SandboxLaunchSpecBuilder` and `McpTransportFactory`: every refusal text, not-Ready → `SandboxStartException`
  with the issue text, a throwing detector → the path-free check text and no start, a Ready Node spec end to
  end with a fake detector and host, blanking with user env winning, blanking without a host (through the
  names seam), runners refused when the provisioner is missing, exe path unchanged apart from blanking,
  `cmd /c <exe>` unwrapped, working directory never the state, support or a toolchain folder.
  `McpServerProbe` and `McpPluginToolHandler` return to their caller while a blocking factory seam waits.
  `BootstrapperGraphValidationTests` (with the new descriptor case) and `DiRegistrationTests` (with the
  `Runtimes` namespace) pass.
- `PluginService` with a recording store: remove deletes after sweep and profiles; rename deletes the old
  prefix; same-prefix save deletes nothing; sweep roots include `SweepRoots`; orphans are swept, profiles
  deleted, folders deleted (sweep and profiles inside `beforeDelete`), a re-claimed prefix skipped, a busy
  prefix (`TryDelete` false) left alone with its profiles and ACEs; `PreviewLocalMcpToolPrefix` returns `x_2`
  on a collision and the current prefix for the server itself.
- `ToolchainAclService` on a temp folder: grant adds an inheritable ACE that a child file inherits;
  `HasAllAppPackagesAce` before and after. `SandboxRuntimeService`: preview strings exact, refusal under
  `%WINDIR%`, `CanGrantMachineWide` mirrors elevation, `GuideFor` returns the exact commands.
- `McpServersSettingsViewModel`: refusal reasons per kind; runner → toggle enabled; network Allow for a new
  package runner, Deny for an exe, `node <script>` and `dotnet <dll>`, Allow after a folder was added first,
  kept after the user touched it, never changed for a saved server; `cmd /c npx` becomes a runner through its
  arguments; detection debounced on command,
  args, working directory, not on name, not while the toggle is off; panel state per outcome; step 2 only when
  elevated; confirmation text carries both commands; existing ACE refused; copy hits the clipboard; preview
  prefix used by Test connection and the proposed folder; the guard sees the effective command.
- `LocalizationTests`: the three enum→key tests and the parity theory. `ViewAutomationIdTests`: the new floor.
  `McpServersPeerRefreshTests`: unchanged list.
- Real containers, in the gate, runtime-skipped (R13):
  - `dotnet Pia.TestMcpServer.dll` through the real factory and detector (readable `Program Files\dotnet`):
    tools listed, a recording launcher seam shows no toolchain root, a blanked name reads empty or absent.
    Skipped unless the Program Files .NET 10 is already container-readable.
  - `node <fixture>\server.js` with `TestData/node-mcp/server.js` (*Appendix B*), a dependency-free MCP stdio
    server whose entry check compares `fs.realpathSync(process.argv[1])` with its own path, so it exits
    silently without the preload. The toolchain is a temp copy of `node.exe` with a minimal
    `node_modules\npm\package.json`, reached through a host decorator whose `CandidateOverride` returns that
    folder, so the developer's own Node is never granted. Tools listed proves the preload; the support folder
    holds `node-sandbox.cjs`; no `pia-mcp-*` ACE remains on the temp toolchain after dispose (the test sweeps
    it in `DisposeAsync` regardless). Skipped without any `node.exe` on the host.
- Live, `[LiveApiFact]`, against temp copies or staged toolchains through the same decorator:
  `npx -y @modelcontextprotocol/server-everything` cold, warm, warm under Deny; `dnx Community.Mcp.DotNet@1.*`
  without `--yes` in the user's arguments (proves the inserted `--yes`); `uvx mcp-server-time` under Deny
  after an Allow start.

## Live end-to-end scenarios (WinWright, e2e stack)

Read `docs/ui_automation/ui-automation-playbook.md` first. Stack `https://localhost:18081` only, never 8081.
The harness `tests/ui-scripts/mxc-sandbox-e2e/Invoke-MxcSandboxE2e.ps1` gains (X1):
- `StageToolchain`: the Node LTS zip (as in the guide's fallback) into `<Root>\toolchain\node-user`
  (user-owned); a copy at `<Root>\toolchain\node-admin` given the Node MSI's ACL: owner through
  `icacls <dir> /setowner *S-1-5-18 /T` (the SID form is locale-safe; .NET cannot set a foreign owner without
  `SeRestorePrivilege`), then a protected DACL through .NET (Users RX, Authenticated Users RX, Administrators F,
  SYSTEM F; no `S-1-15-2-1`), its SDDL recorded in `state.json`; a junction `<Root>\toolchain\node-link` to
  `node-user`; with `-Kind Uv` the uv zip from
  `https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip` into
  `<Root>\toolchain\uv`. Everything under `artifacts\`, never `%TEMP%`.
- `Launch -RuntimeRoots <dirs>` writes `PIA_DEBUG_SANDBOX_RUNTIME_ROOTS` into `launch.cmd`.
- `Launch -Elevated`: starts the Debug exe straight from the elevated shell, with its own profile
  `<Root>\profile-elevated` (files an elevated Pia creates are read-only to the de-elevated user later).
- `UndoToolchainGrant`: `icacls <node-admin> /remove:g *S-1-15-2-1`, then compares the SDDL with the recorded
  one (read through .NET, never by parsing localised `icacls` output).
- `Status` and `EndSession` add `<Root>\toolchain`, every `*.runtime` folder and the support folder to the ACE
  roots; `Status` prints each `.runtime` folder's entry count and size and lists the grant records; `Verify`
  snapshots `%LOCALAPPDATA%\npm-cache`, `%APPDATA%\npm`, `%USERPROFILE%\.nuget\packages`, `%LOCALAPPDATA%\uv`,
  `%APPDATA%\uv` (other tools on the host write `npm-cache` too, so a change there needs a look, not an
  automatic fail).

Scenarios. Verdicts from files, ACLs and log lines, never from the model's wording:

1. **npx on a user-owned Node.** `-RuntimeRoots <node-user>`. New server, command `cmd`, args `/c`, `npx`,
   `-y`, `@modelcontextprotocol/server-everything`. The toggle is on, network Allow with its warning, the panel
   shows the Node and npm versions, the read-only folder and the managed folder. Test connection lists tools;
   record its duration cold (must stay well under the 90 s probe timeout). Save: `Running`. The window stays
   responsive during both (a UIA query answers while the start runs).
2. **Containment.** `<slug>.runtime\npm-cache` holds the package; while running, `Status` shows `pia-mcp-*`
   ACEs on the toolchain, the state folder and the support folder; after `EndSession`, none. Record the state
   folder's entry count and the start time with it.
3. **Hard kill.** `Stop -Kill`, `Launch`: the sweep line counts the toolchain, state and support roots,
   `Status` clean.
4. **Deny.** Network off on the saved server: the deny hint shows, a warm start works. Remove it and add it
   again under Deny: `Not running`, with npm's `ENOTFOUND` in the reason.
5. **Not found and the guide.** `-RuntimeRoots <empty dir>`: Node not found, the guide text, both commands;
   Copy puts the command on the clipboard. Copy the staged Node into that folder, press Detect again: Ready.
6. **Admin-owned, not elevated.** `-RuntimeRoots <node-admin>`: no fix button, the guide shows; Save →
   `Not running` with the admin-owned reason, no uncontained start.
7. **Step 2, elevated.** `Launch -Elevated -RuntimeRoots <node-admin>`: the fix button and its text; the
   confirmation shows both `icacls` lines; confirm → "readable already"; Test connection works and the
   launcher's read-only count has no toolchain root. `UndoToolchainGrant` restores the exact SDDL. Run last.
8. **dnx.** `dnx Community.Mcp.DotNet@1.*` (no `--yes`; Pia inserts it): readable already, tools listed.
9. **uvx.** `-RuntimeRoots <toolchain\uv>`, `uvx mcp-server-time`: the first start installs; the state folder
   gets a read-only ACE only; tools listed; a warm Deny start works. Change the server to
   `uvx --with tzdata mcp-server-time` and save: `tools` is rebuilt, tools listed.
10. **Environment blanking.** The test server sandboxed: `get_environment` for the host's `HKCU\Environment`
    names reads empty or absent; uncontained, the same names read `set` (length only).
11. **cmd metacharacters.** Args `/c`, `npx -y x & calc`: the toggle is disabled with the cmd reason.
12. **Prefix.** A second server with the same name: Test connection creates only `<name>_2.runtime`.
13. **Rename and remove.** A rename deletes the old `.runtime`; remove deletes the `.runtime`, both profiles and
    both grant records; no ACE left.
14. **The originating server.** `cmd /c npx -y @tiberriver256/mcp-server-azure-devops` with env
    `AZURE_DEVOPS_ORG_URL`, `AZURE_DEVOPS_AUTH_METHOD=pat`, `AZURE_DEVOPS_PAT` (the owner's token, never logged):
    Test connection lists tools. Skipped with a note when no token is given.
15. **A link-resolved install.** `-RuntimeRoots <node-link>`: the panel names `node-user` as the read-only
    folder; while running, `Status` shows the ACEs on `node-user`.

Results go into *Live verification* below, the way the 2026-10-17 plan records its run.

## Known limits

- Node child processes with piped stdio hang at 100 % CPU (libuv's global pipe names are denied);
  `stdio: 'ignore'` and `NUL` fail with `EPERM`. Servers that shell out to git or python with captured output
  fail. Packages whose install scripts capture output probably hang too (not measured).
- uvx: wheels only; sdist-only and git sources are refused by `UV_NO_BUILD=1`, and paths, URLs and archive
  file names (local wheels and sdists) by the parser. Each server carries its own CPython (about 62 MB, 2,269
  files). `uv run` is refused. Only `--from`, `--with` and `--python` (a version) are accepted. An installed
  tool is never upgraded: changing the pinned version or the spec is the only way to update it.
- uv: a `uv.exe` in a folder this server can write, or under `McpSandboxRoot`, is skipped at start, and the
  path guard refuses a read-write folder that is, or sits above, a registry `PATH` entry or a fixed uv folder.
  A `PATH` entry added after a server with read-write access to its folder was saved is caught at that
  server's next start, since the factory runs the guard on every start; until then a running server can still
  plant a program there. A uv.exe link on a network drive is not followed by the guard.
- The user's `.npmrc`, `uv.toml` and `NuGet.Config` do not apply, so private registries, registry auth and
  proxy settings from there do not reach sandboxed servers (proxy environment variables do reach the uv
  install). Cached CLI logins (`az login`) do not reach them either; use a token in the server's env.
- Blanking hides every `HKCU\Environment` variable outside the allowlist, also ones a server needs:
  `HTTPS_PROXY`, `HTTP_PROXY`, `NO_PROXY`, `NODE_EXTRA_CA_CERTS`, `SSL_CERT_FILE`, `DOTNET_ROOT`. npx behind a
  corporate proxy or TLS inspection fails until the user adds those names to the server's own env entries,
  which win. The host-side uv install keeps `UV_NATIVE_TLS` and drops every other `UV_*`.
- One cache per server. A rename drops it. `server-memory` and similar keep data inside the npx cache unless
  pointed at the server folder (`MEMORY_FILE_PATH`).
- The state folder grows without a cap (npm and NuGet caches are never pruned; about 16,000 entries for the
  Azure DevOps server), and every start, Test connection included, pays MXC's ACL rewrite over it (0.5–1.5 s
  for a typical npx package, about 1.2 s for the Azure DevOps server, 1.3–2 s for a uv state) and over the
  toolchain (about 0.2 s for a plain Node, 1–2 s for a 16k-entry one, about 0.4 s for a user .NET SDK), each
  way. Starts are serialized, so several servers at startup come up one after another.
- `node <script>` in a locally built repo fails when its `node_modules` are hoisted above the package root
  (monorepos) or the package root exceeds 20,000 entries.
- Under Deny: npx needs a warm cache, and a pinned version needs its own cached install; on SDK 11 a floating
  dnx version never updates once cached.
- Step 2 has no undo in Pia, and an MSI repair may remove it.
- Bare `python` still uses today's exe path; a user Python (26,099 entries here) exceeds the 20,000-entry cap.
- Machine-level (`HKLM`) environment variables still reach containers; only `HKCU\Environment` is blanked.
- The probe and the running server share one npm cache and rely on npm's own locking.
- Startup sweeps the ACEs a never-saved probe leaked only as far as its grant record reaches, and nothing is
  recorded when the factory has no provisioner.
- A definition whose tool prefix holds a character outside `[A-Za-z0-9_]` is unreadable.
- If another server claims a previewed prefix between Test connection and Save, the probe's state folder
  belongs to that server. Only one editor saves local servers, so this needs two saves at once; startup
  removes the folders of servers never saved.
- Detection grants only resolved final paths; how MXC treats links inside a granted tree is answered by G-LINK.
- The preload's realpath `lstat`s every root it walks through. A policy folder that is itself a junction
  reaches `PIA_SANDBOX_ROOTS` unresolved, so realpath below it fails with `EPERM` when the container cannot
  `lstat` the junction or its target's parent (not measured).
- Volta, fnm, nvm and scoop detection follows their documentation; only a junction-resolved install is
  checked live (scenario 15).
- Windows 10/11 client editions were not measured; their `C:\` ACL may make the preload unnecessary there,
  where it is harmless.
- An elevated Pia's grant verifier still uses the elevated token; only detection uses the linked token.

## Docs to update

- This plan: *Live verification* and the Status line after the live run (X2); the checklist ticks with each
  landing commit.
- [2026-10-18-sandbox-runtimes-design.md](2026-10-18-sandbox-runtimes-design.md) line 3: Status → planned here.
- [2026-10-18-sandbox-runtimes-spike-results.md](2026-10-18-sandbox-runtimes-spike-results.md): Status names
  this plan.
- [2026-10-17-mcp-sandbox.md](2026-10-17-mcp-sandbox.md): the decision row at line 62, the command-dir rule at
  163-171, gate G3 at 302, scenario 11 at 378 and its result at 424 (a historical record: annotate, don't
  rewrite), the known gap at 482-483, the partial-protection item at 495-496, *Owner to confirm* at 527-530,
  *Not yet planned* at 542.
- [2026-10-17-mcp-sandbox-checklist.md](2026-10-17-mcp-sandbox-checklist.md) lines 25, 53 and 81.
- [2026-10-17-mcp-sandbox-gate-results.md](2026-10-17-mcp-sandbox-gate-results.md) line 43 (admin-installed
  servers: point at step 2).
- `docs/ui_automation/ui-automation-playbook.md` line 54: the new `McpServers_Sandbox_Runtime*` ids (U2).
- `tests/ui-scripts/README.md` 252-322: the new harness actions, parameters, the elevated profile, and that
  `ResetSandboxRoot` also wipes the `.runtime` caches (X1).
- At the next `release/*` branch, not in this work: `docs/release_notes/RELEASE.md` names the sandboxed
  runners and that user environment variables no longer reach any sandboxed server, plain exe servers
  included, and how to pass one through the server's env.
- Not in this repo: the Pia.Docs desktop guide page for sandboxed runners, then the help corpus at the next
  `release/*` (the `help-corpus` skill).

## Open questions

See the checklist's decision gates. In short: whether `""` blanks a variable (G-ENV); whether a contained
server can plant a link that a later spawn's ACL rewrite follows (G-LINK); whether the uv venv survives the
cache prune (G-UVCACHE); the owner's PAT for scenario 14; which branch carries this work (the MXC sandbox is
not in `develop` yet, so this plan assumes a branch from `feature/mxc-sandboxed-mcp`); and, for the owner,
whether proxy and CA variables (`HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`, `NODE_EXTRA_CA_CERTS`) should join the
blanking allowlist in a later change. Decision 4 stays as decided until then.

## Live verification

Not run yet.

## Appendix A: the shipped Node preload

`src/Pia.Wpf/Services/Plugins/Sandbox/Runtimes/pia-node-sandbox.cjs`, verbatim.

```js
'use strict';
// An AppContainer denies lstat on every ancestor of a granted folder: answer those as folders, resolve realpath by hand.
const fs = require('fs');
const path = require('path');

const roots = (process.env.PIA_SANDBOX_ROOTS || '').split('|').filter(Boolean).map((r) => path.resolve(r).toLowerCase());
const denied = (e) => e && (e.code === 'EPERM' || e.code === 'EACCES');

function isAncestor(p) {
  if (typeof p !== 'string' && !(p instanceof URL) && !Buffer.isBuffer(p)) return false;
  let s = p instanceof URL ? require('url').fileURLToPath(p) : String(p);
  s = path.resolve(s).toLowerCase();
  if (!s.endsWith(path.sep)) s += path.sep;
  return roots.some((r) => (r + path.sep).startsWith(s) && (r + path.sep) !== s);
}

const orig = {
  lstatSync: fs.lstatSync, statSync: fs.statSync, lstat: fs.lstat, stat: fs.stat, readlinkSync: fs.readlinkSync,
  realpathSync: fs.realpathSync, realpathSyncNative: fs.realpathSync.native,
  realpath: fs.realpath, realpathNative: fs.realpath.native,
  pLstat: fs.promises.lstat, pStat: fs.promises.stat, pRealpath: fs.promises.realpath,
};

// __dirname is Pia's support folder, which every Node start grants.
const dirStats = [null, null];
function fakeDir(opts) {
  const k = opts && opts.bigint ? 1 : 0;
  if (!dirStats[k]) dirStats[k] = orig.statSync(__dirname, k ? { bigint: true } : undefined);
  return dirStats[k];
}

function wrapSync(fn) {
  return function (p, opts) {
    try { return fn.call(fs, p, opts); } catch (e) {
      if (!denied(e) || !isAncestor(p)) throw e;
      try { return fakeDir(opts); } catch { throw e; }
    }
  };
}
function wrapCb(fn) {
  return function (p, opts, cb) {
    if (typeof opts === 'function') { cb = opts; opts = undefined; }
    fn.call(fs, p, opts, (e, st) => {
      if (denied(e) && isAncestor(p)) { try { st = fakeDir(opts); } catch { return cb(e); } return cb(null, st); }
      cb(e, st);
    });
  };
}
function wrapP(fn) {
  return async function (p, opts) {
    try { return await fn.call(fs.promises, p, opts); } catch (e) {
      if (!denied(e) || !isAncestor(p)) throw e;
      try { return fakeDir(opts); } catch { throw e; }
    }
  };
}

// Ancestors cannot be lstat'ed, so they count as plain folders; every link below them is followed.
function resolveLinks(start) {
  let s = start;
  for (let hops = 0; ; hops++) {
    const root = path.parse(s).root;
    const parts = s.slice(root.length).split(path.sep).filter(Boolean);
    let cur = root;
    let i = 0;
    while (i < parts.length && isAncestor(path.join(cur, parts[i]))) cur = path.join(cur, parts[i++]);
    let next = null;
    for (; i < parts.length; i++) {
      const c = path.join(cur, parts[i]);
      if (orig.lstatSync(c).isSymbolicLink()) { next = path.resolve(cur, orig.readlinkSync(c), ...parts.slice(i + 1)); break; }
      cur = c;
    }
    if (next === null) return cur;
    if (hops >= 32) {
      throw Object.assign(new Error(`ELOOP: too many symbolic links encountered, realpath '${start}'`),
        { code: 'ELOOP', syscall: 'realpath', path: start });
    }
    s = next;
  }
}
function realFallback(p, opts) {
  const s = resolveLinks(path.resolve(p instanceof URL ? require('url').fileURLToPath(p) : String(p)));
  orig.statSync(s);
  const enc = typeof opts === 'string' ? opts : opts && opts.encoding;
  if (enc === 'buffer') return Buffer.from(s);
  return enc && enc !== 'utf8' && enc !== 'utf-8' ? Buffer.from(s).toString(enc) : s;
}
function wrapRealSync(fn) {
  return function (p, opts) {
    try { return fn.call(fs, p, opts); } catch (e) { if (denied(e)) return realFallback(p, opts); throw e; }
  };
}
function wrapRealCb(fn) {
  return function (p, opts, cb) {
    if (typeof opts === 'function') { cb = opts; opts = undefined; }
    fn.call(fs, p, opts, (e, r) => {
      if (denied(e)) { try { r = realFallback(p, opts); } catch (e2) { return cb(e2); } return cb(null, r); }
      cb(e, r);
    });
  };
}

fs.lstatSync = wrapSync(orig.lstatSync);
fs.statSync = wrapSync(orig.statSync);
fs.lstat = wrapCb(orig.lstat);
fs.stat = wrapCb(orig.stat);
fs.realpathSync = wrapRealSync(orig.realpathSync);
fs.realpathSync.native = wrapRealSync(orig.realpathSyncNative);
fs.realpath = wrapRealCb(orig.realpath);
fs.realpath.native = wrapRealCb(orig.realpathNative);
fs.promises.lstat = wrapP(orig.pLstat);
fs.promises.stat = wrapP(orig.pStat);
fs.promises.realpath = async function (p, opts) {
  try { return await orig.pRealpath.call(fs.promises, p, opts); } catch (e) { if (denied(e)) return realFallback(p, opts); throw e; }
};
require('module').syncBuiltinESMExports();
```

## Appendix B: the Node test fixture server

`tests/Pia.Wpf.Tests/TestData/node-mcp/server.js`, copied to the test output. Dependency-free MCP over stdio
(newline-delimited JSON-RPC). Its entry check is what `server-everything` and `server-memory` do: without the
preload `realpathSync` fails inside the container and the server exits silently.

```js
'use strict';
const fs = require('fs');
const readline = require('readline');

function isEntryPoint() {
  try { return fs.realpathSync(process.argv[1]) === fs.realpathSync(__filename); } catch { return false; }
}
if (!isEntryPoint()) process.exit(0);

const send = (message) => process.stdout.write(JSON.stringify(message) + '\n');
readline.createInterface({ input: process.stdin }).on('line', (line) => {
  let request;
  try { request = JSON.parse(line); } catch { return; }
  if (request.id === undefined) return;
  const reply = (result) => send({ jsonrpc: '2.0', id: request.id, result });
  switch (request.method) {
    case 'initialize':
      return reply({
        protocolVersion: (request.params && request.params.protocolVersion) || '2025-06-18',
        capabilities: { tools: {} },
        serverInfo: { name: 'pia-node-fixture', version: '1.0.0' },
      });
    case 'tools/list':
      return reply({ tools: [{ name: 'echo', description: 'Returns the text it gets.',
        inputSchema: { type: 'object', properties: { text: { type: 'string' } } } }] });
    case 'tools/call':
      return reply({ content: [{ type: 'text',
        text: String((request.params && request.params.arguments && request.params.arguments.text) || '') }] });
    case 'ping':
      return reply({});
    default:
      return send({ jsonrpc: '2.0', id: request.id, error: { code: -32601, message: 'Method not found' } });
  }
});
```

## Appendix C: new and changed strings (English originals)

`ViewStrings.resx`, `.de.resx`, `.fr.resx` (U0). Keys marked *(n)* take n placeholders and get a row in the
parity theory. The issue and refusal texts follow `SandboxRuntimeMessages`, reworded where the editor shows
the next step right below.

Reworded:
- `McpServers_Sandbox_Reason_Shim`: "Pia can't sandbox servers started with pnpm, yarn, bun, deno, pipx or
  docker, or with .cmd, .bat or .ps1 scripts. npx, npm exec, uvx and dnx work; otherwise point the command at
  the server program itself."

Refusals:
- `McpServers_Sandbox_Reason_CmdNotSystem`: "Pia can't sandbox this server: 'cmd' must be the Windows command
  processor."
- `McpServers_Sandbox_Reason_CmdWithoutC`: "Pia can't sandbox this server: only 'cmd /c' followed by a program
  can be unwrapped."
- `McpServers_Sandbox_Reason_CmdMetacharacters`: "Pia can't sandbox this server: its cmd /c line uses cmd
  features (& | < > ^ % or quotes) a sandbox can't reproduce. Put the program in the command field and its
  arguments one per line."
- `McpServers_Sandbox_Reason_UnsupportedForm_Npm`: "Pia can sandbox npm only as npx or npm exec."
- `McpServers_Sandbox_Reason_UnsupportedForm_Uv`: "Pia can sandbox uv only as uvx or uv tool run, with a
  command name it can read."
- `McpServers_Sandbox_Reason_UnsupportedForm_Dotnet`: "Pia can sandbox dotnet only as dnx, dotnet dnx or
  dotnet <app>.dll."
- `McpServers_Sandbox_Reason_UnsupportedForm_Node`: "Pia can sandbox node only as node <script>."
- `McpServers_Sandbox_Reason_UvxOption` *(1)*: "Pia can't sandbox this uvx call: the option {0} is not
  supported. Supported are --from, --with and --python."
- `McpServers_Sandbox_Reason_UvxValue`: "Pia can't sandbox this uvx call: packages must be names from the
  package index, with an optional version, and --python takes a version number."
- `McpServers_Sandbox_Reason_RelativePath`: "Give the full path of the script or .dll, or set a working
  directory."

Runner names (detail pane):
- `McpServers_Sandbox_Runner_Npx` "npx" · `_NpmExec` "npm exec" · `_NodeScript` "Node.js script" · `_Dnx`
  "dnx" · `_DotnetDll` ".NET app" · `_Uvx` "uvx".

Panel:
- `McpServers_Sandbox_Runtime_Title`: "Runtime"
- `McpServers_Sandbox_Runtime_Detect`: "Detect runtimes"
- `McpServers_Sandbox_Runtime_Detecting`: "Looking for the runtime…"
- `McpServers_Sandbox_Runtime_CheckFailed`: "Pia could not check this server's runtime."
- `McpServers_Sandbox_Runtime_Found` *(1)*: "Found {0}." (`{0}` is composed in code from product names and
  versions: "Node.js 22.23.2, npm 10.9.8", ".NET SDK 10.0.402", ".NET 10.0.1", "uv 0.13.1", "uv")
- `McpServers_Sandbox_Runtime_ReadonlyFolders`: "Shared read-only with the server at each start:"
- `McpServers_Sandbox_Runtime_ReadableAlready`: "Sandboxed apps can read this runtime already, so Pia shares
  nothing extra."
- `McpServers_Sandbox_Runtime_StateFolder` *(1)*: "Pia keeps this server's downloads and caches in {0}."
- `McpServers_Sandbox_Runtime_StateFolderReadOnly` *(1)*: "Pia installs this server's package into {0}; the
  server can only read it."
- `McpServers_Sandbox_Runtime_DenyHint`: "With network access off, this server only starts once its package
  is in Pia's runtime folder. Start it once with network access on."
- `McpServers_Sandbox_Runtime_DetectAgain`: "Detect again"
- `McpServers_Sandbox_Runtime_Copy`: "Copy"
- `McpServers_Sandbox_Runtime_Copied`: "Command copied."
- `McpServers_Sandbox_EnvHint`: "Your Windows user environment variables are hidden from sandboxed servers.
  Add the ones this server needs under Environment."

Issues (`McpServers_Sandbox_Runtime_Issue_<RuntimeIssue>`):
- `_NodeNotFound`: "Node.js was not found. Install it for your user only, as shown below."
- `_NodeAdminOwned`: "The only Node.js found is installed for all users, and sandboxed apps can't read its
  folder. Run Pia as administrator once to allow it, or install Node.js for your user only."
- `_DotnetSdkMissing`: "dnx needs a .NET 10 SDK, and none was found. Install one for your user only, as shown
  below."
- `_DotnetRuntimeMissing` *(1)*: "The .NET runtime this program needs ({0}) was not found. Install it for
  your user only, as shown below." (`{0}` names the runtime, composed in code from the runtimeconfig)
- `_DotnetAdminOwned`: "The only .NET found is installed for all users, and sandboxed apps can't read its
  folder. Run Pia as administrator once to allow it, or install .NET for your user only."
- `_UvNotFound`: "uv was not found. Install it for your user only, as shown below."
- `_ToolchainTooLarge`: "The runtime's folder holds too many files to share with a sandbox. Install a separate
  copy for your user only, as shown below."
- `_ToolchainTooWide`: "The runtime sits directly in a folder Pia won't share (your profile, Desktop,
  Documents or Downloads). Install it into a folder of its own, as shown below."
- `_ToolchainPathUnsupported`: "The runtime's folder path contains a semicolon, which a sandbox can't use.
  Install it into another folder, as shown below."
- `_ProgramFolderTooLarge`: "The program's folder holds too many files to share with a sandboxed server. Put
  the program in a smaller folder."
- `_ProgramFolderTooWide`: "Put the program in its own folder: Pia would have to share all of the folder
  around it."
- `_ScriptMissing`: "The script was not found."
- `_DllMissing`: "The .dll was not found."
- `_UvInWritableFolder`: "The only uv found sits in a folder a sandboxed server can write, so Pia won't run it.
  Install uv for your user only, as shown below."

Step 2:
- `McpServers_Sandbox_Runtime_FixText` *(1)*: "Pia runs as administrator, so it can let every sandboxed app on
  this PC read {0}."
- `McpServers_Sandbox_Runtime_Fix`: "Allow sandboxed apps to read this folder"
- `McpServers_Sandbox_Runtime_FixConfirmTitle`: "Change this folder's permissions?"
- `McpServers_Sandbox_Runtime_FixConfirmMessage` *(3)*: "Pia will let every sandboxed app on this PC read
  {0}. That is read-only access, the same Program Files gives elsewhere. Pia can't undo it; an administrator
  can, with the second command." then a blank line, "Grant: {1}", a line break, "Remove: {2}".
- `McpServers_Sandbox_Runtime_FixExistingAce`: "This folder already carries a permission for all sandboxed
  apps. Pia leaves it alone, because removing Pia's change later would take that permission away too."
- `McpServers_Sandbox_Runtime_FixFailed` *(1)*: "Pia could not change the folder's permissions: {0}"
- `McpServers_Sandbox_Runtime_FixDone`: "Sandboxed apps can now read the folder."

Step 3:
- `McpServers_Sandbox_Runtime_Guide_Node`: "Install Node.js for your user only, no administrator needed. Run
  this in a terminal, then press Detect again:"
- `McpServers_Sandbox_Runtime_Guide_NodeAlt`: "No winget? This PowerShell command puts Node.js into your user
  folder instead:"
- `McpServers_Sandbox_Runtime_Guide_Uv`: "Install uv for your user only, no administrator needed. Run this in
  a terminal, then press Detect again:"
- `McpServers_Sandbox_Runtime_Guide_UvAlt`: "Or with winget:"
- `McpServers_Sandbox_Runtime_Guide_Dotnet`: "Install the .NET 10 SDK for your user only, no administrator
  needed. Run this in a terminal, then press Detect again:"
- `McpServers_Sandbox_Runtime_Guide_DotnetRuntime`: "Install the .NET runtime this program needs for your user
  only, no administrator needed. Run this in a terminal, then press Detect again:"
- `McpServers_Sandbox_Runtime_Guide_Caveat`: "Behind a proxy, or where your organisation blocks scripts or
  programs in user folders, these may fail. Ask your IT team, or run Pia as administrator once to use an
  installed copy."

Detail:
- `McpServers_Detail_SandboxRuntime` *(2)*: "{0} · runtime folder {1}"
