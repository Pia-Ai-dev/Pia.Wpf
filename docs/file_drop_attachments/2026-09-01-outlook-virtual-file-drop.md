# Dragging a mail straight out of Outlook: the virtual-file drop

**Status:** Shipped — confirmed end to end in the real app 2026-09-01 (§9); new Outlook measured and ruled out.
Extended 2026-09-02 with a third gate so a nested target can claim a drop (§10)
**Owner:** Marco Altmann
**Written:** 2026-09-01
**Origin:** Open question 5 of [2026-08-31-file-drop-attachments-plan.md](2026-08-31-file-drop-attachments-plan.md) ("Does the Outlook drag have to work before this ships?"), answered *yes* by the owner. §16 of that plan sketched this branch and priced it `L`; gate **G0** had closed it out of scope for round 1.

---

## 1. What was wrong

`FileDropBehavior` accepted one clipboard format, `DataFormats.FileDrop` (CF_HDROP), in both `TryAcceptDrag`
and `OnDrop`. A mail in Outlook's message list is a MAPI store row with no path on disk, so the drag carries
no CF_HDROP at all — it carries `CFSTR_FILEDESCRIPTORW` naming the items and `CFSTR_FILECONTENTS` streams the
receiver has to pull and write itself. Explorer does exactly that, which is why dropping a mail into a folder
yields a real `.msg`. Pia rejected the drag at `DragEnter`: no overlay, no handler, nothing.

Everything downstream of a path already shipped in rounds 1 and 2 — the `.msg`/`.eml` readers, the chip strip,
the `<attached_file>` wrapper, the caps. This work only had to produce a path.

## 2. What each source actually publishes

Measured on 2026-09-01 by enumerating `IDataObject::EnumFormatEtc` inside `DragEnter` against real drags. This
is the fact the whole design rests on, and no amount of reading settles it — the enumeration is still in the
code, DEBUG-only, so it can be re-run (§7).

| Source | Formats advertised | Outcome |
|---|---|---|
| **Outlook classic** (Office16 `OUTLOOK.EXE`) | `RenPrivateItem`, `FileGroupDescriptor`, `FileGroupDescriptorW`, `FileNameW` (`TYMED_FILE`), **`FileContents` (`TYMED_ISTREAM, TYMED_ISTORAGE`)**, `Object Descriptor`, `CSV`, … — **no CF_HDROP** | Works. Materialised through **ISTORAGE**. |
| **Explorer** | `Shell IDList Array`, `FileDrop`, `FileName`, `FileNameW`, `FileContents`, `FileGroupDescriptorW`, `ZoneIdentifier`, … | Works. Takes the CF_HDROP path — the file already exists, so there is nothing to materialise. |
| **Outlook new** (MSIX `olk.exe`) | `DragContext`, `DragImageBits`, `chromium/x-renderer-taint`, `FileDrop`, `Chromium Web Custom MIME Data Format` | **Cannot work.** See §5. |

The important line is Outlook classic's `FileContents`: it offers **`TYMED_ISTORAGE`**, not just a stream. A
`.msg` *is* a compound file, so Outlook hands over the live `IStorage` and the receiver is expected to create
a docfile and `CopyTo` into it. An ISTREAM-only implementation would have failed with `DV_E_TYMED` against
the one source that matters most.

## 3. How it works now

Two gates instead of one, in `src/Pia.Wpf/Behaviors/FileDropBehavior.cs`:

```
DragEnter (first enter only)          Drop
  |                                    |
  +-- CF_HDROP present?                +-- verdict == Paths      -> read CF_HDROP, filter by extension
  |     yes -> DragVerdict.Paths       |
  |                                    +-- verdict == Descriptor -> materialise, THEN filter by extension
  +-- FileGroupDescriptorW present     |
        and a descriptor name          +-- verdict == Reject     -> ignore
        passes the filter?
          yes -> DragVerdict.Descriptor
```

**The verdict is cached in an attached DP** (`DragVerdictProperty`), computed once when the drag arrives and
reused by `DragOver`. Both reads cross a process boundary and `DragOver` fires at mouse-move frequency, so
recomputing it per move would put a cross-process call on every mouse message. This mirrors the existing
`DragCounterProperty` trick.

**A third gate arrived 2026-09-02**, ahead of both of the above: see §10.

**Materialisation runs synchronously on the drag's own thread**, inside `OnDrop`. The source is free to tear
its data object down the moment `DoDragDrop` returns, and the interface is apartment-bound, so awaiting or
handing it to a worker produces intermittent `RPC_E_DISCONNECTED`. A few hundred KB of synchronous disk write
is the correct trade.

