# Customer feedback, round 2 — twelve reports across six surfaces

**Status:** In progress — 11 of 15 steps landed (de729b7a, f52dc026, ce3fee9d, bed9e8b7,
97e25f38). Left: B1 (needs the server half), F1 and G1a (need a live run), C2 and C3 (the
redesign).
**Owner:** Marco Altmann
**Written:** 2026-09-03
**Origin:** A customer's written feedback list handed over on 2026-09-03, with the owner's
inline annotations on four of the items. Round 1 of the same channel shipped as
`230af895`, `04435fa0`, `971280c8`, `71d54ae4` and was noted in `RELEASE.md` by `14441b72`.

Companion tracking surface:
[2026-09-03-feedback-round2-checklist.md](2026-09-03-feedback-round2-checklist.md).

---

## How to read this

Each item below is the customer's report (translated), then what the code actually does,
then the fix. Where the report could not be reproduced from the code alone it says so and
names what a repro has to show; those items are gated in the checklist rather than guessed
at. Owner annotations are marked **Owner:** and they narrow the scope — where the owner
scoped an item down, the narrow scope wins.

---

## A — Optimize hotkey fires repeatedly

**Report.** `Ctrl + Alt + O` makes the Optimize window open and close over and over. The
Assistant's hotkey leaves its window open. The two should behave the same way.

**What the code does.** `NativeHotkeyServiceFactory.Create` passes the shortcut's modifiers
straight to `RegisterHotKey` (`src/Pia.Wpf/Services/NativeHotkeyService.cs:55`) without
`MOD_NOREPEAT` (`0x4000`). Windows therefore re-posts `WM_HOTKEY` for as long as the combo
is held. Each post runs `TrayIconService.OnHotkeyPressed`
(`src/Pia.Wpf/Services/TrayIconService.cs:223`), which toggles: visible-and-foreground means
hide, otherwise show.

The two modes differ in `WindowManagerService.CanDismissWithHotkey` (`:349-361`): Optimize
may be dismissed when its input is empty and it is not comparing or optimizing; every other
mode returns `false` and can only ever be shown.

`HotkeyDebounceInterval` exists but is only consulted on the not-visible branch (`:243`), so
it never sees the repeat that hides the window.

**Two separate things are in this one report, and both are real.**

1. *The repeat.* A held combo flip-flops Optimize and merely re-shows the Assistant. Fixed
   by `MOD_NOREPEAT` on registration: one press, one toggle.
2. *The asymmetry.* Pressing each hotkey twice, deliberately, still closes Optimize and
   leaves the Assistant open. That is `CanDismissWithHotkey`, it is by design, and it is
   what the customer asked to have unified. Fixing the repeat does not answer it.

**Still open.** Which way to unify — see gate **G1**. The plain reading of the request is
that Optimize should stop closing on its own hotkey; the opposite reading, extending the
empty-composer toggle to every mode, is also coherent and is the owner's call.

## B — No character count on the Optimize input, and a bare limit error at 10,000

**Report.** The Optimize input is capped at 10,000 characters. A visible counter, or a
warning before the cap, would help.

**Where the cap lives.** Not in this repo. It is
`Pia.Server/Configuration/AiPayloadOptions.cs` — `MaxOptimizeTextLength = 10_000`, enforced
in `AiProxy/AiProxyActionService.cs:34` and surfaced as the English string
`'text' must be under 10,000 characters.` It is an admin-editable setting on the server's
Limits and Policies page, and it only applies to traffic through the Pia Cloud proxy: a user
on their own provider key has no such cap.

Two consequences the report does not state. The client has no idea the limit exists — there
is no `MaxLength` on the composer (`src/Pia.Wpf/Views/OptimizeView.xaml:151`) and no length
check anywhere in `TextOptimizationService`. And the rejection the customer saw was the raw
English server sentence, in a German UI.

**Fix, two parts.**

1. A live counter under the Optimize composer, hidden until the text is within reach of the
   limit and turning to the danger brush past it. Shown only when the mode's provider is
   Pia Cloud, because that is the only case where the number is true.
2. Map the server's 400 to a localized sentence that names the limit and the current length,
   so the message is useful even when the counter is off.

**Still open.** Where the number comes from — see gate **G2**.

## C — The edit button on a self-made template is mostly invisible

**Report.** On templates the user created, the edit affordance is hard to find because a
large part of the button is not visible.

