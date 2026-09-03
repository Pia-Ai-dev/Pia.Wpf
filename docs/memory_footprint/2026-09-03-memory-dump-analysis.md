# Pia.Wpf 2.5 GB memory footprint — dump analysis

**Status:** Finding 1 fixed and covered by tests; findings 2 and 3 open
**Owner:** man
**Written:** 2026-09-03
**Origin:** Observation after several hours of production use (chats, routines, meeting
transcription, live transcription): Task Manager reported ~2.5 GB. A full-memory dump was
taken from Task Manager and analysed at `G:\tmp\Pia\Pia.Wpf.DMP` (3.46 GB, captured
2026-09-03 11:10).

## How the dump was analysed

`dotnet-dump` (installed as a global tool) reads the managed side. Its `maddress` command
needs a WinDbg/cdb memory-region service and does **not** work under `dotnet-dump analyze`,
so the native partition was obtained by parsing the minidump's own streams directly — that
is the same `VirtualQuery` data `!address -summary` reads, so no debugger install is needed.

```bash
dotnet tool install -g dotnet-dump
dotnet-dump analyze G:/tmp/Pia/Pia.Wpf.DMP -c "eeheap -gc" -c "exit"
dotnet-dump analyze G:/tmp/Pia/Pia.Wpf.DMP -c "dumpheap -stat" -c "exit"
dotnet-dump analyze G:/tmp/Pia/Pia.Wpf.DMP -c "clrthreads" -c "finalizequeue -stat" \
  -c "dumpheap -stat -min 85000" -c "exit"
dotnet-dump analyze G:/tmp/Pia/Pia.Wpf.DMP -c "dumpheap -mt <MT>" -c "exit"
dotnet-dump analyze G:/tmp/Pia/Pia.Wpf.DMP -c "gcroot <addr>" -c "exit"
```

The minidump-stream parsers used for the native side (address-space summary by memory type,
private-allocation histogram, per-block content classification, duplicate-content detection,
thread-to-module attribution via each thread's TEB stack range, and block ownership via
embedded module pointers) are throwaway PowerShell and were not committed. Rebuild them from
this description if needed, or reach for WinDbg `!address -summary` / `!heap -s`, which give
the same partition plus NT-heap attribution.

Two traps worth remembering:

- Probing a block at a few offsets and hashing the result reports "N identical blocks" when
  the probes all land on zero-filled pages. Always check the candidate hash against the hash
  of an all-zero buffer before believing a duplicate group.
- In a full-memory dump, `MINIDUMP_THREAD.Stack` carries almost no data (0.3 MB across 155
  threads); stacks live in `Memory64ListStream`. Reach them via the thread's TEB
  (`StackBase` at TEB+0x08, `StackLimit` at TEB+0x10), not the thread stream's RVA.

## What the memory actually is

Total committed in the dump: **3360 MB** — Private 2417 MB, Mapped 510 MB, Image 432 MB
(212 loaded DLLs). Task Manager's 2.5 GB is the working set, a subset of this.

| Bucket | Size | Attribution |
|---|---|---|
| GC heap | 482 MB | 475 MB objects incl. 87 MB free; WPF binding/visual graph dominates |
| Embedding model weights | 366 MB | one float-dense private block, ~0% zero end to end |
| Repeating 4 MB blocks | 409 MB | 102 blocks, 1 region each, dense, no module pointers |
| Native heap segments | 331 MB | 21 blocks of 15–16 MB: ORT 79, Intel GPU 63, UIA 31, icu, … |
| ONNX Runtime arenas | 263 MB | 128 / 64 / 39 / 32 MB singles, largely zero = committed but unused |
| Thread stacks | ~159 MB | 159 blocks of ~1 MB, matching 155 threads |
| 2–3 MB blocks | 130 MB | 62 blocks, no module pointers |

