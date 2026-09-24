# Assistant ↔ Chat-history switching is slow after a large import

**Status:** Cause measured against the customer's real archive shape (148 chats / 195 MB). Rendering
is the cause; the store gate is not. Fixes are tracked in
[2026-09-11-chat-history-performance-checklist.md](2026-09-11-chat-history-performance-checklist.md)
**Owner:** Marco Altmann
**Written:** 2026-09-11
**Origin:** A user imported a 195 MB OpenWebUI export — **148 chats, ten of them holding ~90 %** — and
switching between Assistant and Chat history then takes ~20 s each way, with the whole window sluggish.

## Summary

**Rendering the open transcript is the cause.** One navigation rebuilds the whole message list, and
the cost tracks **message count**, not bytes: ~17 ms and ~1.5-5 MB of heap per message, in Release.

The archive is skewed — **10 chats hold ~90 % of the 195 MB, ~140 small ones hold the rest**. That
makes the symptom depend entirely on *which chat is open*:

| Open chat | Per navigation | Heap |
|---|---|---|
| one of the ~140 light chats (11-22 msgs, 65 KB) | ~0.3 s | ~70 MB |
| 400 messages / 2 MB | 10.9-14.9 s | 808 MB |
| 800 messages / 4 MB | **19.9-22.3 s** | 1 995 MB |
| **1 573 messages / 7.9 MB** (one of the ten) | **43.1-49.9 s** | **3 152 MB** |

The reported "~20 s each way" corresponds to roughly an 800-message chat; the heaviest ten are
**twice that again**. Both sides of the switch pay it: `PiaAssistantChatInspector` re-renders the
selected chat on the history side, so a round trip is two full rebuilds.

**The store is not the cause, under any shape tested.** Opening even the heaviest chat reads its
whole transcript in **12-26 ms**. Steady-state navigation queries are 0-12 ms. Retention eviction
holds `AssistantChatService._gate` across its whole batch, which is a real unbounded hold worth
fixing, but at 148 chats it costs 4.5-6.7 s — not 20 s, and it runs once per launch.

**Correction:** an earlier pass on this page blamed the store gate, on a proxy corpus inflated to
**2 865** chats — 20x the real count. The per-chat costs were right; the conclusion was an artifact of
the wrong corpus. Chat *count* drives eviction; chat *size* drives rendering, and this archive is
few-and-huge.

## What is still unknown, and how to settle it

The shape above is inferred from the counts the reporter gave (148 chats / 195 MB / ten dominant
ones); the customer's file itself was never available. One thing would pin it exactly: **the message
count of their biggest chats.**

The converter keeps message `content` and drops `files` (attachments) and citation bodies, so the
same 195 MB export lands very differently depending on what those bytes are:

| 148-chat, ~195 MB export | `history.db` | Import | Eviction | Heaviest chat |
|---|---|---|---|---|
| skewed text (10 x 1 573 msgs) | **241 MB** | 12.0 s | 6.7 s | 43-50 s to render |
| even text (127 msgs/chat) | 225 MB | 8.4 s | 4.5 s | 3.4 s |
| attachment-heavy (8 msgs/chat) | **21 MB** | 0.8 s | 0.4 s | ~0.3 s |

Two ways to settle it, cheapest first:

1. **Ask for the size of `%LOCALAPPDATA%\Pia\history.db`.** ~240 MB means the text survived import and
   rendering is certainly the cause. **A ~20 MB database does not clear rendering**, though — it caps
   the *total* text, not the largest chat, and 20 s needs only ~800 messages (~4 MB) in the one chat
   the user had open. It only says the archive is not uniformly heavy.
2. **If you can get a copy of their `history.db`, run `ChatStorePerfProbe.MeasureOpenChat` against
   it.** It prints the message count and text size of the heaviest and lightest chats directly, which
   is the number the whole estimate turns on.

For scale, the proxy export (`artifacts/chat-export-1787290830479.json`, 573 chats / 36 MB) is only
**15.8 %** message content; 19.4 % attachments, 8.2 % citation bodies — and every chat over 0.5 MB in it
is attachment-driven with 2-6 messages. So the 21 MB column is not a hypothetical.

## Measurements

Release build throughout. The probes are `ChatStorePerfProbe` (`tests/Pia.Wpf.Tests/Services/`) and
`AssistantViewPerfProbe` (`tests/Pia.Wpf.Tests/Views/`), both `Explicit` so the gate ignores them.

