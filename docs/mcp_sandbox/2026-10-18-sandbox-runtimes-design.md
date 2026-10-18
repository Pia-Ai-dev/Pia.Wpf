# Sandboxed MCP servers on npx, uvx and dnx

**Status:** Owner-approved and planned in [2026-10-18-sandbox-runtimes-plan.md](2026-10-18-sandbox-runtimes-plan.md).
Measured in [2026-10-18-sandbox-runtimes-spike-results.md](2026-10-18-sandbox-runtimes-spike-results.md), which
changes the Node and uv recipes; where this concept and the plan differ, the plan holds.
**Owner:** Marco Altmann
**Written:** 2026-10-18
**Origin:** The owner added `cmd /c npx -y @tiberriver256/mcp-server-azure-devops` to the live profile. It
worked uncontained and died sandboxed with `Der Befehl "npx" ist entweder falsch geschrieben oder konnte
nicht gefunden werden.` Owner: "we need a better solution for this. in this way too many mcp servers will
be unusable for most users." This is the follow-up the sandbox plan parked as gate G3 and under *Not yet
planned*: [2026-10-17-mcp-sandbox.md](2026-10-17-mcp-sandbox.md).

## Why

Most published MCP servers are started through a package runner: `npx -y <pkg>` (Node), `uvx <pkg>`
(Python/uv) or `dnx <pkg>` (.NET 10). The sandbox currently refuses all of them, which leaves the safe
path closed for most of the servers people actually paste in. The sandbox's promise is that **a server
reads and writes only its own sandbox folder and the folders the user granted**. That especially keeps
it away from what lives in the user profile: browser data, SSH keys, `~\.azure`, `.npmrc` tokens. Running
a package runner does not need to break that promise. It needs **read** access to a toolchain, which holds
no user data, and a **writable cache**, which can live inside the server's own sandbox folder.

## What happened in the failing case

- `SandboxCommandRules.IsShimCommand` checked only the command. `cmd` resolves to
  `C:\Windows\System32\cmd.exe`, so `cmd /c npx …` passed, and `npx` was never looked at. (The plan
  replaces that class with `RunnerCommandParser`, which unwraps `cmd /c`.)
- Inside the AppContainer, `cmd` finds no `npx` it can read:
  - `C:\Program Files\nodejs` has a **protected** ACL from the Node MSI: Users, Authenticated Users,
    Administrators, SYSTEM, and **no** `ALL APPLICATION PACKAGES` (`S-1-15-2-1`). `C:\Program Files` and
    `System32` carry that ACE, which is why `cmd.exe` itself starts.
  - The copies under `%APPDATA%\npm` and `%LOCALAPPDATA%\pi-node` are in the profile, which no container
    reaches.
- Pia cannot add the ACE to `C:\Program Files\nodejs` itself: changing a DACL needs `WRITE_DAC`, which only
  Administrators and SYSTEM hold there. `SandboxGrantVerifier` already refuses such folders up front.
- Measured on this host: `C:\Program Files\dotnet` **does** carry `S-1-15-2-1` and is not protected, so a
  machine-wide .NET is already readable from a container. Node's MSI is the outlier, not the rule.

## Goals

- A server whose command is `npx …`, `uvx …` or `dnx …`, or `cmd /c` wrapping one of them, runs sandboxed
  with no extra steps whenever a usable runtime exists.
- A user always learns *why* a runtime is not usable and *what to do*, in the server editor, before
  saving, never as a "server shut down unexpectedly" after it.
- The sandbox's promise holds unchanged: the server writes only to its sandbox folder and the granted
  read-write folders, and reads only those plus the toolchain.

## Non-goals

- Bun, Deno, Docker, `pipx`, `pnpm dlx`, `yarn dlx`. These keep today's refusal with its reason.
- Arbitrary `.cmd`, `.bat`, `.ps1` scripts. Still refused, except the known runtime launchers below.
- Pia downloading and managing its own copies of Node, Python or .NET. The owner chose the user's own
  toolchain plus guidance instead.
- Server-pushed (org) MCP plugins. Unchanged, still uncontained.

