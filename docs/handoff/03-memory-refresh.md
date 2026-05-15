# Pia.Wpf — Memory View Refresh (Phase 3)

> **Prerequisite:** Phase 1 (`01-migration-guide.md`) is shipped — tokens, styles, theme switcher
> are in place. Phase 2 is optional for this work.
>
> Goal: bring the **Memory** screen up to the same modern bar as the chat view. Same constraint as
> the other phases: **no structural / view-model rewrites**. Only new `UserControl`s, `Style`s and
> a re-layout of `MemoryView.xaml`. View-model touches are additive properties only (Step 9).

---

## Design principle

The current Memory screen leaves both panes empty. The refresh treats the left pane as a
**filterable memory tree** (categories that open into typed rows) and the right pane as a **full
memory inspector** (tags, JSON value, lifecycle, usage timeline, related memories, embedding meta,
persistent action toolbar).

Every new visual unit is a self-contained `UserControl` you can reuse outside `MemoryView` (e.g.
the inspector also shows up in chat overlays). Memory categories and items continue to come from
the existing `MemoryViewModel.Categories` / `Items` collections — we just bind them differently.

---

## Control inventory

All under `Pia.Wpf/Controls/Memory/`.

| Control | File | Role |
| --- | --- | --- |
| **PiaMemoryHeader** | `PiaMemoryHeader.xaml` | Page title + sync chip + stats line ("26 objects · 38.4 KB · 1 stale") + action buttons (Export · Review Stale · Regenerate · **New memory**). |
| **PiaMemorySearchBar** | `PiaMemorySearchBar.xaml` | Single elevated search input with ⌘K kbd-hint, plus a segmented filter strip (All · Pinned · Stale · Today). |
| **PiaTypeChip** | `PiaTypeChip.xaml` | Pill that color-codes a memory's type: Profile / Preference / Project / Skill / Context / Note. Backed by a `MemoryType` → brush converter. |
| **PiaMemoryCategoryCard** | `PiaMemoryCategoryCard.xaml` | Collapsible card hosting one category. Header: chevron, title, count badge, optional "X stale" badge, "updated …" timestamp. Body: `ItemsControl` of `PiaMemoryRow`. |
| **PiaMemoryRow** | `PiaMemoryRow.xaml` | One memory in the tree: pin star, title, single-line value preview, `PiaTypeChip`, timestamp. Selectable — selected row uses accent-soft fill + left accent bar (same vocabulary as the chat sidebar). |
| **PiaMemoryInspector** | `PiaMemoryInspector.xaml` | Right-pane detail card. Composes the next four. |
| **PiaInspectorHeader** | `PiaInspectorHeader.xaml` | Type chip + mono memory id, large title, tag chips with `+ tag` affordance, icon toolbar (Pin · Edit · Copy · Archive · Delete). |
| **PiaJsonView** | `PiaJsonView.xaml` | Syntax-highlighted JSON in a sunk panel. Tab strip toggles `JSON / Raw / Diff`. Below it, an accent-soft "Source · inferred from chat …" strip with deep-link. |
| **PiaInspectorMeta** | `PiaInspectorMeta.xaml` | 260px right rail: **Lifecycle** (created/updated/accessed), **Access · 14d** sparkline, **Embedding** (model / dim / size), **Related** (top-N similar memories with score). |
| **PiaSparkline** | `PiaSparkline.xaml` | Tiny SVG-style area+line chart used by the meta rail. `Values` DP takes a `IReadOnlyList<double>`. |
| **PiaMemoryStatusBar** | `PiaMemoryStatusBar.xaml` | Footer strip: model dot + "Ready" + index info, right-aligned ghost "Regenerate embeddings" button. Replaces the current footer. |

The existing `MemoryView.xaml` becomes a thin shell that composes Header + SearchBar + a two-pane
`Grid` of CategoryCards (left) and Inspector (right) + StatusBar.

---

## Step 1 — Token additions

Add to **both** `PiaTokens.Light.xaml` and `PiaTokens.Dark.xaml`. These are all that's missing for
the Memory work; everything else reuses existing tokens.

