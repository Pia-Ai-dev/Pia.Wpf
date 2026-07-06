# Memory Vault Visualization — "Vault at a glance" (2026-07-06)

## Goal

Hand-rolled "Vault Overview" in the Memory screen right pane (`MemoryView.xaml` Grid.Row=3,
Grid.Column=2): when no memory is selected and the vault is non-empty, show a composition-by-category
visualization (one proportional segmented bar + legend) instead of the plain "select a memory"
placeholder. One tight control, no dashboard sprawl, no charting NuGet.

## Right-pane state machine

Three mutually exclusive states, all in `MemoryView.xaml` column 2:

| Control | Visible when | Binding |
|---|---|---|
| `PiaMemoryInspector` | `SelectedMemory != null` | UNCHANGED (existing `NullToVisibilityConverter` on its own DataContext) |
| `PiaVaultOverview` (NEW) | `SelectedMemory == null && TotalObjectCount > 0` | `IsVaultOverviewVisible` via `BooleanToVisibilityConverter` |
| `PiaInspectorEmptyState` | `SelectedMemory == null && TotalObjectCount == 0` | `IsInspectorPlaceholderVisible` via `BooleanToVisibilityConverter` (replaces the current `InverseNullToVisibilityConverter` binding) |

## Data flow — full-vault composition, stable during search

Computed in `MemoryViewModel.LoadMemoriesAsync` from **`snapshot.Items`** (the unfiltered
`ListMemoriesAsync` result), NOT from `MemoryGroups` — during a search `MemoryGroups` is filtered to
recall hits, which would make the chart disagree with the header total mid-search.

Algorithm (mirrors `CountDisplayable` / `BuildGroups` semantics):

1. Filter `snapshot.Items` to `DisplayableTypes` (the existing case-insensitive canonical set built
   from `VaultIndexService.CanonicalGroups`). `totalDisplayable` = that count — identical to what
   feeds `TotalObjectCount`, so bar and header always agree.
2. Group case-insensitively by type; walk `VaultIndexService.CanonicalGroups` in order
   (`personal_profile`, `contact_list`, `preference`, `note`, `project`, `topic`); skip types with
   count 0.
3. `Fraction = count / (double)totalDisplayable`; if `totalDisplayable == 0`, emit an empty
   composition (divide-by-zero guard).
4. Rebuild `VaultComposition` (clear + add) on each load.

No logging of memory titles/bodies anywhere in this path; counts are non-sensitive.

## View-model surface (MemoryViewModel.cs)

```csharp
public record VaultCategorySegment(string Type, string DisplayName, int Count, double Fraction);
```

- `[ObservableProperty] ObservableCollection<VaultCategorySegment> _vaultComposition = new();`
  (rebuilt each `LoadMemoriesAsync`).
- `public bool IsVaultOverviewVisible => SelectedMemory is null && TotalObjectCount > 0;`
- `public bool IsInspectorPlaceholderVisible => SelectedMemory is null && TotalObjectCount == 0;`
- Add `[NotifyPropertyChangedFor(nameof(IsVaultOverviewVisible))]` and
  `[NotifyPropertyChangedFor(nameof(IsInspectorPlaceholderVisible))]` to BOTH the `_selectedMemory`
  and `_totalObjectCount` observable fields, so the derived bools notify on either change.

## Color palette — 6 distinct colors, no new theme brushes

The existing `Type*Brush` set collides for two canonical types (`contact_list`→Profile,
`topic`→Note via `MemoryObjectTypes.ToKind`), so a NEW converter maps the canonical **type string**
directly to existing resource keys, reusing the unused Skill/Context brushes:

| Canonical type | Resource keys |
|---|---|
| `personal_profile` | `TypeProfile{Bg,Fg}Brush` |
| `contact_list` | `TypeSkill{Bg,Fg}Brush` |
| `preference` | `TypePreference{Bg,Fg}Brush` |
| `note` | `TypeNote{Bg,Fg}Brush` |
| `project` | `TypeProject{Bg,Fg}Brush` |
| `topic` | `TypeContext{Bg,Fg}Brush` |
| fallback | `SurfaceMutedBrush` (Bg) / `TextMutedBrush` (Fg) |

