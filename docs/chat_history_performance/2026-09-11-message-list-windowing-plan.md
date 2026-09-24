# Plan: bound what the chat transcript renders

**Status:** Implemented. Every acceptance criterion below has a test; tracked step by step in
[2026-09-11-chat-history-performance-checklist.md](2026-09-11-chat-history-performance-checklist.md).
It was the critical path — the archive is skewed, ten chats holding ~90 % of 195 MB, and one navigation
over one of those (1 573 messages) cost **43–50 s and 3.2 GB of heap** in Release.
**Owner:** Marco Altmann
**Written:** 2026-09-11
**Origin:** [2026-09-11-large-import-slowdown.md](2026-09-11-large-import-slowdown.md), which measured
~24 ms per message per navigation after a 195 MB / 148-chat OpenWebUI import, and cleared the store:
reading even the heaviest transcript takes 26 ms.

## Goal

Rendering the Assistant transcript and the history inspector must cost a bounded amount, not the whole
chat. Today it costs the whole chat, every time the view is built, and the view is rebuilt on every
navigation.

**Approach: a windowed projection plus an explicit "load older" step — not WPF virtualization.** The
owner has been burned by container recycling before, and the measurements say the two approaches are
much closer to each other than either is to today.

| Rendered messages | Per navigation | Heap |
|---|---|---|
| 10 | 0.76–0.90 s | 51 MB |
| 15 | 0.99–1.10 s | 87 MB |
| 25 | 1.41–1.65 s | 117 MB |
| 50 | 1.62–1.78 s | 131 MB |
| 75 | 1.99–2.02 s | 282 MB |
| **unbounded (1 573)** | **43–50 s** | **3 152 MB** |

