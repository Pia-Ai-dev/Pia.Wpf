# File-drop attachments: text and mail files become chips, not composer text

**Status:** Planned — not started
**Owner:** Marco Altmann
**Written:** 2026-08-31
**Origin:** Owner request 2026-08-31 ("drag-and-drop text files pollutes the chat input; show a file chip like we do for images, and let me drop Outlook .msg / .eml so I can speak to mails"), plus the senior review that settled the thirteen constraints in §2.

Companion tracking surface: [2026-08-31-file-drop-attachments-checklist.md](2026-08-31-file-drop-attachments-checklist.md).

---

## 1. What is wrong today, and what we want instead

Dropping `notes.txt` onto the Assistant window today calls `AssistantViewModel.ExecuteHandleFilesDropped` (`src/Pia.Wpf/ViewModels/AssistantViewModel.cs:1692-1707`), which reads the file through `DroppedFileImporter.TryImportAsync` and **splats the whole file into `InputText`**. The composer fills with 4,000 lines of log, the user's own question is buried, and the displayed user bubble is that same wall of text forever.

Dropping an image behaves the way we want: `PrepareImageAttachmentAsync` (`:1727-1750`) stages an `ImageAttachment` in `PendingAttachment`, a thumbnail appears next to the composer, the typed text stays clean, and the bytes ride the message separately.

**Target behaviour**

1. A dropped/picked **text-family** file (`.txt .md .json .cs .docx .xlsx …`) or **mail** file (`.msg .eml`) becomes a **chip** — file icon, file name, ✕ — in a strip above the composer. `InputText` is not touched.
2. The extracted text rides the turn inside an `<attached_file …>` wrapper appended to the **AI-visible** user message only. The displayed bubble stays whatever the user typed.
3. The wrapper is stored on the `AssistantMessage`, so a follow-up question two turns later still sees the mail.
4. Outlook `.msg` and RFC 5322 `.eml` are parsed to normalized text (whitelisted headers + plain-text body) by hand-rolled readers — no new NuGet package.
5. Images keep their existing single-`PendingAttachment` path untouched. A mixed drop routes each file to the right bucket.

**What this does not deliver, stated up front.** The Origin's literal wording is "drop Outlook `.msg`". A message dragged straight out of Outlook's message list **does not offer `CF_HDROP`** — it offers the shell's `FileGroupDescriptorW` + `FileContents` pair, because the item has no path on disk yet. `FileDropBehavior` hard-gates on `DataFormats.FileDrop` in both `TryAcceptDrag` (`src/Pia.Wpf/Behaviors/FileDropBehavior.cs:127-130`) and `OnDrop` (`:105-125`), so that drag is rejected at `OnDragEnter`: no overlay, no handler, nothing at all. As scoped, the feature covers a `.msg` the user **saved to disk first** and dragged from Explorer, and the Attach-file picker. Gate **G0** in the checklist answers, in thirty seconds against today's build, whether that is acceptable or whether a `FileGroupDescriptorW`/`FileContents` materializer has to be scoped in before A1 (§16 has the shape it would take).

> **Superseded 2026-09-01.** The owner answered open question 5 with *yes*, and the materializer shipped as round 4 — see [2026-09-01-outlook-virtual-file-drop.md](2026-09-01-outlook-virtual-file-drop.md). A mail dragged out of **Outlook classic** now works, through `TYMED_ISTORAGE`. **Outlook new** still does not, and cannot: its drag carries mailbox row identifiers rather than a file, so it now shows a snackbar telling the user to save the item as a file first.

---

## 2. Settled decisions (do not re-litigate)