**Several mails in one drag work.** Measured 2026-09-01: a two-message selection dragged out of classic
Outlook logged `wrote item 0 from a storage` / `wrote item 1 from a storage` / `materialised 2 of 2 items`,
and the two chips carried different names and different sizes (2,942 and 3,085 extracted characters) — so the
per-item `lindex` really does reach Outlook, rather than both items resolving to the first stream. There is no
single-item cap.

This corrects a detail in the plan's §16 risk row, which says "WPF's managed `DataObject` returns only the
first". That is true of the *managed* `GetData(string)`, but `System.Windows.DataObject` implements
`System.Runtime.InteropServices.ComTypes.IDataObject` explicitly and forwards those calls, `lindex` included,
straight to the source's native object. Casting `e.Data` is enough; no reflection into WPF internals is needed.

**Three media are handled**, tried as a combined mask first and then one at a time for a source that refuses
a combined `tymed` (`ShellFileContentsMaterializer.TryWriteItem`):

- `TYMED_ISTORAGE` — `StgCreateDocfile` + `IStorage::CopyTo` + `Commit`. **This is the classic-Outlook path.**
- `TYMED_ISTREAM` — read the `IStream` to a file. Attachments dragged out of an open mail arrive this way.
- `TYMED_HGLOBAL` — `GlobalLock`/`GlobalSize`/copy.

### The accepted-extension check moved after materialisation

A dragged mail has no path, so the descriptor's `cFileName` is the only place an extension exists. The check
therefore runs **twice**, deliberately:

- **At hover, on the descriptor names** — not for correctness but for feedback. Without it the overlay would
  invite a drop of anything a shell source happens to offer. The CF_HDROP branch has no equivalent when the
  drag carries no readable paths yet: it is accepted unconditionally, because a source that defers the write
  to the drop cannot be told apart at hover from one carrying nothing. §5 is where that leads.
- **After materialisation, on the real path** — this one is authoritative, and it is the one the task
  required. `FilterAccepted(result.Paths, …)` in `MaterializeShellDrop`.

### The descriptor name is not trusted as a path

`cFileName` is a `WCHAR[260]` from another process and is used to build a path.
`FileGroupDescriptor.ToSafeFileName` truncates at the first NUL, replaces every
`Path.GetInvalidFileNameChars()` character, trims trailing dots and spaces, caps the stem at 80 characters
(a mail subject is often far longer) while keeping the extension, and returns null unless
`Path.GetFileName(name) == name`. `..\..\evil.msg` becomes the harmless leaf `.._.._evil.msg`.

## 4. Where the materialised files live, and where they are deleted

Written to `PiaPaths.DropCacheDirectory` → `%LOCALAPPDATA%\Pia\DropCache\<12-hex>\<name>.msg`. It is a
**property** off `LocalDataDirectory`, so `PIA_LOCAL_DATA_DIR` reroutes it and a UI-test profile gets a
throwaway one; `PiaPathsTests.RoutedMember_ObservesAnOverrideAppliedAfterItsTypeIsLoaded` has a row for it.
No `Path.GetTempPath`, no `Environment.GetFolderPath` — CLAUDE.md forbids the latter.

One fresh GUID subdirectory **per drop**, not a fixed name. A fixed name would make two mails with the same
subject collide and silently lose one; a per-drop directory costs only a duplicate chip if the same mail is
dragged twice, which is the cheaper mistake.

Deleted in exactly three places, all in `ShellDropCache`:

1. **`App.OnStartup` calls `ShellDropCache.Clear()`.** Nothing in the cache survives a run. This is the
   guarantee — it holds even if the app is killed mid-drop.
2. **Every drop first sweeps drop directories older than two minutes.** The grace period exists only to
   protect a second drop landing while the first is still reading.
3. **The drop's own directory is deleted immediately if nothing usable materialised.**

The tight lifetime is safe because **nothing re-reads the path after staging**:
`DroppedFileAttachmentImporter.TryStageAsync` copies the extracted text into `PendingFileAttachment.Text`
during the drop, and `AssistantPromptComposer.BuildAttachedFileBlock` renders from `Text`. `FullPath` survives
only as the chip's tooltip and its dedup key. `ShellDropCacheTests` holds all of this.

## 5. New Outlook is out of scope, and why

This is the one part of the request that cannot be delivered, and it is not a matter of effort.