`VaultCategoryColorConverter` (namespace `Pia.Converters`) is modeled on
`MemoryTypeToBrushConverter`: `IValueConverter` with a `Kind { Background, Foreground }` property,
resolving via `Application.Current.TryFindResource` (case-insensitive type match, ordinal-ignore-case
switch). Registered in `App.xaml` next to `MemoryTypeToBrushBg/Fg` (lines ~57-58):

```xml
<converters:VaultCategoryColorConverter x:Key="VaultCategoryBrushBg" Kind="Background" />
<converters:VaultCategoryColorConverter x:Key="VaultCategoryBrushFg" Kind="Foreground" />
```

## Visual — bar + legend (chosen rendering approach)

**Chosen idiom: ItemsControl over `VaultComposition` with a horizontal `StackPanel` panel; each
segment is a `Rectangle` whose `Width` comes from a new `FractionToWidthConverter`
(`IMultiValueConverter`: `values[0]` = `Fraction`, `values[1]` = bar-host `ActualWidth`; returns
`Max(0, fraction * width)`, guards NaN/Infinity).** Rationale vs. star-weighted Grid columns:
dynamic star columns need code-behind column generation, while the ItemsControl idiom is pure XAML
and resize-safe (`ActualWidth` binding re-fires on size change). Floating-point width sums are exact
to sub-pixel (fractions sum to 1.0 by construction), so no visible gap/overflow; the single-category
case is simply one full-width segment.

Bar structure in `PiaVaultOverview.xaml`:

- `Grid x:Name="BarHost"` `Height="16"` `HorizontalAlignment="Stretch"` containing the ItemsControl.
- Rounded corners: set `BarHost.Clip` to a `RectangleGeometry` (`RadiusX/Y = 8`) updated in a
  `SizeChanged` handler in `PiaVaultOverview.xaml.cs` (purely visual clipping — the only code-behind;
  `ClipToBounds` does not respect corner radii and a plain rounded `Border` does not clip children).
- Segment `Rectangle.Fill = {Binding Type, Converter={StaticResource VaultCategoryBrushBg}}`.

Legend below the bar: a second ItemsControl over `VaultComposition`; each row is a `Grid` with
`SharedSizeGroup`-free fixed columns: 10x10 rounded swatch `Rectangle` (Bg brush) | `DisplayName`
(`Width="*"`, `TextTrimming="CharacterEllipsis"`) | `Count` (right-aligned, min-width column) |
percentage (`{Binding Fraction, StringFormat=P0}`, right-aligned).

Card chrome: root `Border Style="{StaticResource PiaCardStyle}"` (like `PiaInspectorEmptyState`),
containing title (`{loc:Str Memory_VaultOverview_Title}`, 14 SemiBold, `TextDefaultBrush`) +
subtitle (`{loc:Str Memory_VaultOverview_Subtitle}`, 12, `TextMutedBrush`) + bar + legend. Do NOT
re-print the header total/storage.

`PiaVaultOverview` is a `UserControl` (`x:Class="Pia.Controls.Memory.PiaVaultOverview"`) that
inherits the `MemoryViewModel` DataContext (no local DataContext), binding `VaultComposition`.

## Localization

Add to ALL THREE resx files (identical key sets — parity tests fail otherwise; do NOT touch
`ViewStrings.Designer.cs`), as single-line `<data>` entries next to the existing `Memory_*` block
(~line 289):

| Key | en (`ViewStrings.resx`) | de (`.de.resx`) | fr (`.fr.resx`) |
|---|---|---|---|
| `Memory_VaultOverview_Title` | Vault at a glance | Der Speicher auf einen Blick | Aperçu du coffre |
| `Memory_VaultOverview_Subtitle` | Composition by category | Zusammensetzung nach Kategorie | Composition par catégorie |

Format: `<data name="KEY" xml:space="preserve"><value>TEXT</value></data>`.

## File-by-file change list (build order)