**Owner:** the Providers and the Optimize-templates views should adopt the Personas and
Routines layout.

**What the code does.** The template card
(`src/Pia.Wpf/Views/SettingsViews/OptimizeView.xaml:110-208`) sits in a
`ColumnsPanel MaxColumns="3" MinColumnWidth="260"`. Its footer is a `DockPanel` whose button
strip is `DockPanel.Dock="Right"`: View prompt (text + icon), Set default (text + icon),
Edit (30 px), Delete (30 px). A built-in template hides the last two, so it fits; a user
template does not, and a right-docked child that is wider than the space left over is
arranged from x = 0 and overflows to the *right*. The two square buttons at the end of the
strip are the ones that fall off — the edit button first.

**Fix, in two steps that ship separately.** The clipping is one line of layout and should
not wait for a redesign, so:

- **C1** makes the footer wrap instead of clip, and is worth landing on its own.
- **C2 / C3** are the owner's redesign. Personas and Routines both use the same shape: a
  340 px `ListBox` of `PiaMasterRowCardStyle` rows on the left, and a right pane with three
  states — placeholder, read-only detail, inline editor — switched by `ShowsPlaceholder` /
  `ShowsDetail` / `IsEditorOpen` (`PersonasView.xaml:55-300`, `RoutinesView.xaml:194-584`).
  Row actions move out of the row and into the detail pane, which is what removes the
  crowding for good. Both target views are card grids today (`OptimizeView.xaml:110`,
  `ProvidersView.xaml:154`) and both need the same three view-model properties added before
  the XAML can be rewritten.

## D — Chat titles cannot be set by hand

**Report.** Being able to give a chat its own title, shown in the history, would help —
especially with automatic titling switched off.

**What the code does.** `ChatAutoTitleEnabled` defaults to `false`
(`src/Pia.Wpf/Models/AppSettings.cs:454`), so out of the box no chat is ever titled. A title
field and an `AutoTitleApplied` flag already exist on `ChatSession`
(`ViewModels/Models/ChatSession.cs:66`), and `IAssistantChatService` already has a
title-only writer that the auto-title path uses precisely so a rename does not rewrite a
growing chat (`Services/AssistantChatService.cs:284-311`).

So the persistence is in place; what is missing is the affordance and one rule.

**Fix.** A rename entry point on the history row and on the open chat's header, writing
through the same title-only path. A hand-set title sets `AutoTitleApplied` so a later
auto-title never overwrites it. Both new controls need an
`AutomationProperties.AutomationId` and a row in
`tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs` in the same change.

## E — The Closed column collapses, and where a restored task lands

**Report (two items).** Reopening a closed task always puts it in the first column; it
should go back where it came from. And the Closed column collapses by itself — both when a
task is removed and when a task is added.

**Owner** (on the first): it only has to be the *default* column.

**Restore — already correct, no change needed.** `TodoService.UncompleteAsync` resolves
`GetDefaultViewColumnAsync()` and writes that id (`Services/TodoService.cs:225-243`); the
column service returns the `IsDefaultView` column and only falls back to the lowest
`SortOrder` non-closed column when no default is set (`KanbanColumnService.cs:61-81`). The
view-model moves the item to the same column in memory
(`ViewModels/TodoViewModel.cs:421-427`). Against the owner's scoping this item is closed.
What is missing is a test that says so, so the next refactor cannot quietly regress it.

The customer's "first column" is what the fallback produces on a board where no column is
marked as the default view. Worth confirming against their install before dismissing it, but
it is not a code defect.

**Collapse — one cause, both reports.** `IsExpanded` lives on `KanbanColumnViewModel`, whose
constructor sets `_isExpanded = !column.IsClosedColumn`
(`ViewModels/Models/KanbanColumnViewModel.cs:38`). `LoadTodosAsync` clears `Columns` and
builds fresh view-models on every reload (`TodoViewModel.cs:267-296`), and adding or
deleting a todo reloads. The expansion state is therefore reconstructed from scratch and the
Closed column snaps shut.

**Fix.** Capture expansion by column id before the clear and reapply it after the rebuild.
One change covers both reports.

## F — The dismiss-all pop-up ignores its X

**Report.** Running "Dismiss all" raises a pop-up that the X button does not close.