```xml
<!-- Sunk panel — JSON viewer, related cards, embedded meta tiles -->
<Color x:Key="SurfaceSunkColor">#FFF7F9FC</Color>           <!-- light -->
<Color x:Key="SurfaceSunkColor">#FF0E1626</Color>           <!-- dark  -->
<SolidColorBrush x:Key="SurfaceSunkBrush" Color="{StaticResource SurfaceSunkColor}"/>

<!-- Memory-type palette: backgrounds + foregrounds for PiaTypeChip -->
<Color x:Key="TypeProfileBgColor">#FFF0E7FE</Color>
<Color x:Key="TypeProfileFgColor">#FF6D28D9</Color>

<Color x:Key="TypePreferenceBgColor">#FFE7EFFE</Color>      <!-- = AccentSoft -->
<Color x:Key="TypePreferenceFgColor">#FF2563EB</Color>      <!-- = Accent     -->

<Color x:Key="TypeProjectBgColor">#FFE6F4EF</Color>         <!-- = SuccessSoft -->
<Color x:Key="TypeProjectFgColor">#FF0F8662</Color>         <!-- = Success    -->

<Color x:Key="TypeSkillBgColor">#FFFEF3C7</Color>           <!-- = WarnSoft -->
<Color x:Key="TypeSkillFgColor">#FFB45309</Color>           <!-- = Warn     -->

<Color x:Key="TypeContextBgColor">#FFFFE4E6</Color>
<Color x:Key="TypeContextFgColor">#FFE11D48</Color>         <!-- = Danger -->

<Color x:Key="TypeNoteBgColor">#FFEEF1F6</Color>            <!-- = SurfaceMuted -->
<Color x:Key="TypeNoteFgColor">#FF525E73</Color>            <!-- = TextMuted    -->

<!-- emit SolidColorBrush x:Key="Type{Name}{Bg|Fg}Brush" for each pair -->

<!-- Sparkline + sparkline gradient -->
<SolidColorBrush x:Key="SparklineStrokeBrush" Color="{StaticResource PiaAccentColor}"/>
<LinearGradientBrush x:Key="SparklineFillBrush" StartPoint="0,0" EndPoint="0,1">
    <GradientStop Offset="0"   Color="{StaticResource PiaAccentColor}" Opacity="0.25"/>
    <GradientStop Offset="1"   Color="{StaticResource PiaAccentColor}" Opacity="0"/>
</LinearGradientBrush>
```

> The Dark theme's type palette uses translucent fills over `SurfaceColor` — see
> `tokens/PiaTokens.Dark.xaml` Patch B at the end of this document for the exact values.

---

## Step 2 — `PiaTypeChip`

A leaf control. Drives every chip in the tree, in the inspector header and in the "Related" list.

```xml
<!-- PiaTypeChip.xaml -->
<UserControl x:Class="Pia.Wpf.Controls.Memory.PiaTypeChip"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:conv="clr-namespace:Pia.Wpf.Converters">
    <UserControl.Resources>
        <conv:MemoryTypeToBrushConverter x:Key="TypeBg" Kind="Background"/>
        <conv:MemoryTypeToBrushConverter x:Key="TypeFg" Kind="Foreground"/>
        <conv:MemoryTypeToLabelConverter x:Key="TypeLabel"/>
    </UserControl.Resources>
    <Border Background="{Binding Type, RelativeSource={RelativeSource AncestorType=UserControl},
                                  Converter={StaticResource TypeBg}}"
            CornerRadius="4"
            Padding="7,1">
        <TextBlock Text="{Binding Type, RelativeSource={RelativeSource AncestorType=UserControl},
                                   Converter={StaticResource TypeLabel}}"
                   Foreground="{Binding Type, RelativeSource={RelativeSource AncestorType=UserControl},
                                       Converter={StaticResource TypeFg}}"
                   FontSize="10.5" FontWeight="SemiBold"
                   TextTransform="Uppercase"   <!-- via converter if your FW lacks this -->
                   />
    </Border>
</UserControl>
```

```csharp
public partial class PiaTypeChip : UserControl
{
    public static readonly DependencyProperty TypeProperty =
        DependencyProperty.Register(nameof(Type), typeof(MemoryType), typeof(PiaTypeChip));

    public MemoryType Type
    {
        get => (MemoryType)GetValue(TypeProperty);
        set => SetValue(TypeProperty, value);
    }
}
```

