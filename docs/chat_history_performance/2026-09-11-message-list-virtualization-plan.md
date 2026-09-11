# Plan: make the chat transcript cheap to render

**Status:** Planned, not started
**Owner:** Marco Altmann
**Written:** 2026-09-11
**Origin:** [2026-09-11-large-import-slowdown.md](2026-09-11-large-import-slowdown.md), which measured
the cause of a ~20 s view switch and a ~140 MB-per-switch retention after a 195 MB OpenWebUI import.

## Goal

Rendering the Assistant transcript and the history inspector must cost what is on screen, not what is
in the chat. Today it costs the whole chat, every time the view is built, and the view is rebuilt on
every navigation.

## Acceptance criteria

Measured, not judged. Each is a test, not an opinion.

1. **Bounded realization.** Opening a 500-message chat realizes a number of item containers
   proportional to the viewport, not to the message count. Assert the container count, not a wall
   clock — a time budget is flaky on CI and a count is not.
2. **Constant cost across sizes.** Layout time and visual-element count for a 50-message and an
   800-message chat are within a small factor. Today they are 2.5 s / 8 415 and 14.1 s / 83 813.
3. **Nothing retained.** A discarded `AssistantView` over a live ViewModel is collected. First half
   already landed (`ChipOverflowPanelLifetimeTests`); the criterion extends it to the whole view.
4. **Behaviour unchanged.** `AssistantOpensAtTheLatestTurnTests` and `MessageListPeerRefreshTests`
   stay green without being weakened, and the recorded flows in `tests/ui-scripts/` still replay.

## Why it cannot virtualize today

`src/Pia.Wpf/Views/AssistantView.xaml`:

```xml
<ScrollViewer x:Name="MessageScrollViewer" ScrollChanged="MessageScrollViewer_ScrollChanged" …>
  <ItemsControl x:Name="MessageItemsControl" ItemsSource="{Binding Messages}" …>
```

An outer `ScrollViewer` measures its child with infinite height. The `ItemsControl` is therefore asked
for its full extent and realizes every item, whatever panel it uses. Virtualization needs the items
host to *be* the scrolling element, so the panel learns the viewport.

`PiaAssistantChatInspector` has the same shape over `SelectedChatMessages`.

## Design

### 1. The `ItemsControl` becomes the scroll host

Move the `ScrollViewer` into the `ItemsControl`'s `ControlTemplate`, keeping every attribute it carries
today (the `OpacityMask` fade band, `ScrollChanged`, padding, the `HasMessages` visibility trigger):

```xml
<ItemsControl x:Name="MessageItemsControl"
              ItemsSource="{Binding Messages}"
              VirtualizingPanel.IsVirtualizing="True"
              VirtualizingPanel.VirtualizationMode="Recycling"
              VirtualizingPanel.ScrollUnit="Pixel"
              VirtualizingPanel.CacheLength="1,1"
              VirtualizingPanel.CacheLengthUnit="Page">
  <ItemsControl.Template>
    <ControlTemplate TargetType="ItemsControl">
      <ScrollViewer x:Name="MessageScrollViewer" CanContentScroll="True" …><ItemsPresenter/></ScrollViewer>
    </ControlTemplate>
  </ItemsControl.Template>
  <ItemsControl.ItemsPanel>
    <ItemsPanelTemplate><VirtualizingStackPanel/></ItemsPanelTemplate>
  </ItemsControl.ItemsPanel>
```

`CanContentScroll` goes on the inner `ScrollViewer`, not as an attached value on the `ItemsControl`:
an attached value does not reach a custom `ControlTemplate`, and `ListBox`'s default template only
works because it carries `CanContentScroll="{TemplateBinding ScrollViewer.CanContentScroll}"`. Set it
in the wrong place and the panel silently stops virtualizing — nothing fails, it is just slow again,
which is why A7's container count is the only honest check. The other attached properties belong on
the `ItemsControl`; the panel reads them off its `ItemsOwner`.

`ListBox` was the alternative — it virtualizes out of the box. Rejected: it adds selection, focus and
item chrome the transcript does not want, every message gains a `ListBoxItem`, and the WPF-UI styles
would have to be neutralized. The template move keeps the element types, the item template and every
`AutomationId` exactly as they are.

`ScrollUnit="Pixel"` because messages have wildly different heights and item-unit scrolling would jump
a whole message per wheel notch. The cost is that `VirtualizingStackPanel` *estimates* the extent from
realized items, so `ScrollToEnd` can land short on the first call — see Risks.

### 2. Code-behind: `MessageScrollViewer` moves out of the field scope

`x:Name` inside a `ControlTemplate` is not a generated field. Six members reference it —
`ScrollToBottom`, `MessageScrollViewer_ScrollChanged`, `MessageItemsControl_RequestBringIntoView`,
`PinToEnd`, `OnMessagesCollectionChanged`, `OnViewModelPropertyChanged`. Resolve it once, after the
template is applied, through the items control's template:

```csharp
private ScrollViewer? Scroller =>
    _scroller ??= MessageItemsControl.Template.FindName("MessageScrollViewer", MessageItemsControl) as ScrollViewer;
```