**What the code does.** `ExecuteDismissAllAsync` (`ViewModels/RemindersViewModel.cs:306-349`)
shows no dialog. The only thing it raises is a snackbar — the success count, or "nothing to
dismiss" when the list holds nothing active. The app's snackbar carries a close button bound
to `TemplateButtonCommand` (`Resources/Styles/Snackbar.xaml:80-89`), which exists on
WPF-UI 4.3.0's `Snackbar`, so the binding is not the obvious kind of broken.

**Half of it is answered, statically.** The command is not inert: showing a `Snackbar` and
executing its `TemplateButtonCommand` does clear `IsShown`. So the X, when it is reached,
works — which leaves "something is over it".

And there is something over it. `FlowView`'s collapse scrim is a full-window
`Border Background="Transparent"` (`Controls/Flow/FlowView.xaml:325-331`) that is visible
whenever the flow rail is open and unpinned (`IsOverlayMode => IsOpen && !IsPinned`). The
whole `FlowView` sat at `Panel.ZIndex="16"` against the snackbar presenter's `15`
(`MainWindow.xaml:199-204`), so with the rail open every click in the window — the
snackbar's X included — hit the scrim, collapsed the rail and went no further. The
presenter now sits at `17`.

**Whether that is what the customer hit is still unproven.** It only bites while the flow
rail is open and unpinned, and the report does not say it was. Gate **G3** stays open until
a live Reminders → Dismiss all run either reproduces the old behaviour or does not.

## G — Restoring after minimize, and the offline error

### G-a — restore after minimize

**Report.** Opening the app after minimizing it sometimes behaves conspicuously.

**What the code does.** Minimizing is intercepted: `Window.StateChanged` turns any
`WindowState.Minimized` into `HideWindow(mode)`
(`Services/WindowManagerService.cs:100-108`), so the window leaves the taskbar rather than
minimizing. `HideWindow` then hides it and, one dispatcher pass later, sets `WindowState`
back to `Normal` while it is hidden (`:285-296`). Showing it again runs `Show()` →
`Visibility` → `WindowState = Normal` → `Topmost = true` → `Activate()` → `Focus()` →
`Topmost = false` (`:65-72`).

That sequence has three plausible sources of something visible: the un-minimize animation
running against a hidden window, the topmost flip, and the fact that a window the user
"minimized" is not where they will look for it.

**Not fixable blind** — "teilweise ein auffälliges Verhalten" does not say what was seen.
Gate **G4** asks the customer what happens, and on which path (title-bar minimize, tray
restore, or hotkey).

### G-b — the offline error

**Report.** With no internet connection an error appears. Understandable in principle, but
it should be phrased so a user can act on it.

**What the code does.** A connection failure has no arm of its own. It falls through to the
generic catch in `ChatSession.RunTurnAsync` (`ViewModels/Models/ChatSession.cs:489-502`),
which writes the **unlocalized** literal `Error: {ex.Message}` into the assistant bubble and
raises `Msg_Assistant_ResponseFailed` with the same raw text. On a German install that is an
English socket message — "No such host is known." — inside a chat bubble that will be there
forever.

**Fix.** A named arm for the offline case, ahead of the generic one: `HttpRequestException`
whose `HttpRequestError` is `NameResolutionError` or `ConnectionError`, and a
`SocketException` inner. It gets its own localized sentence saying the machine looks offline
and that the turn can be retried once the connection is back. The bubble text stops being a
raw English literal.

## H — Drag and drop: PDFs, and files that are too big

### H-a — PDF

**Report.** PDF files are not supported.

**Owner:** check whether a small free library or an OS facility can do it.

**What the code does.** `.pdf` is already classified — `FileKind.Pdf`
(`Helpers/DroppedFileReader.cs:51`) — but nothing reads it. `DroppedFileImporter` groups
`Pdf` with `Image`, `Audio` and `Unsupported` and shows "file type isn't supported"
(`:49-55`), so the classification currently buys nothing.

**The two routes, and what separates them.**

- **Text extraction.** PdfPig (Apache-2.0, pure managed, no native dependency) is the
  smallest thing that does this properly. It slots in behind the existing `ReadResult`
  contract exactly where `.docx` and `.xlsx` already sit, so the chip, the
  `<attached_file>` wrapper and the size caps all keep working unchanged.
