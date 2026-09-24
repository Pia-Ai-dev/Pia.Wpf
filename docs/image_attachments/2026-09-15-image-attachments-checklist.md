# Image attachments — checklist

**Status:** Groups A and B complete as of 2026-09-17. Open: **C1**'s vision half (G2/G3) and **C5**.
**Owner:** Marco Altmann
**Written:** 2026-09-15
**Origin:** Customer ask relayed 2026-09-15 — multiple images per message, and letting Pia read an
image that is already in its files folder. Two plans feed this checklist.

| Group | Plan |
|---|---|
| **A** | [2026-09-15-multi-image-composer-plan.md](2026-09-15-multi-image-composer-plan.md) — more than one image per chat message |
| **B** | [2026-09-15-image-file-tools-plan.md](2026-09-15-image-file-tools-plan.md) — images through `read_file` / `search_files` |
| **C** | Housekeeping that spans both |

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new
surface · `L` a week or more, a new subsystem.

**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline ·
`Enabler` little standalone value, unblocks a High.

## Decision gates

| Gate | Question | Blocks | Status |
|---|---|---|---|
| **G1** | Does anything other than `PiaCloudChatClient` have to learn about N images? | A3, A4 | **Answered 2026-09-15: no.** `PiaCloudChatClient` already iterates `imageParts` into an OpenAI-style `image_url` array (`src/Pia.Wpf/Services/PiaCloudChatClient.cs:517-536`), so this is client-only and no server change is needed. Had it held one image, A would have become cross-repo and `L`. |
| **G2** | Does a live turn with four images actually come back with all four seen? | A10 sign-off, B live round | **Still open, but the precondition is answered: the deployment HAS a vision endpoint.** A one-image turn against `cloud.pia-ai.de` on 2026-09-15 came back describing the picture correctly (both shapes, both colours, the text) — so the 2026-09-09 `No endpoints found that support image input` failure does not apply today, and the gate is exercisable. What is still unanswered is the *four*-image half, which cannot be asked until A2 lets the composer hold four. |
| **G3** | Is 4 the right cap? | A2, B3 | **Open until G2.** The binding constraints are the 12 MB byte budget and how many thumbnails the strip can show, not the compactor — at the 128 000 default window four images pin 11% of it. A live round may argue for 2 or for 6. Both caps are one constant each, so this is cheap to settle late. Four thumbnails do fit the strip legibly (measured 2026-09-17 against the real composer), so only the model-side half is unanswered. |

## Group A — more than one image per chat message

- [x] **A1 · `ImageAttachment` gains identity.** Add `Guid Id` and `string? SourcePath`; the file-path
  `TryPrepare` sets the path, the clipboard one leaves it null. *Deps:* — · *Effort:* XS · *Value:* Enabler
- [x] **A2 · The pending collection, its caps, and append semantics.** `PendingAttachments`
  replaces `PendingAttachment`; 4 images / 12 MB; a second image adds instead of silently
  overwriting. *Deps:* A1 · *Effort:* S · *Value:* High
- [x] **A3 · The message carries N.** `AssistantMessage.Attachments` plus one `DataContent` per
  image after the text in `BuildChatMessage`. *Deps:* A1, G1 · *Effort:* XS · *Value:* Enabler
  **Landed 2026-09-15.** Two bindings in `UserMessageTemplate` had to move with it
  (`Attachments[0].Thumbnail`, `HasAttachments`) — they are inside a `DataTemplate`, which
  `BindingPathWalker` does not walk, so a rename there breaks the bubble with a green build. The
  indexer also had to move inside the `HasAttachments` trigger: a null `Attachment` bound silently,
  an indexer against an empty collection does not. Both are now verified live (one image sent through
  the real composer on the live profile) and pinned by `UserBubbleAttachmentBindingTests`, which
  listens to WPF's own data-binding trace across a real layout pass — reverting the trigger makes it
  fail with `Cannot get 'Item[]' value`. A7 replaces the block with the strip. Until A4 lands, the
  regenerate path narrows with `FirstOrDefault()`.
- [x] **A4 · The send signature.** `StartTurnAsync` takes `IReadOnlyList<ImageAttachment>?`; the
  parked-run answer guard and five test files move with it. *Deps:* A3 · *Effort:* S · *Value:* Enabler
- [x] **A5 · The compactor charges per image.** `ImageCountIn` in both `ChargeFor` and the
  pin-admission loop. Closes a real context-overflow risk that exists the moment A3 lands.
  *Deps:* A3 · *Effort:* S · *Value:* High
  **Landed 2026-09-15 with A3**, and both new tests were run against the pre-change compactor first —
  they fail there, so the differential is real. `AFourImageGoal_OnASmallConfiguredWindow_LeavesNoInputBudget`
  now also reads the skip warning back and asserts the pin came to at least four charges, so the
  boundary is held by the number in the log line and not only by the outcome.