### Rendering: what one navigation costs

A navigation discards the view and builds a new one over the same ViewModel — measured through a real
`ContentPresenter` swap inside a real `Window`, 900×700.

| Open chat | Build | Pump | Heap |
|---|---|---|---|
| empty | 156–229 ms | 22–50 ms | 65 MB |
| 10 messages | 395–491 ms | 32–59 ms | 88 MB |
| 50 messages | 1 416–1 685 ms | 67–78 ms | 197 MB |
| 200 messages | 3 120–3 474 ms | 618–857 ms | 672 MB |
| 400 × 5 150 chars | 10 882–14 915 ms | — | 808 MB |
| 800 × 5 150 chars | 19 933–22 254 ms | — | 1 995 MB |
| 1 573 × 5 150 chars | 43 114–49 875 ms | — | 3 152 MB |

Marginal cost ~17 ms per message. **A brand-new empty chat is ~0.2 s** — that was G3's question, and
it is answered: an empty chat is not slow, but it is also not the state the report was made in.

**The driver is message count, not bytes.** At a fixed 1.27 MB of text: 1 300 messages of 1 000 chars
cost 22.8–29.7 s, while 40 messages of 32 500 chars cost 3.0 s — the same bytes, 8× apart. Every
message pays a fixed price for its own `PiaAssistantMessage`, `MarkdownMessageControl` and the
`DataTrigger`-hidden second branch, so a chat's cost is its turn count.

Heap is measured after a forced full GC with the chat open and one view built, so it is the standing
cost of having that chat open, not a leak. Note that before `cf57398b` the discarded tree was also
never released, so each navigation *added* that much — which is what "the whole window turns
sluggish" describes.

### The store

Navigation queries — what `AssistantHistoryViewModel.LoadChatsAsync` issues — against 148 chats:
`first=13 ms, median=0 ms` with the seeded 30-day filter, `0–1 ms` with it cleared. Indexed and paged.

**Opening a chat is cheap even when the chat is huge.** `GetAsync` reads the whole transcript under
the gate, so it was the obvious suspect under a skewed archive — it is not:

| Chat | `GetAsync` |
|---|---|
| 1 603 messages / 8.1 MB of text | **26 ms** |
| 1 581 messages / 8.1 MB of text | 12 ms |
| 11–22 messages / 65 KB | 0 ms |

So of the ~43 s it takes to open one of the ten heavy chats, the store contributes ~0.03 s.

Eviction, 123 of 148 chats past the 180-day default:

| Corpus | Eviction | Longest blocked query |
|---|---|---|
| skewed text (241 MB db) | 6.7 s | 6 695 ms |
| even text (225 MB db) | 4.5 s | 4 496 ms |
| attachment-heavy (21 MB db) | 0.4 s | 427 ms |

Splitting the batch's two statements shows where even that goes — 148 chats, transaction rolled back:

| Statement | text-heavy | attachment-heavy | Plan |
|---|---|---|---|
| `DELETE FROM AssistantChats WHERE Id = ?` | 0.7 ms | 0.1 ms | `SEARCH … USING INDEX` |
| `DELETE FROM AssistantChatsFts WHERE ChatId = ?` | **31.4 ms** | 2.7 ms | `SCAN … VIRTUAL TABLE` |

The FTS delete is ~98 % of the batch and scales with the *index*, not the row — it is a native scan of
a content-carrying FTS5 table over an `UNINDEXED` column, so Release is no faster than Debug (measured
18.1 vs 18.2 ms on the proxy corpus). The same scan runs on every chat **save**, which is what makes a
bulk import superlinear in chat count.

### The other gate sharers are cleared

- **Push drain** (`AssistantChatSyncService.DrainAsync` → `ProcessOpAsync`) takes the gate for one
  `GetAsync`, releases it, and only then does the HTTP PUT. No batch.
- **Pull** re-saves each remote chat via `SaveFromRemoteAsync`, taking the gate per chat.
- `DeleteAllUnderGateAsync` **is** a second batch under one hold, same shape as eviction, but
  user-initiated.

`EvictUnderGateAsync` is the only batch-under-one-hold that runs unprompted.

## Retention deletes the archive — independent of any of the above