| # | Decision |
|---|---|
| D1 | Dropping/selecting a text file must **stop** inserting into the composer; show a chip instead. |
| D2 | The file text goes into the AI-visible user message inside a wrapper that names it as file content. Typed text and the displayed bubble stay clean. |
| D3 | `.msg` and `.eml` are droppable and parsed to text (header whitelist + body). |
| D4 | A **new** in-memory field on `AssistantMessage` (`AttachedFileContext`) holds the rendered wrapper. **No `Pia.Shared` DTO change, no sync-contract change.** It must survive across turns within the session. |
| D5 | The field must reach **both** `ToChatMessage()` paths. See §8.1 — this plan replaces the two-overload edit with a single private builder, because the naive edit has a worse trap than the stated one. |
| D6 | A **new** `ObservableCollection<PendingFileAttachment> PendingFiles` alongside the existing single `PendingAttachment`. Do **not** generalize `PendingAttachment` (it ripples into `ToChatMessage`'s `DataContent` branch, `AgentContextCompactor`, and the compaction byte-scoring). |
| D7 | Cap the injected text per file well below `DroppedFileReader.MaxTextBytes` (1 MB). This text is resent on **every** turn, unlike the one-shot `@Files` injection. Follow the `MaxFilePreviews` / `FilePreviewLines` precedent. |
| D8 | Do **not** route text/mail through `PrepareImageAttachmentAsync` — it hard-gates on `ProviderType == PiaCloud`, correct for vision, wrong for text. A BYOK user must be able to drop a `.txt`. |
| D9 | Do **not** change `DroppedFileImporter.TryImportAsync`'s contract — `OptimizeViewModel.cs:690` shares it and Optimize is out of scope. Add a sibling API. |
| D10 | Mixed drops work: image → `PendingAttachment`, text/mail → `PendingFiles`, in one drop. |
| D11 | Mail parsing is hand-rolled. No new NuGet package. |
| D12 | A DEBUG-only bypass (env var) lets a WinWright script feed a path list straight into the drop handler — UIA cannot synthesize a shell OLE drag-drop into a WPF window. |
| D13 | Icons must be `SymbolRegular` members inside the BMP (≤ U+FFFF). Verified safe: `Mail24` (U+F507), `DocumentText24` (U+E558), `Document24` (U+F379), `Attach24` (U+F1AA), `Dismiss16`. |

### Decisions this plan adds (previously implicit)

| # | Decision | Why |
|---|---|---|
| D14 | `FileKind.Email` is added to `DroppedFileReader` **and** an `Email` case is added to `DroppedFileImporter.TryImportAsync`'s switch. | Without the case, `.msg`/`.eml` hits `default:` and Optimize would show "unsupported" for a type its own picker now offers. Optimize gains mail-text insertion, which is harmless and keeps the two views consistent. |
| D15 | The Assistant gets its **own** overlay-hint loc key. `FileDrop_Overlay_Hint` stays as-is for Optimize, where "insert its contents" is still literally true. | Rewording the shared key would make Optimize's overlay lie. |
| D16 | Two images in one drop: **first wins**, the rest get a caution snackbar. | `PendingAttachment` stays singular (D6); the corner has to be defined. |
| D17 | **A file-bearing send is forced to Chat shape**: `ExecuteSendMessage` passes `planned: false` whenever the turn carries an attached file. The Agent lever itself is **not** touched — only this turn is downgraded; the next send with no chip attached plans normally. | An earlier draft of this plan claimed the agent-run path needed no gate because `AttachedFileContext` flows into every step. That is true **only** while the run stays inside the live `ChatSession`, and three measured legs break it. See §8.4.2 for the chain and the code refs. |
| D18 | The chip's per-item AutomationId identity is **`FileName`**, not an id/GUID. | A WinWright script driving `PIA_DEBUG_DROP_FILES` must be able to target a *known* chip's ✕. Consequence, stated openly: two files with the same name from different folders share an automation id. Dedup is by full path, so both chips exist. |
| D19 | The chip strip is a **second, sibling `Border`** above the existing image-preview `Border`, not a rework of it. | The `{x:Null}` DataTrigger at `AssistantView.xaml:285-294` cannot express "collection empty," and D6 says leave the image path alone. Two stacked boxes on a mixed drop is acceptable; polishing it is a round-3 item. |
| D20 | **Run-in-background is disabled while any file is attached**, with a hint saying why. The same hint explains D17's downgrade — one loc key, one composer line. | A detached run cannot carry the payload: `HeadlessTurnExecutor` builds from persisted DTOs (`:304`, `:326`) and `ctx.Goal` (`:313`), and D4 forbids persisting `AttachedFileContext`. The alternative — folding the block into `ctx.Goal` — puts up to 40,000 chars into the compactor's pinned head on **every** step of a 20-step run, which is the one place §5's arithmetic does not protect. See §8.4.1. |
| D21 | The `Date:` line §6.4 renders uses **`yyyy/MM/dd HH:mm zzz`** — slashes, not hyphens. | `StructuredPiiDetector.PhoneRegex` (`src/Pia.Wpf/Services/StructuredPiiDetector.cs:32`) is `\+?[\d\s\-().]{7,20}\d` with a ≥7-digit filter, and `TokenizingAiClientService.TokenizeMessages` (`:269-288`) rewrites user-role text before it leaves the process. Measured: `Date: 2026-08-31 11:46 +00:00` matches (10 digits) and reaches the model as `Date: [Phone_N]:46 +00:00`; the slash form matches nothing. The in-repo precedent and its comment are at `src/Pia.Wpf/Services/AssignmentToolHandler.cs:383-385`. |

---

## 3. Data flow

```
drop / Attach-file / PIA_DEBUG_DROP_FILES
        |
        v
AssistantViewModel.ExecuteHandleFilesDropped(paths)
        |
        +-- Classify == Image  --> ExecuteHandleImageAttached (first only) --> PendingAttachment
        |
        +-- Text/Docx/Xlsx/Email --> DroppedFileAttachmentImporter.TryStageAsync
                                          |
                                          v
                                   PendingFileAttachment  --> PendingFiles (ObservableCollection)
                                          |
                    Send  ------------------
                                          v
                     AssistantPromptComposer.BuildAttachedFileBlock(files)  -> string
                                          |
                                          v
        ChatSessionManager.StartTurnAsync(..., attachedFileContext)
                                          |
                                          v
                 AssistantMessage.AttachedFileContext  (in-memory, never persisted)
                                          |
                                          v
                 AssistantMessage.BuildChatMessage(text)  -> ChatMessage
                    (used by BOTH ToChatMessage overloads, every turn)
```

---

## 4. New types

### 4.1 `src/Pia.Wpf/Models/PendingFileAttachment.cs` (new file, LF or CRLF: new file, use CRLF to match `Models/`)

```csharp
using Wpf.Ui.Controls;

namespace Pia.Models;

public enum PendingFileKind
{
    Text,
    Document,
    Email,
}

public sealed class PendingFileAttachment
{
    public required string FullPath { get; init; }
    public required string FileName { get; init; }
    public required PendingFileKind Kind { get; init; }
    public required string Text { get; init; }
    public required bool Truncated { get; init; }
    public required int OriginalCharCount { get; init; }

    public SymbolRegular Icon => Kind switch
    {
        PendingFileKind.Email => SymbolRegular.Mail24,
        PendingFileKind.Document => SymbolRegular.Document24,
        _ => SymbolRegular.DocumentText24,
    };
}
```

`SymbolRegular` in a model type has precedent: `src/Pia.Wpf/Models/AutocompleteSuggestion.cs:8`.

`Kind` mapping: `FileKind.Text` → `PendingFileKind.Text`; `FileKind.Docx` / `FileKind.Xlsx` → `Document`; `FileKind.Email` → `Email`.

### 4.2 `src/Pia.Wpf/Helpers/Email/EmailMessage.cs` (new)

```csharp
namespace Pia.Helpers.Email;

public sealed record EmailMessage(
    string? Subject,
    string? From,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    DateTimeOffset? Date,
    string Body,
    IReadOnlyList<string> AttachmentNames,
    bool BodyIsFromHtmlFallback);
```

Field contracts:

- `Subject` — decoded, every whitespace run collapsed to one space, trimmed. `null` when absent or empty after normalization.
- `From` — `"Display Name <addr@host>"` when both are known, else whichever exists.
- `To` / `Cc` — each entry `"Display Name <addr@host>"`. **Empty list, never null.**
- `Date` — `DateTimeOffset?`, **not** `DateTime`. A MSG FILETIME must land as `+00:00`; an EML `Date:` carries an explicit offset that must be preserved. Never collapse to local time. **`DateTimeOffset.FromFileTime` does not do this** — see §7.2.
- `Body` — line endings normalized to `\n`, trimmed, runs of 3+ blank lines collapsed to 2. Never null; `""` when there is no body.
- `BodyIsFromHtmlFallback` — true when the plain body had to be derived by stripping HTML. Reachable from **both** readers: EML via `text/html` part selection (§7.3), MSG via `10130102` PR_HTML (§7.2).

There is deliberately **no `ReplyTo` member** — see §7.5.

### 4.3 `src/Pia.Wpf/Helpers/DroppedFileAttachmentImporter.cs` (new)

The sibling API demanded by D9. `DroppedFileImporter.TryImportAsync`'s `IReadOnlyList<string> → Task<string?>` shape is **untouched**.

```csharp
namespace Pia.Helpers;

public static class DroppedFileAttachmentImporter
{
    public const int MaxPendingFiles = 5;
    public const int MaxFileChars = 20_000;
    public const int MaxTotalChars = 40_000;

    public sealed record StageResult(
        IReadOnlyList<PendingFileAttachment> Staged,
        IReadOnlyList<string> ImagePaths);

    /// <summary>Reads the non-image drops into pending chips and hands back the image paths untouched,
    /// so one drop can carry both.</summary>
    public static async Task<StageResult> TryStageAsync(
        IReadOnlyList<string> paths,
        IReadOnlyCollection<PendingFileAttachment> alreadyPending,
        ILogger logger,
        ISnackbarService snackbarService,
        ILocalizationService localizationService,
        CancellationToken ct = default);
}
```

Behaviour, in order, per path:

1. `DroppedFileReader.Classify(path)`.
2. `FileKind.Image` → append to `ImagePaths`, continue (the VM decides what to do with them).
3. `FileKind.Unsupported` / `Pdf` / `Audio` → `Msg_File_UnsupportedAttachment` caution snackbar, continue.
4. Duplicate — `alreadyPending` (plus what this call has staged) already holds this `FullPath`, `OrdinalIgnoreCase` — → `Msg_File_DuplicateAttachment` caution snackbar, continue.
5. Count ceiling — `alreadyPending.Count + staged.Count >= MaxPendingFiles` → `localizationService.Format("Msg_File_AttachLimit", MaxPendingFiles, fileName)` as a caution snackbar, continue (do **not** break: the user should hear about every skipped file). **Two placeholders**, like `Msg_File_ReadFailed` below and unlike every other key in this list — `At most {0} files can be attached to one message — "{1}" was skipped.`
6. Read: `Text` → `DroppedFileReader.ReadTextAsync`; `Docx` → `ReadDocxAsync`; `Xlsx` → `ReadXlsxAsync`; `Email` → `DroppedFileReader.ReadEmailAsync`.
7. `ReadStatus.TooLarge` → `localizationService.Format("Msg_File_TooLargeAttachment", fileName)` — its own key, because Optimize's `Msg_File_TooLarge` says "too large to insert" and this path never inserts; `ReadStatus.Failed` → `localizationService.Format("Msg_File_ReadFailed", fileName, result.Error ?? string.Empty)`. **`Msg_File_ReadFailed` takes two placeholders** — `Couldn't read "{0}": {1}` (`src/Pia.Wpf/Resources/Strings/MessageStrings.resx:52`, DE `:49`, FR `:49`) — and `LocalizationService.Format` is a plain `string.Format` (`src/Pia.Wpf/Services/LocalizationService.cs:24-28`), so copying the one-arg shape of its neighbours throws `FormatException` on the first locked file. The existing correct call site is `DroppedFileImporter.cs:74`. For `FileKind.Email`, `{1}` is the exception **type** name, not `ex.Message` (§7.4) — a deliberate, user-visible consequence of the privacy rule in §12. See §12 for the log line.
8. Text is empty or whitespace-only → `Msg_File_Empty` caution snackbar, continue. Covers the 0-byte file and the mail with no readable body.
9. Truncate: per-file to `MaxFileChars`; then, against the running total including `alreadyPending`, to whatever is left of `MaxTotalChars`. If nothing is left, `localizationService.Format("Msg_File_AttachBudget", fileName)` — **one** placeholder, and a sentence about the message being full rather than step 5's file count, which two 20,000-char files have not come close to — and continue. Record `Truncated` and `OriginalCharCount`.
10. Truncation happened → `Msg_File_Truncated` caution snackbar (one per file).

`Msg_File_TooLargeAttachment` for `FileKind.Email` fires from either of two gates inside `ReadEmailAsync`, never from the shared 1 MB `FileInfo.Length` guard: the container ceiling of `MaxTextBytes * 8`, and `MaxTextBytes` on the **rendered text**. The rendered-text gate is the one that matters, because a `.msg` with a 30 MB attachment can have a 500-byte body; the container ceiling only keeps a pathological file out of memory, since both readers are synchronous and hold the whole file (§7.4).

### 4.4 `src/Pia.Wpf/Services/AssistantPromptComposer.cs` — new static method

Added next to `BuildFileContextBlock` (`:277-312`) so it reuses the existing `private static string EscapeAttr(string)` at `:314-315` — do **not** copy that helper, and do **not** "improve" its escaping (`&` first, then `"`, then `<`; `>` and `'` are deliberately not escaped). A different construct is a different thing to the model.

```csharp
public static string BuildAttachedFileBlock(IReadOnlyList<PendingFileAttachment> files)
```

---

## 5. Caps and limits, with the arithmetic

`AgentContextCompactor.ChargeFor` (`src/Pia.Wpf/Services/AgentContextCompactor.cs:356-357`) estimates a message at `Text.Length / 4` tokens. Everything below is derived against that estimator and `ContextWindowDefaults.Fallback = 128_000` (`src/Pia.Wpf/Models/ContextWindowDefaults.cs:21`).

| Constant | Value | Derivation |
|---|---|---|
| `MaxPendingFiles` | **5** | Mirrors `ChatSessionManager.MaxFilePreviews = 5` (`:81`). Five chips also fit one wrapped row at the composer's width. |
| `MaxFileChars` | **20_000** | ≈5,000 tokens. ~250 lines of 80-column text — 2.5× `FilePreviewLines = 100` (`:78`), justified because this is the *whole* attachment, not a peek. 2% of `DroppedFileReader.MaxTextBytes` (1 MB), satisfying D7's "well below". |
| `MaxTotalChars` | **40_000** | ≈10,000 tokens for the entire `<attached_file>` block on one message. Against the 128,000 fallback that is 8%; against a 32,768-token model it is 30%, still leaving an 8,192-token output reservation intact. This is the number that keeps `AgentContextCompactor.cs:237-258` ("the pinned prefix leaves no input budget" → uncompacted send → provider 400) out of reach for a single file-bearing turn. |
| Chip filename display width | **180 px**, `TextTrimming="CharacterEllipsis"` | Five chips × (icon + 180 + ✕) wraps inside the composer card. |

**Honest limit:** the interactive message list is deliberately never compacted (`ChatSession.cs:361-367`). Three file-bearing turns in one session therefore pin ~120,000 chars ≈ 30,000 tokens of attachment text with nothing to shrink it — fine on a 128k model, fatal on a 32k one. Mitigation is the user's: remove chips, or start a new chat. Recorded as a risk in §16, not solved here.

---

## 6. The wrapper format

### 6.1 Exact shape

Mirrors `AssistantPromptComposer.BuildFileContextBlock` (`:277-312`): XML-style elements so the payload cannot collide with Markdown code fences that appear inside it.

- One preamble line, appended once, no trailing newline:
  `The user attached the following file(s) to this message. Use them as context for the request.`
- `"\n\n"` before **every** element.
- Element open tag attributes, in this order: `name`, `type`, then `truncated` + `note` only when the text was cut.
- Attribute values go through the existing `EscapeAttr`. The body is appended **raw**, exactly as `BuildFileContextBlock` does.
- `name` carries the **file name only**, never the full path — the user's directory layout is not the model's business, and the full path is already in the chip tooltip.
- `type` is `text`, `document`, or `email` (lowercased `PendingFileKind`).

```
The user attached the following file(s) to this message. Use them as context for the request.

<attached_file name="{EscapeAttr(FileName)}" type="{type}">
{Text}
</attached_file>
```

Truncated form — the two extra attributes sit between `type` and `>`:

```
<attached_file name="release-notes.md" type="text" truncated="true" note="Showing the first 20000 of 84213 characters.">
```

No `read_file`-style "read the rest" note: unlike `@Files`, this content has no path the model can re-open (the file may be outside the sandbox entirely), so the advice would be wrong on every turn.

### 6.2 Worked example — a `.txt`

User types `whats wrong here?` and drops `C:\logs\build.log` (2,100 chars, not truncated).

Displayed bubble: `whats wrong here?`

AI-visible message text:

```
whats wrong here?

The user attached the following file(s) to this message. Use them as context for the request.

<attached_file name="build.log" type="text">
MSBuild version 17.11.9+a69bbaaf5 for .NET
  Determining projects to restore...
error CS0246: The type or namespace name 'Foo' could not be found
</attached_file>
```

### 6.3 Worked example — a `.msg`

User types `who sent this and what do they want?` and drops `artifacts\sample.msg`.

Displayed bubble: `who sent this and what do they want?`

AI-visible message text:

```
who sent this and what do they want?

The user attached the following file(s) to this message. Use them as context for the request.

<attached_file name="sample.msg" type="email">
Subject: neo42 Service Portal - Individualpaketierung abgeschlossen
From: neo42 Service Portal <no-reply@neo42.de>
To: Marco Altmann <marco.altmann@neo42.de>
Date: 2026/08/31 11:46 +00:00
===

neo42 GmbH

Eine Datei zur Individualpaketierung wurde soeben im Application Package Center (APC) für Sie bereitgestellt.

Name: neo42_Pia_Ver1.4.15.0_Rev0.zip
Größe: 242.86 MB
Uploader: holger.sundermann@neo42.de
</attached_file>
```

### 6.4 The mail body rendered inside the element

`DroppedFileReader.ReadEmailAsync` returns this as its `ReadResult.Text` — the wrapper does not know it is looking at mail beyond the `type` attribute.

```
Subject: <Subject>                    <- whole line omitted when empty
From: <From>                          <- whole line omitted when empty
To: <To joined by ", ">               <- whole line omitted when empty
Cc: <Cc joined by ", ">               <- whole line omitted when empty
Date: <Date, "yyyy/MM/dd HH:mm zzz">  <- whole line omitted when null
Attachments: <names joined by ", ">   <- whole line omitted when empty
===                                   <- present only when there is both a header line and a body

<Body>
```

Only these fields are emitted. Everything else in the source — `Received`, `DKIM-Signature`, `ARC-*`, `Authentication-Results`, `X-*`, `Message-ID`, `Return-Path`, `List-*`, `Reply-To` — is dropped. §7.5 has the numbers behind that.

**The `===` rule is what separates the header block from the body**, not a blank line. `StructuredPiiDetector.PhoneRegex` accepts whitespace inside a run of digits, newlines included, so a blank line lets one match start in a `Date:` and swallow the opening digits of the body; `=` is outside its character class and stops it dead.

**Slashes in the date are load-bearing, not a style choice** (D21). Format with `CultureInfo.InvariantCulture` so a de/fr user does not get a locale-shifted month order — `LocalizationService.SetLanguage` sets `CultureInfo.DefaultThreadCurrentCulture`.

---

## 7. The mail parsers

New folder `src/Pia.Wpf/Helpers/Email/`, namespace `Pia.Helpers.Email`:

| File | Contents |
|---|---|
| `EmailMessage.cs` | the record in §4.2 |
| `CompoundFile.cs` | the CFB container reader (header, DIFAT, FAT, mini-FAT, directory, `ReadStream`) |
| `MsgReader.cs` | MAPI property extraction on top of `CompoundFile` |
| `EmlReader.cs` | RFC 5322 / MIME |
| `MimeDecoding.cs` | RFC 2047, quoted-printable, base64, the charset table, the HTML strip |

Total ~500-700 lines. Both readers are `internal static` except a single `public static EmailMessage Read(Stream|string path)` entry point each.

### 7.1 `CompoundFile` — required capabilities

Verified against `artifacts/sample.msg` (83,968 bytes, CFB v3).

**Header (offsets):** `0x00` signature `D0CF11E0A1B11AE1` · `0x1A` majorVersion (3 → 512-byte sectors, 4-byte stream sizes; 4 → 4096-byte sectors, 8-byte sizes) · `0x1E` sectorShift · `0x20` miniSectorShift (6 → 64) · `0x2C` numFatSectors · `0x30` firstDirectorySector · `0x38` miniStreamCutoff (4096) · `0x3C` firstMiniFatSector · `0x40` numMiniFatSectors · `0x44` firstDifatSector · `0x48` numDifatSectors · `0x4C…` 109 DIFAT slots.

Sector *n* starts at file byte `512 + n * 512` for v3. Sentinels: `0xFFFFFFFF` FREESECT, `0xFFFFFFFE` ENDOFCHAIN, `0xFFFFFFFD` FATSECT, `0xFFFFFFFC` DIFSECT.

**DIFAT.** The sample uses only 2 of the 109 header slots and has `numDifatSectors = 0`, so continuation is **not covered by the fixture**. Implement it anyway: each continuation sector holds `(sectorSize/4) - 1` FAT sector ids plus a next-pointer in its last 4 bytes.

**Directory sectors come from following the FAT chain from `firstDirectorySector`.** `numDirectorySectors` at `0x28` is **always 0 in v3** — using it yields an empty directory. The sample's chain is 39 sectors → 156 entries, of which 154 are allocated.

**Directory entry, 128 bytes:**

| Off | Size | Field |
|---|---|---|
| 0 | 64 | name, UTF-16LE |
| 64 | 2 | nameLength **in bytes including the U+0000 terminator** → `chars = (len-2)/2` |
| 66 | 1 | objectType: 0 unallocated, 1 storage, 2 stream, 5 root |
| 67 | 1 | colorFlag |
| **68** | 4 | **leftSiblingID** |
| **72** | 4 | **rightSiblingID** |
| **76** | 4 | **childID** |
| 80 | 16 | CLSID |
| 116 | 4 | startingSectorLocation |
| 120 | 4 | streamSize low dword |
| 124 | 4 | streamSize high dword — assert 0 on v3 |

`0xFFFFFFFF` = NOSTREAM. **The left/right/child order above is the real one.** A common mis-statement is `child/left/right` at 68/72/76; that produces a plausible but wrong tree with no exception.

**Red-black tree walk is mandatory, a flat scan is wrong.** Children of a storage are the in-order walk of the tree rooted at its `childID` (`left → self → right`). Build a `Dictionary<string, DirectoryEntry>` per storage; do not rely on the ordering. Concrete proof from the fixture: `__substg1.0_10130102` (PR_HTML) exists **only** as an 8-byte child of `__nameid_version1.0`, never at root. A flat scan reports "PR_HTML present, 8 bytes" and renders garbage.

**Mini-FAT dispatch is the single most dangerous defect in this feature.**

```
streamSize <  4096  ->  mini-sector chain, walked through the miniFAT,
                        addressed INSIDE the Root Entry's own stream (the mini stream)
streamSize >= 4096  ->  normal FAT chain, addressed in FILE sectors
```

`startingSectorLocation` is the same field in two different coordinate systems. Reading `__substg1.0_1000001F` (PR_BODY, 1,100 bytes, start 142) through the normal FAT returns `"sKJEV4cGFuc2lvbldvcmRzVG9DYXBpdGFsSW5pdG"` — valid UTF-16, printable, and completely wrong. **It does not throw.** A test that only asserts "non-empty string" ships a broken parser. The mini stream in the sample is 78 sectors = 39,936 bytes against a declared size of 39,488 — trim to the declared size.

The fixture exercises both allocators: 105 root streams via miniFAT, 2 via the normal FAT (`__substg1.0_007D001F` at 14,674 B and `__substg1.0_8034001F` at 4,808 B).

**Guards.** Validate the signature; reject `majorVersion` ∉ {3,4} and `sectorShift` ∉ {9,12}; bounds-check every sector index against both the FAT length and the file length; keep a visited-set on the FAT, miniFAT and directory walks (a cyclic chain otherwise hangs); cap total allocated bytes; skip `objectType == 0`; tolerate a zero-length stream without touching `startingSectorLocation`.

### 7.2 `MsgReader` — MAPI tags to read

Stream naming: `__substg1.0_<TAG>` where `<TAG>` is `%04X%04X` of (propertyId, propertyType). Type suffixes: `001F` PT_UNICODE (UTF-16LE), `001E` PT_STRING8, `0102` PT_BINARY, `0040` PT_SYSTIME, `0003` PT_LONG.

**Root-scoped:**

| Tag | Property | Use |
|---|---|---|
| `0037001F` | PR_SUBJECT | `Subject` |
| `0E1D001F` | PR_NORMALIZED_SUBJECT | `Subject` fallback |
| `1000001F` | PR_BODY | `Body` |
| `1000001E` | PR_BODY (ANSI) | `Body` fallback, decoded with the `3FDE0003` codepage |
| `0C1A001F` | PR_SENDER_NAME | `From` display name |
| `0C1E001F` | PR_SENDER_ADDRTYPE | selects between the two below |
| `0C1F001F` | PR_SENDER_EMAIL_ADDRESS | `From` address when addrtype is `SMTP` |
| `5D01001F` | PR_SMTP_SENDER | `From` address otherwise |
| `0E04001F` | PR_DISPLAY_TO | `To` fallback when no recipient storages exist |
| `0E03001F` | PR_DISPLAY_CC | `Cc` fallback, same condition |
| `007D001F` | PR_TRANSPORT_MESSAGE_HEADERS | **`Date` fallback only** — parse its `Date:` line through §7.3's date rules. Never emitted. |
| `10130102` | PR_HTML | **`Body` fallback** when `1000001F`/`1000001E` are absent — read from the **root storage only**. See below. |
| `10090102` | PR_RTF_COMPRESSED | **out of scope** — the fixture's is `LZFu`-compressed, and with the PR_HTML fallback in place the remaining gap (RTF-only, no PR_BODY, no PR_HTML) is a headers-only render. Do not build an LZ77 decompressor. |

`3FDE0003` **PR_INTERNET_CPID is not a `__substg1.0_` stream** — PT_LONG values live inside `__properties_version1.0`, exactly like `0C150003` below. Measured on the fixture: `__substg1.0_3FDE0003` is **absent**, and the property record carries `65001`. Read it from the property loop, not by stream name; an implementer who looks for the stream gets `null` and silently falls through to UTF-8.

**The PR_HTML fallback.** Unlike `10090102`, `10130102` is PT_BINARY holding **uncompressed** HTML bytes, so it costs no decompressor. Read it only when neither `1000001F` nor `1000001E` yields text, decode with the §7.6 charset table keyed on PR_INTERNET_CPID, run it through the **same HTML strip §7.3 specifies**, and set `BodyIsFromHtmlFallback = true`. Take it from the **root storage's own child map** and nowhere else: in `artifacts/sample.msg` the only `__substg1.0_10130102` in the whole file is an 8-byte child of `__nameid_version1.0` (measured — the real PR_HTML is absent because there is a real PR_BODY), and a flat directory scan reports "PR_HTML present, 8 bytes" and renders garbage. Without this fallback an HTML-only `.msg` — Outlook's normal shape for marketing and portal mail — reaches the model as headers only, which defeats D3 with no user-visible signal.

**`__properties_version1.0` at root** carries the sent date and the codepage. Header size depends on the container — **32 bytes for a top-level message**, 8 for a recipient/attachment sub-storage, 24 for an embedded message — then 16-byte records: `[0..1]` propertyType LE u16, `[2..3]` propertyId LE u16, `[4..7]` flags, `[8..15]` value.

Read `00390040` (PR_CLIENT_SUBMIT_TIME) as a FILETIME. **`DateTimeOffset.FromFileTime` returns LOCAL time and is the wrong call** — measured on `W. Europe Standard Time` with the fixture's `0x01DD393E56EB4E00`:

```
DateTimeOffset.FromFileTime(ft)                                => 2026-08-31T13:46:20+02:00   WRONG
new DateTimeOffset(DateTime.FromFileTimeUtc(ft), TimeSpan.Zero) => 2026-08-31T11:46:20+00:00   correct
```

Both describe the same instant, so a test that only asserts `DateTimeOffset` equality **passes on the broken one** while the rendered header line reads `2026/08/31 13:46 +02:00` and changes with the machine's time zone. Assert `.Offset == TimeSpan.Zero` (or the rendered string), not just the instant. `2026-08-31T11:46:20Z` matches the transport header's `Date:` byte-for-byte. Prefer the property: transport headers are absent on drafts and locally composed items. Ignore `30070040`/`30080040` — those are when the `.msg` was *saved* (measured `2026-08-31T20:15:25Z`, nearly nine hours later).

Implement this as a ~25-line record loop, not a general MAPI property system.

**Recipients:** sub-storages named `__recip_version1.0_#%08X`. Per recipient read `3001001F` PR_DISPLAY_NAME, `3002001F` PR_ADDRTYPE, `3003001F` PR_EMAIL_ADDRESS, `39FE001F` PR_SMTP_ADDRESS, and `0C150003` PR_RECIPIENT_TYPE from that storage's own `__properties_version1.0` (1 = To, 2 = Cc, 3 = Bcc — Bcc is dropped).

Address selection: `39FE001F` → else `3003001F` **only if `3002001F == "SMTP"`** → else the display name alone. The fixture's `3003001F` is an X.500 DN (`/o=ExchangeLabs/ou=…`); it must never reach the model.

**Three sources disagree on "To" in the fixture** — `0E04001F` says `Marco Altmann`, the recipient storage says `marco.altmann@neo42.de`, the transport header says `manserviceportal@neo42.net` (an alias). **Rule: recipient sub-storages win; `0E04001F`/`0E03001F` are used only when no `__recip_` storage exists.** Expected output is `To = ["Marco Altmann <marco.altmann@neo42.de>"]`. Documented here so nobody later "fixes" the alias mismatch.

**Attachments:** `__attach_version1.0_#%08X` storages; name from `3707001F` PR_ATTACH_LONG_FILENAME, falling back to `3704001F` PR_ATTACH_FILENAME. **Names only — never extract bytes.** The fixture has none.

**String post-processing, all three mandatory:**

1. Use the **directory entry's `streamSize`**, never the `len` field in `__properties_version1.0`. Measured: props `len` = `streamSize + 2` for every `001F`. Using `len` over-reads two bytes.
2. **Trim a trailing `U+0000`.** The fixture is inconsistent — `0037001F` (58 chars) has none, `0E04001F` (14 chars) has one.
3. Decode `001F` as UTF-16LE; `001E` through the §7.6 charset table keyed on `PR_INTERNET_CPID`.

### 7.3 `EmlReader` — required capabilities

Verified against `artifacts/sample.eml` (128,469 bytes, CRLF-only, all-ASCII, header/body split at byte 6,446).

**Line endings.** Accept `\r\n`, `\n`, and bare `\r`. Find the header/body separator as the first of `\r\n\r\n` or `\n\n`. The fixture is CRLF-only; a Unix-generated `.eml` is not.

**Unfolding (RFC 5322 §2.2.3).** A line continues the previous field iff it starts with SP or HTAB. **Drop the CRLF, keep the leading whitespace** — that retained whitespace is what makes the next rule detectable.

```
foreach raw line in headerBlock split on the line terminator:
    if line starts with ' ' or '\t' and fields is non-empty:
        fields[^1] += line
    else:
        fields.Add(line)
```

Field name = text before the first `:`; value = the remainder, trimmed **after** unfolding. Duplicate `Subject:`/`From:` → take the first. A header line with no `:` → skip it.

**`Date:` carries RFC 5322 CFWS comments, and `TryParse` chokes on them.** `artifacts/sample.eml`'s own field is `Date: Mon, 31 Aug 2026 20:12:28 +0000 (UTC)`. Measured: `DateTimeOffset.TryParse` on that string returns **false**; strip the `(UTC)` and it returns true → `2026-08-31T20:12:28+00:00`. A `(UTC)` / `(CEST)` suffix is routine on Gmail- and LinkedIn-relayed mail, and a false here silently drops the whole `Date:` line from the §6.4 render. Before parsing, remove parenthesised comments — `Regex.Replace(value, @"\s*\([^)]*\)", "").Trim()` — and pass `CultureInfo.InvariantCulture` explicitly (the app sets `CultureInfo.DefaultThreadCurrentCulture` in `LocalizationService.SetLanguage`, so nothing should depend on the ambient one). Apply the same two rules to the MSG transport-header fallback (§7.2).

Measured and **not** a hazard, recorded so nobody re-adds a fix for it: `DateTimeOffset.TryParse("Mon, 31 Aug 2026 20:12:28 +0000", …)` returns true under `de-DE` and `fr-FR` as well as `en-US` (checked for Mar/May/Oct/Dec too) — .NET has an RFC 1123 fallback that does not consult the culture's month names. `InvariantCulture` above is determinism, not a bug fix. The hazard would only appear if someone reached for `ParseExact` with a custom format.

**RFC 2047 encoded-words.** Grammar `=?charset?(B|Q)?text?=`; regex `=\?([^?]+)\?([BbQq])\?([^?]*)\?=`. Q-decoding is **not** quoted-printable: `_` → space, `=XX` hex, **no soft line breaks**. B-decoding is base64.

**The adjacent-encoded-word rule is the actual bug** (RFC 2047 §6.2): when two encoded-words are separated **only by linear whitespace**, that whitespace **must be dropped**. Algorithm order: unfold → scan encoded-words left to right → if the gap since the previous word's end is non-empty, all SP/HTAB, and abuts the previous word, emit nothing for it; otherwise emit the gap verbatim → decode and append.

The fixture's `Subject:` folds across three lines and is the proof case:

```
correct: "... Uhr und ich hänge gerade noch über Benchmarks. ..."
naive  : "... Uhr und  ich hänge gerade noch über Be nchmarks. ..."
```

Apply the decoder to `From`/`To`/`Cc` and to attachment filenames too. A malformed encoded-word is emitted verbatim — never throw.

**Subject sanitization** is mandatory: the fixture's decoded subject is 117 chars containing three embedded LFs (from `=0A`) and U+1F4A1 (a surrogate pair). Collapse every whitespace run to one space and trim.

**Content-Type parameter parsing.** Two quirks in this one file break the obvious `split("; ")`: `text/plain;charset=UTF-8` has **no space after the semicolon**, and the root `Content-Type` folds with a **TAB** before `boundary=`. Use a case-insensitive `name\s*=\s*("([^"]*)"|[^;\s]+)` scan after any `;`.

**Multipart walk.** Boundary delimiter is `CRLF + "--" + boundary` at a line start (the first may sit at offset 0 with no leading CRLF); the closing delimiter adds a trailing `--`. **Regex-escape the boundary** — the fixture's contains `=` and `_`. Part content ends at the CRLF *preceding* the next delimiter. Recurse into nested multiparts; **cap depth at 10**. A multipart whose boundary never occurs → treat the whole body as text. An unterminated final part → EOF is the closing delimiter.

**Transfer encodings.** `quoted-printable`: `=` + CRLF (or bare LF) is a soft break emitting nothing; `=` + 2 hex digits is that byte (case-insensitive); `=` + anything else emits a literal `=` (lenient, never throw); everything else passes through. **Decode to bytes first, then apply the charset** — the fixture splits `ä` across `=C3` and `=A4`. `base64`: strip all non-base64 characters, tolerate missing padding. `7bit`/`8bit`/`binary`: identity.

**Part selection.** For `multipart/alternative`, take the **last** `text/plain` child. If the tree has none, take the last `text/html`, strip it, and set `BodyIsFromHtmlFallback = true`. For `multipart/mixed` / `multipart/related`, recurse in order and take the first usable body part; any part with a `filename` parameter or `Content-Disposition: attachment` is an attachment, not a body.

**The HTML strip must remove more than tags.** Measured on the fixture: text/plain is 7,994 chars of real content; a tags-only strip of the HTML part yields 2,462 chars, hundreds of which are U+034F COMBINING GRAPHEME JOINER preheader padding. Strip `<script>`/`<style>` **bodies**, then tags, then decode basic entities, then remove U+034F, U+200B-200D, U+FEFF, U+00AD.

**Attachment detection:** `Content-Disposition` starting with `attachment`, or a `filename=` on `Content-Disposition`, or a `name=` on `Content-Type`. RFC 2231 continued parameters (`filename*0*=`) degrade to the plain `filename` if present, else skip.

### 7.4 Wiring into `DroppedFileReader`

`src/Pia.Wpf/Helpers/DroppedFileReader.cs`:

- Add `Email` to the `FileKind` enum (`:10-19`).
- Add `[".msg"] = FileKind.Email, [".eml"] = FileKind.Email` to the `KindByExtension` seed dictionary (`:42-53`).
- Add `public static Task<ReadResult> ReadEmailAsync(string path, CancellationToken ct)` — `Task.Run` the parse (both readers are synchronous), dispatch on extension, render §6.4, and return `ReadResult.TooLarge` when the **rendered text** exceeds `MaxTextBytes`. Do not gate on `FileInfo.Length` at `MaxTextBytes`: a `.msg` with a 30 MB attachment can carry a 500-byte body. Gate the file length only at the far looser `MaxTextBytes * 8`, which stops a pathological file being read whole into memory before the render can measure it.
- Any parse exception → `ReadResult.Fail(ex.GetType().Name)`. See §12: `ex.Message` from IO routinely embeds the full user path.

`src/Pia.Wpf/Helpers/DroppedFileImporter.cs:34-55` gains a case, per D14 (this file is **LF** in the working tree — the odd one out):

```csharp
case FileKind.Email:
    result = await DroppedFileReader.ReadEmailAsync(path, ct);
    break;
```

### 7.5 Why the header whitelist, in numbers

`sample.eml`'s header block is 6,446 bytes / 84 raw lines / 28 unfolded fields. Of those, 74 lines and 5,904 bytes are `Received` / `DKIM-Signature` ×2 / `ARC-*` ×3 / `Authentication-Results` / `Received-SPF` / `X-*` ×6 / `Delivered-To` / `Return-Path` / `List-*` ×2 / `Feedback-ID`. The whitelisted fields present in this file (`Subject`, `From`, `To`, `Date`, plus the structural `Content-Type`) are 542 bytes. Dumping raw headers would add 5,904 bytes of machine cryptography to a 7,994-char body — 74% pollution — and the single `X-Forwarded-Encrypted` line alone is 143 bytes of base64. On the MSG side the equivalent surface is worse: `007D001F` is 14,674 bytes, roughly **28× the size of the 517-char body it accompanies**.

Whitelist, case-insensitive and **exactly six**: `Subject`, `From`, `To`, `Cc`, `Date` — plus the structural `Content-*`, which is consumed and never surfaced. Everything else is dropped.

`Reply-To` is deliberately **not** whitelisted, and `EmailMessage` has no `ReplyTo` member (§4.2). It has no MSG-side equivalent without another MAPI tag pair (`0050001F`/`0051001F`), which would make the field EML-only, and it is the same address as `From` on the overwhelming majority of mail. Parsing a header the record cannot hold and the render cannot emit is the asymmetry this line exists to prevent.

### 7.6 Charset table — mandatory, because `Encoding.GetEncoding` throws

`System.Text.Encoding.CodePages` is **not** referenced by `src/Pia.Wpf/Pia.Wpf.csproj`, and there is no `GetEncoding` call anywhere in `src/` or `tests/`. `Encoding.GetEncoding(1252)` throws `ArgumentException` on this runtime. **Never pass a file-supplied charset name to `Encoding.GetEncoding`.** Switch on the lowercased, trimmed name:

| charset | encoding |
|---|---|
| `utf-8`, `utf8` | `Encoding.UTF8` |
| `us-ascii`, `ascii` | `Encoding.ASCII` |
| `iso-8859-1`, `latin1`, `latin-1`, `windows-1252`, `cp1252` | `Encoding.Latin1` |
| `utf-16`, `utf-16le`, `unicode` | `Encoding.Unicode` |
| anything else, or absent | `new UTF8Encoding(false, throwOnInvalidBytes: false)` |

Same table serves MSG `001E` keyed on `PR_INTERNET_CPID`: `65001` → UTF-8, `1252` → Latin1, `20127` → ASCII, else UTF-8. **Never throw on an unknown charset** — mojibake beats an unreadable body.

---

## 8. The message model and the send path

### 8.1 `AssistantMessage` — the field and the builder

`src/Pia.Wpf/Models/AssistantMessage.cs`. Add next to `_attachment` (`:86-87`):

```csharp
    [ObservableProperty]
    private string? _attachedFileContext;
```

No `Has…` companion and no `partial void On…Changed` hook: nothing displays it. That matches the seven observable properties in this file that have no hook (`_statusText`, `_meta`, `_isProtectedRoute`, …).

**Replace both `ToChatMessage` bodies (`:278-289` and `:297-308`) with delegation to one private builder.** This is a deliberate deviation from D5's literal instruction, and the reason matters:

Both current bodies begin `if (Attachment is null) return new ChatMessage(Role, Content);`. An implementer told "append the new field inside both overloads" naturally appends to the `contents` list — the branch that **only runs when an image is present**. Every text-only file drop would then silently lose its payload, on both paths. One builder makes the divergence structurally impossible and also resolves the existing `HasContent` vs `!string.IsNullOrEmpty(overrideText)` textual drift.

```csharp
public ChatMessage ToChatMessage() => BuildChatMessage(Content);

/// <summary>
/// Builds the AI-visible message with <paramref name="overrideText"/> instead of <see cref="Content"/>
/// (used to inject @Files context / regeneration instructions without changing the displayed bubble).
/// </summary>
public ChatMessage ToChatMessage(string overrideText) => BuildChatMessage(overrideText);

private ChatMessage BuildChatMessage(string text)
{
    var visible = string.IsNullOrEmpty(AttachedFileContext)
        ? text
        : string.IsNullOrEmpty(text) ? AttachedFileContext : $"{text}\n\n{AttachedFileContext}";

    if (Attachment is null) return new ChatMessage(Role, visible);

    var contents = new List<AIContent>();
    if (!string.IsNullOrEmpty(visible)) contents.Add(new TextContent(visible));
    contents.Add(new DataContent(Attachment.JpegBytes, Attachment.MimeType));
    return new ChatMessage(Role, contents);
}
```

**Correct the premise while you are here.** D5 says "the current turn's user message goes through `ToChatMessage(overrideText)`; prior turns go through `ToChatMessage()`." The second half is right; the first is only conditionally right. `ChatSession.cs:379` reads

```csharp
if (msg == userMessage && (atCommands.Count > 0 || hasInjection))
```

so an ordinary typed send with no `@`-command, no `@Files` injection and no regeneration instruction takes the **parameterless** overload *even on the current turn*. The step builder (`:973`) and voice mode (`AssistantViewModel.cs:2045`) also use the parameterless one. Anyone tempted to collapse to a single overload must not assume the override is the current-turn path.

### 8.2 Not persisted — confirmed, and no DTO work

`AssistantMessageMapper.ToDto` (`src/Pia.Wpf/ViewModels/AssistantMessageMapper.cs:14-28`) projects exactly ten fields and does not touch `Attachment`. `SyncAssistantChat.cs:64-68` forbids non-text payloads outright. `FromDto` (`:30-42`) never assigns `Attachment`. **Do not add `AttachedFileContext` to the mapper or the DTO** — D4 says in-memory only, matching the image attachment's existing behaviour.

Observable consequence, same as the image today: a chat reopened after a restart has no attachment context, and a follow-up question about the mail will be answered from the earlier assistant reply alone. That is accepted, not a bug to fix here.

### 8.3 `IChatSessionManager` / `ChatSessionManager`

`src/Pia.Wpf/ViewModels/Models/IChatSessionManager.cs:54-56` — add an **optional** parameter at the end, matching the existing `regenerationInstruction = null, planned = false` pattern, so no call site breaks:

```csharp
    Task<bool> StartTurnAsync(
        ChatSession session, string userText, ImageAttachment? attachment, string? regenerationInstruction = null,
        bool planned = false, string? attachedFileContext = null);
```

`src/Pia.Wpf/ViewModels/Models/ChatSessionManager.cs`:

- `:700-702` — mirror the signature.
- `:697-698` `StartPlannedTurnAsync` — pass `attachedFileContext: null` explicitly.
- `:730-734` — set the field on the minted user message:
  ```csharp
  var userMessage = new AssistantMessage(ChatRole.User, userText)
  {
      Attachment = attachment,
      AttachedFileContext = attachedFileContext,
  };
  ```
- `:720-721` and `:1024-1032` — `TryAnswerParkedRunAsync` gains the parameter and the refusal:
  ```csharp
  if (_resumeService is null || session.ActiveRunId is not { } runId
      || regenerationInstruction is not null || attachment is not null
      || attachedFileContext is not null
      || string.IsNullOrWhiteSpace(userText))
  {
      return false;
  }
  ```
  The reason is already spelled out in that method's doc comment at `:1019-1022`: the resume channel is `_resumeService.ResumeAsync(runId, userText)` — a single string with no seam for anything else — so an attachment "would be silently dropped." A file payload has exactly the same hazard. Extend the doc comment's existing sentence rather than adding a new comment.

**Fix the existing NSubstitute stubs in this same step, before anything drives a non-null value.** An optional sixth parameter breaks no *call* site, but it does break every *matcher*: `tests/Pia.Wpf.Tests/ViewModels/AssistantViewModelLeverTests.cs:517-518` and `:530-531`, and `AssistantViewModelRegenerateTests.cs:38-40`, all configure five `Arg.Any<>()`s, which NSubstitute reads as "and the 6th argument equals `null`". §15.9's `Send_ClearsPendingFiles` / `RefusedSend_RestoresPendingFiles` pass a **non-null** context, the `Returns` never matches, the substitute hands back `default(Task<bool>)` = `null`, and the `await` throws a `NullReferenceException` that looks like a ViewModel bug. Add a sixth `Arg.Any<string?>()` to all three, so a copy-paste starts from the right shape.

### 8.4 `AssistantViewModel`

`src/Pia.Wpf/ViewModels/AssistantViewModel.cs`.

**Collection and command, next to `_pendingAttachment` (`:93-94`) and the command block (`:236-239`).** A get-only `ObservableCollection`, following the `Suggestions` precedent at `:171` — **not** `[ObservableProperty]`, which would notify only when the whole instance is swapped.

```csharp
    public ObservableCollection<PendingFileAttachment> PendingFiles { get; } = new();

    public bool HasPendingFiles => PendingFiles.Count > 0;
```

```csharp
    public IRelayCommand<PendingFileAttachment> RemovePendingFileCommand { get; }
```

**Construction, next to `:375-378`:**

```csharp
        RemovePendingFileCommand = new RelayCommand<PendingFileAttachment>(file =>
        {
            if (file is not null) PendingFiles.Remove(file);
        });
```

**The notification wiring — the part that is easy to get wrong.** A collection mutation raises `CollectionChanged` and `Count`/`Item[]` `PropertyChanged` **on the collection instance**, never on the ViewModel, so neither name filter in `OnPropertyChanged` (`:842`; the command arm at `:844-850`, the hint arm at `:851-854`) ever fires — Send stays disabled after attaching to an empty composer, *and* the hint in §8.4.1 never appears. Subscribe next to `:385` (`PropertyChanged += OnPropertyChanged;`) and tear down next to `:2277` / `:2298`:

```csharp
        PendingFiles.CollectionChanged += OnPendingFilesChanged;
```

```csharp
    private void OnPendingFilesChanged(
        object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasPendingFiles));
        SendMessageCommand.NotifyCanExecuteChanged();
        RunInBackgroundCommand.NotifyCanExecuteChanged();
        RefreshPendingFilesHint();
    }
```

**Four calls, not three.** `HasPendingFiles` is computed and drives the strip's visibility; the two commands are the CanExecute pair; and `RefreshPendingFilesHint()` (§8.4.1) is the one that is easy to leave out. Routing the hint through `RefreshGoalTooShortHint()` instead does **nothing**: that method is reached only from the `:851-854` name filter, which a collection change cannot trip, so the button would go dead the instant a chip is attached and the explanation would appear on the next keystroke — the exact state §8.4.1 exists to prevent.

Match `OnMessagesCollectionChanged` (`:553`, torn down at `:2298`) for the signature: this file has no `using System.Collections.Specialized;`, so the event-args type is written fully qualified.

**`CanExecuteSendMessage` (`:993-996`):**

```csharp
    private bool CanExecuteSendMessage() =>
        !IsStreaming && !ForeignRunActive && !PlanApprovalParkActive
        && (!string.IsNullOrWhiteSpace(InputText) || PendingAttachment is not null || PendingFiles.Count > 0);
```

**`HasCandidateGoalText` (`:999`) is unchanged** — files must not make a blank composer look like a goal.

### 8.4.1 Run-in-background must be blocked while files are attached

This is a silent data-loss path, not a cosmetic gap. `ExecuteRunInBackground` (`:1069-1103`) is a **fourth** consumer of composer state: it clears `InputText` at `:1077`, restores it at `:1097`, and never touches attachments. With the widened `CanExecuteSendMessage`, a composer holding *text plus a mail chip* would enable the button, launch a headless run from `userText` alone, and leave the chips sitting there — the mail silently gone, and re-attached on the next Send.

The payload cannot reach a detached run at all: `HeadlessTurnExecutor` rebuilds its messages from persisted DTOs (`src/Pia.Wpf/Services/HeadlessTurnExecutor.cs:296-305`, `:326`) and from `ctx.Goal` (`:313`), and D4 forbids persisting `AttachedFileContext`. So the honest answer is to refuse (D20). §8.4.2 shows the *other* detach path — an approved plan — which the same reasoning closes; §8.4.3 is the one hint that explains both.

`CanExecuteRunInBackground` (`:1034-1035`):

```csharp
    private bool CanExecuteRunInBackground() =>
        CanExecuteSendMessage() && HasCandidateGoalText() && PendingFiles.Count == 0
        && !GoalPreflight.IsRefused(InputText);
```

Extend the existing comment at `:1032-1033` with the reason in plain language — a detached run has no way to carry an attached file — rather than adding a second comment block.

The test in §15.9 must be the **text-plus-file** case (`TextPlusPendingFile_DoesNotEnableRunInBackground`). Asserting that files *alone* do not enable it stays true whether or not this fix lands, so it proves nothing.

### 8.4.2 A file-bearing send is forced to Chat shape (D17)

`ExecuteSendMessage` computes `planned = AgentModeEnabled && ActivePersona?.ToolScope != PersonaToolScope.None` at `:1049`. When a file is attached, that must become `false`:

```csharp
        // An attached file rides the live ChatSession only. A Planned run loses it twice — the planner is
        // built from the goal STRING, and Approve hands the run to the headless executor, which rebuilds
        // from persisted rows. Downgrade the TURN; the lever itself stays where the user left it.
        var planned = files.Length == 0
            && AgentModeEnabled && ActivePersona?.ToolScope != PersonaToolScope.None;
```

**`files.Length`, not `PendingFiles.Count`.** The capture block below clears `PendingFiles` at the top of the method, several lines *above* `:1049` — so a live-collection read here is always `0` and the guard silently does nothing while looking correct. This is the same shape of bug as D5's: it compiles, it reads right, and it only shows up in a run that has already lost the mail. `AgentModeSendWithAPendingFile_IsNotPlanned` (§15.9) is the test that catches it.

The earlier claim that this path needed no gate rested on `ChatSession.BuildStepChatMessagesAsync` (`:957-1015`), which does replay `Messages` through `ToChatMessage()` and therefore *does* carry `AttachedFileContext` into every **live** step. That much is true and measured. Three legs break it anyway:

1. **The planner never sees the file.** `ChatSessionManager.cs:904-905` creates the run with `Goal: userText`, and `AgentPlanner.cs:202` plans from `BuildPlanMessages(answeredGoal, …)` — a string. The mail is not in the plan prompt, so the plan is drawn up blind to the thing the user attached.
2. **Every park resumes headless.** `LiveTurnExecutor.SupportsPlanApproval` is `true` (`src/Pia.Wpf/ViewModels/Models/LiveTurnExecutor.cs:91`), so a foreground Planned run with ≥3 steps parks at `AgentRunOrchestrator.cs:257`. Approve/Continue goes through `IAgentRunResumeService` = `HeadlessRunLauncher.ResumeAsync` (`HeadlessRunLauncher.cs:642`), and `HeadlessTurnExecutor` reseeds `_messages` from the **persisted** rows (`:296-305`), which by D4 never carry `AttachedFileContext`. That orchestrator's own comment at `:253-256` says it plainly: "every resume dispatches headless." So 100% of an approved run executes without the mail, silently — the same loss D20 blocks for Run-in-background, reached through the ordinary Send button.
3. **An empty goal becomes reachable.** `AssistantAgentModeDefault` is a persisted lever, and the widened `CanExecuteSendMessage` enables Send on a chip alone. Drop a `.msg`, type nothing, press Send → `CreateAsync(… Goal: "" …)`. Today that accident needs an image *and* a PiaCloud provider (the gate at `AssistantViewModel.cs:1730`); files remove both conditions.

The one-line predicate closes all three. The rejected alternative — folding the block into `ctx.Goal` so the planner and any resume both see it — is the one D20 already refused: up to 40,000 chars pinned in the compactor's head on every step of a 20-step run.

### 8.4.3 The composer hint — visibility, precedence, and no debounce

D17 and D20 are two halves of one rule, so they share **one** loc key and one composer line: `Assistant_PendingFilesBlockRun_Hint`, in the slot the goal-too-short hint uses (`AssistantView.xaml:319-325`), beside the existing `AgentModeHintVisible` / `GoalTooShortHintVisible` pair (`:796-827`, `:1007-1029`).

```csharp
    [ObservableProperty]
    private bool _pendingFilesBlockRunHintVisible;

    private bool PendingFilesBlockRunHolds() =>
        PendingFiles.Count > 0 && !IsStreaming && AgentModeEnabled;

    private void RefreshPendingFilesHint()
    {
        PendingFilesBlockRunHintVisible = !GoalTooShortHintVisible && PendingFilesBlockRunHolds();
        // Not in a partial On…Changed hook: [ObservableProperty] short-circuits on equality, so a hint that
        // is ALREADY true would not re-clear the agent-mode hint the lever flip had just switched on.
        if (PendingFilesBlockRunHintVisible)
            AgentModeHintVisible = false;
    }
```

**Agent mode is the whole condition.** Both clauses of the sentence are Agent-mode claims: the Run-in-background button is `Visibility="{Binding AgentModeEnabled}"` (`AssistantView.xaml:660`), and `planned` in §8.4.2 already requires the lever. In Chat the button is not on screen and no turn was ever going to be planned, so a hint there would describe two restrictions that do not exist — and Chat with a chip and typed text is the *default* path for this feature. No `HasCandidateGoalText()` disjunct.

**Where it is recomputed.** Two places, and both are needed:

- `OnPendingFilesChanged` (§8.4) — the collection path, which nothing else covers.
- the hint arm of the name filter at `:851-854`, immediately after the existing `RefreshGoalTooShortHint();`. That arm already watches `IsStreaming` and `AgentModeEnabled`, which is the input set of `PendingFilesBlockRunHolds()` (it watches `InputText` too, for the goal hint that shares the arm). **Do not** widen the arm with `nameof(HasPendingFiles)` instead — the manual `OnPropertyChanged(nameof(HasPendingFiles))` raise makes that work, but it hides the real dependency behind a notification the collection handler happens to emit.

**No debounce.** `RefreshGoalTooShortHint` runs a 1-second `GoalTooShortHintDebounce` (`:1022-1029`) so a hint never pops mid-typing. Attaching a chip is a discrete click, not a keystroke; borrowing that path would put a second of dead-button silence in front of the explanation. `RefreshPendingFilesHint` assigns straight through.

**Precedence, stated because there is nothing to mirror.** The existing rule (`OnGoalTooShortHintVisibleChanged`, `:822-827`) resolves *two* claimants; with three there is no two-hint form to copy. Order, most concrete first: `GoalTooShortHintVisible` → `PendingFilesBlockRunHintVisible` → `AgentModeHintVisible`. The upward arm is the `AgentModeHintVisible = false` inside `RefreshPendingFilesHint` above; the downward one extends the existing hook:

```csharp
    partial void OnGoalTooShortHintVisibleChanged(bool value)
    {
        if (value)
        {
            AgentModeHintVisible = false;
            PendingFilesBlockRunHintVisible = false;
        }
        else
        {
            // Its true-arm is debounced, so this can land a second after the keystroke that caused it.
            RefreshPendingFilesHint();
        }
    }
```

`[ObservableProperty]` runs `On…Changed(value)` after the backing field is assigned, so the read of `GoalTooShortHintVisible` inside that `else` sees `false`.

**The ordering that makes this work, checked.** `ShowAgentModeHint()` is called from inside `OnAgentModeEnabledChanged` (`:772-791`), and the toolkit runs that hook **before** raising `PropertyChanged`. Flipping the lever to Agent with a chip already attached therefore goes: `AgentModeHintVisible = true` → `PropertyChanged(AgentModeEnabled)` → the `:851-854` arm → `RefreshPendingFilesHint()` → the agent hint is cleared again. Right order — but only because that clear is unconditional. Had it lived in a `partial void OnPendingFilesBlockRunHintVisibleChanged`, the hint would already have been `true`, `[ObservableProperty]` would have short-circuited on equality, no hook would have run, and both TextBlocks would render.

### 8.4.4 `ExecuteSendMessage` (`:1037-1062`) — capture, render, clear, restore

```csharp
        var userText = InputText.Trim();
        InputText = string.Empty;
        var attachment = PendingAttachment;
        PendingAttachment = null;
        // Captured before the Clear, and read again at the `planned` line below — see §8.4.2.
        var files = PendingFiles.ToArray();
        var attachedFileContext = files.Length > 0
            ? AssistantPromptComposer.BuildAttachedFileBlock(files)
            : null;
        PendingFiles.Clear();
```

and inside the `if (!accepted)` block at `:1057-1061`:

```csharp
            InputText = userText;
            PendingAttachment = attachment;
            foreach (var file in files) PendingFiles.Add(file);
```

The `StartTurnAsync` call at `:1053` becomes

```csharp
        var accepted = await _chatSessionManager.StartTurnAsync(
            session, userText, attachment, planned: planned, attachedFileContext: attachedFileContext);
```

### 8.4.5 The rest of the send path

**`RegenerateCore` (`:1351-1381`)** — capture at `:1368-1369` and thread it through, or every regeneration silently drops the file:

```csharp
        var prompt = prior.Content;
        var attachment = prior.Attachment;
        var attachedFileContext = prior.AttachedFileContext;
```

```csharp
        await _chatSessionManager.StartTurnAsync(
            session, prompt, attachment, RegenerateInstructions.For(style, previousAnswer),
            attachedFileContext: attachedFileContext);
```

The empty-message guard at `:1364` also widens:

```csharp
        if (string.IsNullOrWhiteSpace(prior.Content) && prior.Attachment is null
            && string.IsNullOrEmpty(prior.AttachedFileContext)) return;
```

**Clear on chat change.** `StartFreshChat` (`:1220-1238`) clears `InputText` at `:1234` but not `PendingAttachment` — which is why `:947` and `:963` exist as explicit extra clears. Rather than mirroring that accident, add **one** line to `StartFreshChat` next to `:1234`:

```csharp
        PendingFiles.Clear();
```

That covers `ExecuteClearConversation` (`:1199`), `NewChat` (`:1209`), `ExecuteNewChat` (`:1213`), `DeleteChatFromChipAsync` (`:1277`) and the two summarize handlers (`:947`, `:963`) in one place. **Leave `PendingAttachment`'s existing behaviour exactly as it is** — changing it is out of scope and would alter four more call sites.

`AttachToActiveSession` (`:463-496`, the resume/switch path) is deliberately **not** touched: it never clears composer state today, and matching that keeps the two attachment kinds consistent.

**`ExecuteHandleFilesDropped` (`:1692-1707`) — replaced.** The `paths.Count == 1 && Image` special case goes; routing is per file.

```csharp
    private async Task ExecuteHandleFilesDropped(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0) return;
        if (IsStreaming) return;

        var result = await DroppedFileAttachmentImporter.TryStageAsync(
            paths, PendingFiles, _logger, _snackbarService, _localizationService);

        foreach (var file in result.Staged)
            PendingFiles.Add(file);

        if (result.ImagePaths.Count > 0)
            await AttachFirstImageAsync(result.ImagePaths);
    }

    private async Task AttachFirstImageAsync(IReadOnlyList<string> imagePaths)
    {
        var attached = await ExecuteHandleImageAttached(imagePaths[0]);
        if (!attached || imagePaths.Count == 1) return;

        _snackbarService.Show(
            _localizationService["Msg_Warning"],
            _localizationService.Format("Msg_File_OneImageOnly", System.IO.Path.GetFileName(imagePaths[0])),
            Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
    }
```

**`ExecuteHandleImageAttached` (`:1709-1713`) gains the `IsStreaming` guard** its two siblings already have (`:1695`, `:1718`) — one rule for all three staging paths.

**The attach path reports whether it attached.** `ExecuteHandleImageAttached` and `PrepareImageAttachmentAsync` both return `Task<bool>`: `false` from each guard and from both refusal arms below, `true` only after `PendingAttachment` is set. `Msg_File_OneImageOnly` says an image was *kept*, and two of `PrepareImageAttachmentAsync`'s exits keep nothing — a non-PiaCloud provider and an image still too large after re-encoding — so ungated it announces a file the user never got. Method-group conversion keeps `new AsyncRelayCommand<string>(ExecuteHandleImageAttached)` compiling unchanged, and `ExecuteHandleImagePasted` simply discards the result.

`PrepareImageAttachmentAsync` (`:1727-1750`) keeps its PiaCloud gate (`:1730`) and its behaviour otherwise. Text and mail never enter this method (D8).

`InsertOrPromptInsertAnyway` (`:1752-1773`) survives — `OptimizeViewModel` is unaffected and the Assistant no longer calls it from the drop path. If nothing else calls it, delete it and let the build tell you.

### 8.5 View code-behind

`src/Pia.Wpf/Views/AssistantView.xaml.cs:232-247` — delete the `Count == 1 && Image` branch (`:238-243`), which duplicates the VM rule that just disappeared. Both edits land in the same step or the picker and the drop diverge.

```csharp
    private void AttachFileButton_Click(object sender, RoutedEventArgs e)
    {
        var files = DebugDroppedPaths() ?? FilePicker.PickFiles(FileDropBehavior.GetAcceptedExtensions(RootGrid));
        if (files.Count == 0) return;

        if (ViewModel?.HandleFilesDroppedCommand.CanExecute(files) == true)
            ViewModel.HandleFilesDroppedCommand.Execute(files);
    }
```

`DebugDroppedPaths()` is §13.

---

## 9. The view

`src/Pia.Wpf/Views/AssistantView.xaml` (CRLF, 2-space indent).

### 9.1 Accepted extensions — line 21

Append `,.eml,.msg` to the `AcceptedExtensions` string. This one edit reaches both the drop path (`FileDropBehavior.FilterAccepted`) and the Attach-file button, which reads the same string back off `RootGrid` (`AssistantView.xaml.cs:234`). Without it `FilterAccepted` rejects a mail before any handler runs and the overlay never even appears.

Per D14, also append `,.eml,.msg` to `src/Pia.Wpf/Views/OptimizeView.xaml:85` (the same list minus the image extensions), so Optimize's picker offers what its importer can now read.

### 9.2 The chip strip

Inserted **immediately above** the existing image-preview `Border` (before line 280), as a sibling inside the composer card's `StackPanel` (`:256`). The image `Border` at `:280-310` is not modified.

```xml
          <Border Margin="0,0,0,6" Padding="6"
                  CornerRadius="6"
                  BorderBrush="{DynamicResource ControlElevationBorderBrush}"
                  BorderThickness="1"
                  HorizontalAlignment="Left"
                  MaxWidth="560"
                  Visibility="{Binding HasPendingFiles, Converter={StaticResource BooleanToVisibilityConverter}}">
            <ItemsControl ItemsSource="{Binding PendingFiles}">
              <ItemsControl.ItemsPanel>
                <ItemsPanelTemplate>
                  <WrapPanel Orientation="Horizontal" />
                </ItemsPanelTemplate>
              </ItemsControl.ItemsPanel>
              <ItemsControl.ItemTemplate>
                <DataTemplate>
                  <Border MinHeight="34" Margin="0,2,6,2" Padding="8,2,2,2"
                          CornerRadius="{StaticResource BubbleRadius}"
                          Background="{DynamicResource SurfaceBrush}"
                          BorderBrush="{DynamicResource BorderBrush_}"
                          BorderThickness="1"
                          ToolTip="{Binding FullPath}">
                    <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                      <ui:SymbolIcon Symbol="{Binding Icon}" FontSize="16"
                                     Margin="0,0,6,0" VerticalAlignment="Center" />
                      <TextBlock Text="{Binding FileName}"
                                 MaxWidth="180"
                                 TextTrimming="CharacterEllipsis"
                                 VerticalAlignment="Center" />
                      <ui:Button Command="{Binding DataContext.RemovePendingFileCommand,
                                                   RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                 CommandParameter="{Binding}"
                                 Appearance="Transparent"
                                 Padding="4"
                                 Margin="4,0,0,0"
                                 VerticalAlignment="Center"
                                 AutomationProperties.Name="{loc:Str Assistant_RemovePendingFile_Tooltip}"
                                 AutomationProperties.AutomationId="{Binding FileName, StringFormat='Assistant_RemovePendingFile_{0}'}"
                                 ToolTip="{loc:Str Assistant_RemovePendingFile_Tooltip}">
                        <ui:SymbolIcon Symbol="Dismiss16" FontSize="12" />
                      </ui:Button>
                    </StackPanel>
                  </Border>
                </DataTemplate>
              </ItemsControl.ItemTemplate>
            </ItemsControl>
          </Border>
```

Notes that are load-bearing:

- The `ItemTemplate` must be a **local value** (inline, as above, or `ItemTemplate="{StaticResource X}"`). An implicit `DataType`-keyed template in `ItemsControl.Resources` is invisible to `ViewAutomationIdTests`' walker, and the remove button inside it would be uncounted and unenforced.
- The AutomationId **must** be the binding form. A literal there makes every row report the same id and fails `ViewAutomationIdTests.cs:145-154`.
- The remove `Command` reaches the VM via `RelativeSource AncestorType=ItemsControl`; the `ItemsControl` inherits the VM as its DataContext.
- `ToolTip="{Binding FullPath}"` puts the full path on the chip. The full path is **not** in the wrapper sent to the model (§6.1).
- Keep the chip **inline in this file**, not extracted to a `UserControl` — extracting one would change `ViewAutomationIdTests`' `expectedNestedViews` string, which is an *exact-equality* assertion.
- Container theme keys are app-level (`PiaTokens.Light/Dark.xaml`): `BubbleRadius`, `SurfaceBrush`, `BorderBrush_`. `BooleanToVisibilityConverter` is already used in this file at `:263`, `:318`, `:325`.

**Overflow.** `MaxPendingFiles = 5` caps the count, `MaxWidth="560"` plus the `WrapPanel` gives at most two wrapped rows, and the name is ellipsized at 180 px. No `PiaChipOverflowPanel` — a composer chip must stay individually visible and removable, which is exactly what that control's "+N popup" shape prevents.

### 9.3 Drag-over overlay and blur

**No behavioural change.** `FileDropBehavior.IsDragOver` (`FileDropBehavior.cs:84`) is set from `TryAcceptDrag`, which gates on `accepted.Count == 0` — count-agnostic, so ten files behave like one. The blur (`AssistantView.xaml:28-34`) and the overlay (`:760-782`) both already work.

**One cosmetic change:** the overlay's `TextBlock` at `:775` switches from `{loc:Str FileDrop_Overlay_Hint}` to `{loc:Str FileDrop_Overlay_Hint_Assistant}` — "Drop file to insert its contents" is now a lie in the Assistant, and true in Optimize (D15).

Showing "N files" in the overlay is **not** in scope: `IsDragOver` is a bool and the behaviour never exposes the dragged paths, so it would need a new attached DP.

---

## 10. Localization

Existing conventions: snackbar bodies live in `MessageStrings.resx`, tooltips and composer hints in `ViewStrings.resx` (`Assistant_RemoveAttachment_Tooltip` at `:108`, `Assistant_GoalTooShort_Hint` at `:115`). Every entry is **one line**, two-space indent, `xml:space="preserve"`. `MessageStrings.Designer.cs` is badly drifted (370 resx entries vs 142 generated properties) and must **not** be hand-edited — reach new keys only through `_localizationService["Key"]` / `.Format("Key", …)` in C# and `{loc:Str Key}` in XAML.

All three files are CRLF in the working tree.

### 10.1 `MessageStrings.resx` / `.de.resx` / `.fr.resx` — inside the existing `<!-- File drop -->` block

| Key | EN | DE | FR |
|---|---|---|---|
| `FileDrop_Overlay_Hint_Assistant` | `Drop files to attach them` | `Dateien hier ablegen, um sie anzuhängen` | `Déposez des fichiers pour les joindre` |
| `Msg_File_UnsupportedAttachment` | `"{0}" can't be attached — file type isn't supported.` | `„{0}“ kann nicht angehängt werden – Dateityp wird nicht unterstützt.` | `« {0} » ne peut pas être joint – type de fichier non pris en charge.` |
| `Msg_File_DuplicateAttachment` | `"{0}" is already attached.` | `„{0}“ ist bereits angehängt.` | `« {0} » est déjà joint.` |
| `Msg_File_Empty` | `"{0}" contains no readable text.` | `„{0}“ enthält keinen lesbaren Text.` | `« {0} » ne contient aucun texte lisible.` |
| `Msg_File_Truncated` | `"{0}" was shortened to fit the message.` | `„{0}“ wurde gekürzt, damit es in die Nachricht passt.` | `« {0} » a été raccourci pour tenir dans le message.` |
| `Msg_File_AttachLimit` | `At most {0} files can be attached to one message — "{1}" was skipped.` | `Es können höchstens {0} Dateien an eine Nachricht angehängt werden – „{1}“ wurde übersprungen.` | `Au maximum {0} fichiers peuvent être joints à un message – « {1} » a été ignoré.` |
| `Msg_File_TooLargeAttachment` | `"{0}" is too large to attach.` | `„{0}“ ist zu groß zum Anhängen.` | `« {0} » est trop volumineux pour être joint.` |
| `Msg_File_AttachBudget` | `The attached files already fill this message — "{0}" was skipped.` | `Die angehängten Dateien füllen diese Nachricht bereits – „{0}“ wurde übersprungen.` | `Les fichiers joints remplissent déjà ce message – « {0} » a été ignoré.` |
| `Msg_File_OneImageOnly` | `Only one image can be attached — kept "{0}".` | `Es kann nur ein Bild angehängt werden – „{0}“ wurde übernommen.` | `Une seule image peut être jointe – « {0} » a été conservée.` |

`Msg_File_Empty` covers both the 0-byte file and the mail with no readable body — one message, no extra key.

German quotes: the new rows close with `“`, not an ASCII `"`. `MessageStrings.de.resx` is the one file in the family that gets this wrong (33 `„` against 2 `“`, measured); `ViewStrings.de.resx` (35/33) and `CommonStrings.de.resx` (5/5) are correct. New rows follow the correct form; the 31 existing ones are not this plan's business.

`FileDrop_Overlay_Hint`, `Msg_File_Unsupported`, `Msg_File_TooLarge`, `Msg_File_ReadFailed`, `Msg_File_ImageTooLarge`, `Msg_File_ImageProviderUnsupported` are **unchanged** and still used — the first three by Optimize, and `Msg_File_Unsupported` / `Msg_File_TooLarge` now *only* there, since both are worded around inserting. `Msg_File_ReadFailed` is the two-placeholder one — see §4.3 step 7.

### 10.2 `ViewStrings.resx` / `.de.resx` / `.fr.resx` — next to `Assistant_RemoveAttachment_Tooltip` (`:108`)

| Key | EN | DE | FR |
|---|---|---|---|
| `Assistant_RemovePendingFile_Tooltip` | `Remove file` | `Datei entfernen` | `Retirer le fichier` |
| `Assistant_PendingFilesBlockRun_Hint` | `An attached file can only ride a chat message. Run in background is off, and this turn won't be planned as an agent run.` | `Eine angehängte Datei kann nur mit einer Chatnachricht mitgehen. Die Hintergrundausführung ist deaktiviert, und diese Runde wird nicht als Agentenlauf geplant.` | `Un fichier joint ne peut accompagner qu'un message de chat. L'exécution en arrière-plan est désactivée, et ce tour ne sera pas planifié comme exécution d'agent.` |

**Agent mode only, and no imperative.** The hint shows exactly when `PendingFilesBlockRunHolds()` is true (§8.4.3), i.e. the Agent lever is on with a chip attached — the one state where both clauses are true and both are caused by the file. It stays declarative because the same sentence covers a composer with typed text and one with only a chip; "remove the files to plan it as an agent run" would over-promise in the second, where there is no goal to plan yet.

Terminology, checked against the shipped strings: German is **`Hintergrundausführung`** (`ViewStrings.de.resx:116`, `:118`; the button at `:112` is `Im Hintergrund ausführen`) — *Hintergrundlauf* appears nowhere in the product and reads as a calque. The register is informal *du*, matching `:115` / `:117`. French keeps *vous* (`ViewStrings.fr.resx:115`, `:117`) and takes `de` after a negation, never `un`.

### 10.3 Test rows this forces

- `tests/Pia.Wpf.Tests/Architecture/LocalizationTests.cs` — the placeholder count per key. `ADiagnosticsMessageKeyCarriesTheSamePlaceholdersInEveryLocale` is scoped to the diagnostics keys by name, so these get their own `AFileDropMessageKeyCarriesTheSamePlaceholdersInEveryLocale`, with a row per file-drop key: `Msg_File_AttachLimit` and `Msg_File_ReadFailed` take **two**, the rest one. Nothing else pins an argument count, because the substitute in the importer tests returns the key whatever it is handed.
- `AllTranslations_MustBeComplete` (`:124-165`) asserts the key sets are equal in **both** directions — an orphan translation fails too. All **eleven** new keys — nine in the `MessageStrings` family (§10.1), two in `ViewStrings` (§10.2) — land in all three files of their family in the same commit.
- `AllXamlLocalizationKeys_MustExistInResources` (`:58-81`) covers `FileDrop_Overlay_Hint_Assistant`, `Assistant_RemovePendingFile_Tooltip` and `Assistant_PendingFilesBlockRun_Hint` automatically, because those three are reached from XAML as `{loc:Str …}`.
- **`AllCodeLocalizationKeys_MustExistInResources` (`:83-122`) does *not* cover the new importer, and must be widened.** All five of its regexes require the underscore-prefixed field name (`_localizationService\["…"\]`, `_localizationService\.Format\("…"`, `_localization…`, `LocalizationSource\.Instance…`) at `:89-96`. `DroppedFileAttachmentImporter.TryStageAsync` takes `ILocalizationService localizationService` as a **parameter** (§4.3), so its calls read `localizationService.Format("Msg_File_Empty", …)` and match none of them — exactly as `DroppedFileImporter.cs:52` already goes unchecked today. Five of the seven new `MessageStrings` keys (`Msg_File_UnsupportedAttachment`, `_DuplicateAttachment`, `_Empty`, `_Truncated`, `_AttachLimit`) live only in that file, so a typo would ship green. Add two patterns in the same step as the keys, which retroactively covers the existing importer too:
  ```csharp
  new Regex(@"\blocalizationService\[""(\w+)""\]", RegexOptions.Compiled),
  new Regex(@"\blocalizationService\.Format\(""(\w+)""", RegexOptions.Compiled),
  ```
  Run the widened test **once against a clean tree** before adding the keys: the new patterns newly scan `DroppedFileImporter.cs` and anything else taking the service as a parameter, so a pre-existing missing key would fail B5 for a reason that has nothing to do with B5.

  The `\b` keeps them from double-reporting the `_localizationService` hits the first two patterns already find — the underscore is a word character, so `\b` cannot match between `_` and `l`.

---

## 11. AutomationId test row

`tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs:49-51`. Current row:

```csharp
    [InlineData(typeof(Pia.Views.AssistantView), 19, 2,
        "AutocompletePopup,DirectTranscriptionOverlay,MeetingAttendeeOverlay,PersonaGlyph,PiaAssistantMessage," +
        "PiaChatQuickSwitcher,PiaChatTitleChip,PiaPersonaAvatar,RunProgressPanel,TodoPanelControl,VoiceModeOverlay")]
```

The chip strip adds exactly one collectible control (the remove `ui:Button` inside the new `ItemTemplate`), and it is the per-item binding form, so it counts toward **both** floors:

```csharp
    [InlineData(typeof(Pia.Views.AssistantView), 20, 3,
        "AutocompletePopup,DirectTranscriptionOverlay,MeetingAttendeeOverlay,PersonaGlyph,PiaAssistantMessage," +
        "PiaChatQuickSwitcher,PiaChatTitleChip,PiaPersonaAvatar,RunProgressPanel,TodoPanelControl,VoiceModeOverlay")]
```

The nested-views string is **unchanged** — no new `UserControl`.

**Both numbers are `>=` floors, so the test still passes if you do not bump them.** Green is not evidence the row was updated; the project convention is to bump by the delta (commit `ba8dbb81` moved this same row `18, 2` → `19, 2` for one net button).

**Do not derive the numbers by counting tags in the XAML** — a `StaticResource`-keyed template assigned at two sites is `LoadContent()`'d twice and double-counts. To get the true value, temporarily set the floor absurdly high and read it off the failure message (`"only {N} interactive controls were inspected in {view}"` at `:131`, `"only {N} … per-item binding form"` at `:140`), then set the row to what it printed.

---

## 12. Logging

The chip's file name, a mail subject, a mail body, addresses and attachment names are all sensitive under this repo's privacy policy — a support log must not carry them. Use `Pia.Logging`'s `[Conditional("DEBUG")]` helpers, which erase the call **and its argument evaluation** from release IL.

```csharp
// Don't:
logger.LogInformation("Attached {FileName} ({Chars} chars)", fileName, text.Length);

// Do:
logger.LogInformation("Attached a {Kind} file ({Chars} chars, truncated={Truncated})", kind, text.Length, truncated);
logger.SensitiveDebug("Attached {FileName}", fileName);
```

Plain logs may carry: counts, character/byte lengths, `FileKind` / `PendingFileKind`, attachment **count**, "has an HTML part", exception **type**, GUIDs.

**Existing leak not to extend:** `DroppedFileImporter.cs:71` logs `result.Error`, which is `ex.Message` from a `FileStream` open (`DroppedFileReader.cs:84-87`) and routinely embeds the full user path. The new mail path must not repeat it — `ReadEmailAsync` returns `ReadResult.Fail(ex.GetType().Name)` and the importer logs the message body only via `SensitiveDebug`.

---

## 13. The DEBUG bypass

UIA cannot synthesize a shell file drop. `FileDropBehavior.OnDrop` (`FileDropBehavior.cs:105-125`) requires an OLE data object carrying `CF_HDROP`; a shell drop is a Win32 `DoDragDrop` transfer negotiated between two processes, and WinWright's `ww_drag_drop` is an element-to-element synthetic mouse drag that cannot manufacture a `DataFormats.FileDrop` payload — `OnDragEnter` would reject it before the overlay appeared. The playbook (`docs/ui_automation/ui-automation-playbook.md`) contains the substring "drag" zero times.

The bypass therefore targets the **Attach-file click handler**, which already routes to the same `HandleFilesDroppedCommand`.

**Declaration** — `src/Pia.Wpf/Bootstrapper.cs`, next to the chat import/export block at `:42-48` (a plain `public const`, **not** inside `#if DEBUG`, matching every sibling):

```csharp
    // Semicolon-separated paths the Attach-file button uses instead of the picker, so a UI script can
    // exercise the file-attachment flow — UIA cannot synthesize a shell drag-drop. DEBUG builds only.
    public const string DebugDropFilesEnvVar = "PIA_DEBUG_DROP_FILES";
```

**Read** — `src/Pia.Wpf/Views/AssistantView.xaml.cs`, mirroring `AssistantHistoryViewModel.DebugPresetPath` (`:563-578`): the `#if DEBUG` lives inside the helper, callers stay directive-free, and the log line names the variable and never its value.

```csharp
    /// <summary>Dev-only: a preset path list that stands in for the file picker, so a UI script can drive
    /// the real Attach-file button without automating a native dialog. Always null in release.</summary>
    private IReadOnlyList<string>? DebugDroppedPaths()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable(Bootstrapper.DebugDropFilesEnvVar) is not { Length: > 0 } value)
            return null;

        var paths = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return paths.Length == 0 ? null : paths;
#else
        return null;
#endif
    }
```

`AssistantHistoryViewModel.DebugPresetPath` logs a warning naming the variable; this view has **no logger field** and none should be added for one line, so the warning is dropped. `System.IO` is not imported in `AssistantViewModel.cs` either — use the fully-qualified `System.IO.Path.GetFileName`, matching that file's own usage at `:1417`.

The semicolon separator matches `DebugMeetingAttendeeRosterEnvVar`'s precedent (`Bootstrapper.cs:36`).

**Script usage.** WinWright's `ww_launch` takes an `env` map, so no machine-environment change is needed:

```
env: { PIA_DEBUG_DROP_FILES: "C:\\path\\to\\sample.msg;C:\\path\\to\\notes.txt" }
click automationId=Assistant_AttachFile
assert  automationId=Assistant_RemovePendingFile_sample.msg exists
click   automationId=Assistant_RemovePendingFile_notes.txt
```

Add the row to the playbook's DEBUG-bypass table (`docs/ui_automation/ui-automation-playbook.md:126-140`) in the same commit, and add the two new automation ids to its composer-toolbar id list (`:40`).

**Optional hardening**, if the reviewer wants it: `tests/Pia.Wpf.Tests/Architecture/TourDumpDebugOnlyRuleTests.cs` is the in-repo template for a source-text rule that proves a helper's sensitive call is inside `#if DEBUG` / `#endif` with no directive splitting the region.

---

## 14. Error and edge cases — required behaviour

| Case | Behaviour |
|---|---|
| **Mail dragged straight out of Outlook** | Nothing happens — no overlay, no snackbar. Outlook's message list offers `FileGroupDescriptorW` + `FileContents`, not `CF_HDROP`, and `FileDropBehavior` gates on `DataFormats.FileDrop` in both `TryAcceptDrag` (`:127-130`) and `OnDrop` (`:105-125`). The user must save the mail to disk first, or use Attach-file. Stated in §1; gate **G0** decides whether that stands. |
| **Unreadable file** (locked, denied, corrupt `.msg`) | `Format("Msg_File_ReadFailed", fileName, result.Error ?? string.Empty)` — **two** placeholders (§4.3 step 7) — as a danger snackbar; no chip. Log the exception **type** plainly, the message via `SensitiveDebug`. |
| **Oversize file** (`> MaxTextBytes` before extraction) | `Msg_File_TooLargeAttachment` caution snackbar; no chip. Optimize keeps the insert-worded `Msg_File_TooLarge`. |
| **Text over `MaxFileChars`** | Chip appears with `Truncated = true`; `Msg_File_Truncated` caution snackbar; the wrapper carries `truncated="true"` + the `note` attribute. |
| **Message total over `MaxTotalChars`** | The file that crosses the line is truncated to whatever is left; when nothing is left, `Format("Msg_File_AttachBudget", fileName)` — **one** placeholder, describing the exhausted character budget rather than the file count — and no chip. |
| **More than `MaxPendingFiles`** | The same two-argument `Msg_File_AttachLimit` per skipped file — do not stop at the first. |
| **Unsupported type in a mixed drop** | Per-file `Msg_File_UnsupportedAttachment`; the supported files in the same drop still attach. Note `FileDropBehavior.FilterAccepted` already discards anything outside `AcceptedExtensions`, so this fires only for the picker's typed-path route and for `.pdf`-class kinds. |
| **`.pdf`** | Classified `FileKind.Pdf`, no reader exists, absent from `AcceptedExtensions` — rejected at drag-over, as today. Not in scope. |
| **Same file dropped twice** | Dedup by `FullPath`, `OrdinalIgnoreCase`; `Msg_File_DuplicateAttachment` caution snackbar; the existing chip stays. |
| **0-byte file / whitespace-only file** | `Msg_File_Empty` caution snackbar; no chip. Never stage an empty `<attached_file>`. |
| **Mail with no text body and no HTML** | Same as above — `ReadEmailAsync` returns `ReadResult.Success` with a headers-only render, and the importer rejects it as empty **only if the headers are empty too**. A mail with a subject but no body still attaches: the headers alone are useful. |
| **Mail with HTML only** | Stripped and `BodyIsFromHtmlFallback = true` on **both** sides — `text/html` part selection for `.eml` (§7.3), `10130102` PR_HTML for `.msg` (§7.2). The chip is identical; nothing in the UI distinguishes it. |
| **`.msg` whose only body is RTF** | `10090102` is not decompressed (§7.2), so the render is headers-only and the importer keeps it if the headers are non-empty. The one shape the PR_HTML fallback does not cover; accepted. |
| **Send in Agent mode with files attached** | The turn is forced to Chat shape (`planned: false`, D17/§8.4.2) and the hint says so. The Agent lever is **not** flipped — the next send with an empty chip strip plans normally. |
| **`Date:` with an RFC 5322 comment**, e.g. `… +0000 (UTC)` | Comment stripped before parsing (§7.3); the line still renders. Without the strip the whole `Date:` line silently disappears. |
| **Two images in one drop** | First wins; `Msg_File_OneImageOnly` caution snackbar (D16) — but only if the first image actually attached. Refused by the vision-provider or size gate, nothing was kept and only that gate's own caution shows. |
| **Image + text in one drop** | Image → `PendingAttachment` (subject to the PiaCloud gate, which may reject it), text → `PendingFiles`. The two are independent: a rejected image must not prevent the text chips (D10, D8). |
| **Attach while streaming** | All three staging paths refuse (`IsStreaming` guard), including `ExecuteHandleImageAttached`, which lacks one today. |
| **Run-in-background with files attached** | The button is disabled and the hint says why (§8.4.1). A detached run cannot carry the payload, and launching one anyway would drop the mail silently. |
| **Send refused** (`StartTurnAsync` returns false) | `InputText`, `PendingAttachment` **and** `PendingFiles` are all restored. |
| **Parked run answered while files are attached** | `TryAnswerParkedRunAsync` refuses to treat it as an answer and it becomes a normal turn (§8.3) — the resume channel is text-only and would drop the payload silently. |
| **Cyclic / truncated CFB, missing MIME boundary** | Never hang, never throw out of `ReadEmailAsync`; return `ReadResult.Fail(ex.GetType().Name)` or degrade to "whole body is text". |

---

## 15. Tests

New parser tests go in `tests/Pia.Wpf.Tests/Helpers/`, namespace `Pia.Tests.Helpers`, xunit v3 + plain `Xunit.Assert`, `public sealed class`, following `DroppedFileReaderClassifyTests.cs`.

### 15.1 The fixture problem — read this before writing a parser test

`artifacts/` is **gitignored** (`.gitignore:79`, confirmed by `git check-ignore`), `git ls-files artifacts` is empty, and there is no negation rule. `tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj` has **zero** `Content` / `None` / `EmbeddedResource` / `CopyToOutputDirectory` entries. A test that reads `artifacts/sample.msg` fails on every other machine, and one that *skips* when the file is missing reports `Not Run` under the `failed: 0` gate — it proves nothing.

Two tracks, both required:

**Gate track (portable).**
- `.eml`: build the fixtures **as strings in the test**. RFC 5322 is plain text, so the traps can be encoded exactly — a `Subject` folded across three encoded-words, QP soft breaks, `text/plain;charset=UTF-8` with no space, a TAB before `boundary=`.
- `.msg`: add a **small, tracked, redacted** `.msg` to a new `tests/Pia.Wpf.Tests/TestData/` folder, plus the csproj wiring that does not exist today:
  ```xml
  <ItemGroup>
    <None Update="TestData\*.msg">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
  ```
  **The fixture must contain at least one stream below 4,096 bytes and one at or above it**, or the mini-FAT defect ships green.

  Two body shapes are needed, and `artifacts/sample.msg` is only the first: measured, it has `__substg1.0_1000001F` (1,100 B) and **no root PR_HTML at all**. `Read_FallsBackToStrippedPrHtmlWhenPrBodyIsAbsent` (§15.3) therefore needs a *second* tracked fixture — PR_HTML present, PR_BODY absent — or it cannot run. Gate **A2** answers both questions at once. If a second CFB proves awkward to build, the acceptable fallback is: unit-test the HTML strip itself EML-side (`Read_FallsBackToStrippedHtmlWhenNoPlainPart`, §15.2 — it is literally the same code path per §7.2) and move the MSG row into the explicit probe track against a real HTML-only mail from the owner's mailbox. Do **not** leave a §15.3 row no fixture can exercise.

**Local-verification track.** An explicit (`[LiveApiFact]`-style, excluded by default) test against `artifacts/sample.msg` and `artifacts/sample.eml` asserting the exact values in §15.4. It is documentation of measured truth, not a gate.

### 15.2 `tests/Pia.Wpf.Tests/Helpers/EmlReaderTests.cs`

| Test method | Asserts |
|---|---|
| `Read_UnfoldsAHeaderFoldedWithSpaceAndWithTab` | both continuation forms join into one field, leading whitespace retained |
| `Read_DecodesQEncodedSubjectAcrossThreeFoldedWords` | result contains `"und ich"` and `"Benchmarks"` |
| `Read_DropsWhitespaceBetweenAdjacentEncodedWords` | result does **not** contain `"und  ich"` or `"Be nchmarks"` |
| `Read_DecodesBEncodedSubject` | base64 encoded-word |
| `Read_EmitsMalformedEncodedWordVerbatim` | no throw, literal passthrough |
| `Read_CollapsesWhitespaceAndNewlinesInSubject` | a subject carrying `=0A` becomes single-line |
| `Read_JoinsQuotedPrintableSoftLineBreaks` | the two-line `ich h=C3=A4nge =` / `gerade noch` case joins with no inserted space |
| `Read_DecodesMultiByteUtf8SplitAcrossEscapes` | `=C3=A4` → `ä` |
| `Read_ParsesContentTypeParameterWithNoSpaceAfterSemicolon` | charset resolved |
| `Read_ParsesBoundaryParameterPrecededByTab` | boundary resolved |
| `Read_PrefersTextPlainOverTextHtml` | `BodyIsFromHtmlFallback` is false, body is the plain part |
| `Read_FallsBackToStrippedHtmlWhenNoPlainPart` | fallback flag true, `<script>`/`<style>` bodies and U+034F gone |
| `Read_RecursesIntoNestedMultipart` | `multipart/mixed` wrapping `multipart/alternative` |
| `Read_DecodesBase64TransferEncoding` | with and without padding |
| `Read_HandlesBareLfLineEndings` | a Unix-generated message parses |
| `Read_ReturnsEmptyListsNotNullWhenThereAreNoRecipients` | `To`/`Cc`/`AttachmentNames` are empty, never null |
| `Read_PreservesTheDateOffset` | `+02:00` survives, not collapsed to local |
| `Read_ExtractsAttachmentNamesWithoutBytes` | `filename=` and `name=` forms |
| `Read_TreatsWholeBodyAsTextWhenTheBoundaryNeverOccurs` | no throw |
| `Read_DoesNotThrowOnAMessageWithNoBlankLine` | headers-only input |
| `Read_FallsBackToUtf8OnAnUnknownCharset` | no `ArgumentException` |
| `Read_ParsesADateCarryingAnRfc5322Comment` | `Mon, 31 Aug 2026 20:12:28 +0000 (UTC)` → `2026-08-31T20:12:28+00:00` with `Offset == TimeSpan.Zero`. Without the comment strip this returns null and the whole `Date:` line vanishes. |
| `Read_ParsesADateUnderANonEnglishCurrentCulture` | set `CultureInfo.CurrentCulture` to `de-DE` for the duration. This **passes today** (measured — .NET's RFC 1123 fallback ignores culture month names); it is a guard against a future `ParseExact`, not a bug repro. Say so in the test's one-line comment. |

### 15.3 `tests/Pia.Wpf.Tests/Helpers/MsgReaderTests.cs`

| Test method | Asserts |
|---|---|
| `Read_ReadsASmallStreamThroughTheMiniFat` | the exact leading characters of a `<4096`-byte stream — **not** "non-empty" |
| `Read_ReadsALargeStreamThroughTheNormalFat` | the exact leading characters of a `>=4096`-byte stream |
| `Read_ScopesPropertiesToTheRootStorage` | a `__substg1.0_10130102` that exists only under `__nameid_version1.0` is **not** reported as PR_HTML |
| `Read_WalksTheDirectoryChainFromTheFatNotNumDirectorySectors` | all entries found when `numDirectorySectors == 0` |
| `Read_SkipsUnallocatedDirectoryEntries` | `objectType == 0` ignored |
| `Read_TrimsATrailingNulFromAUnicodeProperty` | the inconsistent-terminator case |
| `Read_UsesTheDirectoryStreamSizeNotThePropsLength` | no two-byte over-read |
| `Read_ReadsTheSentDateFromClientSubmitTime` | FILETIME → `DateTimeOffset` with **`Offset == TimeSpan.Zero`**, not merely the right instant. `DateTimeOffset.FromFileTime` returns local time and passes an instant-equality assertion (§7.2). |
| `Read_FallsBackToTheTransportHeaderDateWhenTheStreamIsAbsent` | including an RFC 5322 `(UTC)` comment on that header |
| `Read_ReadsTheCodepageFromThePropertyRecordNotAStream` | `3FDE0003` is PT_LONG and has no `__substg1.0_` stream (measured absent in `sample.msg`, value 65001 in `__properties_version1.0`) |
| `Read_FallsBackToStrippedPrHtmlWhenPrBodyIsAbsent` | `10130102` read from the **root**, decoded via the CPID, stripped, `BodyIsFromHtmlFallback == true`. Needs a fixture with PR_HTML and no PR_BODY — see §15.1. |
| `Read_PrefersSmtpAddressOverTheX500Dn` | `39FE001F` beats an `EX` `3003001F` |
| `Read_PrefersRecipientStoragesOverDisplayTo` | |
| `Read_ReturnsEmptyRecipientListsWhenThereAreNone` | |
| `Read_DoesNotDecompressRtf` | `10090102` ignored, plain body used |
| `Read_DoesNotHangOnACyclicFatChain` | visited-set guard |
| `Read_RejectsABadSignature` | |
| `Read_RejectsAnUnsupportedMajorVersion` | |

### 15.4 `tests/Pia.Wpf.Tests/Helpers/EmailSampleProbeTests.cs` (explicit, local only)

Against `artifacts/sample.msg`:

```
Subject  "neo42 Service Portal - Individualpaketierung abgeschlossen"
From     "neo42 Service Portal <no-reply@neo42.de>"
To       ["Marco Altmann <marco.altmann@neo42.de>"]
Cc       []
Date     2026-08-31T11:46:20.000+00:00, and Offset == TimeSpan.Zero
Body     517 chars, starts "neo42 GmbH", contains "neo42_Pia_Ver1.4.15.0_Rev0.zip",
         ends "Steuernummer 212 / 5756 / 1164"
Attach   []
Fallback false
```

All measured against the real file. Two traps in those two lines:

- **517, not 550.** The raw `__substg1.0_1000001F` stream is 1,100 bytes = 550 UTF-16 chars and begins `"\r\n \t \r\nneo42 GmbH…"`. 550 is the *pre-normalization* count; after §4.2's contract (CRLF→`\n`, trim, collapse 3+ blank lines to 2) it is 517 and starts at `neo42 GmbH`. "550 chars **and** starts `neo42 GmbH` after trim" cannot both hold.
- **Assert the offset, not just the instant.** `Assert.Equal` on two `DateTimeOffset`s compares instants, so a `FromFileTime` implementation returning `2026-08-31T13:46:20+02:00` passes while the rendered header line reads `2026/08/31 13:46 +02:00` and moves with the machine's time zone. Assert `Offset` separately, or assert the rendered `Date:` line.

Against `artifacts/sample.eml`:

```
Subject  "Maik Behring hat Folgendes gepostet: Es ist 23:17 Uhr und ich hänge gerade noch über Benchmarks. Der Grund: Für… 💡"
From     "LinkedIn <updates-noreply@linkedin.com>"
To       ["Marco Altmann <marco.altmann@googlemail.com>"]
Cc       []
Date     2026-08-31T20:12:28.000+00:00, and Offset == TimeSpan.Zero
Body     ~7,994 chars, starts "Maik Behringhat einen Beitrag geteilt:"
Attach   []
Fallback false
```

The `Date:` field here is `Mon, 31 Aug 2026 20:12:28 +0000 (UTC)` — measured, and it does **not** parse without the §7.3 comment strip, so this row is the probe that catches a missing strip. The 7,994 is post-decode and pre-normalization; hedge it as `>= 7,900` rather than pinning an exact count.

### 15.5 `tests/Pia.Wpf.Tests/Helpers/DroppedFileReaderClassifyTests.cs` — extend

Add `[InlineData(".msg", FileKind.Email)]`, `[InlineData(".eml", FileKind.Email)]`, `[InlineData("C:\\mail\\Report.MSG", FileKind.Email)]` to `Classify_MapsKnownExtensions`.

### 15.6 `tests/Pia.Wpf.Tests/Helpers/DroppedFileAttachmentImporterTests.cs` (new)

`TryStageAsync_StagesATextFile` · `TryStageAsync_SeparatesImagePathsFromStagedFiles` · `TryStageAsync_SkipsADuplicatePath` · `TryStageAsync_StopsAtMaxPendingFiles` · `TryStageAsync_TruncatesAFileOverMaxFileChars` · `TryStageAsync_TruncatesAgainstTheRunningTotal` · `TryStageAsync_SkipsAnEmptyFile` · `TryStageAsync_SkipsAnUnsupportedFileButKeepsTheRest` · `TryStageAsync_CountsAlreadyPendingFilesTowardBothCaps` · `TryStageAsync_OverTheReadCeiling_SaysTooLargeToAttach` · `TryStageAsync_WithTheCharacterBudgetSpent_NamesTheBudgetNotTheFileCount` · `TryStageAsync_ThreeFilesInOneDrop_RefuseTheThirdOnTheBudget`

The last three are about *wording*, and each needs its negative: the budget arm must not reach for `Msg_File_AttachLimit` and the read ceiling must not reach for Optimize's `Msg_File_TooLarge`. Assert the argument list too (`Arg.Is<object[]>(args => args.Length == 1 …)`), because `string.Format` drops a surplus argument in silence and the substitute here returns the key whatever it is handed.

### 15.7 `tests/Pia.Wpf.Tests/Services/AssistantPromptComposerAttachedFileTests.cs` (new)

`BuildAttachedFileBlock_ReturnsEmptyForNoFiles` · `BuildAttachedFileBlock_EmitsThePreambleOnce` · `BuildAttachedFileBlock_EmitsNameAndTypeAttributes` · `BuildAttachedFileBlock_OmitsTheFullPath` · `BuildAttachedFileBlock_EscapesAttributeValues` · `BuildAttachedFileBlock_AddsTruncatedAttributesOnlyWhenTruncated` · `BuildAttachedFileBlock_LeavesTheBodyUnescaped`

### 15.8 `tests/Pia.Wpf.Tests/Models/AssistantMessageAttachedFileContextTests.cs` (new)

**The regression that D5 is about:**

- `ToChatMessage_NoAttachment_AppendsAttachedFileContext` — the parameterless overload, **no image**. This is the case a naive two-branch edit loses.
- `ToChatMessage_WithOverrideText_NoAttachment_AppendsAttachedFileContext` — the override overload, no image.
- `ToChatMessage_WithImage_AppendsAttachedFileContextToTheTextContent` — the fused `[TextContent, DataContent]` shape is preserved and the text carries the block.
- `ToChatMessage_WithOverrideText_AndImage_AppendsAttachedFileContext`
- `ToChatMessage_EmptyText_UsesTheAttachedFileContextAlone` — no leading blank lines.
- `ToChatMessage_NoAttachedFileContext_IsUnchanged` — byte-identical to today's output.

Also update the two existing rows in `tests/Pia.Wpf.Tests/Models/AssistantMessageFileRefsTests.cs:79-90` (`ToChatMessage_WithOverrideText_NoAttachment_ReturnsPlainText`) and `:92-101` (`…_PreservesImageAttachment`) if the builder changes their observed output (it should not — with a null `AttachedFileContext` the builder is behaviour-identical).

### 15.8a `tests/Pia.Wpf.Tests/ViewModels/ChatSessionManagerTests.cs` — extend (step B2)

B2 ships two named behaviours that nothing else would notice being dropped, and both have a ready-made harness in this file: `AttachParkedRun` (`:1837`) and the parked-run suite at `:1882-2000`.

- `StartTurnAsync_SetsAttachedFileContextOnTheUserMessage` — the minted `AssistantMessage` at `:730-733` carries the block. Without it the whole feature is a no-op that still compiles.
- `StartTurnAsync_RunParkedForClarification_WithAnAttachedFile_StartsAnOrdinaryTurn` — modelled on `StartTurnAsync_RunParkedForClarification_AnswersItAndResumes_WithoutStartingATurn` (`:1882`) but with a non-null `attachedFileContext`, asserting `_resumeService` is **never** called. The refusal is a one-line predicate and its failure mode is silent payload loss.

### 15.9 `tests/Pia.Wpf.Tests/ViewModels/AssistantViewModelPendingFilesTests.cs` (new)

- `AddingAPendingFile_EnablesSend` — the `CollectionChanged` → `NotifyCanExecuteChanged` wiring. Without it Send stays disabled until a keystroke.
- `RemovingTheLastPendingFile_DisablesSend`
- `TextPlusPendingFile_DoesNotEnableRunInBackground` — the D20 gate. Must be the text-plus-file case: files alone leave the command disabled anyway, so that assertion proves nothing.
- `RemovingTheLastPendingFile_ReEnablesRunInBackground` — with real goal text still in the composer.
- `AddingAPendingFile_ShowsTheBlockRunHint` — the §8.4.3 wiring. This is the one that fails if the hint is routed through `RefreshGoalTooShortHint`: assert the hint is visible **immediately after the collection change**, with no keystroke and no `Task.Delay` in the test.
- `RemovingTheLastPendingFile_HidesTheBlockRunHint`
- `GoalTooShortHint_WinsOverTheBlockRunHint` — short refused goal + a chip; only `GoalTooShortHintVisible` is true.
- `ChatModeWithTypedText_ShowsNoBlockRunHint` — the Agent-only condition in §8.4.3. Chat lever, real typed text, a chip: no hint. This is the headline path, and a `HasCandidateGoalText()` disjunct makes it fire there.
- `AddingAPendingFile_RaisesHasPendingFiles`
- `AgentModeSendWithAPendingFile_IsNotPlanned` — the D17 gate (§8.4.2): Agent lever on, a chip attached, and `StartTurnAsync` is received with `planned: false`. Assert the lever is **still on** afterwards.
- `Send_ClearsPendingFiles`
- `RefusedSend_RestoresPendingFiles`
- `StartFreshChat_ClearsPendingFiles`
- `RemovePendingFileCommand_RemovesOnlyThatFile`
- `HandleFilesDropped_WhileStreaming_StagesNothing`
- `HandleFilesDropped_RoutesImagesAndTextSeparately`
- `TwoImagesRefusedByTheProvider_DoNotClaimOneWasKept` — two real PNGs, no vision provider: the provider caution shows and `Msg_File_OneImageOnly` never formats.
- `TwoImagesKeptByAVisionProvider_NameTheOneThatWasKept` — the same drop with a PiaCloud provider, so the suppression above cannot become a blanket one.

Any `StartTurnAsync` stub in this file needs a **sixth** `Arg.Any<string?>()` — see §8.3 for why five silently returns a null `Task`.

### 15.10 `tests/Pia.Wpf.Tests/Models/PendingFileAttachmentTests.cs` (new)

`Icon_IsInsideTheBasicMultilingualPlane` — a `[Theory]` over every `PendingFileKind` asserting `(int)attachment.Icon <= 0xFFFF`. A `SymbolRegular` member above U+FFFF compiles clean and renders a garbage letter; 2,863 of the 9,235 members are in that range.

### 15.11 `tests/Pia.Wpf.Tests/Views/AssistantViewParseTests.cs`

The pattern in this file is unambiguous — **one method per composer hint**: `ComposerHint_Parses_AndTracksForeignRunActive` (`:48`), `…PlanApprovalParkActive` (`:103`), `…GoalTooShortHintVisible` (`:153`), `…AgentModeHintVisible` (`:203`). Add two, by name:

- `ComposerHint_Parses_AndTracksPendingFilesBlockRunHintVisible` — otherwise the hint that never fires never gets parse-tested either.
- `ChipStrip_Parses_AndTracksHasPendingFiles` — the new `Border`'s `Visibility` binding.

### 15.12 `tests/Pia.Wpf.Tests/Views/FileDropAcceptedExtensionsTests.cs` (new)

The guard §9.1 has no other way to keep. Each view's `AcceptedExtensions` is a hand-maintained copy of what its importer can read, and `FileDropBehavior.FilterAccepted` drops a path before any handler runs — so a kind added to `DroppedFileReader` reaches the user only if both XAML lists are widened too, and nothing said so out loud.

Read the attribute out of the XAML as text and reflect `DroppedFileReader`'s private `KindByExtension` table, then assert **both** directions per view: every extension of a kind the view's importer handles is declared, and every declared extension classifies to one of those kinds. Handled kinds are `Text, Docx, Xlsx, Email` for both, plus `Image` for the Assistant, whose ViewModel keeps images for the vision path. Pin a floor on the reflected table (`>= 40`) so a renamed field fails loudly instead of passing vacuously. `.env`, `.gitignore` and `.editorconfig` are excluded by name: they are whole file names rather than extensions, and neither list has ever offered one.

---

## 16. Risks and open questions

### Risks

| Risk | Mitigation |
|---|---|
| **Mini-FAT dispatch read the wrong way returns plausible text, not an exception.** Reading PR_BODY through the normal FAT yielded `"sKJEV4cGFuc2lvbldvcmRzVG9DYXBpdGFsSW5pdG"`. | The fixture must exercise both allocators and the test must assert exact leading characters (§15.3). |
| **The adjacent-encoded-word rule produces 98%-correct output**, so a casual eyeball passes it. | Assert the two exact substrings `"und ich"` and `"Benchmarks"`, and their absence in the naive forms. |
| **`AttachedFileContext` appended only in the image branch** silently loses every text-only attachment. | One `BuildChatMessage` builder (§8.1) plus `ToChatMessage_NoAttachment_AppendsAttachedFileContext`. |
| **Interactive context growth.** The interactive list is never compacted (`ChatSession.cs:361-367`), so three file-bearing turns pin ~30,000 tokens with nothing to shrink them — fine at 128k, fatal at 32k. | Caps in §5; the user can remove chips or start a new chat. Not solved here; gate **C1** measures it against a real three-turn session, and step **C6** is the per-session total it would buy — never a bigger per-message cap. |
| **Restart asymmetry.** A reopened chat has no `AttachedFileContext`, so the same follow-up question gets a different answer before and after closing the app. | Accepted — identical to the image attachment's existing behaviour (§8.2). D4 forbids persisting it. |
| **`ViewAutomationIdTests`' floors are `>=`**, so a forgotten bump is green. | §11 states the delta and how to measure it. |
| **A same-named file from two folders shares an automation id** (D18). | Documented; dedup is by full path so both chips exist and a script targeting that id hits the first. |
| **`artifacts/` fixtures cannot be committed**, and a skipping test proves nothing. | Two-track testing (§15.1); the gate track must stand alone. |
| **Adding `.msg`/`.eml` to `Classify` changes Optimize's behaviour.** | D14 makes it deliberate and adds the importer case, so Optimize inserts mail text instead of showing "unsupported". |
| **The headline gesture — dragging a mail out of Outlook — does not work**, and nothing in rounds 1-2 would reveal it. Outlook offers `FileGroupDescriptorW`/`FileContents`, never `CF_HDROP`. | Gate **G0**, answerable in thirty seconds against today's build, sits above A1 so the answer lands before the expensive parser work. §1 and §14 state the limitation. If G0 says it must work, the branch is: in `OnDragEnter`/`OnDrop`, accept `FileGroupDescriptorW`, read the descriptor for names, pull each `FileContents` stream by `lindex` through the COM `IDataObject` (WPF's managed `DataObject` returns only the first), write to a temp file under `PiaPaths`, and hand *that* path to the existing command — with the accepted-extension check moved **after** materialization, since the descriptor's name is the only extension source. That is a new subsystem (`L`), not a tweak; it is a separate plan if it is wanted. |
| **The rendered `Date:` line is destroyed by the PII tokenizer** if written with hyphens. Measured: `2026-08-31 11:46` is 10 digits inside `PhoneRegex`'s character class and becomes `[Phone_N]`. | D21 renders with slashes, following `AssignmentToolHandler.cs:383-385`. The mail *body* is still tokenized — that is the privacy feature working, not a defect. |
| **`DateTimeOffset.FromFileTime` returns local time and passes an instant-equality test.** | §7.2 names the correct call; §15.3 and §15.4 assert `Offset == TimeSpan.Zero`, not just the instant. |
| **The composer hint is recomputed by nothing a collection change raises**, so the blocked button looks dead. | §8.4 adds the fourth call in `OnPendingFilesChanged`; §8.4.3 fixes the precedence order and drops the debounce; `AddingAPendingFile_ShowsTheBlockRunHint` (§15.9) fails if it regresses. |

### Open questions

1. **Should a chip survive a chat switch (`AttachToActiveSession`, `:463-496`)?** This plan says yes, matching `PendingAttachment`'s current behaviour — but that behaviour is an accident (the comment at `:961-962` exists because someone hit it), and clearing on switch is arguably what a user expects. Decide during the round-3 UI pass, when both behaviours can be tried.
2. **Should the chip show that the file was truncated?** A snackbar fires once and is gone; the model is told via the `note` attribute; the user is not. A subtle badge or a tooltip suffix is cheap, but it is one more thing on a small chip. Deferred to round 3.
3. **How small can the tracked `.msg` fixture be while still exercising both allocators — and can one file also cover the PR_HTML-without-PR_BODY shape?** A hand-built CFB with one 100-byte stream and one 5,000-byte stream is ~10 KB, but nobody has built one yet, and §15.3 now needs a second body shape too. If it proves awkward, the fallback is a base64 blob embedded in the test source, generated once from a redacted sample. Gate A2.
4. **Should the Agent→Chat downgrade be silent-with-a-hint, or should Send be disabled outright while a chip is attached?** The body chooses the downgrade (D17/§8.4.2): the mail still reaches the model, which is what the user asked for, and the hint says the turn is not planned. The alternative — refuse the send entirely, reusing the `Assistant_PendingFilesBlockRun_Hint` slot the way D20 does for Run-in-background — is more honest about the lever and less surprising to someone who deliberately turned Agent mode on. Cheap to switch either way; decide when C1 puts it on screen.
5. **Does the Outlook drag have to work before this ships?** ~~Gate G0.~~ **ANSWERED 2026-09-01: yes, and it does.** Not before round 1 shipped, but as round 4 — [2026-09-01-outlook-virtual-file-drop.md](2026-09-01-outlook-virtual-file-drop.md). The `L` estimate in the risk table above was right about the shape and wrong about one detail worth recording: the risk row says to pull `FileContents` by `lindex` through the COM `IDataObject`, which is necessary but not sufficient — Outlook’s message list serves that format as **`TYMED_ISTORAGE`**, so the receiver has to `StgCreateDocfile` and `IStorage::CopyTo`, not just read a stream. An ISTREAM-only implementation fails `DV_E_TYMED` against the one source the feature exists for.
