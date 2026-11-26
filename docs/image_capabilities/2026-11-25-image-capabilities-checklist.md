# Image Capabilities (Desktop Client) — Checklist

**Status:** Open
**Owner:** Marco Altmann
**Written:** 2026-11-25
**Origin:** GitHub issue #96, which is the client part of ADO User Story 8235 (server and client
analysis `2026-11-16-image-capabilities-analysis.md`, attached to 8235). Steps 0, A1 and A2 are planned
in [2026-11-25-image-gate-and-viewer-plan.md](2026-11-25-image-gate-and-viewer-plan.md).

Tick a box in the commit that lands its step. Step ids match #96. **0b** is new: it was found while
planning step 0.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new
surface · `L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler`
little standalone value, unblocks a High.

## Steps

- [x] **0 · Image gate reads the turn's provider** — `AssistantProviderTakesImagesAsync` resolves the
  active persona and then `AssistantTurnProvider.ResolveAsync`, with tests for a persona pinned to a
  bring-your-own-key provider. Plan Task 1.
  *Deps:* none · *Effort:* XS · *Value:* High
- [ ] **0b · Earlier images follow the turn's provider** — sent images ride every later turn
  (`AssistantMessage.BuildChatMessage`), so a switch to a non-Pia-Cloud provider mid-chat still
  carries them. A planned (Agent-mode) run can also hand a step to a persona pinned to another
  provider, and that step's request carries the goal's images (`LiveTurnExecutor` substitutes the
  step persona's provider). The turn must refuse or strip them. Not planned yet; see gate G1.
  *Deps:* 0 · *Effort:* S · *Value:* High
- [x] **A1 · Image viewer overlay** — a full-window `ImageViewerOverlay` with fit, 100 %, wheel and
  keyboard zoom, drag to pan, previous/next, copy, save as, Esc, `ImageViewer_*` ids and en/de/fr
  strings. Plan Tasks 2 and 3.
  *Deps:* none · *Effort:* M · *Value:* Enabler
- [x] **A2 · Enlarge input images** — composer and sent-bubble thumbnails open A1, with per-item ids
  `Assistant_OpenPendingImage_{id}` and `Assistant_OpenSentImage_{id}`. Plan Task 4.
  *Deps:* A1 · *Effort:* S · *Value:* High
- [ ] **A3 · Show tool-fetched images** — `screen_capture` and `read_file` pictures become clickable
  thumbnails on the tool round, for the live session only.
  *Deps:* A1 · *Effort:* S · *Value:* Med
- [ ] **B1 · Receive output images** — parse `images` in `PiaCloudChatClient`, add a new
  `ChatStreamItem` case, stop counting an image-only answer as empty, add
  `AssistantMessage.OutputImages` and the consumer cases, and bound the decoding.
  *Deps:* G2 · *Effort:* M · *Value:* Enabler
- [ ] **B2 · Render output images and enlarge** — a strip under the answer that opens A1 on click,
  with save, copy and use as input.
  *Deps:* B1, A1 · *Effort:* S · *Value:* High
- [ ] **B3 · Persist output images** — a file in the chat's working directory, a relative path on the
  message, metadata-only sync and a "not on this device" chip.
  *Deps:* B1, G3 · *Effort:* M · *Value:* High
- [ ] **B4 · No automatic re-send** — history replaces output images with `[image generated, WxH]`.
  *Deps:* B1 · *Effort:* XS · *Value:* Enabler
- [ ] **C1 · Helper-model status line** — "image read by a helper model" under the answer.
  *Deps:* server-side signal · *Effort:* XS · *Value:* Med
- [ ] **D1 · Docs and release notes** — the Pia.Docs attachments page (viewer, generated images) and
  release-notes bullets on the release branch.
  *Deps:* A2, B2 · *Effort:* XS · *Value:* Med

## Decision gates

| Gate | Blocks | Question it answers |
|---|---|---|
| G1 | 0b | When earlier images meet a provider that must not receive them, does the turn **refuse** (the user switches back or starts a new chat) or **strip** them (the model gets `[image omitted: this provider does not receive images]`)? |
| G2 | B1 and everything after it | What does Pia Cloud's output-image field look like on the wire? A server-side spike call fixes `choices[0].delta.images` / `message.images` before the client parses it. |
| G3 | B3 | Where does a generated image go on a profile with no working directory: a fallback folder under `PiaPaths`, or not saved at all? |
| G4 | B2 | Does a generated image carry an "AI-generated" mark (EU AI Act Art. 50)? |

## Not yet planned

- **HTML export emits live remote images.** `MarkdownExportService.ToHtml` runs Markdig with no
  sanitising, so `![](https://…)` becomes an `<img>` that fetches when the file is opened. Found in
  #96; it is not caused by the image work, but output images make it likelier to matter.
- **Opening file-chip images in the viewer.** This is one of #96's open questions.

## Suggested order

1. **0**, its own PR. It is XS and closes a data-boundary gap today.
2. **A1 → A2**, one PR. This answers ask 3 and builds the viewer that ask 2 needs.
3. **G1, then 0b.** Answer the gate before building 0b.
4. **G2, then B1 → B2 → B4 → B3.**
5. **C1, A3, D1.**
