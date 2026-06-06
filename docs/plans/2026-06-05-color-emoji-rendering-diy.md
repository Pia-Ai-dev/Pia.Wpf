# Color-emoji rendering (hand-rolled OS interop) — implementation plan

> Status: ready to implement. Written 2026-06-05. Decision: **hand-roll the native
> interop ourselves** (no Vortice, no Emoji.Wpf). This file is self-contained so a fresh
> session can execute it without re-deriving the research.

## 1. Context — the problem

Emoji in Pia.Wpf render as black-outline monochrome glyphs or empty boxes (tofu). Two
causes, both rooted in a permanent WPF limitation:

1. **Monochrome** — WPF's text stack (`TextBlock`, `GlyphRun`, `FlowDocument`) draws only
   the base glyph layer and ignores the `COLR`/`CPAL` color layers in *Segoe UI Emoji*.
   No native WPF fix exists (dotnet/wpf#91 has been open for years).
2. **Boxes** — emoji `TextBlock`s set **no `FontFamily`**, so they inherit *Segoe UI
   Variable* (zero emoji glyphs) and rely on WPF's unreliable fallback, which fails on
   multi-codepoint sequences (ZWJ 👨‍👩‍👧, skin tone 👍🏽, regional-indicator flags 🇩🇪, VS16, keycaps).

**The OS itself can render color emoji** — DirectWrite/Direct2D have done so since
Win8.1 via the installed font's `COLR`/`CPAL` tables. WPF just never calls that path.

## 2. Decision & scope (confirmed with user)

- **Color where emoji are *displayed*** — render through the OS engine (Direct2D +
  DirectWrite + WIC) into cached `BitmapSource`s, shown as `Image` / `InlineUIContainer`.
- **Reliable monochrome in *editable* fields** — set `FontFamily="Segoe UI Emoji"` on the
  composer and the custom-emoji box. Kills boxes; stays B&W (acceptable while typing).
- **Hand-rolled interop**, not Vortice — using C# **`delegate* unmanaged`** calls into the
  COM vtables. `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` is already set in the csproj,
  and the project already does native interop (e.g. hotkey handling), so this is not new
  territory.

### Why hand-rolled, and why `delegate*` (not `[GeneratedComInterface]`)

- Vortice would be **one** package (`Vortice.Direct2D1` — WIC + DirectWrite bindings are
  generated *into* it, not separate packages), ~6 transitive managed assemblies, ~5.5 MB,
  zero native binaries. Not "huge," but more than this one feature needs.
- `[GeneratedComInterface]` builds the vtable from declaration order, forcing you to
  declare **every** method preceding the ones you call (e.g. ~27 methods of
  `ID2D1RenderTarget` before `DrawText`). That's the ~1800-LOC, error-prone path.
- `delegate* unmanaged` lets you call **slot N directly by offset** and skip everything
  else. For "a few methods from deep interfaces" this is far leaner: **~350–500 LOC** total
  for interop + renderer + cache.

### Rejected alternatives (for the record)

- **Emoji.Wpf** — latest 0.3.4, targets netcoreapp3.1/net40, unmaintained.
- **SkiaSharp** — documented Windows color-emoji failures (mono/SkiaSharp#3244).
- **TerraFX.Interop.Windows as a dependency** — ~15 MB package (full Win32 surface); larger
  than Vortice. **BUT** keep its *source* open as a reference (see §5).

## 3. Verified facts (and corrections from the research pass)

Adversarially fact-checked. Confidence noted; **verify *-marked items against headers /
TerraFX before trusting**.

- ✅ `d2d1.dll`, `dwrite.dll`, `windowscodecs.dll` are **in-box since Win7/Vista** —
  present on our floor (`net10.0-windows10.0.17763.0`, Win10 1809). No redistributable.
- ✅ `D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT = 0x00000004`. Without it, `DrawText`
  renders monochrome outlines. It is **not** the default — must be passed explicitly.
- ✅ `DrawText`/`DrawTextLayout` with that flag render color emoji; DirectWrite's GDI path
  `IDWriteBitmapRenderTarget::DrawGlyphRun` is **monochrome only** (don't use it).
- ⚠️ **LYNCHPIN — must settle in the spike (§6):** one verifier *refuted* that a
  **software / WIC bitmap render target** can produce color emoji, claiming it's
  "GPU-exclusive." **That refutation is almost certainly wrong** (it even denied the flag
  exists, contradicting the header-confirmed value — a self-contradiction). Direct2D color
  font rendering is CPU-capable; emoji→PNG tools use WIC render targets routinely.
  **The legitimate kernel:** `ENABLE_COLOR_FONT` arrived with the **D2D 1.1
  `ID2D1DeviceContext`** (Win8.1). On a bare `ID2D1RenderTarget` (1.0) the flag may be
  ignored. **Recommended path:** create the WIC render target, then `QueryInterface` for
  `ID2D1DeviceContext` and call `DrawText` on *that*. The spike confirms which interface is
  required — this risk applies to Vortice too; Vortice would not have avoided it.
- ✅ `GeneratedComInterfaceAttribute` is real on .NET 8–10 (the "refuted" verdict was only a
  naming nitpick) — we're just not using it here.

## 4. Minimal native API surface

### Module entry points (P/Invoke via `LibraryImport`)

| Export | DLL | Signature (simplified) |
|---|---|---|
| `D2D1CreateFactory` | d2d1.dll | `HRESULT (D2D1_FACTORY_TYPE, REFIID, D2D1_FACTORY_OPTIONS*, void** factory)` |
| `DWriteCreateFactory` | dwrite.dll | `HRESULT (DWRITE_FACTORY_TYPE, REFIID, IUnknown** factory)` |
| `CoCreateInstance` | ole32.dll | `HRESULT (REFCLSID, IUnknown*, DWORD clsctx, REFIID, void** ppv)` |

`D2D1_FACTORY_TYPE`: SINGLE_THREADED=0, MULTI_THREADED=1. `DWRITE_FACTORY_TYPE`: SHARED=0.

### Call sequence (one emoji → premultiplied-BGRA buffer → `BitmapSource`)

1. `CoCreateInstance` WIC factory → `CreateBitmap(px, px, GUID_WICPixelFormat32bppPBGRA, WICBitmapCacheOnLoad=2, out wicBitmap)`
2. `D2D1CreateFactory` → `ID2D1Factory` → `CreateWicBitmapRenderTarget(wicBitmap, &props, out rt)`
3. **QI `rt` → `ID2D1DeviceContext`** (per §3 lynchpin) — draw on this if needed
4. `DWriteCreateFactory` → `IDWriteFactory` → `CreateTextFormat("Segoe UI Emoji", null, NORMAL=400, NORMAL=0, NORMAL=5, fontSize, "en-us", out fmt)`; optional `SetTextAlignment(CENTER=2)` + `SetParagraphAlignment(CENTER=2)`
5. `CreateSolidColorBrush(&white, null, out brush)` (color for monochrome fallback glyphs)
6. `BeginDraw()` → `Clear(&transparent)` → `DrawText(emoji, len, fmt, &rect, brush, ENABLE_COLOR_FONT=4, DWRITE_MEASURING_MODE_NATURAL=0)` → `EndDraw(null, null)` (check HRESULT; handle `D2DERR_RECREATE_TARGET=0x8899000C`)
7. `wicBitmap->CopyPixels(null, stride=px*4, bufSize, pBuffer)` (or `Lock`+`GetDataPointer`)
8. `WriteableBitmap(px, px, dpi, dpi, PixelFormats.Pbgra32, null)` → `WritePixels(...)` → `Freeze()` → cache
9. `Release` every COM pointer (reverse order). Keep the **factories** long-lived; only the WIC bitmap / render target / brush are per-call.

### Reconstructed vtable offsets (0-based, incl. IUnknown 0–2)

> ⚠️ The research agent's guesses were **wrong** for most of these (e.g. it put
> `CreateWicBitmapRenderTarget` at 5, actual **13**; `CreateBitmap` at 3, actual **17**).
> The values below are reconstructed from `d2d1.h`/`dwrite.h`/`wincodec.h` method order.
> **Cross-check each against TerraFX's `[VtblIndex(n)]` attributes before shipping** (§5).

**`ID2D1Factory`** (IUnknown 0–2, then): `CreateWicBitmapRenderTarget` = **13**
(order: ReloadSystemMetrics3, GetDesktopDpi4, CreateRectangleGeometry5, CreateRoundedRectangleGeometry6, CreateEllipseGeometry7, CreateGeometryGroup8, CreateTransformedGeometry9, CreatePathGeometry10, CreateStrokeStyle11, CreateDrawingStateBlock12, **CreateWicBitmapRenderTarget13**, …)

**`ID2D1RenderTarget`** (extends ID2D1Resource; GetFactory=3, then CreateBitmap=4 …):
- `CreateSolidColorBrush` = **8**
- `DrawText` = **27**
- `DrawTextLayout` = **28**
- `Clear` = **47**
- `BeginDraw` = **48**
- `EndDraw` = **49**

**`ID2D1DeviceContext`** extends `ID2D1RenderTarget` — same offsets for the inherited
`DrawText`/`Clear`/`BeginDraw`/`EndDraw`/`CreateSolidColorBrush` (new methods are appended
after the base). So you can QI to DeviceContext and reuse the same offsets above.

**`IDWriteFactory`** (IUnknown 0–2, GetSystemFontCollection=3 …): `CreateTextFormat` = **15**

**`IDWriteTextFormat`** (IUnknown 0–2, then): `SetTextAlignment` = **3**, `SetParagraphAlignment` = **4** (both optional)

**`IWICImagingFactory`** (IUnknown 0–2, CreateDecoderFromFilename=3 …): `CreateBitmap` = **17**

**`IWICBitmap`** (extends IWICBitmapSource: GetSize3, GetPixelFormat4, GetResolution5, CopyPalette6, **CopyPixels7**, then IWICBitmap: **Lock8**, SetPalette9, SetResolution10)
- `CopyPixels` = **7**
- `Lock` = **8**

**`IWICBitmapLock`** (IUnknown 0–2, GetSize3): `GetStride` = **4**, `GetDataPointer` = **5**

### Structs / enums (exact layout matters)

```c
// type is FIRST — the research agent omitted it.
struct D2D1_RENDER_TARGET_PROPERTIES {
  D2D1_RENDER_TARGET_TYPE type;     // int. DEFAULT=0 (or SOFTWARE=2)
  D2D1_PIXEL_FORMAT pixelFormat;    // { int format; int alphaMode; }
  float dpiX; float dpiY;           // 96.0f * dpiScale
  D2D1_RENDER_TARGET_USAGE usage;   // int, NONE=0
  D2D1_FEATURE_LEVEL minLevel;      // int, DEFAULT=0
}
struct D2D1_PIXEL_FORMAT { int format; int alphaMode; }
   // format = DXGI_FORMAT_B8G8R8A8_UNORM = 87
   // alphaMode = D2D1_ALPHA_MODE_PREMULTIPLIED = 1   (NOT straight)
struct D2D1_COLOR_F { float r,g,b,a; }      // white=(1,1,1,1); transparent=(0,0,0,0)
struct D2D1_RECT_F  { float left,top,right,bottom; }  // {0,0,px,px}
struct WICRect { int X,Y,Width,Height; }    // pass null for whole bitmap
```

- `D2D1_DRAW_TEXT_OPTIONS`: NONE=0, NO_SNAP=1, CLIP=2, **ENABLE_COLOR_FONT=4**
- `DWRITE_MEASURING_MODE`: NATURAL=0
- `DWRITE_FONT_WEIGHT` NORMAL=400, `DWRITE_FONT_STYLE` NORMAL=0, `DWRITE_FONT_STRETCH` NORMAL=5
- `WICBitmapCreateCacheOption`: WICBitmapCacheOnLoad=2
- GUIDs (**verify exact bytes from TerraFX/headers — do not trust memory**):
  `GUID_WICPixelFormat32bppPBGRA` (32bpp premultiplied BGRA — the only format that works),
  `IID_IWICImagingFactory`, `CLSID_WICImagingFactory`, `IID_ID2D1Factory`,
  `IID_IDWriteFactory`, `IID_ID2D1DeviceContext`. HRESULT: `S_OK=0`,
  `D2DERR_RECREATE_TARGET=0x8899000C`.

### Pitfalls

- **Premultiplied alpha** end-to-end (`PBGRA` WIC format + `PREMULTIPLIED` D2D alpha +
  `PixelFormats.Pbgra32`). Don't re-premultiply in C#.
- **`ENABLE_COLOR_FONT` is mandatory** and only on the DeviceContext path (see lynchpin).
- **COM lifetime** — `Release` every pointer; leaks lock the device. Wrap pointers so
  failures still release (try/finally or a tiny `ComPtr` ref struct).
- **Stride** — use the returned stride (may be padded), not blindly `width*4`, when using `Lock`.
- **STA / threading** — guard shared factories with a `lock`, or create the factory
  `MULTI_THREADED`. Frozen `BitmapSource`es are safe to hand to the UI thread.
- **DPI** — render at device pixels (`size * dpiScale`) or oversample 2–3× and let
  `Stretch="Uniform"` downscale. Verify at 100/125/150/200%.

## 5. De-risking tactic: use TerraFX source as the oracle

`github.com/terrafx/terrafx.interop.windows` annotates every COM method with
`[VtblIndex(n)]` and contains exact GUIDs, struct layouts, and enum values for D2D1 /
DirectWrite / WIC. **Read it** to confirm every offset/GUID/layout in §4 — but **do not add
the package**. It is the fastest way to eliminate the #1 risk (wrong vtable offsets).

## 6. Phase 0 — SPIKE FIRST (do before anything else)

A throwaway single-file spike that renders 👨‍👩‍👧 / 👍🏽 / 🇩🇪 / 1️⃣ to a `BitmapSource` shown in
a tiny window. Goal: **prove color lands in a software/WIC target, and learn whether the
bare `ID2D1RenderTarget` works or the `ID2D1DeviceContext` QI is required.** Also validates
the §4 vtable offsets empirically (a wrong offset crashes or draws nothing). ~½ day. Do not
proceed to the real implementation until color emoji visibly render.

## 7. Architecture — new files

`src/Pia.Wpf/Emoji/`:

- **`EmojiInterop.cs`** — `LibraryImport` for the 3 exports; the `delegate* unmanaged`
  vtable callers; structs/enums/GUIDs; a small `ComPtr`/`Release` helper; `HRESULT` checks.
- **`EmojiImageRenderer.cs`** — `BitmapSource Render(string emoji, int pixelSize)`.
  Long-lived factories (lazy, lock-guarded). Per-call WIC bitmap + render target + brush +
  (cached-per-size) text format → draw → `CopyPixels` → frozen `WriteableBitmap`.
  **Cache** `Dictionary<(string,int), BitmapSource>` (consider an LRU / size cap if many
  unique emoji). DI **singleton** (register in `App.xaml.cs`); also expose a static
  accessor for XAML/converters.
- **`EmojiScanner.cs`** — `IEnumerable<(string Text, bool IsEmoji)> Segment(string)`.
  Enumerate grapheme clusters via `StringInfo.GetTextElementEnumerator`; classify a cluster
  as emoji from Unicode ranges: Misc Symbols & Pictographs (U+1F300–1F5FF), Emoticons
  (U+1F600–1F64F), Transport/Map (U+1F680–1F6FF), Supplemental/Extended-A
  (U+1FA00–1FAFF), Misc Symbols (U+2600–26FF), Dingbats (U+2700–27BF), regional indicators
  (U+1F1E6–1F1FF), skin-tone modifiers (U+1F3FB–1F3FF), keycap base + U+20E3, plus any
  cluster containing VS16 (U+FE0F) or ZWJ (U+200D) joins. **No built-in `IsEmoji` in the
  BCL — implement carefully and unit-test.**
- **`EmojiInlineBuilder.cs`** — `IEnumerable<Inline> Build(string text, double fontSize)`
  for FlowDocument hosts: `Run` for text, `InlineUIContainer`>`Image` (Source from renderer,
  `Height≈fontSize`, tune `BaselineAlignment`) for emoji.

`src/Pia.Wpf/Controls/`:

- **`EmojiPresenter.cs`** — an `Image` subclass with `Emoji` + `GlyphSize` dependency
  properties that pulls `Source` from the renderer. The reusable block for single-emoji
  *display* surfaces.

`tests/Pia.Wpf.Tests/Emoji/`:

- **`EmojiScannerTests.cs`** — ZWJ family, skin tone, flags, VS16, keycap, mixed text/emoji,
  plain ASCII, surrogate pairs.

## 8. Per-surface integration

| Surface | File (line) | Change |
|---|---|---|
| Persona avatar (lists, picker, history) | `Controls/PersonaGlyph.xaml(.cs)` `:12`, `:55–67` | Replace `EmojiText` `TextBlock` with `EmojiPresenter`; keep the Pia-icon branch (`UpdateGlyph`, `:60–67`) untouched. |
| Persona-edit preview avatar | `Views/Dialogs/PersonaEditContentDialog.xaml` `:151` | Swap display `TextBlock` → `EmojiPresenter`. |
| Persona-edit toggle current-emoji | same `:177–183` | `EmojiPresenter` for the shown emoji. |
| Persona-edit picker swatches | same `:235–257` | Retemplate `PiaEmojiSwatchButtonStyle` content → `EmojiPresenter`. |
| Assistant markdown messages | `Controls/Markdown/PiaMarkdownRenderer.cs` `RenderInline()` `:284–285` | In the `LiteralInline` case, return a `Span` whose children come from `EmojiInlineBuilder` instead of one `Run`. Host is the read-only `RichTextBox` in `Controls/MarkdownMessageControl.xaml` (supports `InlineUIContainer`; set `IsDocumentEnabled` if needed). |
| **Editable** composer | `Views/AssistantView.xaml` `InputTextBox` `:255` | Add `FontFamily="Segoe UI Emoji"` (monochrome). |
| **Editable** custom-emoji box | `Views/Dialogs/PersonaEditContentDialog.xaml` `:199`/`:231` | Add `FontFamily="Segoe UI Emoji"`. |

### User-message bubble — structural decision

User messages render in a **plain `TextBlock`** (`Views/AssistantView.xaml:154–155`) with
the `AtCommandHighlightBehavior` building highlight `Run`s. **A `TextBlock` cannot host
`InlineUIContainer`** (verified), so inline color emoji is impossible there without changing
the host.

- **Full color (matches the goal):** convert the bubble to a read-only
  `RichTextBox`/`FlowDocument` host and reuse `EmojiInlineBuilder`; port the `@`-command
  highlighting to emit FlowDocument inlines. **Heaviest sub-task; touches
  `AtCommandHighlightBehavior`.**
- **Lower-risk fallback:** keep the `TextBlock`, add `FontFamily="Segoe UI Emoji"`
  (monochrome, no boxes).

Recommended order: ship personas + assistant messages + editable-field monochrome first
(clear wins), then decide the user-bubble option.

## 9. Recommended build order

1. **Spike** (§6) — prove color on software/WIC target; lock down interface + offsets.
2. `EmojiInterop` + `EmojiImageRenderer` + cache (offsets verified vs TerraFX).
3. `EmojiScanner` + unit tests.
4. `EmojiPresenter`; wire `PersonaGlyph` + persona-dialog display surfaces.
5. `EmojiInlineBuilder`; hook `PiaMarkdownRenderer` (assistant messages).
6. Editable fields → `Segoe UI Emoji` font.
7. User-message bubble decision (color via FlowDocument host, or monochrome).
8. DPI + perf pass; full verification.

## 10. Verification

- **Spike** renders color 👨‍👩‍👧 / 👍🏽 / 🇩🇪 / 1️⃣ before any real wiring.
- `dotnet build`; `dotnet test` (EmojiScanner + any renderer-cache tests).
- `dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj` and **verify manually** (do not use
  winwright): persona list + persona-edit dialog show **color** emoji incl. flags/ZWJ/
  skin-tone; emoji `TextBox` + composer show **clean B&W** (no boxes); an assistant message
  with emoji shows inline color. Check at 100 / 150 / 200% DPI.

## 11. Risks (ranked)

1. **Software/WIC color path** (§3 lynchpin) — settle in the spike; QI `ID2D1DeviceContext`.
2. **Vtable offsets** — mitigate with TerraFX `[VtblIndex]` cross-check (§5).
3. **Emoji segmentation correctness** — mitigate with `EmojiScanner` unit tests.
4. **COM lifetime leaks** — disciplined `Release` / `ComPtr` helper.
5. **DPI scaling** — render at device pixels / oversample; verify across scales.
6. **User-bubble host change** — scoped as a separate, optional sub-task.

## 12. Implementation notes (2026-06-05, as built)

Built and verified. Files: `src/Pia.Wpf/Emoji/{EmojiInterop,EmojiImageRenderer,EmojiScanner,EmojiInlineBuilder}.cs`,
`src/Pia.Wpf/Controls/EmojiPresenter.cs`, tests under `tests/Pia.Wpf.Tests/Emoji/`.

- **Lynchpin resolved.** Color emoji render on the WIC/software target. We QI the
  `CreateWicBitmapRenderTarget` result to `ID2D1DeviceContext` and `DrawText` on it with
  `ENABLE_COLOR_FONT` (falling back to the base RT). Verified visually via a spike that rendered
  👨‍👩‍👧 / 👍🏽 / 1️⃣ / 🌍 in full color to PNG. All §4 vtable offsets/GUIDs/layouts confirmed against TerraFX
  and proven empirically (a wrong offset crashes/draws nothing — none did).
- **Correction to §8: a `TextBlock` *does* host `InlineUIContainer` on .NET 10.** Verified by probe
  (no exception; the child gets a real arranged size — it's also how the existing `@`-command pills
  work). So the **user-message bubble got full color for free**: `AtCommandHighlightBehavior` now
  routes its non-command text spans through `EmojiInlineBuilder`. No FlowDocument refactor was needed.
- **Flags are monochrome on Windows.** Segoe UI Emoji has no color country-flag glyphs; it renders the
  two regional-indicator letters (e.g. "DE") as a *monochrome* glyph. `EmojiImageRenderer.Render` takes
  a `foreground` color for these fallback glyphs (true color emoji ignore it); `EmojiPresenter` passes
  the inherited `TextElement.Foreground` so flags match surrounding text instead of vanishing.
- **Editable fields** use a new `EmojiCapableFontFamily` resource (`Segoe UI Variable, …, Segoe UI Emoji`)
  so Latin text keeps the UI font and emoji fall back to monochrome instead of tofu.
- **Known tradeoff:** emoji rendered as `InlineUIContainer` images are excluded from WPF text-selection
  copy (`TextRange.Text`). The toolbar **Copy** button is unaffected (it copies the raw message source),
  so only manual in-document selection / "Add to PII" selection drops the emoji glyph.