Threads: **155 total, only 16 managed.** By owning module (most specific module found on
each thread's stack): 54 `igd9trinity64.dll`, 52 `igdml64.dll` (both Intel graphics), 23
`onnxruntime.dll`, 16 `Pia.Wpf.exe`, 10 miscellaneous (sqlite, dns, MMDevAPI, DWrite).

## Findings, in order of confidence

### 1. Event-handler leak retaining 18 `AssistantView` instances — confirmed

**18 `Pia.Views.AssistantView` objects are alive**, held by the invocation list of the
singleton `AssistantViewModel`'s `PropertyChanged` event. Proof, not inference:

```
gcroot <ListeningIndicator>
  ... -> Pia.ViewModels.MainWindowViewModel
      -> Pia.ViewModels.AssistantViewModel
      -> System.ComponentModel.PropertyChangedEventHandler
      -> System.Object[]                      <- multicast invocation list, 32 slots, 20 used
      -> System.ComponentModel.PropertyChangedEventHandler
      -> Pia.Views.AssistantView -> ... -> ListeningIndicator

dumparray -details <the Object[]>, then dumpobj on each delegate's _target:
  18 x Pia.Views.AssistantView
   1 x System.ComponentModel.PropertyChangedEventManager   (WPF's own weak-event manager)
   1 x Pia.ViewModels.AssistantViewModel                   (self-subscription)
```

The subscriber count matches the live-instance count exactly: navigation recreates the view,
each new instance subscribes, none of the previous 18 ever unsubscribed.

**Root cause, `src/Pia.Wpf/Views/AssistantView.xaml.cs:30,54-58`.** The `+=` in `OnLoaded`
and the `-=` in `OnUnloaded` are symmetric, but both are guarded by
`private AssistantViewModel? ViewModel => DataContext as AssistantViewModel` — a lookup
re-resolved at call time. By the time `Unloaded` fires, WPF has commonly already cleared the
view's `DataContext`, so `ViewModel` is null, the `if` body is skipped, and the subscription
survives in the long-lived VM. The subscribe path also becomes asymmetric if `Loaded` fires
more often than `Unloaded`, which WPF does on re-parenting and template reapplication. The
usual remedy is to cache the instance actually subscribed to in a field and unsubscribe from
that field, never from a re-resolved `DataContext`.

Everything below the `AssistantView` link is the interior of a retained view, not a second
leak. That accounts for the rest of the counts, all of which scale with the 18 views:

- **3,690 `Pia.Views.Controls.ListeningIndicator`** — 3690 / 18 ≈ 205 per retained view
- 599,829 `WeakReference` (592,860 registered for finalization), 145,276
  `WeakDependencySource`, 74,608 `PropertyPathWorker`, 74,487 `BindingExpression`,
  43,905 `DoubleCollection`, 23,689 `RenderData`, 17,509 `Grid`, 14,210 `TextBlock`
- 11,746 `HashSet<AutomationPeer>+Entry[]`, 2,159 `ItemAutomationPeer+ItemWeakReference`,
  and 31 MB of native heap under `UIAutomationCore.dll`

`AutomationPeer` links are pervasive in the `gcroot` output (780 across three traces), but
that is what any walk through a WPF visual subtree looks like — peers hold their owners and
WPF trees are reachable many ways. The peers are an amplifier of the per-view cost, not the
retainer. Peers were materialised for these trees; whether an external UIA client caused that
is not established here, since WPF loads `UIAutomationCore.dll` itself.

Separately and minor: 7,434 boxed `Pia.Models.TranscriptSpeaker` (24-byte boxed enum)
indicates enum boxing on a hot path — 178 KB, worth a look but not a footprint driver.

### 1b. Sweep of the other views

The heap answers this directly: every other screen is alive exactly once
(`VaultView`, `RoutinesView`, `AssistantHistoryView`, `NavigationSidebarView` = 1 each).
Everything sitting at 18 — `VoiceModeOverlay`, `TodoPanelControl`, `MeetingAttendeeOverlay`,
`DirectTranscriptionOverlay`, `RecordingIndicator`, `AutocompletePopup`, `RunProgressPanel`,
`PiaChatTitleChip`, `PiaChatQuickSwitcher` — is a child inside `AssistantView`'s XAML and is
cargo, not an independent leak. **Only `AssistantView` leaks today.**

The same *pattern* is latent in one more place:

- `Views/OptimizeView.xaml.cs:13,43-56` — identical `DataContext as OptimizeViewModel`
  guard around the unsubscribe, for two events (`PropertyChanged`, `FocusInputRequested`).
  It additionally does `Window.GetWindow(this)` in `OnUnloaded` to detach
  `parentWindow.Activated`; once the view is detached from the tree that call returns null
  and the unsubscribe is skipped, leaving an app-lifetime `Window` holding the view. Latent
  only because this session never navigated repeatedly to Optimize.

Correct, leave alone: `PiaVaultCategoryCard`, `PiaAssistantChatGroupCard`,
`PiaHistoryGroupCard`, `PiaReminderGroupCard` all cache the VM in a `_vm` field at `Loaded`
and unsubscribe from that field — the pattern the two broken views should adopt. The
`DirectTranscriptionOverlay` / `MeetingAttendeeOverlay` / `PiaReasoningView` /`FlowView`
overlays track old-vs-new VM through `DataContextChanged` and detach cleanly.

`ListeningIndicator` stops its `Forever` pulse storyboard in `OnUnloaded` and on
`IsVisibleChanged`, so the retained copies are not additionally rooted by animation clocks —
which also tells us `Unloaded` *did* fire on the leaked views. That is the corroboration for
the root cause: the handler ran, and `DataContext` was already null when it did.

### 1c. The fix

Both broken views now cache the instance they subscribed to and detach from that field, never from a
re-resolved `DataContext`:

- `AssistantView` — `_subscribedViewModel`, with a `DetachViewModel()` called from `OnUnloaded` *and*
  at the top of `OnLoaded` so a repeated `Loaded` cannot double-subscribe.
- `OptimizeView` — `_subscribedViewModel` and `_subscribedWindow`, same shape. The cached window is
  what fixes the `Window.GetWindow(this)` hole: the handle is captured while the view is still in the
  tree, so the detach no longer depends on finding the window after it left.

`tests/Pia.Wpf.Tests/Views/ViewUnsubscribesOnUnloadTests.cs` locks it in, asserting on the VM's real
`PropertyChanged` invocation list after a Loaded → DataContext-cleared → Unloaded cycle. All three
tests were watched failing against the old code (1 stale subscriber each) before the fix.

**Test gap, deliberate:** the `Window.Activated` detach is fixed but *not* covered. A test needs a real
`Window` ancestor, and `Window.GetWindow` returns null for a window that was never shown, so the view
never subscribes in the STA harness and the test passes vacuously — it cannot fail, so it was removed
rather than kept green. Covering it needs a shown window, which this suite deliberately avoids.

The verification that matters is empirical: re-dump after a comparable session and check that
`dumpheap -stat -type Pia.Views.AssistantView` reports 1, not 18.

### 2. Resident model — by design, but the single biggest line item

`%LOCALAPPDATA%\Pia\Models\Embeddings\paraphrase-multilingual-MiniLM-L12-v2.onnx` is
**448.5 MB of fp32**, and exactly one `Microsoft.ML.OnnxRuntime.InferenceSession` is alive.
That matches the 366 MB float-dense private block, and its SentencePiece tokenizer accounts
for another ~30 MB of managed heap on its own (250,000 `SortedSet` nodes, a 7.8 MB
`Dictionary<string,int>`, 4.3 MB `DoubleArrayUnit[]`). With the ORT arenas the embedding
stack costs roughly **630 MB**, and ORT arenas never shrink once grown.

That is not a leak. It is a sizing decision worth revisiting (int8 quantisation is ~4x
smaller; alternatively run embeddings out-of-process or server-side). Note the model-size
figure was read on HYD-DEV1; confirm the prod device carries the same model file.

### 3. Open question — 409 MB of 4 MB buffers, and 106 GPU driver threads

102 allocations of exactly ~4 MB, one region each, `PAGE_READWRITE`, dense (avg 4.9% zero),
entropy 5–6, smooth adjacent 16-bit samples, and **no module pointers anywhere inside**, so
they carry no allocator fingerprint. 4 MB is exactly 1024x1024x4 — surface/bitmap shaped, not
audio (a PCM read gives the wrong delta profile) and not fp32 weights. Circumstantially this
is GPU/render memory: 106 of 155 threads belong to the Intel display driver and several other
large regions are `PAGE_WRITECOMBINE` (GPU-visible).

A dump cannot prove this — attribution needs allocation call stacks. Cheapest test first:
fix finding 1 and re-dump. Retained views with animations, opacity or `BitmapCache` keep
intermediate render surfaces alive, so if the 4 MB block count falls with the 18 views, this
is a symptom rather than a second cause. Only if it survives that is it worth tracing
`VirtualAlloc`/native heap on the live process (ETW native-heap tracing, or WPR's heap
profile).

## What this dump cannot tell us

A single dump shows what is **retained**, never what is **growing**. Nothing here proves the
2.5 GB is unbounded rather than a high plateau. Before or alongside any fix, get a growth
signal:

- `dotnet-gcdump collect -p <pid>` twice, hours apart, on the live process, then diff in
  PerfView — small artifacts (tens of MB), full reference graphs, no string contents.
- Or a second full dump after further use, and re-run the `dumpheap -stat` comparison. If
  finding 1 is unbounded, the `AssistantView` count (and with it `ListeningIndicator` and
  `WeakReference`) climbs with the number of navigations away from and back to the chat.

## Handling note

The `.dmp` contains chat text, transcripts and probably API tokens in cleartext. A `.gcdump`
carries types, sizes and references but not string contents, and is the safer artifact to move
between machines or attach to a work item.