`Normalize` sets an imported chat's `LastAccessedAt` from its original timestamps, and
`AssistantChatRetentionService` runs 5 s after startup with a 180-day default. **123 of 148 chats were
evicted** in all three corpora. That is silent data loss on the next launch, it has nothing to do with
latency, and if the customer has relaunched since importing it has already happened to them.

## How to reproduce

1. Build a corpus at the real shape: 148 chats, ~195 MB, and decide the split. Each chat needs a
   unique top-level `id` **and** `chat.id` — `StoreAsync` skips a chat whose id already exists with an
   `UpdatedAt` at least as new, so duplicates import as `upToDate`. Message ids need no help;
   `Normalize` re-keys collisions inside the batch. Spread `updated_at` over years so retention has
   something to target. OpenWebUI repeats every message in both `chat.messages` and
   `chat.history.messages`, so a text-heavy chat's JSON is ~2× its text.
2. `PIA_PERF_DIR=<scratch>` `PIA_PERF_EXPORT=<export.json>`, then run the probes off the built exe —
   never `dotnet test`, and launch it detached so nothing owns its stdout:

   ```
   Pia.Wpf.Tests.exe -explicit only -method Pia.Tests.Services.ChatStorePerfProbe.Import
   Pia.Wpf.Tests.exe -explicit only -method Pia.Tests.Services.ChatStorePerfProbe.Measure
   Pia.Wpf.Tests.exe -explicit only -method Pia.Tests.Services.ChatStorePerfProbe.MeasureEvictionSql
   Pia.Wpf.Tests.exe -explicit only -method Pia.Tests.Views.AssistantViewPerfProbe.MeasureLargeChatShapes
   ```

   `Measure` evicts, so copy the built `history.db` aside first if you want to re-run it.

## The rendering defects

1. `AssistantView`'s message list is a plain `ItemsControl` inside an outer `ScrollViewer` — a shape in
   which WPF cannot virtualize at all. Every message in the open chat is fully realized.
2. Views are hosted by `DataTemplate` (`App.xaml`) behind `MainWindow`'s
   `NavigationContentPresenter`, so each navigation destroys the old tree and builds a new one. The
   cost in (1) is paid again on every switch.
3. ~~`PiaChipOverflowPanel` never released its `ItemsSource.CollectionChanged` subscription~~ — fixed;
   `ChipOverflowPanelLifetimeTests` holds it.

The item template in `src/Pia.Wpf/Views/AssistantView.xaml` builds **both** branches for every message
— the user bubble and the assistant bubble — and hides the wrong one with a `DataTrigger`. Collapsed
is not uncreated: the objects, styles and bindings are all built. So a user message still constructs
`PiaAssistantMessage`, and an assistant message still constructs `PiaCollapsibleMessageText`.

`PiaAssistantMessage` alone pulls in `PiaReasoningView`, two `PiaChipOverflowPanel`s,
`PiaAnswerToolbar` (~250 lines of XAML), `PiaSuggestionChips` and `MarkdownMessageControl`. The last
is a `RichTextBox` carrying an inline 8-item `ContextMenu`, and its constructor allocates an empty
`FlowDocument` and assigns it before the bound text ever arrives.

`PiaAssistantChatInspector` (the history view's detail pane) has the same unvirtualized shape over
`SelectedChatMessages`, and `AssistantHistoryViewModel.LoadChatsAsync` re-resolves `SelectedChat` on
every navigation — which is why the history side pays the transcript cost too.

## Secondary findings

- `AssistantHistoryViewModel` seeds `FilterStartDate` to today minus 30 days. An imported archive
  keeps its original timestamps, so only 5 of 148 chats are visible until the filter is cleared —
  `RevealImportedChatsAsync` widens it right after an import, but not on any later launch.
- The import event storm is already handled: `OnChatsChanged` returns early while `IsImporting`, and
  debounces otherwise.
- A text-heavy import roughly **doubles on disk**: a 189 MB export became a 225 MB database, because
  the content-carrying FTS index holds a second copy of every message.
- The SQL read paths are fine: `SearchAsync`/`CountAsync` are paged and use `IX_AssistantChats_UpdatedAt`
  and `IX_AssistantChatMessages_ChatId_Ordinal`, and `TouchLastAccessedAsync` is a plain indexed `UPDATE`.