New Outlook (`olk.exe`) is a Chromium/WebView2 host. Its message-list drag advertises `FileDrop` in
`EnumFormatEtc` and then **refuses to serve it**: `GetData(CF_HDROP)` returns `0x80040064`
(`DV_E_FORMATETC`) — measured both at hover *and* at drop, through the COM `IDataObject` directly rather than
through WPF's managed `GetData`, which swallows the HRESULT and returns null. There is no
`FileGroupDescriptorW` and no `FileContents` either. What the drag really carries is
`Chromium Web Custom MIME Data Format`, a JSON blob of the app's own state:

```json
{"itemType":"multimaillistconversationrows", "rowKeys":["AQAACIWzWL4B…"],
 "subjects":["…"], "latestItemIds":["AQMkADAw…"],
 "mailboxInfos":[{"mailboxSmtpAddress":"…","mailboxProvider":"Outlook"}], "sizes":[195908]}
```

Those are mailbox row identifiers, not bytes. Getting the message would mean calling Microsoft Graph with
`latestItemIds` — a different feature with its own authentication, consent and offline story, not a
drag-and-drop fix. Dragging to the desktop works only because the shell has a path we do not.

**What the user sees instead.** The drag is accepted (a Chromium source that defers CF_HDROP until the drop is
a real pattern, and cannot be told apart at hover), and on drop, when nothing arrives, `DropFailedCommand`
fires with a null name and `Msg_File_DropNoFile` is shown: *"That app hands over no file to drag — only its
own internal reference. Save the item as a file first, then drag that."* Saving from new Outlook yields an
`.eml`, which round 1 already reads. A silent no-op or a bare red cursor would have left the user with no idea
what to do.

**And it is shown in the composer, not only as a snackbar** — `AssistantViewModel.DropFailureMessage`, a line
above the input box where the chip would have appeared, cleared as soon as the user types or a drop succeeds.
The snackbar alone was measured insufficient: WPF-UI renders it in the **top-right corner**, and a drop does
not activate the target window, so after dragging out of Outlook the source app stays in front and can cover
that corner while the user is watching the composer. The owner reported "nothing happens" twice against a
build that was raising the snackbar correctly every time — confirmed in the log, and confirmed rendering by
capturing the window at 400 ms intervals while triggering an unrelated snackbar through
`PIA_DEBUG_DROP_FILES`. Feedback about a drop belongs where the drop landed. The composer line was then
confirmed on screen against a real new-Outlook drag.

## 6. Files

| File | What |
|---|---|
| `src/Pia.Wpf/Native/ShellDataObject.cs` | new — `IStorage`, `StgCreateDocfile`, `ReleaseStgMedium`, `GlobalLock`/`Size`/`Unlock`, clipboard-format registration |
| `src/Pia.Wpf/Helpers/FileGroupDescriptor.cs` | new — the 592-byte `FILEDESCRIPTORW` parser and `ToSafeFileName` |
| `src/Pia.Wpf/Helpers/ShellFileContentsMaterializer.cs` | new — descriptor fetch, per-`lindex` `FileContents` pull, the three media, the COM CF_HDROP read |
| `src/Pia.Wpf/Helpers/ShellDropCache.cs` | new — the scratch directory and its three deletions |
| `src/Pia.Wpf/Behaviors/FileDropBehavior.cs` | the second gate, the cached verdict, `DropFailedCommand`; `HasNearerTarget` (§10) |
| `src/Pia.Wpf/Paths/PiaPaths.cs` | `DropCacheDirectory` |
| `src/Pia.Wpf/App.xaml.cs` | the startup clear |
| `src/Pia.Wpf/ViewModels/AssistantViewModel.cs`, `OptimizeViewModel.cs` | `HandleDropFailedCommand` |
| `src/Pia.Wpf/Views/AssistantView.xaml`, `OptimizeView.xaml` | the `DropFailedCommand` binding |
| `MessageStrings{,.de,.fr}.resx` | `Msg_File_DropFailed`, `Msg_File_DropNoFile` |

Optimize gets the working drag for free, because the gate is in the shared behavior and both views already
list `.eml,.msg`.

## 7. Re-running the measurement

The format enumeration is permanent but DEBUG-only (`[Conditional("DEBUG")]`, so it is erased from release
IL along with its arguments). Run a Debug build, drag from the source in question, and read
`%LOCALAPPDATA%\Pia\Logs\pia-*.log`:

```
grep -E "Drag arrived|advertises|wrote item|materialised|Drop verdict" pia-2026-09-01.log
```

`Drag arrived with N formats` proves the drag reached the handler at all — the first thing to establish, and
the thing that separates "we rejected it" from "it never got here".