`MemoryTypeToBrushConverter` resolves `Type{Profile|Preference|…}{Bg|Fg}Brush` from
`Application.Current.Resources` at convert time — keeps the chip dark-mode-aware automatically.

---

## Step 3 — `PiaMemoryRow`

Layout matches the row in the design canvas exactly: indent slot (28 px), title + subtitle column,
type chip, right-aligned timestamp. Selection state is driven by an attached property so the row
participates in the parent `ListBox`'s selection.

```xml
<Border x:Name="Root"
        CornerRadius="8"
        Padding="28,8,14,8"
        Background="Transparent"
        Cursor="Hand">
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="Auto"/>
            <ColumnDefinition Width="Auto" SharedSizeGroup="RowTime"/>
        </Grid.ColumnDefinitions>

        <!-- Title + subtitle stack -->
        <StackPanel Grid.Column="0">
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="★"
                           Foreground="{DynamicResource WarnBrush}"
                           Visibility="{Binding Pinned, Converter={StaticResource BoolToVis}}"
                           FontSize="11" Margin="0,0,4,0"/>
                <TextBlock Text="{Binding Title}"
                           FontSize="13"
                           Foreground="{DynamicResource TextDefaultBrush}"
                           TextTrimming="CharacterEllipsis"/>
            </StackPanel>
            <TextBlock Text="{Binding ValuePreview}"
                       FontSize="11.5"
                       Margin="0,1,0,0"
                       Foreground="{DynamicResource TextSubtleBrush}"
                       TextTrimming="CharacterEllipsis"/>
        </StackPanel>

        <local:PiaTypeChip Grid.Column="1" Type="{Binding Type}" Margin="10,0"/>

        <TextBlock Grid.Column="2"
                   Text="{Binding Updated, StringFormat='{}{0:MM-dd}'}"
                   FontSize="10.5"
                   FontFamily="{DynamicResource PiaMonoFont}"
                   Foreground="{DynamicResource TextSubtleBrush}"
                   TextAlignment="Right" MinWidth="56" VerticalAlignment="Center"/>
    </Grid>

    <Border.Style>
        <Style TargetType="Border">
            <Style.Triggers>
                <DataTrigger Binding="{Binding IsSelected,
                                              RelativeSource={RelativeSource AncestorType=ListBoxItem}}"
                             Value="True">
                    <Setter Property="Background"  Value="{DynamicResource PiaAccentSoftBrush}"/>
                </DataTrigger>
                <Trigger Property="IsMouseOver" Value="True">
                    <Setter Property="Background" Value="{DynamicResource SurfaceMutedBrush}"/>
                </Trigger>
            </Style.Triggers>
        </Style>
    </Border.Style>
</Border>
```

> The 3-px left accent bar belongs to the **selected `ListBoxItem` template**, not the row — that
> way the bar tracks selection at the container level. Override the `ListBoxItem`
> `ControlTemplate` in `PiaStyles.xaml` to add a `Border.Width=3` decoration when `IsSelected`.

---

## Step 4 — `PiaMemoryCategoryCard`

