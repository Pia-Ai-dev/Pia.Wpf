# Screen capture — desktop probe results

**Status.** Measured 2026-09-09. Verdict: GDI holds — no black frames on this desktop.
**Owner.** Marco Altmann
**Written.** 2026-09-09
**Origin.** The desktop-probe step of [2026-09-07-screen-vision-checklist.md](2026-09-07-screen-vision-checklist.md).

Before running this, open on the probed desktop, un-minimized: Word or Excel with a paragraph of 10 pt text, Teams, Edge or Chrome on a text-heavy page, VS Code, a PDF, an mstsc session, Task Manager as administrator, Settings and Calculator, and one Explorer window minimized. Run from an interactive terminal on that desktop — a disconnected or background session gets no DWM composition.

## Environment

- OS: `Microsoft Windows NT 10.0.26200.0`
- WDA_EXCLUDEFROMCAPTURE available: `True`
- preferred affinity: `0x11`
- test-host thread DPI awareness: `1` (0 unaware, 1 system, 2 per-monitor)
- fixture window rect: `PixelRect { X = 80, Y = 80, Width = 400, Height = 300, IsEmpty = False, Right = 480, Bottom = 380 }`
- fixture popup rect: `PixelRect { X = 520, Y = 80, Width = 200, Height = 150, IsEmpty = False, Right = 720, Bottom = 230 }`
- monitor `\\.\DISPLAY1`: `PixelRect { X = 0, Y = 0, Width = 2560, Height = 1440, IsEmpty = False, Right = 2560, Bottom = 1440 }` (primary)

## Windows

| # | process | title chars | physical | ms | result | dominant share | jpeg | legibility (human) |
|---|---|---|---|---|---|---|---|---|
| 1 | WindowsTerminal | 9 | 284x73 | 10 | ok | 0.743 | 284x73, 5 KB | n/a, a 284px flyout |
| 2 | WindowsTerminal | 45 | 2560x1392 | 24 | ok | 0.930 | 1568x853, 177 KB | yes |
| 3 | explorer | 21 | 160x28 | 0 | Minimized | - | - | |
| 4 | devenv | 29 | 160x28 | 0 | Minimized | - | - | |
| 5 | chrome | 48 | 1580x922 | 11 | ok | 0.321 | 1568x915, 65 KB | yes |
| 6 | devenv | 78 | 2294x1249 | 21 | ok | 0.549 | 1568x854, 218 KB | yes, incl. 9pt code comments |
| 7 | Docker Desktop | 27 | 1717x969 | 12 | ok | 0.884 | 1568x885, 138 KB | yes |
| 8 | msedge | 81 | 2560x1392 | 18 | ok | 0.962 | 1568x853, 43 KB | yes |

## Monitors

| # | device | physical | ms | result | dominant share | jpeg | Pia window | Pia popup |
|---|---|---|---|---|---|---|---|---|
| 9 | \\.\DISPLAY1 | 2560x1440 | 47 | ok | 0.896 | 1568x882, 187 KB | excluded-see-through | excluded-black |

## Affinity after the captures

- Pia-shaped window: `0x0` (must be `0`)
- layered popup: `0x0` (must be `0`)

## Capture log

```
Debug: Capture thread DPI awareness 1 -> 2
Debug: Enumerated 1 monitors, 8 eligible windows, 367 rejected
Debug: Capture thread DPI awareness 1 -> 2
Information: Captured Window WindowsTerminal 284x73 in 8 ms
Debug: Capture target title: PopupHost
Debug: Preparing image attachment from clipboard (284x73)
Debug: Capture thread DPI awareness 1 -> 2
Information: Captured Window WindowsTerminal 2560x1392 in 24 ms
Debug: Capture target title: ✳ Submodule issue fix and branch verification
Debug: Preparing image attachment from clipboard (2560x1392)
Debug: Capture thread DPI awareness 1 -> 2
Warning: Capture of Window explorer refused: Minimized
Debug: Refused capture target title: maltm - File Explorer
Debug: Capture thread DPI awareness 1 -> 2
Warning: Capture of Window devenv refused: Minimized
Debug: Refused capture target title: Pia - Microsoft Visual Studio
Debug: Capture thread DPI awareness 1 -> 2
Information: Captured Window chrome 1580x922 in 11 ms
Debug: Capture target title: Sign in to Pia Admin - Pia Admin - Google Chrome
Debug: Preparing image attachment from clipboard (1580x922)
Debug: Capture thread DPI awareness 1 -> 2
Information: Captured Window devenv 2294x1249 in 21 ms
Debug: Capture target title: Pia.Wpf - Diff - HeadlessTurnExecutor.cs [Read Only] - Microsoft Visual Studio
Debug: Preparing image attachment from clipboard (2294x1249)
Debug: Capture thread DPI awareness 1 -> 2
Information: Captured Window Docker Desktop 1717x969 in 12 ms
Debug: Capture target title: Containers - Docker Desktop
Debug: Preparing image attachment from clipboard (1717x969)
Debug: Capture thread DPI awareness 1 -> 2
Information: Captured Window msedge 2560x1392 in 18 ms
Debug: Capture target title: Sign in successful | Claude Platform and 1 more page - Personal - Microsoft​ Edge
Debug: Preparing image attachment from clipboard (2560x1392)
Debug: Capture thread DPI awareness 1 -> 2
Information: Captured Monitor  2560x1440 in 46 ms
Debug: Capture target title: 
Debug: Preparing image attachment from clipboard (2560x1440)
```

## Legibility and verdict

Filled in 2026-09-09 from the JPEGs, which have since been deleted.

- usable frames: 6 of 6 non-minimized windows, 1 of 1 display
- black-frame rate: 0. The two `Minimized` rows are the designed refusal, not a black frame: a
  minimized window is refused with a reason rather than silently restored.
- legibility at the 1568 px ceiling: the Visual Studio grab downscaled 2294 -> 1568 and its code
  comments and line numbers stayed readable, so ~10 pt text survives the ceiling.
- Pia region in a display grab: no leak. The Pia-shaped fixture window came back see-through and
  the layered popup black, and both affinities read `0x0` afterwards, so the lease put them back.
- verdict: **GDI holds.** No move to `Windows.Graphics.Capture`.

### What this run did not cover

Office, Teams, a PDF viewer and an `mstsc` session were not open, so the composition families
actually measured were Chromium (chrome, msedge), Electron (Docker Desktop), a WPF/Win32 mix
(Visual Studio, Explorer) and a DirectX-rendered terminal. A password manager and DRM video — the
two cases the OS is expected to refuse — were not probed at all, so the near-uniform refusal is
proven against a synthetic bitmap in the unit tests but never against a real refusing app.

Re-running with those apps open would strengthen the result; it cannot weaken it, because a black
frame in an app not yet probed is a reason to refuse that app, not a reason to change the backend.

### One thing worth knowing

The capture log shows `Capture thread DPI awareness 1 -> 2` on every call: the test host runs
system-DPI-aware and the capture raises the calling thread to per-monitor-v2 for the duration.
That is why the display came back at a true 2560x1440 rather than a virtualized size. The real app
is expected to be per-monitor-v2 already, making the scope a no-op there — but the scope is what
makes the capture correct regardless of who calls it.
