# More than one image per chat message

**Status:** Planned, not started
**Owner:** Marco Altmann
**Written:** 2026-09-15
**Origin:** A customer ask relayed 2026-09-15 — "why do we only support one image as input but
multiple files?". The single-image limit is decision **D6**/**D16** of
[../file_drop_attachments/2026-08-31-file-drop-attachments-plan.md](../file_drop_attachments/2026-08-31-file-drop-attachments-plan.md)
and appears again in the *Deliberately out of scope* list of
[../screen_capture/2026-09-07-screen-vision-design.md](../screen_capture/2026-09-07-screen-vision-design.md).
Sibling plan: [2026-09-15-image-file-tools-plan.md](2026-09-15-image-file-tools-plan.md).
Tracking surface: [2026-09-15-image-attachments-checklist.md](2026-09-15-image-attachments-checklist.md).

## The short version

There is no technical reason for the limit. It is a scope fence, and the only real engineering
behind it is one line of the context compactor that charges tokens per image-bearing *turn* rather
than per image. Everything else is a type change from `ImageAttachment?` to a collection.

## What is true today

| Fact | Where |
|---|---|
| The composer holds exactly one image | `_pendingAttachment` — `src/Pia.Wpf/ViewModels/AssistantViewModel.cs:100` |
| Two images in one drop: first wins, caution snackbar | `AttachFirstImageAsync` — `AssistantViewModel.cs:2034`, `Msg_File_OneImageOnly` |
| A second image attached **separately** silently replaces the first | `PrepareImageAttachmentAsync` assigns `PendingAttachment` unconditionally — `AssistantViewModel.cs:2105`. No snackbar, no warning. |
| The message carries one image | `AssistantMessage.Attachment` — `src/Pia.Wpf/Models/AssistantMessage.cs:103`; one `DataContent` in `BuildChatMessage` — `:400` |
| **The wire already takes N images** | `PiaCloudChatClient` collects `imageParts` and emits one OpenAI-style `image_url` part per image — `src/Pia.Wpf/Services/PiaCloudChatClient.cs:493`, `:517-536`. **No server change is needed.** |
| The compactor charges a flat 3500 tokens per image-bearing *turn* | `ChargeFor` and the pin-admission loop both read `HasImageContent` as a bool — `src/Pia.Wpf/Services/AgentContextCompactor.cs:72`, `:196`, `:359` |
| One template renders an attachment | `UserMessageTemplate` — `src/Pia.Wpf/Views/AssistantView.xaml:20-42`. The history inspector renders file chips only, no image. |
| Images are never persisted | Absent from `AssistantMessageMapper`; `SyncAssistantChatMessage` is text-only by server contract (`src/Pia.Shared/Models/SyncAssistantChat.cs:68-70`) |
| Images only reach Pia Cloud | `AssistantProviderTakesImagesAsync` — `AssistantViewModel.cs:2067` |

Files were easy for a different reason, and it does not transfer: a file chip is **text**, extracted
at attach time into `AttachedFileContext` and budgeted with `MaxPendingFiles = 5` /
`MaxTotalChars = 40_000`. An image is bytes on a separate content slot with no aggregate cap at all.

## Decisions

| # | Decision | Why |
|---|---|---|
| **D1** | `PendingAttachments` is a new `ObservableCollection<ImageAttachment>`; `PendingAttachment` is **removed**, not kept beside it. | The opposite of file-drop D6 on purpose. D6 added `PendingFiles` *alongside* because files and images are two concepts; here there is one concept, and a shim would give "the image" two spellings and a way for them to disagree. |
| **D2** | Cap: **4 images** per message, **12 MB** of encoded JPEG across them. | `ImageAttachmentProcessor.ThresholdBytes` is 3.5 MB per image, so four is 14 MB worst case. The byte budget is the real gate (it is what files have and images do not); the count is what keeps the thumbnail strip readable. |
| **D3** | Attaching **appends**. A drop, a paste and a screen grab all add to the strip. Over either cap → caution snackbar naming the file that did not fit. | Retires the silent-overwrite defect by construction, and retires `Msg_File_OneImageOnly`. |
| **D4** | `ImageAttachment` gains `Guid Id { get; } = Guid.NewGuid()` and `string? SourcePath`. | The strip's per-item remove button needs a **unique bound** AutomationId. A literal id would make all four report the same one, and `ViewAutomationIdTests` cannot catch that — a bound-but-not-unique id passes it (the pending-file chip binding `FileName` under a `FullPath` dedup is the precedent). `SourcePath` carries the tooltip and the dedup key. |
| **D5** | The compactor charges `ImageTokenCharge` **per image**, in both `ChargeFor` and the pin-admission loop. | Its own doc comment states the asymmetry: under-charging leaves a larger input budget, less compaction, and a context overflow. Four images charged as one under-charges by 10 500 tokens. |
| **D6** | The Pia Cloud provider gate is **inherited unchanged**. | `screen_capture` D2. Lifting it needs a real per-provider vision-capability model — a separate workstream, listed in *not yet planned*. |
| **D7** | Persistence stays **out of scope** (owner decision, 2026-09-15). | A reopened chat shows no images, exactly as today. Logged in the checklist because four missing thumbnails is a louder gap than one. |
| **D8** | Dedup on `SourcePath` when there is one; a pasted or captured image has no path and is never deduped. | Mirrors `DroppedFileAttachmentImporter.IsStaged`. Two pastes of different clipboard contents must both land. |
| **D9** | `CanExecuteRunInBackground` keeps ignoring images, as it does today. | A detached run has no way to carry pixels; nothing here changes that. |

