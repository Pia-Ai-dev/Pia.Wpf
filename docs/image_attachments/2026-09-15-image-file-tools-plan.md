# Images through the file tools — `read_file` and `search_files`

**Status:** Implemented 2026-09-17. Tracking surface: [2026-09-15-image-attachments-checklist.md](2026-09-15-image-attachments-checklist.md).
**Owner:** Marco Altmann
**Written:** 2026-09-15
**Origin:** The same customer ask relayed 2026-09-15 — Pia cannot look at an image that is sitting
in its own files folder. `read_file` refuses it outright and tells the user to attach it instead.
The delivery mechanism this plan uses was built for `screen_capture` in
[../screen_capture/2026-09-07-screen-vision-design.md](../screen_capture/2026-09-07-screen-vision-design.md).
Sibling plan: [2026-09-15-multi-image-composer-plan.md](2026-09-15-multi-image-composer-plan.md).
Tracking surface: [2026-09-15-image-attachments-checklist.md](2026-09-15-image-attachments-checklist.md).

## The short version

Everything needed already exists and is in production for screen capture: `ToolLoopImageChannel` is
installed per round, `ImageAttachmentProcessor.TryPrepare` already takes a file path, and
`ToolLoopImageMessages.Build` fuses caption and pixels into one tagged message the compactor knows
how to pin. This plan wires `read_file` into that channel and stops `search_files` from reporting
every image in the tree as a file it failed to read.

## What is true today

| Fact | Where |
|---|---|
| `read_file` refuses an image | `"Error: '{path}' is an unsupported binary file (image); attach the image instead."` — `src/Pia.Wpf/Services/FilesToolHandler.cs:1143-1144` |
| That refusal lives in a **shared** helper | `ReadFileTextAsync` is called by `HandleReadFileAsync` (`:1085`), `HandleSearchFilesAsync` (`:756`) and `ReadPromptPreviewAsync` (`:1189`, the @Files prompt preview) |
| `search_files` counts an image as an unreadable skip | `RecordSkipped` on any `readError` — `:756-757`; the diagnostic already lumps it in as `"(binary, image, or over the size limit)"` — `:820-822` |
| The channel is installed per **round**, not per turn | `AiClientService.cs:683-686`, drained after the round's last tool result — `:731-735` |
| An injected image is visible in exactly **one** request | Parked at the end of round N, it rides round N+1's request and `Consume` swaps it for a placeholder right after that response — `AiClientService.cs:382`, `ToolLoopImageMessages.cs:34-51`. Verified by reading both call sites, not inferred from the comment. |
| The placeholder hardcodes one source | `"[screen capture, {w}x{h}, consumed]"` — `ToolLoopImageMessages.cs:53` |
| `read_file` is auto-approved | `ToolPermissionService.ReadOnlyBuiltInTools` — `:99-109`. It returns `(result, null)`, so no grant tier is ever consulted. |
| Images only reach Pia Cloud | The channel carries `ProviderType`; `screen_capture` refuses on anything else — `ScreenCaptureToolHandler.cs:209-215` |
| `write_file` has a precedent for "read_file returns a rendered view" | `RenderedReadOnlyExtensions` blocks writing `.msg`/`.eml` for exactly that reason — `FilesToolHandler.cs:1459-1463` |
| The image extension set already exists | `ReadImageExtensions` — `:1308-1311` (`.png .jpg .jpeg .gif .bmp .webp .ico .tiff .tif`) |

## Decisions