- **OS rendering.** `Windows.Data.Pdf` is reachable on this TFM
  (`net10.0-windows10.0.17763.0`) and needs no package, but it renders pages to bitmaps — it
  has no text API. That path only pays off if the pages then go to the model as images,
  which means the Pia Cloud vision gate and one image per page. It does not fit the chip
  path at all.

They are not equivalent, and only the first answers the report as written. A scanned PDF has
no text layer and needs the second — that is a later, separate feature, not a substitute.
Gate **G5**.

### H-b — very large files

**Report.** Very large files are not accepted.

**Owner:** it only has to show a message, the way modern Outlook does.

**What the code does.** Every rejection path already shows something — `Msg_File_TooLarge`,
`Msg_File_TooLargeAttachment` (`Helpers/DroppedFileImporter.cs:67-70`,
`Helpers/DroppedFileAttachmentImporter.cs:94-97`) and `Msg_File_ImageTooLarge` for the image
path (`ViewModels/AssistantViewModel.cs:1907-1911`). What none of them do is say what the
limit *is*: "„report.log" ist zu groß zum Einfügen." leaves the user to guess whether the
answer is 2 MB or 200.

Outlook's version names the number. So does this fix: the limit rides on the read result and
the strings format it, because there is no single cap to hard-code — a plain file is capped
at 1 MB on disk, a container (`.docx`, `.xlsx`, `.msg`) at 8 MB on disk **and** 1 MB of
extracted text.

That last case is the one imprecision left: a 3 MB Word file full of text is turned down
with "the limit is 1 MB", which is true of its text and not of the file. Splitting it into
its own string — "contains more than 1 MB of text" — is the follow-up.

One caveat found while reading it: `Msg_File_ImageTooLarge` is shown whenever
`ImageAttachmentProcessor.TryPrepare` returns null, which also covers a corrupt or
unreadable image. Naming a size limit there would sometimes lie, so that path needs its
failure reasons separated before its string can name a number.

---

## I — a third round, 2026-09-03: the composer and where a chat opens

**Report (two items).** A very long draft should leave the input collapsed to four or five rows
with a way to expand it. And a chat entered from the history or the picker should open at the very
bottom.

### I-a — the composer

It already collapsed: the input carried `MaxHeight="120"` with an auto scrollbar, which is about
five lines at its font size. What was missing is the other half of the ask — a way to grow it. The
height now lives in the code-behind (`CollapsedComposerHeight` / `ExpandedComposerHeight`), a toggle
sits at the head of the composer's button strip, and it is only offered while the draft is measured
taller than the collapsed box, so it is never a dead control. Sending — which clears the draft —
puts the box back.

### I-b — where a chat opens

`AssistantViewModel.Messages` is **re-pointed** to the new session's list
(`AssistantViewModel.cs:545`), and that list is already full. No `Add` is raised, so
`OnMessagesCollectionChanged` — the only thing that scrolled — never ran, and the view kept the
offset from the chat before it.

One `ScrollToEnd` would not have been enough either: the markdown bubbles keep growing the extent
for several layout passes after the swap, and `MessageScrollViewer_ScrollChanged` deliberately
ignored growth. So the re-point now re-arms auto-scroll and posts a scroll, **and** growth is
honoured while auto-scroll is on. `IsAutoScrollEnabled` carries both meanings; a second flag was
tried and removed.

Two things this must NOT do, both held by tests. `Loaded` repeats on a re-parent, so it does not
re-arm auto-scroll — a reader who scrolled up mid-answer would be yanked to the bottom by a layout
event they never caused. And the growth branch fires on *every* growth while auto-scroll is on,
including a streaming `Content` change that `OnMessagePropertyChanged` already posts a scroll for.
Two `ScrollToEnd` calls per delta, which WPF coalesces into one layout pass — do not "fix" the
duplicate by deleting either one; each covers a case the other misses.

Two smaller things fell out. A duplicate `ScrollChanged` handler and its `_autoScroll` field were
dead — `ScrollToBottom` only ever read the dependency property — and are gone. And the collapsed
composer height was being applied on `Loaded`, leaving the box unbounded until then; it is applied
at construction now, which is how the test found it.

## What this plan does not cover

- Any server-side change. Gate **G2** may conclude that the Optimize cap belongs in a
  payload the client already receives; that work would be planned separately, in the server
  repo, and this plan's counter would then read it instead of a constant.
- Scanned-PDF OCR (see H-a).
- The Assistant's own composer length. Only Optimize is capped by the proxy.
