# Checklist: chat history performance

**Status:** A1, A2, A3, A6, A8 and B1 done. G1 closed: a script may load an older message into the window. G3 closed
by measurement against the real archive shape (148 chats / 195 MB, ten of them holding ~90 %): rendering is the cause,
the store is not. Rest open.
**Owner:** Marco Altmann
**Written:** 2026-09-11
**Origin:** [2026-09-11-large-import-slowdown.md](2026-09-11-large-import-slowdown.md)

| Group | Plan |
|---|---|
| A — transcript rendering | [2026-09-11-message-list-windowing-plan.md](2026-09-11-message-list-windowing-plan.md) |
| B — the store after a bulk import | [2026-09-11-large-import-slowdown.md](2026-09-11-large-import-slowdown.md), Secondary findings |

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.

**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little
standalone value, unblocks a High.

## Decision gates

Do not tick a dependant of an open gate without revisiting it.

| Gate | Question it answers | Blocks |
|---|---|---|
| ~~G1~~ | **Closed.** A script loads an older message into the window first; a message outside it need not stay addressable. The technique is in `docs/ui_automation/ui-automation-playbook.md`. | — |
| ~~G2~~ | **Moot.** Asked whether container recycling was safe while a reply streams. The windowing design recycles nothing, so it cannot arise. | — |
| ~~G3~~ | **Closed.** An empty chat is ~0.2 s, but that is not the reported state: one of the ten heavy chats costs 43–50 s per navigation and 3.2 GB of heap. A leads. | — |
| G4 | Should `ChatSession.Messages` ever be bounded, given it also builds the model's context? | not-yet-planned item |
| G5 | How long is the customer's **longest** chat? `history.db` size only bounds the total (~240 MB = uniformly heavy; ~21 MB still permits one 800-message chat, which is 20 s on its own). Only the per-chat message count settles it, via `ChatStorePerfProbe.MeasureOpenChat`. | nothing — A4 is the best bet either way |

## A — transcript rendering

Design: [2026-09-11-message-list-windowing-plan.md](2026-09-11-message-list-windowing-plan.md).
WPF virtualization was considered and rejected there — it is ~1 s faster per navigation and carries
container-recycling hazards the window bound avoids entirely.

- [x] **A1 · Release the chip-panel subscription.** `PiaChipOverflowPanel` held its `ItemsSource`'s
      `CollectionChanged` forever, rooting every discarded message tree; the subscription now lives
      between `Loaded` and `Unloaded`. *Deps:* — · *Effort:* `XS` · *Value:* `High`
- [x] **A2 · Split the item template by role.** One `DataTemplate` per `IsUser` behind a selector, so a
      message stops constructing both the user and the assistant bubble. *Deps:* — · *Effort:* `S` ·
      *Value:* `High`
- [x] **A3 · Settle the automation contract.** G1 is closed, and the playbook now names the id families a
      window drops plus the deterministic way back to one: invoke `Assistant_LoadOlderMessages` /
      `AssistantHistory_LoadOlderMessages` until the id resolves, never scroll-until-realized. No recorded
      flow in `tests/ui-scripts/` addresses a message id. `MessageListPeerRefreshTests` uses 2 messages, but
      `AssistantOpensAtTheLatestTurnTests` builds 60 — above a 50 window, so A4 must load older rather than
      shrink it. *Deps:* — · *Effort:* `XS` · *Value:* `Enabler`
- [ ] **A4 · Window the Assistant transcript.** A `VisibleMessages` projection holding the newest N,
      plus a "load older" button that prepends another N and restores the scroll offset. `Messages`
      stays whole. This is the fix for the report: 1 573 messages cost 43–50 s and 3.2 GB per
      navigation today, a 50-message window 1.7 s. *Deps:* A3 · *Effort:* `S` · *Value:* `High`
- [ ] **A5 · Window the history inspector.** The same projection for `PiaAssistantChatInspector`, plus
      skipping the detail reload in `LoadChatsAsync` when the resolved selection has not changed. Half
      the reported cost: the switch renders the transcript on the history side too. *Deps:* A4 ·
      *Effort:* `S` · *Value:* `High`
