# Assistant ↔ Chat-history switching is slow after a large import

**Status:** Analysis complete; the leak (fix 1) is fixed, the rest is planned in
[2026-09-11-chat-history-performance-checklist.md](2026-09-11-chat-history-performance-checklist.md)
**Owner:** Marco Altmann
**Written:** 2026-09-11
**Origin:** A user imported a 195 MB OpenWebUI export; switching between Assistant and
Chat history then takes ~20 s each way and the whole window turns sluggish.

## Summary

The store is not the problem. The message list is: it is unvirtualized, its item
template is heavy, the whole view is rebuilt from scratch on every navigation, and the
discarded tree is never released.

Everything below scales with the **open** chat (Assistant side) and the **selected**
chat (history side), not with the size of the import. The link to the import is an
inference: a large chat got opened. Confirm it in one step — start a new empty chat,
then switch views. Fast means this is the cause. Still 20 s means the next suspect is
`AssistantChatService._gate`, which every UI query shares with the sync drain and the
retention pass (see Secondary findings).

Three defects, measured, in order of impact:

1. `AssistantView`'s message list is a plain `ItemsControl` inside an outer
   `ScrollViewer` — a shape in which WPF cannot virtualize at all. Every message in the
   open chat is fully realized: ~105 visual elements and ~600 KB of managed heap each.
2. Views are hosted by `DataTemplate` (`App.xaml`) behind `MainWindow`'s
   `NavigationContentPresenter`, so each navigation destroys the old tree and builds a
   new one. The cost in (1) is paid again on every switch.
3. `PiaChipOverflowPanel` subscribes to its `ItemsSource.CollectionChanged` and never
   unsubscribes on discard. The bound collections (`AssistantMessage.Sources` /
   `.FileRefs`) outlive the view, so every discarded message tree stays rooted.

## Measurements

Debug build, `WpfStaHost`, `AssistantView` measured/arranged at 900×700. Absolute
times are Debug; the scaling is what matters.

| Messages | Layout | Visual elements | Managed heap |
|---|---|---|---|
| 50 | 2.5 s | 8 415 | 57 MB |
| 200 | 6.3 s | 21 713 | 182 MB |
| 800 | 14.1 s | 83 813 | 539 MB |

Marginal cost is ~13 ms and ~600 KB per message.

Retention, building and discarding the view over the same 200-message ViewModel, heap
read after a full blocking GC each round:

```
round 1  168 MB
round 2  308 MB
round 3  451 MB
round 4  590 MB
```

Linear, nothing released. Reproduced with a real `Window` and a real `ContentPresenter`
content swap, so WPF's genuine `Loaded`/`Unloaded` broadcast runs, and with keyboard
focus parked off the view first: an empty chat is collected, a 200-message chat is not.

## Why each message is so expensive

The item template in `src/Pia.Wpf/Views/AssistantView.xaml` builds **both** branches for
every message — the user bubble and the assistant bubble — and hides the wrong one with
a `DataTrigger`. Collapsed is not uncreated: the objects, styles and bindings are all
built. So a user message still constructs `PiaAssistantMessage`, and an assistant
message still constructs `PiaCollapsibleMessageText`.

`PiaAssistantMessage` alone pulls in `PiaReasoningView`, two `PiaChipOverflowPanel`s,
`PiaAnswerToolbar` (~250 lines of XAML), `PiaSuggestionChips` and
`MarkdownMessageControl`. The last is a `RichTextBox` carrying an inline 8-item
`ContextMenu`, and its constructor allocates an empty `FlowDocument` and assigns it
before the bound text ever arrives.

