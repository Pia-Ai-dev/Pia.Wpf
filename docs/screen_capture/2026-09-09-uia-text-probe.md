# Screen text — UI Automation probe results

**Status.** Measured 2026-09-09. Verdict: Mixed — Chromium yields page text, Electron yields none.
**Owner.** Marco Altmann
**Written.** 2026-09-09
**Origin.** The UIA text-snapshot step of [2026-09-07-screen-vision-checklist.md](2026-09-07-screen-vision-checklist.md).

## Procedure

With NO screen reader and no accessibility setting enabled, and each app STARTED FRESH for the run — Chromium builds its accessibility tree the first time a UIA client asks, so a warm hit after a cold miss is a finding, not a failure — open: Edge or Chrome on a text-heavy page; VS Code with a source file; Teams (a chat with text; it is WebView2/Chromium); Slack or another Electron app if present; Word with a paragraph; Notepad; an mstsc window (expect `NoText`); a PDF in Edge. Unlock the desktop and run from an interactive terminal:

```
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --explicit only --filter-class Pia.Tests.Services.Screen.UiaTextSnapshotDesktopProbe
```

Then pick one visible paragraph per app, count how many of its words appear in that window's text file, fill in **coverage**, delete the temp folder named at the end, commit this document, and write the verdict below into the checklist.

Cell format: `outcome/stage/elements/textNodes/chars/ms`. `stage` is `fetch` when the one unbounded subtree fetch timed out before any element was visited, `walk` otherwise.

## Environment

- OS: `Microsoft Windows NT 10.0.26200.0`
- MaxElements: `4000`, MaxChars: `8000`, shipped watchdog: `5s`, probe watchdog: `30s`

## Positive control

- outcome: `Ok`
- elements: 10, text nodes: 8, chars: 75, ms: 54

## Windows

| # | process | title chars | cold | warm | stable | coverage (human) | fresh start? (human) |
|---|---|---|---|---|---|---|---|
| 1 | WindowsTerminal | 9 | Ok/walk/4/1/72/7ms | Ok/walk/4/1/72/2ms | True | n/a, a flyout | no |
| 2 | WindowsTerminal | 45 | Ok/walk/39/15/418/21ms | Ok/walk/39/15/418/11ms | True | high, tabs + buffer | no |
| 3 | chrome | 48 | Ok/walk/49/19/398/21ms | Ok/walk/61/29/725/13ms | True | ~100%, full page body | no |
| 4 | devenv | 78 | Ok/walk/443/174/2794/348ms | Ok/walk/443/174/2794/339ms | True | high, native tree | no |
| 5 | Docker Desktop | 27 | Ok/walk/10/1/27/11ms | Ok/walk/10/1/27/5ms | True | **0%, title only** | no |
| 6 | msedge | 81 | Ok/walk/75/39/1152/20ms | Ok/walk/75/39/1152/10ms | True | browser chrome + tab titles only | no |

## Coverage

Extracted text per window: `C:\Users\maltm\AppData\Local\Temp\pia-uia-probe-20260909-082338` — the user's own screen. Read it, fill the table's coverage column, then delete the folder. Never commit it.

## Verdict

Filled in 2026-09-09 from the extracted text, which has since been deleted.

**Mixed.** Chromium passes; the one Electron app on the desktop returns nothing but its own title.

- **Chromium: yes.** Chrome's snapshot carried the whole visible document — headings, form labels,
  the body paragraph and the address-bar URL — at 725 chars in 13 ms warm, with no accessibility
  setting touched. That is the answer the gate was asking for.
- **Electron: no.** Docker Desktop returned 27 chars, which is exactly its window title and nothing
  else: 10 elements, 1 text node. The renderer's tree is simply not there for a UIA client.
- Every window returned `Ok` and `stable`, and the slowest warm walk was 339 ms against Visual
  Studio's 443 elements — an order of magnitude inside the 5 s shipped watchdog. Nothing here is a
  timeout or a performance problem; the failure is an empty tree, not a slow one.

### What follows from Mixed

Per this document's own bar: **the OCR fallback stops being conditional and becomes required.** A
watch loop that reads only UIA would be blind to every Electron target, and Teams is an Electron
target. So step E2 moves from "only if the probe came back mixed" to a prerequisite of E3, and E3
must treat an empty snapshot as "use the fallback", not as "the window has no text".

### How much weight this verdict carries

Two caveats, both of which argue for one more run rather than against the conclusion:

- **The Electron half rests on a single app.** Docker Desktop was the only Electron window open.
  Teams and VS Code — the two that actually matter for this feature — were not running, and Teams
  is WebView2, which may behave like Edge rather than like Docker Desktop. "Electron yields
  nothing" is therefore one measurement, not a family result.
- **No app was started fresh.** The procedure above asks for a cold start, because Chromium builds
  its accessibility tree the first time a UIA client asks. Chrome's cold-to-warm growth
  (398 -> 725 chars) is that construction happening mid-probe. Both snapshots still returned
  `Ok`, so the lazy build did not produce a failure here — but a genuinely cold first tick in
  production may return less than the second one, which E3 should expect rather than treat as an
  empty tree.
- Neither browser was on a text-heavy page: Chrome held a login form and Edge an OAuth success
  page. Chrome still returned its full body text, so the positive result stands; Edge's thin
  result is the page's fault, not the extractor's.

A re-run with Teams and VS Code open and every app started fresh would settle whether the Electron
result is universal. It cannot overturn the Chromium "yes", and it cannot make OCR unnecessary —
one confirmed blind target is already enough to require the fallback.