| # | Decision | Why |
|---|---|---|
| **B-D1** | `read_file` on an image parks the pixels on `ToolLoopImageChannel` and returns a **text** result describing what was delivered. | A tool result has no image slot and any non-string result is JSON-serialized — that is the premise the channel was built on. |
| **B-D2** | Approval tier **unchanged**: `read_file` stays in `ReadOnlyBuiltInTools`, no card, no grant (owner decision, 2026-09-15). | The user already pointed the sandbox at that folder, and a `.docx` from the same folder goes to the provider as text with no card. `screen_capture` needs one because it reaches outside the sandbox; this does not. |
| **B-D3** | The **one-shot** contract is stated in the result text. | `Consume` withdraws the image before the next round, so the model sees it in one request only. Left unsaid, the model plans a second look it will never get. |
| **B-D4** | At most **4 images per round** may be parked, enforced in the channel (`TryPark` returns false past the cap). Matches the composer cap in the sibling plan. | Four `read_file` calls in one round is a plausible model behaviour and 4 × 3500 tokens is the compactor's whole image allowance on a small window. A per-*turn* cap is unnecessary: `Consume` means only one round's images are ever in flight. |
| **B-D5** | A **raw-byte ceiling checked before decoding**: refuse an image over 25 MB on disk without opening it. | `BitmapDecoder` allocates the decoded surface, not the file. A 40 MP TIFF is ~480 MB of pixels; the existing `MaxReadFileBytes` (1 MB) never guarded this path because the image branch returns before it. |
| **B-D6** | `search_files` counts images **separately** from unreadable files and says so once, pointing at `read_file`. | Today every PNG in the tree lands in "N file(s) could not be searched", which reads like a defect and is misleading once `read_file` can in fact show them. |
| **B-D7** | `write_file` and `edit_file` **refuse** image extensions explicitly, reusing the `RenderedReadOnlyExtensions` reasoning. | `write_file` is a real hole: nothing stops it writing UTF-8 over a PNG today. `edit_file` already refuses — it reads current content through the same `ReadFileTextAsync` (`:1379-1380`) — but it emits the *read* message, "attach the image instead", which becomes wrong advice the moment B2 lands. Both need the guard; only one needs it for correctness. |
| **B-D8** | No staleness record for an image read. | `RecordRead` exists to gate read-before-edit; with B-D7 nothing can edit an image, so recording one would be dead state. The file-touch chip (`FileTouchKind.Read`) **is** raised — the user should see what was looked at. |
| **B-D9** | Pia Cloud gate inherited, refused **before** any decode with a legible message. | Same gate as everywhere else. Refusing first means a non-vision provider never pays the decode. |
| **B-D10** | `ReadPromptPreviewAsync` (@Files) keeps refusing images unchanged. | It builds a prompt *string* with no tool loop and no channel to park on. |

## Steps

### B1 — move the image branch out of the shared helper

`ReadFileTextAsync` keeps its image refusal — `search_files` and `ReadPromptPreviewAsync` still need
it. `HandleReadFileAsync` short-circuits **before** calling it:

```csharp
if (IsImageExtension(Path.GetExtension(safePath)))
    return await DeliverImageAsync(safePath, requested, cancellationToken);
```

Placed after the containment, `SensitivePathGuard` and `File.Exists` checks and before the
offset/limit parse — a windowed read of an image is meaningless, and `offset`/`limit` are simply
ignored (say so in the result text rather than erroring).

### B2 — `DeliverImageAsync`

In order, each arm returning a plain-text refusal the model can act on:

1. `ToolLoopImageChannel.Current is null` → "This turn cannot receive a picture." (matches
   `screen_capture`'s `NoDeliveryChannel`.)
2. `channel.ProviderType != AiProviderType.PiaCloud` → the provider cannot read pictures.
3. `new FileInfo(safePath).Length > MaxImageFileBytes` (25 MB, B-D5) → too large to open.
4. `channel.Count >= MaxImagesPerRound` → "Already showing 4 pictures this round; ask again after
   you have used them."
5. `await Task.Run(() => ImageAttachmentProcessor.TryPrepare(safePath, _logger))` returns null →
   too large to show even re-encoded. (JPEG encoding off the loop thread, as `ScreenCaptureToolHandler`
   does.)
6. Otherwise `channel.TryPark(new ToolLoopImage(callId, image.JpegBytes, image.MimeType,
   image.Width, image.Height, caption))` and return the success text.

The caption (model-facing, English, alongside the pixels):

> `Image file from your read_file call: <relative path>, 1024x768 px.`

The result text (what the tool returns):

```
Showed you "diagrams/flow.png" (1024x768). It is attached to your next message — say what you
need from it now; it is withdrawn afterwards.
```

Two wrinkles worth knowing:

- `HandleReadFileAsync` does not currently receive the `FunctionCallContent`, only `args` — the
  `ToolLoopImage` needs `CallId`. Thread the call id into the read arm of the dispatch switch
  (`:224`), the way `ScreenCaptureToolHandler` reads `toolCall.CallId`.
- `TryPrepare` also builds a 300 px `BitmapSource` thumbnail that nothing on this path consumes.
  Harmless (it is the same freezable work the capture path pays), but if it shows up in a profile,
  give `Prepare` a `withThumbnail: false` arm rather than duplicating the encoder ladder.

### B3 — the channel gets a cap

```csharp
public const int MaxImagesPerRound = 4;

public bool TryPark(ToolLoopImage image)
{
    lock (_parked)
    {
        if (_parked.Count >= MaxImagesPerRound) return false;
        _parked.Add(image);
        return true;
    }
}
```

`Park` stays as-is for `ScreenCaptureToolHandler` (one capture per approved call, already gated by
a confirmation card) or becomes a thin wrapper over `TryPark` that logs the refusal. Either way the
cap has to be inside the lock — two handlers in one round dispatch sequentially today, but nothing
in the type says they must.

### B4 — generalize the placeholder and the caption

`ToolLoopImageMessages.Placeholder(int width, int height)` hardcodes `"screen capture"`. Give
`ToolLoopImage` a `SourceKind` (or reuse the caption's first words) so the placeholder reads
`[image file, 1024x768, consumed]` for this path and keeps saying `[screen capture, …]` for the
other. `ToolLoopImageMessagesTests` asserts the exact string — extend it with a row rather than
loosening the assertion.

### B5 — `search_files` stops calling an image a failure

Add an `imageCount` counter alongside `skippedCount` and check the extension before the read:

```csharp
if (IsImageExtension(Path.GetExtension(canon))) { imageCount++; continue; }
```

Then one extra diagnostic, and the existing note drops the word "image":

```
Note: 7 image file(s) were not searched (images have no text to match). read_file can show you one.
Note: 2 file(s) could not be searched (binary, or over the size limit): dump.bin, huge.csv.
```

`find_files` and `list_files` already list images, so finding one by name needs no change — the
model's path is `find_files` → `read_file`.

### B6 — `write_file` / `edit_file` refuse images

Add `ReadImageExtensions` to the write-side guard next to `RenderedReadOnlyExtensions`
(`FilesToolHandler.cs:1459`), with the same reasoning in the message:

```
Error: '.png' files are read-only here — read_file shows you the picture, not the file's own
bytes, so writing that back would destroy the image.
```

The two paths are **not** symmetric:

- `write_file` never reads the target first, so today it will happily write UTF-8 over a PNG. This
  is the hole, and the guard closes it.
- `edit_file` already refuses, because `PrepareEditFileAsync` reads current content through
  `ReadFileTextAsync` and returns its error verbatim (`:1379-1380`). What it says is
  "attach the image instead" — advice that is wrong the moment B2 lands. Put the explicit guard
  **before** that read so the message is the one above.

### B7 — tool descriptions

`read_file`'s description opens with "Read the contents of a **text** file" and lists the extracted
formats. Add one clause: image files are shown as a picture, in the next message, once. Keep it to a
sentence — the description is sent on every request.

### B8 — tests

New, in `FilesToolHandlerReadImageTests` (mirroring the existing `Screen` handler tests, which
already show how to install a channel in a test):

- `ReadFile_OnAPng_ParksTheImageAndSaysSo`
- `ReadFile_OnAPng_WithNoChannel_RefusesWithoutDecoding`
- `ReadFile_OnAPng_OnANonCloudProvider_Refuses`
- `ReadFile_OnAHugeImage_RefusesOnTheByteCeiling_WithoutDecoding` — assert no decode happened
  (a truncated/garbage file over the ceiling is the cheap way: a decode would throw)
- `ReadFile_FifthImageInARound_IsRefused`
- `ReadFile_OnAnImage_RaisesTheFileTouchChip_AndRecordsNoStaleness`
- `ReadFile_OutsideTheSandbox_StillRefusesBeforeAnyImageHandling` — containment must not regress
- `ReadFile_OnASensitivePath_StillRefuses`
- `SearchFiles_CountsImagesSeparatelyFromUnreadableFiles`
- `PromptPreview_OnAnImage_StillRefuses` (B-D10)
- `WriteFile_OnAPng_IsRefused` / `EditFile_OnAPng_IsRefused`
- `Placeholder_SaysImageFile_ForAFileSourcedImage`

## Deliberately out of scope

- **Rendering a PDF page as an image.** `Windows.Data.Pdf` can do it with no new package, and it is
  the only answer for a scanned PDF with no text layer — but it is one image per page against a
  4-per-round cap, and it is a separate feature (customer-feedback H-a, gate G5).
- **OCR.** Not needed: the model reads the picture.
- **`screen_capture` keeping its approval card.** Unchanged by this plan.
- **Lifting the Pia Cloud gate** (B-D9). Same separate workstream as the sibling plan.
- **Writing images** (B-D7 refuses them). Generating or editing an image is not this.
