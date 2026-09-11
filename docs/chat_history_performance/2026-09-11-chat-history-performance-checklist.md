# Checklist: chat history performance

**Status:** A1 done, rest open
**Owner:** Marco Altmann
**Written:** 2026-09-11
**Origin:** [2026-09-11-large-import-slowdown.md](2026-09-11-large-import-slowdown.md)

| Group | Plan |
|---|---|
| A — transcript rendering | [2026-09-11-message-list-virtualization-plan.md](2026-09-11-message-list-virtualization-plan.md) |
| B — the store after a bulk import | [2026-09-11-large-import-slowdown.md](2026-09-11-large-import-slowdown.md), Secondary findings |

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.

**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little
standalone value, unblocks a High.

## Decision gates

Do not tick a dependant of an open gate without revisiting it.

| Gate | Question it answers | Blocks |
|---|---|---|
| G1 | Must every message in a chat stay addressable by UIA, or may scripts scroll a message into view first? | A3, A4 |
| G2 | Is container recycling safe while a reply streams and a chip popup is open, or does the list need `VirtualizationMode="Standard"`? | A4 |
| G3 | Does the reported 20 s survive a new empty chat? If it does, the store gate — not rendering — is the primary cause and B moves ahead of A. | A2–A6 ordering |
| G4 | Should `ChatSession.Messages` ever be bounded, given it also builds the model's context? | not-yet-planned item |

## A — transcript rendering

- [x] **A1 · Release the chip-panel subscription.** `PiaChipOverflowPanel` held its `ItemsSource`'s
      `CollectionChanged` forever, rooting every discarded message tree; the subscription now lives
      between `Loaded` and `Unloaded`. *Deps:* — · *Effort:* `XS` · *Value:* `High`
- [ ] **A2 · Split the item template by role.** One `DataTemplate` per `IsUser` behind a selector, so a
      message stops constructing both the user and the assistant bubble. *Deps:* — · *Effort:* `S` ·
      *Value:* `High`
- [ ] **A3 · Settle the automation contract.** Decide G1, then update
      `docs/ui_automation/ui-automation-playbook.md` with how a script addresses a message in
      scrollback, and re-read `MessageListPeerRefreshTests` against a viewport-bounded tree.
      *Deps:* — · *Effort:* `S` · *Value:* `Enabler`
- [ ] **A4 · Virtualize the Assistant transcript.** Move the `ScrollViewer` into the `ItemsControl`'s
      `ControlTemplate`, add a `VirtualizingStackPanel` with pixel scrolling, and re-resolve
      `MessageScrollViewer` through the template in the code-behind. *Deps:* A3 · *Effort:* `M` ·
      *Value:* `High`
- [ ] **A5 · Virtualize the history inspector.** The same change for `PiaAssistantChatInspector`, plus
      skipping the detail reload in `LoadChatsAsync` when the resolved selection has not changed.
      *Deps:* A4 · *Effort:* `S` · *Value:* `High`
- [ ] **A6 · Trim `MarkdownMessageControl`.** Drop the constructor's `RenderMarkdown(string.Empty)` and
      move the 8-item `ContextMenu` to a shared resource resolved through `PlacementTarget`.
      *Deps:* — · *Effort:* `XS` · *Value:* `Med`
- [ ] **A7 · Lock the budget with a test.** Assert that opening a 500-message chat realizes a
      viewport-proportional number of item containers, and that a discarded `AssistantView` over a live
      ViewModel is collected. *Deps:* A4 · *Effort:* `S` · *Value:* `High`

## B — the store after a bulk import

- [ ] **B1 · Stop retention deleting an imported archive.** An imported chat keeps its original
      `LastAccessedAt`, so the 180-day default evicts most of a years-old export 5 s after the next
      launch. *Deps:* — · *Effort:* `S` · *Value:* `High`
- [ ] **B2 · Take the store gate off the eviction batch.** `EvictUnderGateAsync` holds the gate every UI
      query needs for the whole delete loop; chunk it, or yield between batches. *Deps:* B1 ·
      *Effort:* `S` · *Value:* `High`
- [ ] **B3 · Make the FTS delete an indexed lookup.** `DELETE FROM AssistantChatsFts WHERE ChatId = …`
      scans a content-carrying index on every save and every delete, which makes import O(N²) and
      doubles the database on disk. *Deps:* — · *Effort:* `M` · *Value:* `High`
- [ ] **B4 · Reconsider the 30-day history filter.** `AssistantHistoryViewModel` seeds
      `FilterStartDate` to today minus 30 days, so a freshly imported archive looks empty until the
      filter is cleared. *Deps:* — · *Effort:* `XS` · *Value:* `Med`

## Suggested order

Cheapest decisive work first, then the vertical slices.

1. **G3** — one question to the user, and it can reorder everything below.
2. **B1** — data loss beats latency, and it is on a 5-second timer from the next launch.
3. **A2**, **A6**, **B4** — independent, cheap, and A2 alone halves the per-message cost.
4. **A3** — the gate that can send A4's design back; answer it before touching markup.
5. **A4 → A5 → A7** — the vertical slice, measured at the end.
6. **B2**, **B3** — the store work, which stands on its own and needs no UI change.

## Not yet planned

- Bounding `ChatSession.Messages` (G4). It is also the model's context, so a view-side cap needs a
  separate projection rather than a smaller collection.
- A ceiling on import size, or a warning before importing an archive this large.
- Whether `MaxRetainedSessions = 8` is still right when a retained session can be a multi-megabyte
  imported chat.
