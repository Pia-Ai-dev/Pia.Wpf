# File drop — Stage 2: vision attachments for AssistantView

Stage 1 (shipped) supports plain-text and `.docx` drops on both Optimize and Assistant: contents are inserted into the input box via the existing Insert-anyway snackbar pattern. Other file kinds are rejected with a per-file snackbar.

This document plans Stage 2: extend Assistant (only) to accept **images** and **PDFs** as multimodal attachments that travel with the next user message.

## Goals

- Drop one or more images (`.png .jpg .jpeg .gif .bmp .webp`) onto AssistantView → image becomes an attachment on the next user message.
- Drop a `.pdf` onto AssistantView → each page is rendered to a PNG and added as an image attachment, named e.g. `report.pdf (page 3)`.
- The Optimize view keeps rejecting these types — they aren't meaningful for text optimization.

## Architecture

### New package

- **`PDFtoImage`** (sungaila, MIT) — pure-managed wrapper over PDFium, .NET 8+. Renders a `Stream` page to `SKBitmap`/`Image`, easy to convert to PNG bytes.
  - Alternative: `Docnet.Core` (MIT, also PDFium). Pick `PDFtoImage` unless a project we share code with already uses Docnet.

### Model changes (`src/Pia.Wpf/Models/`)

Add `DroppedImage.cs`:

```csharp
public sealed class DroppedImage
{
    public required byte[] Bytes { get; init; }
    public required string MediaType { get; init; } // e.g. "image/png"
    public required string DisplayName { get; init; }
    public required BitmapImage Thumbnail { get; init; }
}
```

Update `AssistantMessage`:

- Add `ObservableCollection<DroppedImage> Attachments { get; } = [];`
- Add `bool HasAttachments => Attachments.Count > 0;` (with `OnAttachmentsChanged` partial).
- Rewrite `ToChatMessage()` to build a multipart `ChatMessage` when `Attachments.Count > 0`:

```csharp
public ChatMessage ToChatMessage()
{
    if (Attachments.Count == 0)
        return new ChatMessage(Role, Content);

    var contents = new List<AIContent>();
    if (!string.IsNullOrEmpty(Content))
        contents.Add(new TextContent(Content));
    foreach (var img in Attachments)
        contents.Add(new DataContent(img.Bytes, img.MediaType));
    return new ChatMessage(Role, contents);
}
```

`Microsoft.Extensions.AI.ChatMessage` already accepts `IList<AIContent>` in its constructor — no provider-side changes required.

### ViewModel changes (`AssistantViewModel`)

- Add `ObservableCollection<DroppedImage> PendingAttachments { get; } = [];` (cleared on send and on `ClearConversation`).
- Add `RemoveAttachmentCommand` (param: `DroppedImage`) for the ✕ button on each chip.
- Replace Stage-1 rejection in `ExecuteHandleFilesDropped` for `FileKind.Image`:
  - Load bytes via `File.ReadAllBytesAsync`.
  - Detect MIME from extension.
  - Downscale to 2048 px on the long edge (use `WriteableBitmap` / `TransformedBitmap`) and re-encode to PNG **only if** original is over a size cap (e.g. 10 MB) — most images go through untouched.
  - Build a small `BitmapImage` thumbnail (256 px max) for the chip.
  - Append to `PendingAttachments`.
- Replace Stage-1 rejection for `FileKind.Pdf`:
  - Use `PDFtoImage.Conversion.ToImage(stream, pageIndex, ...)` per page (cap at e.g. 20 pages; if more, snackbar a warning and take the first 20).
  - Encode each as PNG bytes, append as `DroppedImage` with `DisplayName = "$"{filename} (page {N})"`.
- In `ExecuteSendMessage`:
  - When creating the user `AssistantMessage`, copy `PendingAttachments` into the new message's `Attachments` collection.
  - Clear `PendingAttachments` (after the message is added to `Messages`).
- Caps:
  - Max 8 attachments per turn — surplus rejected with a snackbar.
  - Per-image cap noted above.

### View changes (`AssistantView.xaml`)

Above the input box, add a horizontally-scrolling chip row bound to `PendingAttachments`:

```xml
<ItemsControl ItemsSource="{Binding PendingAttachments}"
              Visibility="{Binding PendingAttachments.Count,
                          Converter={StaticResource CountToVisibilityConverter}}">
  <ItemsControl.ItemsPanel>
    <ItemsPanelTemplate>
      <StackPanel Orientation="Horizontal" />
    </ItemsPanelTemplate>
  </ItemsControl.ItemsPanel>
  <ItemsControl.ItemTemplate>
    <DataTemplate>
      <Border Margin="0,0,8,8" CornerRadius="6"
              BorderThickness="1"
              BorderBrush="{DynamicResource ControlStrokeColorDefaultBrush}">
        <Grid>
          <Image Source="{Binding Thumbnail}" Width="64" Height="64" Stretch="UniformToFill" />
          <Button Width="20" Height="20"
                  HorizontalAlignment="Right" VerticalAlignment="Top"
                  Command="{Binding DataContext.RemoveAttachmentCommand,
                                    RelativeSource={RelativeSource AncestorType=UserControl}}"
                  CommandParameter="{Binding}">…</Button>
          <TextBlock Text="{Binding DisplayName}" ... />
        </Grid>
      </Border>
    </DataTemplate>
  </ItemsControl.ItemTemplate>
</ItemsControl>
```

Inside the user-message bubble template, render attached image thumbnails the same way so history shows what was sent.

### Behavior change

In `AssistantView.xaml`, extend `AcceptedExtensions` to include `.png,.jpg,.jpeg,.gif,.bmp,.webp,.pdf` so the drag-over overlay lights up for those types. Optimize keeps the text-only list.

## Privacy logging

Image bytes are user content. The classifier and reader paths in `DroppedFileReader` should only log filenames behind `SensitiveDebug` (or omit them); never log byte content. Existing rule per `CLAUDE.md` — filenames are user-named items, treat as sensitive.

## Open questions to decide before starting

1. **Provider capability**: configured providers may not support vision. Options:
   - (a) Trust the user, surface API errors via the existing snackbar.
   - (b) Add `SupportsVision` to providers and disable drop / grey out the chip row when the active provider can't see images.
   - Recommendation: start with (a); revisit only if it bites in practice.
2. **PDF page cap**: 20 sounds reasonable; reduce if image payloads bloat the provider request budget. Surface the cap in a snackbar when truncated.
3. **History persistence**: `Messages` is in-memory only today. If we later add conversation persistence, attachments need a storage policy (cache file paths vs. embed bytes in JSON).

## Touched files (preview)

- `src/Pia.Wpf/Pia.Wpf.csproj` — add `PDFtoImage` package
- `src/Pia.Wpf/Models/DroppedImage.cs` — new
- `src/Pia.Wpf/Models/AssistantMessage.cs` — add `Attachments`, rewrite `ToChatMessage`
- `src/Pia.Wpf/ViewModels/AssistantViewModel.cs` — image/PDF dispatch in `ExecuteHandleFilesDropped`; `PendingAttachments`; `RemoveAttachmentCommand`; send-path wiring
- `src/Pia.Wpf/Views/AssistantView.xaml` — attachment chip strip; extended `AcceptedExtensions`; user-bubble thumbnails
- `src/Pia.Wpf/Resources/Strings/MessageStrings*.resx` — strings for attachment caps and remove-attachment tooltip