Reset `_scroller` on `Unloaded`, and keep every null-guard: the template is not applied before the
first layout pass, and `ScrollToBottom` is already posted at `DispatcherPriority.Loaded`.

The item template also binds `MaxWidth="{Binding ActualWidth, ElementName=MessageScrollViewer, …}"` —
an `ElementName` that no longer resolves from inside the item template once the scroller lives in a
different name scope. Rebind to the `ItemsControl` via
`RelativeSource={RelativeSource AncestorType=ItemsControl}`, which gives the same width.

### 3. Split the item template by role

The template builds the user bubble *and* the assistant bubble for every message and collapses the
wrong one; collapsed still constructs. Two `DataTemplate`s behind a `DataTemplateSelector` keyed on
`AssistantMessage.IsUser` halves the per-item cost, and is worth doing independently of the rest —
it is the only step that helps the *first* realization as much as the later ones.

Both templates keep their existing `AutomationId` shapes verbatim; `ViewAutomationIdTests` is the guard.

### 4. Trim `MarkdownMessageControl`

- The constructor calls `RenderMarkdown(string.Empty)`, allocating a `FlowDocument` that the
  `MarkdownText` binding replaces moments later. Drop it; the `RichTextBox` renders empty without help.
- The 8-item `ContextMenu` is declared inline, so every message owns one. Move it to a shared resource
  and resolve the clicked message through `ContextMenu.PlacementTarget` instead of the per-instance
  `Tag` — only one context menu can be open at a time.

### 5. The history inspector gets the same treatment

`PiaAssistantChatInspector` is the identical shape over `SelectedChatMessages`. Additionally,
`AssistantHistoryViewModel.LoadChatsAsync` re-resolves `SelectedChat` on every navigation, which
reloads the detail pane even when the selection has not changed. Skip the reload when the resolved id
equals the one already loaded.

## Risks and what to do about them

**Scroll-to-latest with estimated extents.** With pixel virtualization the extent is an estimate until
the relevant items are realized, so a single `ScrollToEnd` can land short when a chat opens. Handle it
by re-issuing `ScrollToEnd` from `ScrollChanged` while `IsAutoScrollEnabled` and the offset is not yet
at the bottom — the existing handler already does this for the streaming case (`AssistantView.xaml.cs`
line ~229); it needs to survive the settle loop without fighting a reader who scrolls up. That
handler's at-bottom arithmetic is in pixels, which only stays true under `ScrollUnit="Pixel"`; under
`Item` the offsets become indices and the comparison quietly means something else.

`AssistantOpensAtTheLatestTurnTests` is the guard and its assertions must not be relaxed — but its
*locator* has to change: `view.FindName("MessageScrollViewer")` returns null once the name lives in a
template scope. The first failure there is the rename, not the design.

**Off-screen messages have no automation peers.** This is the sharp edge. UI scripts address
`MarkdownViewer_<messageId>`, `Assistant_MessageText_<guid>` and `Assistant_CopyMessage_<guid>`, and
`docs/ui_automation/ui-automation-playbook.md` documents them as always-present. After virtualization
only realized messages exist in the UIA tree. Two things follow: the playbook needs a "scroll it into
view first" technique for addressing a message in scrollback, and `MessageListPeerRefreshTests` needs
re-reading — its premise (messages arrive in one go into an already-walked view) still holds, but the
set it observes becomes viewport-bounded. Decide this before the markup changes; it is the one item
that can send the design back.

**Peer refresh on every scroll.** `AutomationPeerRefreshBehavior` posts a subtree walk each time the
generator reaches `ContainersGenerated`. Today that is once per chat load; with virtualization it is
once per scroll batch. Scope it to a UIA client actually being attached (it already checks
`FromElement`) and confirm it is not a scroll-time cost.

**Container recycling swaps DataContext under live controls.** `MarkdownMessageControl` holds pending
text and a debounce timer while streaming, and `PiaChipOverflowPanel` can have an open popup. A
recycled container hands both a different message. Verify by streaming a reply while scrolling; fall
back to `VirtualizationMode="Standard"` if either misbehaves, at the cost of rebuilding containers.

## Out of scope — but urgent, and in the same area

Tracked separately in the checklist, not here:

- **Retention deletes the imported archive.** `ChatArchiveService.Normalize` gives an imported chat its
  original `LastAccessedAt`; `AssistantChatRetentionService` evicts below a 180-day default, 5 s after
  startup. That is data loss on the next launch, and `EvictUnderGateAsync` holds the store's gate for
  the whole batch while doing it.
- **The FTS delete is a full scan.** `DELETE FROM AssistantChatsFts WHERE ChatId = …` targets an
  `UNINDEXED` column of a content-carrying FTS5 table, so every save and every delete scans the index.

## Not planned, deliberately

Capping how many messages `ChatSession.Messages` holds would bound the ViewModel side too, but that
collection is also what builds the model's context. Paging the view must not page the conversation.
If the VM side ever needs bounding it has to be a separate projection, and that is a design question,
not a performance fix. Left as a decision gate in the checklist.