- [x] **A6 · The composer thumbnail strip.** Horizontal `ItemsControl` with a per-item remove
  button carrying a bound, unique AutomationId. *Deps:* A2 · *Effort:* S · *Value:* High
  **Verified live 2026-09-17** over a throwaway profile, driving the real composer: six images through
  `PIA_DEBUG_DROP_FILES` stage four; each remove button resolves as exactly one element under its own
  `Assistant_RemoveAttachment_<guid>`; invoking one drops that guid and leaves the other three;
  re-attaching fills the freed slot instead of replacing anything. `Assistant_Send` is enabled with
  four images and an empty box, which is the `CollectionChanged` re-raise A2 warns about — without it
  the button is dead there and no test at this level would see it.
- [x] **A7 · The bubble renders N.** `UserMessageTemplate` wraps the thumbnails.
  *Deps:* A3 · *Effort:* XS · *Value:* Med
  **Verified live 2026-09-17**: the sent bubble's UIA subtree holds four `ImageAttachment` items, each
  with its own `Image`. The old template rendered `Attachments[0]` and would have shown one.
- [x] **A8 · Strings.** Add `Msg_File_ImageLimit` and `Msg_File_ImageBudget`, delete
  `Msg_File_OneImageOnly`, en/de/fr. *Deps:* A2 · *Effort:* XS · *Value:* Enabler
- [x] **A9 · Screen capture appends.** `PrepareImageAttachmentAsync` returns the attachment instead
  of the caller re-reading a property. *Deps:* A2 · *Effort:* XS · *Value:* Med
- [x] **A10 · Tests.** Fifteen new, one rewritten, plus the `ViewAutomationIdTests` row.
  *Deps:* A2–A9 · *Effort:* S · *Value:* High

## Group B — images through the file tools

- [x] **B1 · Short-circuit the image branch in `HandleReadFileAsync`.** The shared
  `ReadFileTextAsync` keeps refusing, so `search_files` and @Files are untouched.
  *Deps:* — · *Effort:* XS · *Value:* Enabler
- [x] **B2 · `DeliverImageAsync`.** Six ordered refusal arms, then park; a 25 MB pre-decode byte
  ceiling; the call id threaded into the read arm of the dispatch switch.
  *Deps:* B1, B3 · *Effort:* M · *Value:* High
- [x] **B3 · Per-round image cap in the channel.** `TryPark` refuses past four, inside the lock.
  *Deps:* — · *Effort:* XS · *Value:* Enabler
- [x] **B4 · Generalize the placeholder and caption.** `[image file, WxH, consumed]` for this path;
  `[screen capture, …]` stays exact. *Deps:* B2 · *Effort:* XS · *Value:* Med
- [x] **B5 · `search_files` counts images separately.** Stop reporting every PNG as a file it failed
  to read; point at `read_file`. *Deps:* — · *Effort:* XS · *Value:* Med
  **Landed 2026-09-15** without the `read_file` pointer, for the same reason as B6 — add it there when
  B2 makes it true.
- [x] **B6 · `write_file` / `edit_file` refuse images.** Closes the "write UTF-8 over a PNG" hole in
  `write_file`, and replaces `edit_file`'s inherited "attach the image instead" message.
  *Deps:* — · *Effort:* XS · *Value:* High
  **Landed 2026-09-15.** The refusal says why writing is refused and does *not* point at `read_file`,
  because until B2 lands `read_file` still refuses images; B7 adds the pointer.
- [x] **B7 · Tool descriptions.** One clause on `read_file` — shown once, in the next message.
  *Deps:* B2 · *Effort:* XS · *Value:* Enabler
- [x] **B8 · Tests.** Thirteen, including two that assert a refusal happens *before* any decode.
  *Deps:* B1–B7 · *Effort:* S · *Value:* High

## Group C — housekeeping

- [ ] **C1 · Live round.** Four images through the composer and two `read_file` image calls against
  a vision-capable Pia Cloud deployment. The evidence is the Debug `AiClientService` args/result log
  lines, not the UI reply. Answers **G2** and **G3**. *Deps:* A10, B8 · *Effort:* S · *Value:* High
  **Client half done 2026-09-17, model half still owed.** The throwaway profile's Pia Cloud provider
  has no account, so the turn reaches `Round 1: request carries 1 image message(s)` — the four images
  are on one fused user message, which is the shape `PiaCloudChatClient` expands — and then fails at
  the transport. What that cannot answer is whether the model comes back having seen all four, which
  is the whole of **G2**. Re-run against an account that can reach a vision endpoint. The same log
  also confirmed the admission order: a refused image never reaches `Preparing image attachment`, so
  the count and dedup arms really do sit before the encode.
