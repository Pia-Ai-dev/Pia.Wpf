# Handoff: publish the Velopack feed to Pia.Storage

**Status:** Client side landed 2026-08-29 (`Services/UpdateService.cs`, `appsettings.json`); server side
open — this document is the handoff · **Owner:** Marco Altmann · **Written:** 2026-08-29 ·
**Origin:** the "Not covered" gap in `Pia/docs/guides/production-deployment.md` §11, which names pointing
`UpdateService` at a `SimpleWebSource` as the one remaining client-side change

## 1. Why

Installed clients check **github.com** for updates. In customer networks that block github.com the app
silently never updates — `CheckAndDownloadUpdateAsync` swallows the failure and retries in 4–6 h, so it
looks like nothing is wrong. The fix is to serve the Velopack feed ourselves.

## 2. What already shipped (client)

`AutoUpdateOptions` gained a `FeedUrl` key. `UpdateService.CreateSource` picks `SimpleWebSource(FeedUrl)`
when it is set and falls back to the existing `GithubSource` when it is blank, so a deployment moves feeds
by setting one key. The shipped `appsettings.json` carries:

```json
"Update": { "FeedUrl": "https://storage.pia-ai.de/f/wpf/", ... }
```

**This is inert until the server side below exists.** Nothing breaks in the meantime — a dead feed is the
same swallowed-and-retried failure as a blocked github.com — but no client updates either.

## 3. What the server side has to do

Publish every Velopack asset to the storage pod under a flat `wpf/` prefix, so that
`https://storage.pia-ai.de/f/wpf/releases.win.json` and every `.nupkg` it names are anonymously readable.

Do this by extending **`Pia/.github/workflows/publish-wpf-download.yml`**, not by adding steps to
`Pia.Wpf`. That workflow already resolves the release, downloads every asset and prunes; and the write
credential must not sit in the public repo — the same reasoning that put the Hetzner SSH key there. This
secret grants write *and delete* on a public file service.

Leave `pia-ai.de/dl` exactly as it is: it backs the website download page and is independent of this.

### 3.1 Prerequisites — none of this is deployed yet

The service exists only on branch `feature/storage-server`. Before anything else:

1. Merge it.
2. **Create the DNS record for `storage.pia-ai.de`.** The Caddyfile warns that an ACME issuance Caddy can
   never complete is retried *for every site in the file* — a missing record degrades the whole host.
3. Create `.env.prod.storage` on the host with `Storage__UploadSecret` set to base64 carrying **≥32
   decoded bytes**; the app refuses to boot otherwise, and it rejects the `CHANGE_ME` placeholder by name.
4. Create `public/` and `tmp/` under `/mnt/hc-volume/storage`.
5. Add the same secret to the Pia repo as `PIA_STORAGE_UPLOAD_SECRET`.

### 3.2 Upload

No `vpk upload` backend fits — it offers github, gitea, s3, az and local only, and the pod has no S3
surface. So this is a loop of raw PUTs over the files `gh release download` already fetched:

```bash
curl -fsS -X PUT --data-binary @"$f" \
  -H "X-Pia-Upload-Secret: $SECRET" \
  https://storage.pia-ai.de/upload/wpf/"$f"
```

**The order is the design, not a detail.** The old SCP mirror got atomicity from the filesystem; a
file-by-file API has no directory swap. If `releases.win.json` lands before its packages, every client
polling in that window reads a valid feed naming assets that 404. Clients poll on a 4–6 h jitter, so
someone will. The existing prune step already names this failure: *"delta updates 404 against a feed that
still parses — the failure that looks like a working mirror."*

1. **Every `.nupkg` first**, with **no** overwrite header. Byte-identical content returns `204`, which is
   what makes the step idempotent on a re-run.
   A `409` here means a package of the same name was rebuilt with different bytes: treat it as **fatal,
   investigate** — never retry it with the overwrite header. Republishing a served package changes its
   ETag (mtime + length) and turns every in-flight resume into a full download.
2. **Gate:** every `FileName` listed in the *new* `releases.win.json` must resolve with a correct
   `Content-Length` — not merely the files this run uploaded. Those differ exactly when the feed still
   references an earlier release's package, which it does: see §4.
3. **Only then** PUT `releases.win.json` and `RELEASES`, each with **`X-Pia-Overwrite: true`**. Without
   that header they are a hard `409 already_exists` on every release after the first.

~7 files per release sits far inside the write limiter (30 requests / 60 s).

### 3.3 Prune

`GET /manage/wpf` lists, `DELETE /manage/wpf/<name>` removes; both take the same
`X-Pia-Upload-Secret`. Carry over the existing invariant: **never delete anything the current
`releases.win.json` still references**, regardless of age.

## 4. The contract, verified against the source

Checked in `Pia.storage_pod/src/Pia.Storage`, not assumed from docs:

| Requirement | How it is met |
|---|---|
| Anonymous read | `UseStaticFiles(RequestPath = "/f")`, `Program.cs:189`. No authentication is wired up at all; the secret is checked inside the upload/manage handlers only |
| Velopack's `?arch=…&os=…&rid=…&id=…&localVersion=…` on the feed request | `StaticFileMiddleware` matches on path; the query is never parsed. No signed-URL or expiry logic exists |
| Verbatim filenames | `PUT /upload/{**path}` stores the path as given. Charset regex `^[A-Za-z0-9._/-]+$` accepts every name we produce, including extensionless `RELEASES` |
| `.nupkg` served | Not in the 8-extension allow-list → `application/octet-stream` + `Content-Disposition: attachment`. Irrelevant to an `HttpClient` |
| `releases.win.json` served as JSON | `.json` is mapped → `application/json; charset=utf-8`, no attachment |
| Range / resume on a 312 MB asset | Native; `OnPrepareResponse` deliberately never touches ETag or Last-Modified |
| 312 MB body | `MaxUploadBytes` 2 GB, mirrored by Caddy `request_body max_size 2GB` |

Measured against live release **`v1.3.389`**: the feed names exactly 3 assets —
`Pia.Wpf-1.3.389-full.nupkg` (312 MB), `Pia.Wpf-1.3.389-delta.nupkg` (17.7 MB) and the previous release's
`Pia.Wpf-1.3.385-full.nupkg` (312 MB) — and **all three are already GitHub release assets**. So
`gh release download` yields everything the feed references: no backfill step is needed. Delta generation
is confirmed working.

### Do not publish under `blobs/`

`Program.cs:205-207` gives `/f/blobs/*` `Cache-Control: public, max-age=31536000, immutable`, everything
else `max-age=60, must-revalidate`. A feed under `blobs/` would be frozen in every intermediate proxy for
a year. `blobs/` is also content-addressed (`blobs/<sha256>/<filename>`), which breaks Velopack's relative
resolution of `FileName` anyway. Use the flat `wpf/` prefix.

## 5. Rollout order — this cannot be reordered

1. Storage service deployed and verified (§3.1).
2. Publish step live; feed verified reachable (§6).
3. **Only then** ship a client release carrying the new `appsettings.json`.

Installed clients read the `appsettings.json` inside their own `current\`, so they keep asking GitHub
until they have taken one release carrying the new file. **GitHub must therefore keep receiving the full
feed** (`releases.*.json`, `RELEASES`, `*.nupkg`) for at least one more cycle — the existing
`gh release create` step in `Pia.Wpf` already does this; do not remove it. Keep `vpk download github` as
the delta source too: GitHub stays the publish target, the storage pod is downstream of it.

## 6. Verification

Ask for the feed exactly as the client will, then follow **every** entry — the failure mode is entry 2 or
3 missing while entry 1 is fine:

```bash
base=https://storage.pia-ai.de/f/wpf
curl -fsS "$base/releases.win.json?arch=x64&os=win&rid=win-x64&id=Pia.Wpf&localVersion=1.3.385" \
  | jq -e '(.Assets|length) > 0'
curl -fsS "$base/releases.win.json" | jq -r '.Assets[].FileName' | while read -r n; do
  curl -fsSI "$base/$n" >/dev/null || { echo "MISSING $n"; exit 1; }
done
```

On an asset expect a full `Content-Length`, `Accept-Ranges: bytes` and `max-age=60` — **not** `immutable`,
which would mean it landed under `blobs/`.

Run the publish step **twice** against the same release: it must succeed both times, proving §3.2 step 3
is not a one-shot.

The only test that really proves it: install an older signed build, point its `…\current\appsettings.json`
at the feed, launch, and confirm `%LOCALAPPDATA%\Pia\Logs\pia-*.log` shows
`Downloading release file 'releases.win.json' from 'https://storage.pia-ai.de/f/wpf/…'`, then that the
update bar appears and **Restart Now** applies it. Velopack breakage never shows up locally — it shows up
as shipped clients that can no longer update.

## 7. Open risks

- **Per-IP throttling vs. corporate NAT — this hits the exact deployments the change is for.** Unlike
  Caddy's `file_server` at `/dl`, every `/f` download passes the rate limiter, partitioned on the full
  client IP, so a whole NATed site is one partition: 16 concurrent requests, 8 MB/s shared, 240
  requests/60 s, against a 60 MB/s global ceiling. The 17th machine downloading behind one NAT gets a
  `429`, which is swallowed and retried on the next 4–6 h tick — a slow trickle rather than a visible
  failure. Delta traffic makes this mostly tolerable (17.7 MB ≈ 2 s at the per-IP rate); a client that has
  fallen behind and needs the 312 MB full package takes ~40 s of the site's entire budget. A large
  rollout will want `Storage__Throttle__PerIpBytesPerSecond` and `PerIpConcurrency` raised.
- **The manual publish button becomes the fleet's update gate.** Today, forgetting to run *Publish WPF
  Download* only leaves the website stale. Once clients read the feed from the storage pod, forgetting it
  means nobody gets the update. Flagged deliberately, not fixed — auto-dispatch was declined on
  2026-08-27 to keep a PAT out of the public repo, and `PIA_DISPATCH_TOKEN` still turns the existing step
  automatic if that is revisited.
- **Two client-side unknowns**, both only relevant under the contention above: whether Velopack's
  downloader retries a `429` with backoff, and whether it re-issues a `Range` request after Kestrel's
  slow-reader eviction (`MinResponseBytesPerSecond`, 16 KB/s). The 4–6 h re-check is the backstop either
  way.
