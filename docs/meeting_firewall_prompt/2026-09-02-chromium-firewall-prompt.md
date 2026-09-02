# Why joining a Teams meeting raised a Windows Firewall prompt

**Status:** Clobber fixed and proven; the prompt itself unverified on a client that can show one
**Owner:** Marco Altmann
**Written:** 2026-09-02
**Origin:** a non-admin user got Windows Firewall "Security Alert" dialogs naming Chrome for Testing
when starting a meeting transcription, and had no rights to accept them

## What the user saw

Starting a meeting transcription raised Windows Firewall alerts naming the bundled Chromium. The
user is not an administrator, so the prompts could not be accepted — and the transcription worked
anyway. That is expected, not luck: Windows Firewall only blocks **inbound**, and the transcript
rides the Teams signalling channel and outbound-initiated flows.

It is still a defect. `MeetingBrowserSelection` defaults to `BundledChromium`, so this is the
default path for every user; the browser lives at a revision-stamped path, so a rule keyed to it is
invalidated by every Chromium bump; and `ScheduledMeetingRecorder` joins with nobody present to
dismiss a dialog.

## Cause 1: we replaced Playwright's `--disable-features`

`TeamsMeetingSession.LaunchBrowserAsync` passed `--disable-features=CalculateNativeWinOcclusion`.
The Playwright driver builds its own `--disable-features=<16 names>` and then appends our args
verbatim (`chromeArguments.push(...args)`) with no merge. Chromium keeps only the **last**
occurrence of a switch, so our one-item value replaced Playwright's entire list.

Two of the discarded entries exist specifically because they open local-network sockets, and
Playwright disables them for exactly this reason:

- `MediaRouter` — Cast discovery, mDNS UDP 5353
- `DialMediaRouteProvider` — DIAL/SSDP, UDP multicast 1900

Also silently re-enabled: `HttpsUpgrades`, `OptimizationHints`, `Translate`,
`DestroyProfileOnBrowserClose`, `PaintHolding`, `LensOverlay` and the rest of the list.

The same trap applies to `--enable-features`: Playwright passes
`--enable-features=CDPScreenshotNewSurface`, so one of ours would erase it. Never pass one.

## Last-wins is measured, not assumed

Worth recording, because the whole fix rests on it and it is easy to doubt.
`WebRtcHideLocalIpsWithMdns` doubles as a probe: with it on, a local ICE candidate is an mDNS
`.local` hostname; with it off, a raw IP. A page that gathers candidates and reports the counts in
`document.title` makes the feature state directly observable. Against Chromium 1223:

| Launch | mDNS-obfuscated candidates |
|---|---|
| `--disable-features=WebRtcHideLocalIpsWithMdns` | 0 — the switch took effect |
| the same, then a second `--disable-features=Translate` | 1 — the first switch was discarded entirely |
| both names merged into one `--disable-features` | 0 — the shape the fix uses |

The middle row is the bug. It also proves the probe and the switch spelling work, so the third row
being green means something.

## Cause 2: WebRTC's own mDNS responder

Independent of the clobber, `WebRtcHideLocalIpsWithMdns` is default-on and starts an mDNS responder
as soon as a peer connection gathers candidates — which a Teams call always does. It is disabled
now too. Exposing local IPs in the SDP costs nothing here: the join is anonymous and the SDP never
leaves the meeting.

## What could not be verified here, and why

Two measurement traps, both worth knowing before anyone repeats this:

- **`NotifyOnListen` is `False` on all three firewall profiles of this machine.** No dialog can
  appear regardless of what the process binds, so the prompt is not reproducible locally at all.
- **`svchost` already owns UDP 5353** (`0.0.0.0` and `::`). Chromium's responder therefore never
  appears as a distinct 5353 endpoint in `Get-NetUDPEndpoint`, which is why socket enumeration
  returned nothing useful and the candidate-type probe above had to stand in for it.

Also measured, so nobody chases it: `MediaRouter` does **not** bind at startup. A plain browser at
`about:blank` binds neither 5353 nor 1900 — Cast/DIAL discovery is lazy. The only sockets a quiet
Chromium holds are five wildcard UDP endpoints in the `network.mojom.NetworkService` process
(DNS/QUIC, outbound-initiated), plus two more once ICE gathers.

So the clobber is fixed and proven, and both known local-network binds are disabled by construction.
Whether *that* is what the user's machine prompted about is still open: it needs one join on a
client whose `NotifyOnListen` is `True`.

## Rejected

- **A firewall allow rule** — needs admin, and the revision-stamped path re-prompts on the next bump.
- **Suppressing firewall notifications** — needs admin, fleet-wide policy.
- **`--webrtc-ip-handling-policy=disable_non_proxied_udp`** — stops ICE binding UDP at all, but
  forces media onto a TCP/TURN relay. That is supported yet degraded (retransmits defeat the Satin
  codec's error correction), and that audio *is* the transcription input. Worse, where the relay is
  unreachable — a proxied or TLS-inspecting network — media never connects while capture still
  "succeeds" on an all-zero Web Audio graph, so the meeting records nothing and reports no error.
  Held in reserve: only if a prompt survives this fix, and then as a setting defaulting to off. Note
  the spelling is `--webrtc-ip-handling-policy`; scanning every module of Chromium 1223,
  `force-webrtc-ip-handling-policy` does not occur at all.
- **`default_public_interface_only`** — still binds UDP, so it still prompts.
- **`IgnoreDefaultArgs`** — the driver filters by exact string equality, so it means reproducing
  Playwright's joined value byte-for-byte. More brittle than mirroring, and the .NET API exposes no
  `DefaultArgs` getter.

## Keeping the mirror honest

`PlaywrightDisabledFeatures` in `TeamsMeetingSession` duplicates a list that lives in the driver, and
that list drifts — 1.61.0 had `RenderDocument` and lacked `BlockOriginHeaderModificationOnRedirect`.
`TeamsMeetingSessionLaunchArgsTests` parses `disabledFeatures` out of
`.playwright/package/lib/coreBundle.js` in the test output and asserts **ordered equality**, not a
superset: a superset assertion stays green when Playwright *removes* a name, leaving the mirror to
accumulate dead entries.

On a Playwright bump the test fails naming the position and the differing name, and the fix is to
paste the current list. If the driver's layout changes so the parse fails, the assertion says so
explicitly rather than throwing — the bundle is minified driver output, not a stable contract.

The regression guard that needs no file is separate: exactly one `--disable-features`, zero
`--enable-features`, and the four names that matter present in the value.

## Verifying on a client that can prompt

1. `Get-NetFirewallProfile | Select Name, Enabled, NotifyOnListen` — if `NotifyOnListen` is `False`,
   stop; that machine cannot answer the question.
2. `Get-NetFirewallApplicationFilter -Program <bundled chrome.exe> | Get-NetFirewallRule` — check
   `Action`, not just `Enabled`. A non-admin can only *Cancel*, which writes an inbound **Block**
   rule, after which the prompt never returns regardless of the fix.
3. Join a meeting. Enumerate the process tree by `ExecutablePath -eq <bundled exe>` — the
   revision-stamped path is unique, so one predicate catches browser, renderers, GPU, audio and
   network service. Then `Get-NetUDPEndpoint` and `Get-NetTCPConnection -State Listen` over those
   PIDs, filtered to a `LocalAddress` that is not `127.0.0.1`/`::1`.
4. Confirm the transcript is produced and, on a scheduled capture, that it stays silent.
5. If a prompt still appears, the remaining suspect is bare ICE UDP, and the reserved
   `disable_non_proxied_udp` option above is the next step — with its cost accepted deliberately.