## Steps

### A1 — `ImageAttachment` gains identity

```csharp
public sealed class ImageAttachment
{
    public Guid Id { get; } = Guid.NewGuid();
    public required byte[] JpegBytes { get; init; }
    public required string MimeType { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required BitmapSource Thumbnail { get; init; }

    /// <summary>Null for a paste or a screen grab — those have no file behind them.</summary>
    public string? SourcePath { get; init; }
}
```

`ImageAttachmentProcessor.TryPrepare(string filePath, …)` sets `SourcePath`; the `BitmapSource`
overload leaves it null. `Prepare` takes it as a parameter so the two entry points cannot drift.

### A2 — the collection, the caps, and append semantics

Replace `[ObservableProperty] ImageAttachment? _pendingAttachment` with:

```csharp
public ObservableCollection<ImageAttachment> PendingAttachments { get; } = [];

public const int MaxPendingImages = 4;
public const long MaxPendingImageBytes = 12L * 1024 * 1024;
```

`PrepareImageAttachmentAsync` stops assigning and starts admitting, in this order:

- provider gate (unchanged) → `WarnImageProviderUnsupported`, refuse;
- `PendingAttachments.Count >= MaxPendingImages` → `Msg_File_ImageLimit`, refuse;
- a staged image already has this `SourcePath` (D8) → `Msg_File_DuplicateAttachment`, refuse;
- `TryPrepare` returned null → `Msg_File_ImageTooLarge`, refuse;
- encoded bytes + current total > `MaxPendingImageBytes` → `Msg_File_ImageBudget`, refuse;
- otherwise `PendingAttachments.Add(attachment)`.

The count and dedup checks go **before** the encode so a refused image costs no JPEG work; the byte
check has to come after it, because the size is not known until the quality ladder settles. The
dedup arm only applies to a path-bearing attachment — a paste and a capture have no `SourcePath`
and must never collide on null. `Msg_File_DuplicateAttachment` already exists for file chips and
formats the same way.

`AttachFirstImageAsync` becomes `AttachImagesAsync`: loop every path, stop reporting "kept the
first". The per-file refusals above already name each file that did not fit, so the old aggregate
warning has nothing left to say.

`HasPendingAttachments` drives the strip's visibility. `CanExecuteSendMessage`'s
`PendingAttachment is not null` becomes `PendingAttachments.Count > 0` — and subscribe to
`CollectionChanged` to re-raise it. The `[ObservableProperty]` setter did that for free; this is the
one place the type change silently loses a notification and leaves Send disabled with an image
attached.

Every existing `PendingAttachment = null` (`:599`, `:1154`, `:1170`, `:1277`) becomes
`PendingAttachments.Clear()`. `RemoveAttachmentCommand` becomes
`RelayCommand<ImageAttachment>(a => PendingAttachments.Remove(a))`.

### A3 — the message carries N

`AssistantMessage.Attachment` → `public ObservableCollection<ImageAttachment> Attachments { get; }`,
mirroring `AttachedFiles`. `HasAttachment` → `HasAttachments`; `IsEmptyShell` and the
`OnAttachmentChanged` notification move with it.

```csharp
if (Attachments.Count == 0) return new ChatMessage(Role, visible);

var contents = new List<AIContent>();
if (!string.IsNullOrEmpty(visible)) contents.Add(new TextContent(visible));
foreach (var image in Attachments)
    contents.Add(new DataContent(image.JpegBytes, image.MimeType));
return new ChatMessage(Role, contents);
```