```xml
<Border CornerRadius="{StaticResource LargeRadius}"
        Background="{DynamicResource SurfaceBrush}"
        BorderBrush="{DynamicResource BorderBrush_}"
        BorderThickness="1"
        Padding="0">
    <Border.Effect>
        <DropShadowEffect Color="#0F1729" Opacity="0.04"
                          BlurRadius="6" ShadowDepth="1"/>
    </Border.Effect>
    <StackPanel>
        <!-- Header -->
        <Button x:Name="HeaderToggle"
                Style="{StaticResource UnstyledButton}"
                Command="{Binding ToggleOpenCommand}">
            <Grid Margin="14,11" >
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="20"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>

                <ui:SymbolIcon Symbol="ChevronDown20" Grid.Column="0"
                               Foreground="{DynamicResource TextMutedBrush}"
                               RenderTransformOrigin="0.5,0.5">
                    <ui:SymbolIcon.RenderTransform>
                        <RotateTransform Angle="{Binding IsOpen,
                                              Converter={StaticResource BoolToAngle},
                                              ConverterParameter='0|-90'}"/>
                    </ui:SymbolIcon.RenderTransform>
                </ui:SymbolIcon>

                <StackPanel Grid.Column="1" Orientation="Horizontal">
                    <TextBlock Text="{Binding Title}"
                               FontSize="13" FontWeight="SemiBold"
                               Foreground="{DynamicResource TextDefaultBrush}"/>
                    <!-- count pill -->
                    <Border Background="{DynamicResource PiaAccentBrush}"
                            CornerRadius="10" Padding="6,1" Margin="8,0,0,0">
                        <TextBlock Text="{Binding Count}" Foreground="White"
                                   FontSize="10.5" FontWeight="Bold"/>
                    </Border>
                    <!-- stale pill -->
                    <Border Background="{DynamicResource WarnSoftBrush}"
                            CornerRadius="10" Padding="6,1" Margin="6,0,0,0"
                            Visibility="{Binding StaleCount, Converter={StaticResource IntToVis}}">
                        <TextBlock Foreground="{DynamicResource WarnBrush}"
                                   FontSize="10.5" FontWeight="SemiBold">
                            <Run Text="{Binding StaleCount}"/><Run Text=" stale"/>
                        </TextBlock>
                    </Border>
                </StackPanel>

                <TextBlock Grid.Column="2"
                           Foreground="{DynamicResource TextSubtleBrush}"
                           FontSize="11">
                    <Run Text="updated "/><Run Text="{Binding LastUpdated, StringFormat='{}{0:yyyy-MM-dd}'}"/>
                </TextBlock>
            </Grid>
        </Button>

        <!-- Separator + body -->
        <Border Height="1" Background="{DynamicResource BorderBrush_}"
                Visibility="{Binding IsOpen, Converter={StaticResource BoolToVis}}"/>

        <ListBox ItemsSource="{Binding Items}"
                 SelectedItem="{Binding DataContext.Selected,
                                       RelativeSource={RelativeSource AncestorType=local:MemoryView}}"
                 Background="Transparent"
                 BorderThickness="0"
                 Padding="0,4,0,4"
                 Visibility="{Binding IsOpen, Converter={StaticResource BoolToVis}}">
            <ListBox.ItemContainerStyle>
                <Style TargetType="ListBoxItem"
                       BasedOn="{StaticResource PiaMemoryRowItemStyle}"/>
            </ListBox.ItemContainerStyle>
            <ListBox.ItemTemplate>
                <DataTemplate>
                    <local:PiaMemoryRow/>
                </DataTemplate>
            </ListBox.ItemTemplate>
        </ListBox>
    </StackPanel>
</Border>
```

`PiaMemoryRowItemStyle` strips the default `ListBoxItem` chrome and adds the selection left-bar
as a `Border` at `HorizontalAlignment=Left, Width=3, Margin=18,8`.

---

## Step 5 — `PiaInspectorHeader`