## 8. What is not covered

- **New Outlook** (§5).
- **UI automation.** UIA cannot synthesize a `DoDragDrop` transfer, which is why `PIA_DEBUG_DROP_FILES`
  exists; that bypass feeds a path list and so exercises everything *after* materialisation, never the
  materialiser. The descriptor parser and name sanitizing are unit-tested against synthetic buffers
  (`FileGroupDescriptorTests`), and the layout those buffers encode was confirmed against a real Outlook drag
  — a synthetic fixture cannot validate a struct layout it was generated from.
- **More than 20 items in one drag.** Past that we would be writing an unbounded amount of another process's
  data to disk on the UI thread. The extras are reported through `DropFailedCommand`, not dropped silently.

## 9. Confirmed in the app, 2026-09-01

Driven by hand against the owner's real profile, because UIA cannot synthesize a `DoDragDrop` transfer (§8).
Each line is a log-backed observation, not an inference.

| Leg | Evidence |
|---|---|
| One mail out of Outlook classic | `wrote item 0 from a storage` → `materialised 1 of 1` → `Drop verdict=Descriptor accepted=1` → a chip named `Wöchentlicher Aktivitätsbericht für Mandy.msg`, umlauts intact, composer untouched |
| **Two** mails in one drag | `wrote item 0` / `wrote item 1` → `materialised 2 of 2` → two chips, different names, 2,942 and 3,085 extracted characters — so `lindex` reaches Outlook |
| Repeat drags | Four further drops over twenty minutes, each staging correctly; one drop cache directory per drop, real `.msg` files of 185,344 and 103,936 bytes |
| The startup clear | `DropCache` absent after each relaunch |
| Explorer, unchanged | Staged with no `materialised` line at all, i.e. it took the CF_HDROP branch |
| **A question about a dragged mail** | Answered from the mail's own content, so the wrapper reaches the model through the materialised path as it does through the picker |
| New Outlook | `Drop: the source refused CF_HDROP (0x80040064)` at hover and at drop, `Drop verdict=Paths accepted=0`, and the composer line explaining it |

Still owed, and unrelated to the materialiser: checklist **C5**, the mail-quality pass over a wider spread of
real mail — an HTML-only message and one relayed through Gmail for the `(UTC)` date suffix.

## 10. The nested-target gate, 2026-09-02

Added for the meeting-invite drop, which needed the meeting attendee overlay — a child of
`AssistantView`'s `RootGrid` — to receive a `.msg` that `RootGrid` already accepts.

The handlers here are **tunnelling** (`PreviewDragEnter`/`Over`/`Leave`/`Drop`) and `TryAcceptDrag` set
`e.Handled = true` unconditionally, so an ancestor with the behavior saw every drag first and swallowed it.
A drop meant for the overlay became a chat attachment, under the chat's own hint — which is drawn at
`Panel.ZIndex="20"`, above the overlay's `11`, so the wrong overlay was on screen too.

```
every handler
  |
  +-- HasNearerTarget(sender, e.OriginalSource)?
        yes -> return, touching neither e.Effects nor e.Handled, so the tunnel reaches the nearer target
        no  -> the two gates of §3
```

`HasNearerTarget` walks from `e.OriginalSource` to `sender` (visual parent, logical for a
`ContentElement`) and answers yes if any node between them has `IsEnabled` set. Three things make it safe
for the chat path this doc shipped:

- **A collapsed subtree is not hit-tested**, so `OriginalSource` can never land inside a hidden inner
  target. That matters because the overlay binds `IsEnabled` to `IsJoinSetupVisible`, which is true even
  while the overlay is collapsed.
- **The check is symmetric across enter and leave**, so `DragCounterProperty` stays balanced: a matched
  pair is either both skipped or both counted.
- **In `OnDrop` the stand-down runs *after* the counter and `IsDragOver` reset.** The drag is over either
  way, and returning first would strand the ancestor's hint on screen.

It also means the ancestor's `IsDragOver` never goes true while an inner target is under the cursor, so the
inner target's own hint is the only one drawn — no ZIndex fight.

`HasNearerTarget` is `internal` so `FileDropBehaviorTests` can drive it directly; the handlers cannot be
tested, because `DragEventArgs` has no public constructor (§8 still stands).

Confirmed by hand 2026-09-02 across all three legs: Explorer → overlay, Outlook classic → overlay (the same
link out of both, so the materialiser reaches the nested target), and — the regression this change could have
caused — a drop on the chat with the overlay closed still staging its chip.