Text first, then images, order preserved — that is the shape `PiaCloudChatClient` emits and the
shape the compactor's pin was measured on.

### A4 — the send signature

`IChatSessionManager.StartTurnAsync` and `ChatSessionManager.StartTurnAsync`:
`ImageAttachment? attachment` → `IReadOnlyList<ImageAttachment>? attachments`.

Four call sites and one guard move with it:

- `ExecuteSendMessage` (`AssistantViewModel.cs:1301`) captures `PendingAttachments.ToArray()`,
  clears, and re-adds the array on a refused send (`accepted == false`) — today that restore is a
  single property assignment.
- The regenerate path (`AssistantViewModel.cs:1644`) forwards the attachment of the turn being
  regenerated; it forwards the list instead.
- The agent-mode suggestion path (`AssistantViewModel.cs:2378`) already passes
  `attachment: null` → `attachments: null`.
- `StartPlannedTurnAsync` (`ChatSessionManager.cs:707`) passes `attachments: null`.
- `TryAnswerParkedRunAsync`'s refusal guard becomes `attachments is { Count: > 0 }`. A parked run's
  resume carries only a text nudge, so an image riding beside the answer would still be dropped
  silently — the guard has to keep catching it.

This is the widest edit in the plan. `Arg.Any<ImageAttachment?>()` appears in
`AssistantViewModelPendingFilesTests`, `AssistantViewModelLeverTests`,
`AssistantViewModelRegenerateTests`, `AssistantViewModelOverlayHostingTests` and
`ChatSessionManagerTests`. Mechanical, but land it as its own commit.

### A5 — the compactor charges per image

```csharp
private static int ImageCountIn(ChatMessage message) =>
    message.Contents.Count(c => c is DataContent d
        && d.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

private static int ChargeFor(ChatMessage message) =>
    (message.Text?.Length ?? 0) / 4 + ImageCountIn(message) * ImageTokenCharge;
```

In the pin-admission loop, `var charged = pinnedImageCost + ImageTokenCharge;` becomes
`+ ImageCountIn(messages[i]) * ImageTokenCharge`. `HasImageContent` stays — the loop still uses it
as the cheap "is this a candidate" test.

`ImageTokenCharge` itself does **not** change: 3500 is the *per-image* bound (1568×1568 ÷ 750,
rounded up), and `AgentContextCompactorTests.ImageTokenCharge_StaysAtTheRecordedBound` locks it.

**The trap this makes reachable sooner.** The head and instruction pins are charged
*unconditionally* — they ship by definition, so there is nothing to admit or refuse. A goal message
carrying four images pins 14 000 tokens before any history is counted.

- At the **default** window this is a non-event: `ContextWindowDefaults.Fallback` is 128 000, so
  14 000 leaves 114 000 of input budget.
- On a **small, user-configured** window it is not. `MaxContextWindowTokens` is an editable provider
  field, and at 8000/2000 **two** images already pin 7000 and leave 1000 — at or below
  `MaxOutputTokens`, which trips the early return at `AgentContextCompactor.cs:240`, skips
  compaction, and sends the request as-is. One image (3500, leaving 4500) still compacts today, so
  this is a boundary that moves from "one image" to "two".

Nothing in this plan fixes that, and D2's cap of 4 does not protect it — a mandatory pin cannot be
shrunk. It is already legible: that early return logs a `LogWarning` naming the pinned total, the
configured window and what is left, precisely because the send is "very likely a provider 400".
Agent-run only; the interactive path configures no budget and never compacts.

The other thing to write down: eviction of a mid-list image turn is **all-or-nothing**. A four-image
turn that exceeds `imageAllowance` is dropped whole, so the model is left referring to four pictures
it cannot see. That is the existing "lose the image, keep the request" philosophy applied to four
instead of one, not a new failure mode — but it is worth a sentence in the code comment.

### A6 — the composer strip

Replace the single `Image` + remove button (`AssistantView.xaml:575-600`) with a horizontal
`ItemsControl` over `PendingAttachments`, visible on `HasPendingAttachments`, each item a ~72 px
thumbnail with an overlaid remove button:

```xml
<ui:Button Command="{Binding DataContext.RemoveAttachmentCommand,
                     RelativeSource={RelativeSource AncestorType=ItemsControl}}"
           CommandParameter="{Binding}"
           AutomationProperties.AutomationId="{Binding Id, StringFormat='Assistant_RemoveAttachment_{0}'}" />
```