```xml
<Border Padding="14,14,14,14"
        BorderBrush="{DynamicResource BorderBrush_}"
        BorderThickness="0,0,0,1">
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>

        <StackPanel Grid.Column="0">
            <!-- Chip + id -->
            <StackPanel Orientation="Horizontal" Margin="0,0,0,6">
                <local:PiaTypeChip Type="{Binding Type}"/>
                <TextBlock Text="{Binding ShortId}"
                           FontFamily="{DynamicResource PiaMonoFont}"
                           FontSize="11"
                           Foreground="{DynamicResource TextSubtleBrush}"
                           Margin="8,0,0,0" VerticalAlignment="Center"/>
            </StackPanel>

            <TextBlock Text="{Binding Title}"
                       FontSize="18" FontWeight="SemiBold"
                       Foreground="{DynamicResource TextDefaultBrush}"/>

            <!-- Tag chips -->
            <ItemsControl ItemsSource="{Binding Tags}" Margin="0,8,0,0">
                <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate><WrapPanel Orientation="Horizontal"/></ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Border Background="{DynamicResource SurfaceMutedBrush}"
                                CornerRadius="4" Padding="8,2" Margin="0,0,5,0">
                            <TextBlock FontSize="11.5"
                                       Foreground="{DynamicResource TextMutedBrush}">
                                <Run Text="#"/><Run Text="{Binding}"/>
                            </TextBlock>
                        </Border>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>

            <!-- + tag affordance — opens an inline editor -->
            <Button Content="+ tag"
                    Margin="0,5,0,0" HorizontalAlignment="Left"
                    Style="{StaticResource PiaDashedGhostButton}"
                    Command="{Binding AddTagCommand}"/>
        </StackPanel>

        <!-- Action toolbar -->
        <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Top">
            <ui:Button Icon="{ui:SymbolIcon Symbol=Pin20}"      Appearance="Transparent"
                       ToolTip="Pin"     Command="{Binding TogglePinCommand}"/>
            <ui:Button Icon="{ui:SymbolIcon Symbol=Edit20}"     Appearance="Transparent"
                       ToolTip="Edit"    Command="{Binding EditCommand}"/>
            <ui:Button Icon="{ui:SymbolIcon Symbol=Copy20}"     Appearance="Transparent"
                       ToolTip="Copy JSON"  Command="{Binding CopyJsonCommand}"/>
            <ui:Button Icon="{ui:SymbolIcon Symbol=Archive20}"  Appearance="Transparent"
                       ToolTip="Archive" Command="{Binding ArchiveCommand}"/>
            <ui:Button Icon="{ui:SymbolIcon Symbol=Delete20}"   Appearance="Transparent"
                       ToolTip="Delete"  Foreground="{DynamicResource DangerBrush}"
                       Command="{Binding DeleteCommand}"/>
        </StackPanel>
    </Grid>
</Border>
```

`PiaDashedGhostButton` is in `PiaStyles.xaml`: 1 px dashed border, `BorderStrongBrush`, text muted,
hover → solid border + accent text.

---

## Step 6 — `PiaJsonView`

JSON highlighting is solved in WPF with `AvalonEdit` (already a transitive dep of Markdig.Wpf via
ICSharpCode). Use it in **display mode** — `IsReadOnly=True`, `ShowLineNumbers=False`.

```xml
<Grid>
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="*"/>
        <RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>

    <!-- Tab strip + label -->
    <Grid Grid.Row="0" Margin="0,0,0,8">
        <TextBlock Text="VALUE · JSON" FontSize="11.5" FontWeight="SemiBold"
                   Foreground="{DynamicResource TextMutedBrush}"
                   VerticalAlignment="Center"/>
        <Border HorizontalAlignment="Right"
                Background="{DynamicResource SurfaceMutedBrush}"
                BorderBrush="{DynamicResource BorderBrush_}" BorderThickness="1"
                CornerRadius="7" Padding="2">
            <UniformGrid Rows="1">
                <RadioButton Content="JSON" Style="{StaticResource PiaSegmentedButton}"
                             IsChecked="{Binding ViewMode, ConverterParameter=JSON, …}"/>
                <RadioButton Content="Raw"  Style="{StaticResource PiaSegmentedButton}"/>
                <RadioButton Content="Diff" Style="{StaticResource PiaSegmentedButton}"/>
            </UniformGrid>
        </Border>
    </Grid>

    <!-- Body -->
    <Border Grid.Row="1"
            Background="{DynamicResource SurfaceSunkBrush}"
            BorderBrush="{DynamicResource BorderBrush_}" BorderThickness="1"
            CornerRadius="8" Padding="12,10">
        <avalon:TextEditor x:Name="JsonHost"
                           IsReadOnly="True"
                           ShowLineNumbers="False"
                           Background="Transparent"
                           FontFamily="{DynamicResource PiaMonoFont}"
                           FontSize="12.5"
                           Foreground="{DynamicResource TextDefaultBrush}"
                           SyntaxHighlighting="Json"/>
    </Border>

    <!-- Source strip -->
    <Border Grid.Row="2" Margin="0,12,0,0"
            Background="{DynamicResource PiaAccentSoftBrush}"
            CornerRadius="8" Padding="10,8">
        <StackPanel Orientation="Horizontal">
            <ui:SymbolIcon Symbol="ChatBubblesQuestion20"
                           Foreground="{DynamicResource PiaAccentBrush}"
                           Margin="0,1,8,0"/>
            <TextBlock VerticalAlignment="Center" FontSize="12">
                <Run Text="Source · " FontWeight="SemiBold"/>
                <Run Text="{Binding SourceLabel}"
                     Foreground="{DynamicResource TextMutedBrush}"/>
                <Hyperlink Foreground="{DynamicResource PiaAccentBrush}"
                           Command="{Binding OpenSourceCommand}">Open conversation →</Hyperlink>
            </TextBlock>
        </StackPanel>
    </Border>
</Grid>
```