`PiaAssistantChatInspector` (the history view's detail pane) has the same unvirtualized
shape over `SelectedChatMessages`, and `AssistantHistoryViewModel.LoadChatsAsync`
re-resolves `SelectedChat` on every navigation — which reloads the detail and pays the
same cost on the history side.

## The leak, exactly

`src/Pia.Wpf/Controls/Chat/PiaChipOverflowPanel.xaml.cs`:

```csharp
private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
{
    if (e.OldValue is INotifyCollectionChanged previous)
        previous.CollectionChanged -= panel.OnItemsCollectionChanged;
    if (e.NewValue is INotifyCollectionChanged current)
        current.CollectionChanged += panel.OnItemsCollectionChanged;   // never released on discard
```

`ItemsSource` only changes when the container is re-hosted. When the view is simply
thrown away, the handler stays on `AssistantMessage.Sources`, which the chat session
holds for the life of the process. That roots the panel → its `PiaAssistantMessage` →
the item container → the whole view tree.

Bisected with a real-`Window` harness over an item collection owned by a live
ViewModel: `TextBlock`, `MarkdownMessageControl`, `PiaAnswerToolbar` and
`PiaReasoningView` are all collected; `PiaChipOverflowPanel` and `PiaAssistantMessage`
are not. Moving the subscription to `Loaded`/`Unloaded` — the shape `PiaReasoningView`
already uses — makes both collect, and makes the full `AssistantView` with 200 messages
collect. The `Loaded` handler must also call `Rebuild()`: the collection can change
while the panel is unloaded, and re-observing alone leaves the three slots stale.

## Fix plan

1. ~~**Release the subscription**~~ — done. `PiaChipOverflowPanel` now subscribes on `Loaded` and
   unsubscribes on `Unloaded`; `ChipOverflowPanelLifetimeTests` holds it.
2. **Virtualize the message list** (`M`, `High`). The `ItemsControl` must become the
   scroll host — `ItemsControl.Template` containing the `ScrollViewer`,
   `CanContentScroll="True"`, a `VirtualizingStackPanel` items panel and
   `ScrollUnit="Pixel"` so auto-scroll with variable-height items still behaves. Decide
   `VirtualizationMode` during the work: recycling avoids rebuilding the `RichTextBox`
   and its context menu, but a recycled container swaps DataContext under
   `MarkdownMessageControl`'s pending-text debounce and under an open chip popup.
   Touches `AssistantView.xaml.cs`'s `MessageScrollViewer`, `ScrollChanged`,
   `RequestBringIntoView` and `ScrollToBottom`, plus `AutomationPeerRefreshBehavior`.
   Same change for `PiaAssistantChatInspector`.
3. **Split the item template** (`S`, `Med`). One `DataTemplate` per role behind a
   `DataTemplateSelector`, so a message builds one branch instead of two.
4. **Share the context menu** (`XS`, `Med`). Move `MarkdownMessageControl`'s
   `ContextMenu` to a resource — only one can be open at a time — and drop the
   constructor's `RenderMarkdown(string.Empty)`.

## Secondary findings

- **Retention will delete most of the archive.** `ChatArchiveService.Normalize` sets an
  imported chat's `LastAccessedAt` from its original timestamps, and
  `AssistantChatRetentionService` runs 5 s after startup with a 180-day default. Every
  imported chat older than that is evicted on the next launch. `EvictUnderGateAsync`
  holds `_gate` — the same gate every UI query takes — for the whole batch, and each
  iteration runs the FTS delete below. Thousands of chats there is both data loss and a
  long stall with the UI blocked behind the gate.
- `DELETE FROM AssistantChatsFts WHERE ChatId = @ChatId` runs against an `UNINDEXED`
  column of a content-carrying FTS5 table, so every chat save and every delete scans the
  whole index. That makes import O(N²) and roughly doubles the database on disk. An
  external-content FTS table, or a ChatId→rowid side table so deletes go by rowid, fixes
  it.
- `AssistantHistoryViewModel` seeds `FilterStartDate` to today minus 30 days. An
  imported archive keeps its original timestamps, so most of it is invisible until the
  filter is cleared.
- The SQL read paths are fine: `SearchAsync`/`CountAsync` are paged and use
  `IX_AssistantChats_UpdatedAt` and `IX_AssistantChatMessages_ChatId_Ordinal`, and
  `TouchLastAccessedAsync` is a plain indexed `UPDATE`.