The id binds `Id`, never `SourcePath` — a paste has no path and two pastes would then collide on
the empty string.

`Assistant_RemoveAttachment` (the old literal id) is retired; anything in `tests/ui-scripts/`
matching it must move to the `automationId*=` prefix form.

### A7 — the bubble renders N

`UserMessageTemplate` (`AssistantView.xaml:27-42`): the single `Image` bound to
`Attachment.Thumbnail` becomes an `ItemsControl` over `Attachments` in a `WrapPanel`, triggered on
`HasAttachments`. `MessageRoleTemplateSelector` (`:368`) is its only consumer, and the history
inspector never rendered the image — nothing else to touch.

### A8 — strings (all three resx, or `LocalizationTests` fails)

| Key | EN | DE | FR |
|---|---|---|---|
| `Msg_File_ImageLimit` | `At most {0} images can be attached — "{1}" was left out.` | `Es können höchstens {0} Bilder angehängt werden – „{1}" wurde ausgelassen.` | `{0} images au maximum peuvent être jointes – « {1} » n'a pas été ajoutée.` |
| `Msg_File_ImageBudget` | `The attached images are already at the size limit — "{0}" was left out.` | `Die angehängten Bilder haben die Größengrenze erreicht – „{0}" wurde ausgelassen.` | `Les images jointes atteignent déjà la limite de taille – « {0} » n'a pas été ajoutée.` |
| `Msg_File_OneImageOnly` | **delete from all three** | | |

`MessageStrings.resx` / `.de.resx` / `.fr.resx`. Do not hand-edit `Designer.cs`.

### A9 — screen capture appends

`ExecuteCaptureScreen` re-reads the property after preparing
(`if (!attached || PendingAttachment is null) return; RecordScreenCapture(result, PendingAttachment);`).
Change `PrepareImageAttachmentAsync` to return `Task<ImageAttachment?>` and use its return value —
with a collection, re-reading would pick whatever happens to be last. Callers that only want the
bool test `is not null`.

The capture now **adds** to the strip instead of replacing what is there, which is the behaviour the
screen-vision design wanted and could not have.

### A10 — tests

New:

- `Attach_FourImages_AllLandInTheStrip`
- `Attach_FifthImage_IsRefusedAndNamed` — asserts `Msg_File_ImageLimit` formatted with the file name
- `Attach_OverTheByteBudget_IsRefusedAndNamed`
- `Attach_SecondImageSeparately_DoesNotReplaceTheFirst` — the defect this plan fixes; it must fail
  before A2 lands
- `Send_PassesEveryAttachmentToStartTurn` (order preserved)
- `RefusedSend_RestoresEveryAttachment`
- `ToChatMessage_EmitsOneDataContentPerImage_AfterTheText`
- `ParkedRun_AnswerWithImages_IsNotTreatedAsAnAnswer`
- `ChargeFor_ScalesWithImageCount`
- `FourImageGoal_AtTheDefaultWindow_StillCompacts` — 128 000, the case that has to keep working
- `TwoImageGoal_OnATinyConfiguredWindow_SkipsCompactionAndWarns` — the A5 boundary, asserting the
  existing warning rather than an outcome the pin cannot deliver
- `Attach_TheSameFileTwice_IsRefusedAsADuplicate` and
  `Paste_TwoImages_BothLand` (D8's null-`SourcePath` half)
- `RemoveAttachment_RemovesOnlyTheOneClicked`
- `AttachingAnImage_EnablesSend` — the `CollectionChanged` notification A2 warns about

Changed: `TwoImagesRefusedByTheProvider_DoNotClaimOneWasKept` (keep the provider-refusal half, drop
the `Msg_File_OneImageOnly` half — the claim it guards no longer exists), every
`Arg.Any<ImageAttachment?>()` mock, and the `Pia.Views.AssistantView` row in
`ViewAutomationIdTests` (both the control count and the id count move).

## Deliberately out of scope

- **Persistence** (D7). Images stay turn-scoped.
- **Lifting the Pia Cloud gate** (D6). A real per-provider vision-capability model, which per the
  2026-09-09 G3 round also has to be a *server capability query* — provider type is necessary and
  not sufficient, and a text-only routed endpoint still fails after the pixels are sent.
- **Images on the Optimize view's composer.** It has its own attach path and no image support today.
- **Reordering the strip**, and any editing (crop, annotate, redact).