- [x] **A6 · Trim `MarkdownMessageControl`.** Drop the constructor's `RenderMarkdown(string.Empty)` and
      move the 8-item `ContextMenu` to a shared resource resolved through `PlacementTarget`.
      *Deps:* — · *Effort:* `XS` · *Value:* `Med`
- [ ] **A7 · Lock the budget with a test.** Assert that opening a 1 573-message chat realizes exactly
      the window's worth of item containers, that the full transcript still exports, that a chat shorter
      than the window shows no affordance, and that a discarded `AssistantView` over a live ViewModel is
      collected. *Deps:* A4 · *Effort:* `S` · *Value:* `High`
- [x] **A8 · Log what a navigation cost.** One `LogInformation` per view activation with the elapsed
      build time and the open chat's message count, so a "switching is slow" report arrives with its
      own cause attached. This investigation needed three corpora and two wrong conclusions because no
      such line exists. Covers the first activation of each view instance; a chat opened from history
      re-points `Messages` after that line, so its second render is not timed. *Deps:* — ·
      *Effort:* `XS` · *Value:* `High`

## B — the store after a bulk import

- [x] **B1 · Stop retention deleting an imported archive.** An imported chat kept its original
      `LastAccessedAt`, so the 180-day default evicted most of a years-old export 5 s after the next
      launch — measured, 123 of 148. The import now stamps `LastAccessedAt` on every chat it stores;
      `CreatedAt` and `UpdatedAt` keep the archive's own dates. *Deps:* — · *Effort:* `S` ·
      *Value:* `High`
- [ ] **B2 · Take the store gate off the eviction batch.** `EvictUnderGateAsync` holds the gate every UI
      query needs for the whole delete loop; chunk it, or yield between batches. Measured: a navigation
      query blocked 4.5 s behind it at 148 chats (28 s at 2 865 — it scales with chat count, unbounded). *Deps:* — · *Effort:* `S` · *Value:* `High`
- [ ] **B3 · Make the FTS delete an indexed lookup.** `DELETE FROM AssistantChatsFts WHERE ChatId = …`
      plans as `SCAN … VIRTUAL TABLE` and costs **31.4 ms** against a 225 MB store, versus 0.7 ms for the
      indexed row delete beside it — ~98 % of the eviction batch, and the reason a bulk import is
      superlinear. It scales with the index, so it grows with the archive. An
      external-content FTS table, or a ChatId→rowid side table so deletes go by rowid. *Deps:* — ·
      *Effort:* `M` · *Value:* `High`
- [ ] **B4 · Reconsider the 30-day history filter.** `AssistantHistoryViewModel` seeds
      `FilterStartDate` to today minus 30 days, so a freshly imported archive looks empty until the
      filter is cleared — measured, 5 of 148 visible. *Deps:* — · *Effort:* `XS` · *Value:* `Med`

## Suggested order

The customer cannot be reached, so this is the order that maximises what is covered under an
unconfirmed cause. A is the only mechanism measured to produce 20 s at all, and virtualization is
shape-independent: it makes the cost viewport-bounded whether the archive is uniformly heavy or holds
one long chat among small ones. G5 no longer gates it — see the gates table.

1. **B1** — the only irreversible item, and unrelated to speed: 123 of 148 chats deleted 5 s after the
   next launch.
2. **A8** — navigation timing in the log, so the next report names its own cause instead of costing a
   week of inference.
3. **A2**, **A6** — cheap, independent, and they cut the per-message constant that A4 then multiplies
   by a smaller number.
4. **A3** — the gate that can send A4's design back; answer it before touching markup.
5. **A4 → A5 → A7** — the fix: window both transcripts. A5 matters as much as A4, since a round trip
   renders the transcript on both sides.
6. **B4** — `XS`, and a restored archive looks empty without it.
7. **B2**, **B3** — real, but at 148 chats eviction is 0.4–6.7 s. Worth doing on their own merits
   (B3 is also the superlinear import and the doubled database), not for this report.

## Not yet planned

- Bounding `ChatSession.Messages` (G4). It is also the model's context, so a view-side cap needs a
  separate projection rather than a smaller collection.
- A ceiling on import size, or a warning before importing an archive this large.
- Whether `MaxRetainedSessions = 8` is still right when a retained session can be a multi-megabyte
  imported chat. Measured: the ten heavy chats carry ~8 MB of text each, so eight live sessions can
  hold the transcripts alone — before any view built over them.