Configure AvalonEdit's `Json.xshd` colors at app start to use our tokens:

```csharp
var def = HighlightingManager.Instance.GetDefinition("Json");
def.GetNamedColor("String").Foreground   = brush("#0F8662");
def.GetNamedColor("Number").Foreground   = brush("#B45309");
def.GetNamedColor("Keyword").Foreground  = brush("#6D28D9");
def.GetNamedColor("Property").Foreground = brush("#2563EB");
```

---

## Step 7 — `PiaInspectorMeta` (260 px right rail)

Vertical stack of four sections, each with a small-caps overline. The sparkline is its own
`PiaSparkline` control so it can be reused (e.g. in the future "Memory health" dashboard).

```xml
<!-- PiaSparkline.xaml — pseudocode, see file in repo for full implementation -->
<UserControl x:Class="Pia.Wpf.Controls.Memory.PiaSparkline">
    <Canvas x:Name="Surface" Height="36" ClipToBounds="True">
        <Path x:Name="AreaPath" Fill="{DynamicResource SparklineFillBrush}"/>
        <Path x:Name="LinePath" Stroke="{DynamicResource SparklineStrokeBrush}"
              StrokeThickness="1.6" StrokeLineJoin="Round"/>
    </Canvas>
</UserControl>
```

Code-behind subscribes to `SizeChanged` + `ValuesChanged` and rebuilds the two `Path.Data` strings.
Stay in code-behind (no MVVM) — it's a leaf drawing primitive.

```xml
<!-- PiaInspectorMeta.xaml -->
<StackPanel Width="260" Margin="14,14,16,14">
    <!-- Lifecycle -->
    <local:MetaSection Title="LIFECYCLE">
        <Grid local:MetaSection.Body="True">
            <!-- 80px label col + value col, 4px row gap -->
            ...
        </Grid>
    </local:MetaSection>

    <!-- Access · 14d -->
    <local:MetaSection Title="ACCESS · 14D">
        <local:MetaSection.HeaderRight>
            <TextBlock Text="{Binding AccessCount}" FontWeight="SemiBold"/>
        </local:MetaSection.HeaderRight>
        <local:PiaSparkline Values="{Binding AccessTimeline}"/>
    </local:MetaSection>

    <!-- Embedding -->
    <local:MetaSection Title="EMBEDDING">…</local:MetaSection>

    <!-- Related -->
    <local:MetaSection Title="RELATED">
        <ItemsControl ItemsSource="{Binding Related}">
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <Border Background="{DynamicResource SurfaceSunkBrush}"
                            BorderBrush="{DynamicResource BorderBrush_}" BorderThickness="1"
                            CornerRadius="7" Padding="8,6" Margin="0,0,0,6">
                        <StackPanel>
                            <TextBlock Text="{Binding Title}" FontSize="12" FontWeight="Medium"/>
                            <StackPanel Orientation="Horizontal" Margin="0,2,0,0">
                                <local:PiaTypeChip Type="{Binding Type}"/>
                                <TextBlock Foreground="{DynamicResource TextSubtleBrush}"
                                           FontSize="10.5" Margin="6,0,0,0"
                                           FontFamily="{DynamicResource PiaMonoFont}">
                                    <Run Text="sim "/><Run Text="{Binding Score, StringFormat='{}{0:0.00}'}"/>
                                </TextBlock>
                            </StackPanel>
                        </StackPanel>
                    </Border>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </local:MetaSection>
</StackPanel>
```

`MetaSection` is a tiny `ContentControl` that draws the small-caps overline + optional right-aligned
header content (e.g. the access count), then renders its `Content`. Keep it inside this file —
don't promote it to a global control.

