# Chromium lifecycle for the meeting attendee

**Status:** Cleanup implemented; bundling prepared, switched off
**Owner:** Marco Altmann
**Written:** 2026-08-29
**Origin:** "How do we make sure the Playwright browser is always up to date and we don't gather old
versions on the clients that may get scanned by EDR systems?"

## The defect

`ChromiumProvisioner.ResolveChromiumExecutable` matched *any* `chromium-*` folder holding a
`chrome.exe`, so `EnsureChromiumAsync` skipped the install as soon as anything was cached. Two
consequences, one cause:

- the browser was never updated again after the first download, and
- the old revision was never removed, because Playwright's own garbage collection only runs *inside*
  the installer.

Measured on a dev machine on 2026-08-29, before the fix:

```
%LOCALAPPDATA%\Pia\Browsers\
  chromium-1217                 408 MB   ← in use
  chromium_headless_shell-1217  266 MB   ← never launched
  ffmpeg-1011 / winldd-1007     ~4 MB
```

`Microsoft.Playwright` 1.61.0 pins revision **1228** (Chrome 149.0.7827.55). The client was a
revision behind with no path back. On top of that the uninstaller left the whole tree on disk:
Velopack removes the app directory and knows nothing about `%LOCALAPPDATA%\Pia\Browsers`.

## How Playwright's cache actually behaves

Read out of the bundled driver (`.playwright/package/lib/coreBundle.js`), not from docs:

- The registry root is `PLAYWRIGHT_BROWSERS_PATH`, which the provisioner points at
  `%LOCALAPPDATA%\Pia\Browsers`.
- `install` writes `.links/<sha1(driver path)>` containing that driver path, then — unless
  `PLAYWRIGHT_SKIP_BROWSER_GC` is set — deletes every browser directory in the registry that no
  linked driver's `browsers.json` still references at its *current* revision. So one install both
  lands revision 1228 and removes `chromium-1217` **and** `chromium_headless_shell-1217`.
- The link is keyed on the driver's path, and Velopack installs to a stable `…\current\` folder
  (`%LOCALAPPDATA%\Pia.Wpf\{Pia.exe, Update.exe, current\}`), so the key survives updates.
- If every link is dead, the traversal yields nothing and GC deletes **all** browser directories.
  That is why a bundled payload is never handed to the installer, and why the staging script strips
  `.links`.
- `PLAYWRIGHT_SKIP_BROWSER_GC` must never be set on a client — it is the off switch for the cleanup.

## What happens now

`ChromiumProvisioner.EnsureChromiumAsync`, in order:

1. **A build bundled beside the app wins.** `<app>\Browsers\` is resolved first; it is replaced by an
   update and removed with the app, so it needs no refresh. It is never passed to the installer.
   Taking that path also deletes the download cache, so a client that updates into a bundled build
   does not keep an orphaned 400 MB copy nothing refreshes.

   Because that delete is irreversible on a branch that never installs, the payload is first probed
   with `chrome.exe --version` (once per process). A bundled build that will not start — quarantined,
   half-copied, ACL-blocked — is ignored, and provisioning falls through to the download path with
   the cache intact.
2. **The cache is keyed on the Playwright version**, recorded in `Browsers\.playwright-version`. A
   mismatch (or no marker, which is what every pre-fix client has) re-runs the installer, which
   updates the browser and prunes the old revisions.
3. **`install chromium --no-shell`** — the attendee launches the full headed build for a real audio
   render session, so the headless shell was 266 MB of binary nothing ever executed.
4. **A failed install falls back to the cached browser** instead of throwing: an air-gapped or
   CDN-blocked client keeps joining meetings with an older browser. The path is re-resolved after the
   failure, because the installer prunes before it downloads and can leave the pre-install probe
   dangling. One failed attempt per process is latched, so a blocked CDN does not put a node spawn
   and a connect timeout in front of every join; a restart retries.
5. **Uninstall deletes the cache** — `App.xaml.cs`, `OnBeforeUninstallFastCallback`.

## Turning the bundle on

The release workflow has a `bundle_chromium` input (`workflow_dispatch`, default off). When set it
runs, before `vpk pack`:

```
pwsh scripts/Save-RuntimeAssets.ps1 -Include Chromium -DestinationRoot publish
```

That stages `publish\Browsers\chromium-<revision>\…` — the exact layout step 1 looks for — plus the
~4 MB `ffmpeg-*` and `winldd-*` the installer always brings, using the `playwright.ps1` already in
the publish payload. It strips the `.links` file that would otherwise carry the CI runner's driver
path onto every client.

Trade-off: ~400 MB per release package (Velopack deltas absorb most of it for updaters, not for first
installs), paid by every user whether or not they use the meeting attendee. In exchange the browser is
signed-and-shipped with the app, updated with the app, removed with the app, and there is no runtime
download from a public CDN for EDR to notice. Nothing else has to change to flip it: the app prefers
the payload automatically.

## Known gaps

- **Per-machine uninstall only clears the uninstalling account.** The hook resolves
  `%LOCALAPPDATA%` for whoever runs the uninstall, typically an admin who never ran Pia, so other
  profiles keep their cache. Bundling closes this; an MSI custom action enumerating profiles was not
  attempted.
- **The cache is per user.** On a shared machine with a per-machine install, every profile downloads
  its own ~410 MB copy.
- **`ChromiumProvisioner.DownloadHostOverride` is still null**, i.e. downloads come from
  `cdn.playwright.dev`. An internal mirror would make the traffic explainable to EDR and proxy
  allowlists; the host has not been decided.
- The uninstall delete is best-effort, and Velopack expects a fast callback. A locked file or a
  killed callback leaves a partial tree — and on uninstall nothing repairs it, because the app that
  would re-provision is gone. Reinstalling clears it on the next join.

## Verification

- `ChromiumProvisionerTests` covers the whole decision through the `InstallerInvoker` and
  `ExecutableProbe` seams, with no network and no process: bundled wins and never installs, an
  unstartable bundle is ignored and keeps the cache, marker match skips, missing/stale marker
  reinstalls with `["install","chromium","--no-shell"]`, install failure falls back, a pruned cache
  still throws, the failure latch holds, and the cache delete removes the tree.
- The two claims the tests cannot make were measured against the real driver on 2026-08-29:
  `install chromium --no-shell --dry-run` plans `chromium-1228` and no headless shell (without the
  flag it plans `chromium_headless_shell-1228`, 266 MB), and an install into a scratch registry
  seeded with a stub `chromium-1111` left that folder gone afterwards — GC prunes on any install, so
  proving it cost a 0.1 MiB `install ffmpeg` rather than a browser download.
- Not covered by tests, worth one human check after the next Playwright bump: the cache ends up with
  a single `chromium-<new revision>` folder and no `chromium_headless_shell-*`.