Measured Release, 900×700, 5 150-char bodies (the export's real median body is ~4.5 KB), through a real
`ContentPresenter` swap inside a real `Window`. Reproduce with
`AssistantViewPerfProbe.MeasureWindowSizes`.

A window of 50 is a 25–30× improvement. Virtualization would reach ~0.3–0.8 s; that difference is not
worth the recycling risk, and the window size is a tunable constant if it ever is.

## Acceptance criteria

Measured, not judged. Each is a test, not an opinion.

1. **Bounded realization.** Opening a 1 573-message chat realizes item containers for the window size,
   not for the message count. Assert the container count, not a wall clock — a time budget is flaky on
   CI and a count is not.
2. **Constant cost across chat sizes.** Container count and visual-element count for a 50-message and a
   1 573-message chat are identical on first render; layout time is within a small factor.
3. **Nothing retained.** A discarded `AssistantView` over a live ViewModel is collected. First half
   already landed (`ChipOverflowPanelLifetimeTests`); the criterion extends it to the whole view.
4. **Nothing lost.** The full transcript still reaches the model, export and in-chat search —
   `ChatSession.Messages` is untouched. A test asserts that a windowed view over a 1 573-message chat
   still exports 1 573 messages.
5. **Behaviour unchanged.** `AssistantOpensAtTheLatestTurnTests` and `MessageListPeerRefreshTests`
   stay green without being weakened, and the recorded flows in `tests/ui-scripts/` still replay.

## Why it is expensive today

`src/Pia.Wpf/Views/AssistantView.xaml`:

```xml
<ScrollViewer x:Name="MessageScrollViewer" ScrollChanged="MessageScrollViewer_ScrollChanged" …>
  <ItemsControl x:Name="MessageItemsControl" ItemsSource="{Binding Messages}" …>
```

An outer `ScrollViewer` measures its child with infinite height, so the `ItemsControl` is asked for its
full extent and realizes every item whatever panel it uses. `PiaAssistantChatInspector` has the same
shape over `SelectedChatMessages`.

Virtualization would fix that by making the items host the scrolling element. This plan instead leaves
the shape alone and gives it fewer items — which is why none of the code-behind below has to move.

## Design

### 1. A windowed projection on the ViewModel

`AssistantViewModel.Messages` stays exactly as it is: the whole transcript, which is also what builds
the model's context, the export and in-chat search. Add beside it:

```csharp
public ObservableCollection<AssistantMessage> VisibleMessages { get; } = [];
public bool HasOlderMessages { get; private set; }
public int OlderMessageCount { get; private set; }
[RelayCommand] private void LoadOlderMessages() { … }
```

- **Window size** is a single const — start at **50**, tune against the table above once step 3 has
  cut the per-message cost. `LoadOlderMessages` prepends another window.
- **On chat open or a `Messages` repoint**, rebuild `VisibleMessages` from the last N. Opening a chat
  lands at the newest turn, which is where the reader already expects to be.
- **On append** (a streaming reply, a restored turn), add to `VisibleMessages` too. The tail is always
  in the window, so this is unconditional — no index arithmetic.
- **On clear**, clear both.

Keep the projection in the ViewModel, not a `CollectionView`. A filtered view still walks the whole
source per refresh, and the point is to never touch the other 1 500 items.

### 2. The view binds the projection

`ItemsSource="{Binding VisibleMessages}"` in `AssistantView.xaml`, and `AssistantView.xaml.cs`'s
`SubscribeMessages` / `_subscribedMessages` repoint to it — that is the auto-scroll hook, and it must
watch the collection the list actually shows.

Above the items, inside the same `ScrollViewer` so it scrolls with the content, a button bound to
`LoadOlderMessagesCommand`, visible on `HasOlderMessages`, labelled with `OlderMessageCount`. It needs
`AutomationProperties.AutomationId="Assistant_LoadOlderMessages"` and a row in
`ViewAutomationIdTests` in the same change.

Everything else in the code-behind is untouched: `MessageScrollViewer` stays a generated field,
`ScrollChanged`, `RequestBringIntoView`, `ScrollToBottom`, `PinToEnd` and `AutomationPeerRefreshBehavior`
all keep working against the same element. That is the whole reason for choosing this design.

### 3. Split the item template by role

The template builds the user bubble *and* the assistant bubble for every message and collapses the
wrong one; collapsed still constructs. Two `DataTemplate`s behind a `DataTemplateSelector` keyed on
`AssistantMessage.IsUser` cuts the per-item cost, and is worth doing independently — it is what decides
how large the window can be.

Both templates keep their existing `AutomationId` shapes verbatim; `ViewAutomationIdTests` is the guard.

### 4. Trim `MarkdownMessageControl`

- The constructor calls `RenderMarkdown(string.Empty)`, allocating a `FlowDocument` that the
  `MarkdownText` binding replaces moments later. Drop it; the `RichTextBox` renders empty without help.
- The 8-item `ContextMenu` is declared inline, so every message owns one. Move it to a shared resource
  and resolve the clicked message through `ContextMenu.PlacementTarget` instead of the per-instance
  `Tag` — only one context menu can be open at a time.

### 5. The history inspector gets the same treatment

`PiaAssistantChatInspector` is the identical shape over `SelectedChatMessages`, and it is half the
reported cost: a round trip renders the transcript on the history side too. Same windowed projection,
same "load older" affordance.

Additionally, `AssistantHistoryViewModel.LoadChatsAsync` re-resolves `SelectedChat` on every
navigation, which reloads the detail pane even when the selection has not changed. Skip the reload when
the resolved id equals the one already loaded.

## Why not lazy-load the messages from the store

The obvious alternative — keep only titles in memory and fetch a transcript when it is asked for — is
already in place where it is cheap, and does not pay where it is not.

`SearchAsync` (the history list) returns metadata only: no messages ever load for a chat you have not
opened. The two paths that *do* load everything are `LoadSelectedChatDetailAsync` (history detail) and
`ChatSessionManager`'s hydrate (opening a chat in Assistant). For the heaviest chat in the archive —
1 603 messages, 8.1 MB of text — those cost:

| | |
|---|---|
| `GetAsync` + map to `AssistantMessage` | **26 ms** |
| building the WPF tree over them | **43 000–50 000 ms** |

Loading is ~1/1700th of the cost, so data-side paging buys back 0.03 s of 43 s. Memory is the same
shape: `session.Messages` is ~8 MB of strings per heavy chat, ~64 MB across the eight retained
sessions, against **3.2 GB** for one realized view.

So the window belongs on what is *rendered*, not on what is loaded — which is what this plan does, and
why `ChatSession.Messages` stays whole. Paging the data too would need a virtualizing `IList` with
async placeholders so the scrollbar can still size itself, for ~2 % of the win, and it would collide
with that collection also being the model's context.

Revisit if a single chat ever reaches ~100 MB of text, where `GetAsync` extrapolates to ~0.3 s.

## Risks and what to do about them

**Scroll anchoring when older messages prepend.** The real bug in this design: inserting above the
viewport moves everything down, so the reader's position jumps by the height of what was added. Capture
`ScrollableHeight` before the prepend and restore `VerticalOffset + (newExtent - oldExtent)` after
layout settles — the adjustment has to run after `UpdateLayout`, not in the command, because the added
items have no height until they are measured. This is the one piece worth a dedicated test.

**Deep scrollback re-accumulates the cost.** Paging back 30× at N=50 puts 1 500 messages on screen and
returns to ~43 s and 3.2 GB. Deliberately not mitigated: capping it with a sliding window that drops the
far end is hand-rolled virtualization, with the same recycling hazards this design exists to avoid. The
reported symptom is opening a chat and switching views, which never reaches that depth. Revisit only if
a user actually reports it.

**Auto-scroll to latest.** Unchanged — the window's tail is the transcript's tail, so `ScrollToEnd` and
the streaming pin still target real, realized items. This is strictly simpler than the virtualized case,
where the extent is an estimate until items realize.
`AssistantOpensAtTheLatestTurnTests` is the guard and its assertions must not be relaxed. Unlike the
virtualization route, its `view.FindName("MessageScrollViewer")` locator keeps working.

**Messages outside the window have no automation peers.** UI scripts address
`MarkdownViewer_<messageId>`, `Assistant_MessageText_<guid>` and `Assistant_CopyMessage_<guid>`, and
`docs/ui_automation/ui-automation-playbook.md` documents them as always-present. After this change only
windowed messages exist in the UIA tree. Less sharp than under virtualization — the fix is a
deterministic "click `Assistant_LoadOlderMessages` until the message appears" rather than
scroll-until-realized — but the playbook still needs the technique written down, and
`MessageListPeerRefreshTests` needs re-reading against a window-bounded tree.

**A chat shorter than the window must look untouched.** ~140 of the 148 chats are, so the common case
must show no button, no affordance, no behaviour change at all. Assert it.

## Out of scope — but urgent, and in the same area

Tracked separately in the checklist, not here:

- **Retention deletes the imported archive.** `ChatArchiveService.Normalize` gives an imported chat its
  original `LastAccessedAt`; `AssistantChatRetentionService` evicts below a 180-day default, 5 s after
  startup. That is data loss on the next launch, and `EvictUnderGateAsync` holds the store's gate for
  the whole batch while doing it.
- **The FTS delete is a full scan.** `DELETE FROM AssistantChatsFts WHERE ChatId = …` targets an
  `UNINDEXED` column of a content-carrying FTS5 table, so every save and every delete scans the index.

## Rejected

**WPF virtualization** (`ItemsControl.Template` carrying the `ScrollViewer`, a `VirtualizingStackPanel`
and `ScrollUnit="Pixel"`). It reaches ~0.3–0.8 s against this plan's ~1.7 s, but costs: container
recycling swaps `DataContext` under `MarkdownMessageControl`'s pending-text debounce and under an open
`PiaChipOverflowPanel` popup; `MessageScrollViewer` leaves field scope and six code-behind members have
to re-resolve it through the template; the item template's
`MaxWidth="{Binding ElementName=MessageScrollViewer}"` stops resolving across name scopes;
`CanContentScroll` silently does nothing if set as an attached value rather than on the templated
`ScrollViewer`, so a mis-set property looks like it works and is merely slow; and the extent is
estimated until items realize, so `ScrollToEnd` can land short. Reconsider only if the window bound
proves insufficient in practice.

**Bounding `ChatSession.Messages` itself.** That collection is also what builds the model's context;
paging the view must not page the conversation. This plan's projection is the separate view-side
surface that question was asking for, which closes it.