---

## Step 8 — Re-laying out `MemoryView.xaml`

The new top-level structure. Everything below the title bar/sidebar (which already comes from
Phase 1's `MainWindow.xaml`) is replaced.

```xml
<Grid Background="{DynamicResource BgCanvasBrush}">
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>   <!-- Header     -->
        <RowDefinition Height="Auto"/>   <!-- Search bar -->
        <RowDefinition Height="*"/>      <!-- Two-pane   -->
        <RowDefinition Height="40"/>     <!-- Status bar -->
    </Grid.RowDefinitions>

    <local:PiaMemoryHeader     Grid.Row="0" Margin="24,4,24,14"
                               DataContext="{Binding}"/>
    <local:PiaMemorySearchBar  Grid.Row="1" Margin="24,0,24,14"
                               Query="{Binding SearchQuery, Mode=TwoWay}"
                               ActiveFilter="{Binding ActiveFilter, Mode=TwoWay}"/>

    <Grid Grid.Row="2" Margin="24,0,24,14">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="420"/>
            <ColumnDefinition Width="14"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>

        <!-- Left: category list. ItemsControl, not ListBox — selection lives in the rows. -->
        <ScrollViewer Grid.Column="0" VerticalScrollBarVisibility="Auto">
            <ItemsControl ItemsSource="{Binding Categories}">
                <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate>
                        <StackPanel Orientation="Vertical"/>
                    </ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <local:PiaMemoryCategoryCard Margin="0,0,0,10"/>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </ScrollViewer>

        <!-- Right: inspector. Visible only when Selected != null. -->
        <local:PiaMemoryInspector Grid.Column="2"
                                  DataContext="{Binding Selected}"
                                  Visibility="{Binding Selected,
                                              Converter={StaticResource NullToVis}}"/>

        <!-- Empty state -->
        <local:PiaInspectorEmptyState Grid.Column="2"
                                      Visibility="{Binding Selected,
                                              Converter={StaticResource NullToVisInverse}}"/>
    </Grid>

    <local:PiaMemoryStatusBar Grid.Row="3"/>
</Grid>
```

Empty state: friendly "Select a memory" with a `ui:SymbolIcon` and a CTA to "Create new memory".
Keep it short — one paragraph, one button.

---

## Step 9 — Additive view-model properties

The only `*ViewModel.cs` changes allowed in this phase. All on `MemoryViewModel` unless noted.

| Property | Type | Purpose |
| --- | --- | --- |
| `SearchQuery` | `string` | Two-way bound from `PiaMemorySearchBar`. Existing filter logic remains. |
| `ActiveFilter` | `MemoryFilter` enum (`All`/`Pinned`/`Stale`/`Today`) | Drives the filter strip + filtering pipeline. |
| `Selected` | `MemoryItemViewModel?` | Currently inspected row. Wire to `ListBox.SelectedItem` in the row template. |
| `TotalCount`, `TotalSizeBytes`, `EmbeddingDim`, `StaleCount` | derived | Header stats line. Already computable from `Categories`; just expose them. |
| `LastSyncedAt`, `LastIndexBuiltAt` | `DateTime` | Status chip + footer info. |

On `MemoryItemViewModel`:

| Property | Type | Purpose |
| --- | --- | --- |
| `ShortId` | `string` | First 4 + last 4 of GUID, `…` separator. Pure getter. |
| `Tags` | `ObservableCollection<string>` | Already exists or trivially added. |
| `ValuePreview` | `string` | Single-line preview of `Value` (e.g. JSON.stringify first scalar). |
| `AccessTimeline` | `IReadOnlyList<double>` | 14-element array, recent accesses per day. Populate from your existing access log; default to `Array.Empty<double>()`. |
| `AccessCount` | `int` | Sum of the above (or already tracked). |
| `Related` | `IReadOnlyList<RelatedMemory>` | Top-3 by embedding similarity. Where `RelatedMemory` = `(Title, Type, Score)`. |
| `SourceLabel`, `SourceConversationId` | `string` / `Guid?` | Powers the source strip + deep-link command. Nullable — hide the strip when null. |
| `IsPinned`, `IsStale` | `bool` | Already implied; expose explicitly so the tree row + chip respond. |

Commands on `MemoryItemViewModel` (or its parent VM, your existing pattern):
`TogglePinCommand`, `EditCommand`, `CopyJsonCommand`, `ArchiveCommand`, `DeleteCommand`,
`AddTagCommand`, `RemoveTagCommand`, `OpenSourceCommand`. **No business-logic changes** — wire each
to existing repository methods.

---

## Step 10 — Dark-mode patch

Append to `PiaTokens.Dark.xaml`:

```xml
<Color x:Key="SurfaceSunkColor">#FF0E1626</Color>

<!-- Type chips: translucent fills look right over the dark surface -->
<Color x:Key="TypeProfileBgColor">#3F6D28D9</Color>  <Color x:Key="TypeProfileFgColor">#FFC4B5FD</Color>
<Color x:Key="TypePreferenceBgColor">#3F2563EB</Color> <Color x:Key="TypePreferenceFgColor">#FF7AA3F5</Color>
<Color x:Key="TypeProjectBgColor">#3F0F8662</Color>    <Color x:Key="TypeProjectFgColor">#FF6EE7B7</Color>
<Color x:Key="TypeSkillBgColor">#3FB45309</Color>      <Color x:Key="TypeSkillFgColor">#FFFCD34D</Color>
<Color x:Key="TypeContextBgColor">#3FE11D48</Color>    <Color x:Key="TypeContextFgColor">#FFFCA5A5</Color>
<Color x:Key="TypeNoteBgColor">#FF1F2A40</Color>       <Color x:Key="TypeNoteFgColor">#FF9AA6BE</Color>
```

Verify: switch the theme switcher to Dark, every chip should remain legible (contrast ratio ≥ 4.5
against `SurfaceColor`).

---

## Acceptance checklist (Phase 3)

- [ ] `MemoryView` window background is `BgCanvasBrush`. Sidebar selected item moves from "Chat"
      to "Memory" on entry.
- [ ] Header shows the page title, a synced-status chip, and a live stats line
      ("X objects · Y KB · 1,536-dim embeddings · N stale").
- [ ] Search bar is one elevated card with a ⌘K kbd hint. The filter strip lives next to it.
- [ ] Each category renders as its own card. Counts are accent pills, stale counts are warn pills.
- [ ] Memory rows show pin star (if pinned), title, single-line value preview, type chip and a
      `MM-dd` mono timestamp.
- [ ] Selecting a row paints the row in `PiaAccentSoftBrush` and adds the 3-px accent bar on the
      `ListBoxItem`.
- [ ] Inspector header shows type chip + short memory id, large title, tag chips with a `+ tag`
      affordance, and the icon toolbar (Pin / Edit / Copy / Archive / Delete) on the right.
- [ ] JSON view renders AvalonEdit with our tokens; the JSON/Raw/Diff segmented control switches
      the body text.
- [ ] Source strip is accent-soft, links to the originating conversation, and is hidden when no
      source is known.
- [ ] Meta rail shows Lifecycle, Access · 14d sparkline (line + fill, our accent), Embedding,
      Related (each with type chip and similarity score).
- [ ] Footer shows "Embedding model · Ready · `text-embedding-3-small`" plus index info, ghost
      "Regenerate" button on the right.
- [ ] Theme switcher continues to flip light ↔ dark cleanly. AvalonEdit colors update.
- [ ] No `*ViewModel.cs` file is modified beyond the additive properties in Step 9.

When all boxes are checked, **stop and ask for review**.

---

## Things explicitly out of scope

These are tempting and we explicitly defer them. Push back if asked to do them in this phase.

- **Inline JSON editing.** Edit currently opens an existing dialog (or whatever you have); the
  inspector's Edit button just invokes `EditCommand`. Don't make AvalonEdit editable here.
- **Tag autocomplete.** `+ tag` opens a plain `TextBox`. A type-ahead from existing tags is a Phase 4
  enhancement.
- **Drag-to-reorder categories.** Categories follow the order returned by the repo.
- **Bulk operations.** No multi-select. The selection model is single-row.
- **Memory graph view.** The "Related" list is the only relational surface in Phase 3. A force-graph
  view is a separate explore-and-prove task.