1. **EDIT `src/Pia.Wpf/ViewModels/MemoryViewModel.cs`** — add `VaultCategorySegment` record
   (file-scope, next to `MemoryGroupViewModel`), `_vaultComposition` observable, the two derived
   bools, `[NotifyPropertyChangedFor]` on `_selectedMemory`/`_totalObjectCount`, and a
   `BuildComposition(IReadOnlyList<VaultMemoryItem>)` helper called from `LoadMemoriesAsync` on
   `snapshot.Items` (also refresh after delete, where `TotalObjectCount` is already recomputed).
2. **EDIT `tests/Pia.Wpf.Tests/ViewModels/MemoryViewModelTests.cs`** — composition tests (below);
   VM-only, so tests can run before the XAML exists.
3. **NEW `src/Pia.Wpf/Converters/VaultCategoryColorConverter.cs`** — namespace `Pia.Converters`.
4. **NEW `src/Pia.Wpf/Converters/FractionToWidthConverter.cs`** — `IMultiValueConverter`.
5. **EDIT `src/Pia.Wpf/App.xaml`** — register `VaultCategoryBrushBg`, `VaultCategoryBrushFg`,
   `FractionToWidthConverter`.
6. **EDIT resx ×3** — `src/Pia.Wpf/Resources/Strings/ViewStrings.resx`, `.de.resx`, `.fr.resx`.
7. **NEW `src/Pia.Wpf/Controls/Memory/PiaVaultOverview.xaml` + `.xaml.cs`** — card, bar, legend;
   code-behind = `InitializeComponent` + `SizeChanged` clip update only.
8. **EDIT `src/Pia.Wpf/Views/MemoryView.xaml`** — swap `PiaInspectorEmptyState` visibility to
   `IsInspectorPlaceholderVisible`, add `mem:PiaVaultOverview` bound to `IsVaultOverviewVisible`.

House rules: 4-space C#, 2-space XAML, root namespace `Pia`, no new NuGet packages, new files
normalized to CRLF before committing.

## Test plan

Extend `MemoryViewModelTests.cs` (existing NSubstitute/xunit.v3 `Create(items, bytes)` harness):

1. `LoadMemories_builds_full_vault_composition_in_canonical_order` — mixed-type items; assert
   segment order matches `CanonicalGroups`, per-type `Count`/`DisplayName` correct, fractions sum to
   1.0 within `1e-9` tolerance, zero-count types absent.
2. `Empty_vault_shows_placeholder_not_overview` — no items ⇒ `VaultComposition` empty,
   `IsVaultOverviewVisible == false`, `IsInspectorPlaceholderVisible == true`.
3. `Selecting_a_memory_hides_the_overview_and_raises_change` — non-empty + no selection ⇒ overview
   true / placeholder false; set `SelectedMemory` ⇒ overview false, and a `PropertyChanged`
   subscription observed `nameof(IsVaultOverviewVisible)`.
4. (Search stability, cheap add) — with a `RecallAsync` stub filtering to one hit, composition still
   reflects the full snapshot.

Gate before every commit: `dotnet build` clean + `dotnet test` (MTP runner) with
`--filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"` — no failures outside that
known-live-network namespace.

## Risks

- **Sub-pixel seams in the bar**: `ActualWidth * Fraction` widths can land off pixel grid; mitigate
  with `UseLayoutRounding="False"` + `SnapsToDevicePixels="False"` on the bar host so segments abut
  exactly. Verified visually in light + dark themes.
- **`ActualWidth` is 0 on first measure**: converter guards return 0; a `SizeChanged`-driven binding
  refresh happens automatically when layout completes.
- **Brush reuse (`TypeSkill*`/`TypeContext*`) could later be re-claimed** by chip kinds, silently
  pairing colors again; acceptable for a first shot, noted for a future dedicated palette.
- **Delete path**: after `ExecuteDeleteMemory` the composition must be rebuilt from the fresh
  snapshot (same place `TotalObjectCount` is refreshed) or the bar goes stale.
- **resx parity tests** fail if a key lands in only some of the three files — add all three in one
  edit pass.
- **CRLF**: Write-tool output is LF; new files must be normalized to CRLF or raw-string/byte tests
  and repo convention drift.