## Anti-goals

These count as failure even if a server starts:

- Granting a profile root, `%APPDATA%` or `%LOCALAPPDATA%` as a whole, or any folder that also holds user
  secrets, to reach a toolchain inside it.
- Letting the package runner write its cache, config or downloads outside the server's sandbox folder.
- A machine-wide permission change Pia makes without the user pressing a button that says so.

## The concept: three steps, in order

### 1. Detect and use a runtime the user already has, with no effort

When the command, or the program a `cmd /c` wraps, is a known runtime launcher, Pia resolves the
runtime behind it and classifies each candidate install:

| Outcome | Example | What Pia does |
|---|---|---|
| **Readable already** | `C:\Program Files\dotnet` (carries `S-1-15-2-1`) | Use it. Nothing to grant. |
| **User-owned** | `%APPDATA%\npm`, fnm/nvm/Volta folders, `%LOCALAPPDATA%\Programs\Python`, `%USERPROFILE%\.local\bin` (uv), `%LOCALAPPDATA%\Microsoft\dotnet` | Grant **read-only** to exactly the toolchain folders, never the folder around them. |
| **Admin-owned, not readable** | `C:\Program Files\nodejs` | Step 2 if Pia runs elevated, otherwise step 3. |
| **Not found** | | Step 3. |

Candidates come from `PATH` and the usual user-scope locations of each runtime. The first usable one in
`PATH` order wins, so the sandbox runs the same toolchain the user gets in a terminal whenever possible.

Runtimes and launchers in scope:

| Runtime | Launchers | Must be readable | Writable state to redirect into the sandbox folder |
|---|---|---|---|
| Node | `npx`, `npm` (`.cmd` shims, or `node` plus npm's CLI script) | the `node.exe` folder and the npm package it ships | npm cache, npm user config, global prefix |
| Python via uv | `uvx`, `uv` | the `uv`/`uvx` folder, and the uv-managed Python installs | uv cache, tool dirs |
| .NET | `dnx` (`dnx.cmd` → `dotnet exec <sdk>\dotnet.dll dnx`), `dotnet` | the dotnet root with its SDK (`dnx` needs a .NET 10 SDK, not only a runtime) | NuGet packages folder, `DOTNET_CLI_HOME` |

The bare interpreters `node`, `python` and `dotnet` (e.g. `C:\Program Files\nodejs\node.exe server.js`)
go through the same detection and the same three steps. Today they pass the command check and then die
for the same permissions reason as `npx`. *(Builder-check call.)*

Every runtime must find **its whole writable state** in a **runtime state folder** that Pia owns:
`PiaPaths.McpSandboxRoot\<slug>.runtime`. That covers cache, home/config lookups, and anything it would
otherwise write under `%APPDATA%`, `%LOCALAPPDATA%` or `%USERPROFILE%`. The parent's env vars still point
there, and the container cannot reach those folders. *(Builder-check call.)* The state folder:

- is a sibling of the server's own folder, not inside it. The server's file tools and default working
  directory never see `node_modules`, and the guard against one read-write folder inside another is not
  touched.
- is added read-write by Pia whether or not the user kept the proposed `McpSandboxRoot\<slug>` folder,
  and is listed in the editor as managed by Pia, not editable.
- is deleted when the server is removed, and dropped (re-downloaded) when a rename changes the slug.

Today's refusal of `npx`/`uvx` and of `.cmd` is lifted **only** for these launchers, and only when
detection found a usable runtime.

### 2. When Pia runs as administrator: a button that fixes a machine-wide install

If the only install found is admin-owned and not container-readable, **and Pia is running elevated**,
the editor offers a button that adds `ALL APPLICATION PACKAGES:(OI)(CI)(RX)` to that install folder.
That is read-only, the same access `Program Files` already gives everywhere else. The button names the
folder and says that it changes the folder's permissions for all sandboxed apps on the machine. When Pia
is not elevated, the button is not shown and step 3 applies. Showing it only when elevated is the
owner's call.

Pia offers no undo for this change, since undoing it would need elevation again. The button's
confirmation shows the exact `icacls` grant, so an admin can remove it by hand. *(Builder-check call.)*

A Node update or repair through the MSI may restore the installer's ACL. The next start then fails
detection with the same clear reason and offers step 2 or 3 again. No background re-check is needed.

### 3. A short in-app guide to install a user-scope copy

If neither step applies, the editor shows a short guide for that runtime: how to install it for the
current user only, without admin rights. It ends with a **Detect again** action. One guide per runtime
(Node, uv, .NET SDK), each a few lines with a copy button for the command. The guide's steps must work
on a clean machine as a standard user.

### The editor

- Detection runs automatically when the command or arguments change, and on a **Detect runtimes**
  button in the sandbox section. Both show the same result: the runtime found, its version, and the
  folders that will be granted read-only. Or the reason none is usable, with step 2 or step 3 right there.
- The sandbox toggle is no longer disabled for these launchers. It stays disabled, with today's reason,
  for everything listed under *Non-goals*.
- A new server whose command is a runner (`npx`, `uvx`, `dnx`, or `cmd /c` wrapping one) defaults to
  network **Allow**, with the existing "allows every host" warning. Every other new server keeps the
  **Deny** default. Owner's call: a runner with an empty cache cannot fetch its package under Deny, and
  most of these servers need the network at runtime anyway. A saved server's network setting is never
  changed by this default.
- If the user switches a runner server to **Deny**, it only works once the package is in its runtime
  state folder. The editor says so next to the network switch.
- Test connection and Save keep using the one launch path through `McpTransportFactory`.

## Constraints

- **Grants on user-owned toolchains are per start.** They use the existing mechanism: MXC adds the
  `pia-mcp-*` container's ACE on start and removes it on dispose, and `AceSweepService` cleans up after a
  hard kill. Pia makes no persistent `S-1-15-2-1` change to user folders; only the step-2 button does
  that, on an admin-owned install. *(Builder-check call.)* A toolchain tree can hold thousands of files.
  If measuring shows starts get noticeably slow, that goes back to the owner rather than silently
  switching to a persistent ACE.
- **`SandboxGrantVerifier`** walks a granted command folder recursively and refuses on any entry without
  `WRITE_DAC`. Toolchain folders go through the same check, so a half-granted toolchain is refused before
  the start, not discovered as a crash.
- **Path guard.** Toolchain folders are new read-only roots. They must pass the existing guard: no profile
  root, no Desktop/Documents/Downloads/OneDrive roots, no system roots. `Program Files` installs are never
  policy paths; they are either already readable or go through step 2.
- **Privacy logging.** Toolchain paths are user paths: `SensitiveDebug` only. The Information line carries
  the runtime kind, the outcome and counts.
- **AutomationIds** on every new control, with rows in `ViewAutomationIdTests`.
- **Localization** for every new string, like the existing `McpServers_Sandbox_*` keys.

## Left to the builder

- How each launcher is resolved to its runtime (parse `cmd /c`, read the `.cmd` shim, or know the layout).
- The exact env vars per runtime for the cache/home redirection, and which of them a runtime insists on.
- Per-start grants vs a persistent ACE on user-owned toolchains (see *Constraints*), decided on a
  measurement.
- Whether detection results are cached per command, and for how long.
- The guide's wording and install commands, provided they are verified on a clean standard-user machine.

## Known limits, accepted

- A user's `.npmrc` / `uv.toml` / `NuGet.Config` is not read inside the sandbox, so private registries,
  registry auth and proxy settings from there do not apply to sandboxed servers.
- Credentials that live in the profile do not reach a sandboxed server. An Azure DevOps server using Azure
  identity / `az login` (`~\.azure`) cannot sign in; it must use a personal access token passed as an env
  var. The same goes for any server relying on a CLI's cached login. This is the sandbox's purpose, not a
  gap. The acceptance run for the originating server uses a PAT.
- One cache per server: two sandboxed servers on the same package each download it once.
- Packages with native install scripts (node-gyp and the like) run those scripts inside the sandbox, and
  may fail there. That is the sandbox working, not a bug to route around.