- [x] **C2 · Zero-warning rebuild, Debug and Release.** `dotnet build -t:Rebuild -v:n` both ways;
  read the count off MSBuild's summary line. *Deps:* A10, B8 · *Effort:* XS · *Value:* Enabler
- [x] **C3 · `RELEASE.md`.** One bullet for the composer change, one for the tool change, in
  `docs/release_notes/RELEASE.md` as the work lands. *Deps:* A6, B2 · *Effort:* XS · *Value:* Med
- [x] **C4 · UI script sweep.** Anything in `tests/ui-scripts/` matching the retired literal
  `Assistant_RemoveAttachment` id moves to the `automationId*=` prefix form.
  *Deps:* A6 · *Effort:* XS · *Value:* Enabler
  Nothing to move — no recorded script ever drove that id. The playbook's id table carried it, and
  that entry is now the `Assistant_RemoveAttachment_<attachmentId>` prefix form.
- [ ] **C5 · Pia.Docs.** Update the EN page and note de/fr as owed. *Deps:* C1 · *Effort:* XS ·
  *Value:* Med
  The premise was wrong: `guides/attachments.mdx` never claimed one image per message, it just says
  nothing about how many. What it now owes is the cap, that attaching appends, and that `read_file`
  can show an image in the files folder — the last of which should wait for C1.

## Suggested order

Cheapest decisive work first, then the two vertical slices.

1. **B6, B5, B3** — three XS steps, independently useful, no dependencies, and B6 closes a hole
   that exists today.
2. **A1 → A3 → A5** — the model and the compactor. A5 is the one piece of real engineering in
   group A and it must land in the same slice as A3, never after it: from the moment a message can
   carry four images, charging them as one under-counts the window by 10 500 tokens.
3. **A4** — the mechanical signature ripple, its own commit.
4. **A2 → A8 → A6 → A7 → A9** — the composer slice, ending with something a user can see.
5. **B1 → B2 → B4 → B7** — the tool slice.
6. **A10, B8, then C1** — tests, then the live round that answers G2 and G3.
7. **C2 → C3 → C4 → C5.**

Groups A and B share no code, so they can be worked in parallel by two people — the only coupling
is the cap constant that **G3** settles, and C1 needs both.

## Not yet planned

- **Persistence of attached images.** Out of scope by owner decision (A-D7). Images are absent from
  `AssistantMessageMapper` and the sync DTO is text-only by server contract, so a reopened chat
  shows nothing. Four missing thumbnails is a louder gap than one; if it gets reported, the shape is
  probably the one `AttachedFileRef` already uses — copy into the sandbox, store a relative path,
  never sync the bytes.
- **Lifting the Pia Cloud vision gate.** Excluded by `screen_capture` D2 and by both plans here. It
  needs a **server capability query**, not a wider provider-type allowlist: the 2026-09-09 G3 round
  proved provider type is necessary and not sufficient, since a Pia Cloud router with no vision
  endpoint fails *after* the pixels are sent. Would also fix pasted and dropped images on OpenAI,
  Anthropic and OpenRouter, which is most of the "why was my image refused" surface.
- **The interactive path re-sends every image on every request.** `ToChatMessage` re-emits each
  prior turn's `DataContent` for the life of the session, and the interactive path configures no
  `AgentContextBudget`, so nothing ever compacts it away. One image per message already did this;
  four multiplies it. Inherited, not caused here — but the first "chat got slow after I pasted
  screenshots" report will otherwise look like a new defect. The cheap mitigation, if it lands, is
  the same `Consume`-style withdrawal the tool loop already uses.
- **Splitting `Msg_File_ImageTooLarge`.** It fires both for an oversized image and for a corrupt or
  unreadable one, so naming a number in it would sometimes lie (customer-feedback H-b noticed this).
  Cheap to split while A2 is open; not required by anything here.
- **PDF pages as images.** `Windows.Data.Pdf` renders without a new package and is the only route
  for a scanned PDF, but it is one image per page against a 4-per-round cap (customer-feedback H-a,
  gate G5).
- **Images in the Optimize view's composer.** Separate attach path, no image support at all today.
- **Reordering, cropping, annotating or redacting** an attached image.
