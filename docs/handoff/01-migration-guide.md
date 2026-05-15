# Pia.Wpf — Visual Refresh (Phase 1: Base)

> A step-by-step guide. Stack: .NET 10 · WPF UI · Markdig · ColorCode.Core.
> Strategy: **zero structural changes** — no new views, no view-model edits. Only ResourceDictionaries, Styles, ControlTemplates and a handful of `Border` rewrites.

> **Order matters.** Do steps 1–3 first and ship — every screen in the app will already look ~80 % modernized. Steps 4–6 are per-control polish, do them as you encounter the components. Step 7 wires dark mode.

---

## Step 1 — Add the design-token ResourceDictionary

Create `Resources/Theme/PiaTokens.Light.xaml` and `Resources/Theme/PiaTokens.Dark.xaml`. These are
the _only_ place colors live — every other XAML file references them via `{DynamicResource …}`.

> The full file contents are in `tokens/PiaTokens.Light.xaml` and `tokens/PiaTokens.Dark.xaml`.
> Below is the shape so you understand what's exported.

```xml
<!-- PiaTokens.Light.xaml -->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- Brand -->
    <Color x:Key="PiaInkColor">#FF1E2A4A</Color>
    <Color x:Key="PiaAccentColor">#FF2563EB</Color>
    <Color x:Key="PiaAccentHoverColor">#FF1D4ED8</Color>
    <Color x:Key="PiaAccentSoftColor">#FFE7EFFE</Color>

    <!-- Surfaces -->
    <Color x:Key="BgCanvasColor">#FFF4F6FA</Color>
    <Color x:Key="SurfaceColor">#FFFFFFFF</Color>
    <Color x:Key="SurfaceMutedColor">#FFEEF1F6</Color>
    <Color x:Key="BorderColor">#FFE3E7EE</Color>
    <Color x:Key="BorderStrongColor">#FFD2D8E2</Color>

    <!-- Text -->
    <Color x:Key="TextDefaultColor">#FF0F1729</Color>
    <Color x:Key="TextMutedColor">#FF525E73</Color>
    <Color x:Key="TextSubtleColor">#FF8893A8</Color>

    <!-- Status -->
    <Color x:Key="SuccessColor">#FF0F8662</Color>
    <Color x:Key="SuccessSoftColor">#FFE6F4EF</Color>
    <Color x:Key="DangerColor">#FFE11D48</Color>

    <!-- Brushes — always reference these, not Colors directly -->
    <SolidColorBrush x:Key="PiaInkBrush"         Color="{StaticResource PiaInkColor}"/>
    <SolidColorBrush x:Key="PiaAccentBrush"      Color="{StaticResource PiaAccentColor}"/>
    <!-- … etc — see tokens/PiaTokens.Light.xaml -->

    <!-- Sizing -->
    <CornerRadius x:Key="SmallRadius">6</CornerRadius>
    <CornerRadius x:Key="MediumRadius">9</CornerRadius>
    <CornerRadius x:Key="LargeRadius">12</CornerRadius>
    <CornerRadius x:Key="BubbleRadius">14</CornerRadius>

    <!-- Override WPF-UI system tokens -->
    <SolidColorBrush x:Key="SystemAccentColorPrimaryBrush"   Color="{StaticResource PiaAccentColor}"/>
    <SolidColorBrush x:Key="SystemAccentColorSecondaryBrush" Color="{StaticResource PiaAccentHoverColor}"/>
    <SolidColorBrush x:Key="AccentFillColorDefaultBrush"     Color="{StaticResource PiaAccentColor}"/>
    <SolidColorBrush x:Key="AccentFillColorSecondaryBrush"   Color="{StaticResource PiaAccentHoverColor}"/>
    <SolidColorBrush x:Key="ApplicationBackgroundBrush"      Color="{StaticResource BgCanvasColor}"/>
</ResourceDictionary>
```

Then merge them in `App.xaml`:

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <!-- WPF UI's own theme bundle stays first -->
            <ui:ThemesDictionary Theme="Light" />
            <ui:ControlsDictionary />
            <!-- Then OUR tokens on top so they win -->
            <ResourceDictionary Source="Resources/Theme/PiaTokens.Light.xaml" />
            <ResourceDictionary Source="Resources/Theme/PiaStyles.xaml" />
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

> ⚠ WPF-UI's `ApplicationThemeManager.Apply()` reloads the theme bundle. Hook it (see Step 7) and
> re-merge our tokens after every theme switch, otherwise dark-mode toggling reverts to stock
> Fluent colors.

---

## Step 2 — Re-tint the window chrome & sidebar

Biggest visual win for zero structural work. Open `MainWindow.xaml`:

```xml
<ui:FluentWindow
    Background="{DynamicResource BgCanvasBrush}"
    ExtendsContentIntoTitleBar="True"
    WindowBackdropType="Mica"  <!-- or "Acrylic" -->
    ...>
    <ui:TitleBar Title="Pia"
                 Icon="/Assets/pia.ico"
                 Foreground="{DynamicResource TextDefaultBrush}" />
```

For the sidebar (WPF-UI's `NavigationView` in compact mode), override the item container style:

```xml
<!-- PiaStyles.xaml -->
<Style x:Key="PiaNavItemStyle" TargetType="ui:NavigationViewItem">
    <Setter Property="Margin"  Value="4,2"/>
    <Setter Property="Padding" Value="8"/>
    <Setter Property="MinHeight" Value="36"/>
    <Setter Property="Foreground" Value="{DynamicResource TextMutedBrush}"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="ui:NavigationViewItem">
                <Border x:Name="Root"
                        Background="Transparent"
                        CornerRadius="9"
                        Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Center"
                                      VerticalAlignment="Center"
                                      Content="{TemplateBinding Icon}"/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Root" Property="Background"
                                Value="{DynamicResource SurfaceMutedBrush}"/>
                    </Trigger>
                    <Trigger Property="IsActive" Value="True">
                        <Setter TargetName="Root" Property="Background"
                                Value="{DynamicResource PiaAccentSoftBrush}"/>
                        <Setter Property="Foreground"
                                Value="{DynamicResource PiaAccentBrush}"/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

---

## Step 3 — Restyle the chat bubbles

Bubbles are `Border` elements inside an `ItemsControl` `DataTemplate`. Add two named styles and
reference them from the existing template — no DataTemplate restructure needed.

```xml
<!-- Pia (assistant) bubble: no background, just typography. -->
<Style x:Key="AssistantBubbleStyle" TargetType="Border">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="Padding"    Value="0"/>
    <Setter Property="MaxWidth"   Value="580"/>
    <Setter Property="HorizontalAlignment" Value="Left"/>
</Style>

<!-- User bubble: solid accent, asymmetric corner for visual rhythm -->
<Style x:Key="UserBubbleStyle" TargetType="Border">
    <Setter Property="Background"   Value="{DynamicResource PiaAccentBrush}"/>
    <Setter Property="CornerRadius" Value="14,14,4,14"/>
    <Setter Property="Padding"      Value="14,10"/>
    <Setter Property="MaxWidth"     Value="520"/>
    <Setter Property="HorizontalAlignment" Value="Right"/>
    <Setter Property="Effect">
        <Setter.Value>
            <DropShadowEffect Color="#1E2A4A" Opacity="0.10"
                              BlurRadius="8" ShadowDepth="1"/>
        </Setter.Value>
    </Setter>
</Style>

<Style x:Key="UserBubbleTextStyle" TargetType="TextBlock">
    <Setter Property="Foreground"   Value="White"/>
    <Setter Property="FontSize"     Value="14"/>
    <Setter Property="TextWrapping" Value="Wrap"/>
    <Setter Property="LineHeight"   Value="21"/>
</Style>
```

For the small Pia persona avatar next to assistant messages: a `Border` with a `LinearGradientBrush`
background and a centered `"P"` `TextBlock`. Define it once as a named `PiaAvatarStyle` and reuse via
`<Border Style="{StaticResource PiaAvatarStyle}"/>` (see `tokens/PiaStyles.xaml`).

---

## Step 4 — The Action Card

Treat it as a small reusable `UserControl` — `PiaActionCard.xaml`. Same DPs as today (Title,
Subtitle, AcceptCommand, RejectCommand), so view-models don't change.

```xml
<Border CornerRadius="12"
        Background="{DynamicResource SurfaceBrush}"
        BorderBrush="{DynamicResource BorderBrush_}"
        BorderThickness="1"
        MaxWidth="420">
    <Border.Effect>
        <DropShadowEffect Color="#0F1729" Opacity="0.08"
                          BlurRadius="16" ShadowDepth="3"/>
    </Border.Effect>
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Stripe header -->
        <Border Grid.Row="0"
                Background="{DynamicResource SurfaceMutedBrush}"
                BorderBrush="{DynamicResource BorderBrush_}"
                BorderThickness="0,0,0,1"
                Padding="14,10"
                CornerRadius="12,12,0,0">
            <StackPanel Orientation="Horizontal">
                <Border Width="22" Height="22" CornerRadius="6"
                        Background="{DynamicResource PiaAccentSoftBrush}">
                    <ui:SymbolIcon Symbol="Memory20"
                                   Foreground="{DynamicResource PiaAccentBrush}"
                                   FontSize="13"/>
                </Border>
                <TextBlock Text="ERINNERUNG AKTUALISIEREN"
                           Margin="8,0,0,0" VerticalAlignment="Center"
                           FontSize="11" FontWeight="SemiBold"
                           Foreground="{DynamicResource TextMutedBrush}"/>
            </StackPanel>
        </Border>

        <!-- Body -->
        <StackPanel Grid.Row="1" Margin="14,12,14,14">
            <TextBlock Text="{Binding Title}"
                       FontSize="14" FontWeight="SemiBold"
                       Foreground="{DynamicResource TextDefaultBrush}"/>
            <TextBlock Text="{Binding Subtitle}" Margin="0,2,0,10"
                       FontSize="12.5"
                       Foreground="{DynamicResource TextMutedBrush}"/>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                <ui:Button Content="Ablehnen"
                           Appearance="Secondary"
                           Margin="0,0,8,0"
                           Command="{Binding RejectCommand}"/>
                <ui:Button Content="Akzeptieren"
                           Appearance="Primary"
                           Command="{Binding AcceptCommand}"/>
            </StackPanel>
        </StackPanel>
    </Grid>
</Border>
```

---

## Step 5 — The input bar

Wrap the existing `TextBox` and its button row in a single `Border` with elevation. Stack input
above icons (two rows) so the bar reads as a single composer card.

```xml
<Border Margin="20,8,20,16"
        CornerRadius="14"
        Background="{DynamicResource SurfaceBrush}"
        BorderBrush="{DynamicResource BorderBrush_}"
        BorderThickness="1">
    <Border.Effect>
        <DropShadowEffect Color="#0F1729" Opacity="0.08"
                          BlurRadius="18" ShadowDepth="3"/>
    </Border.Effect>
    <StackPanel Margin="8">
        <ui:TextBox PlaceholderText="Nachricht eingeben…"
                    Text="{Binding InputText, UpdateSourceTrigger=PropertyChanged}"
                    BorderThickness="0" Padding="4,6"
                    Background="Transparent"/>
        <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
            <ui:Button Icon="{ui:SymbolIcon Symbol=Eraser20}"   Appearance="Transparent"/>
            <ui:Button Icon="{ui:SymbolIcon Symbol=Dismiss20}"  Appearance="Transparent"/>
            <Separator Width="1" Margin="6,4" Background="{DynamicResource BorderBrush_}"/>
            <ui:Button Icon="{ui:SymbolIcon Symbol=Speaker220}" Appearance="Transparent"/>
            <ui:Button Icon="{ui:SymbolIcon Symbol=Record20}"   Appearance="Transparent"/>
            <ui:Button Icon="{ui:SymbolIcon Symbol=Mic20}"      Appearance="Transparent"/>
            <ui:Button Content="Senden"
                       Icon="{ui:SymbolIcon Symbol=Send20}"
                       Appearance="Primary"
                       HorizontalAlignment="Right" Margin="auto,0,0,0"
                       Command="{Binding SendCommand}"/>
        </StackPanel>
    </StackPanel>
</Border>
```

---

## Step 6 — Markdig + ColorCode (basics)

This step is the bare minimum so existing Markdown content already looks correct. Phase 2
(`02-modern-pro-controls.md`) goes much further with reusable controls.

Add `PiaMarkdown.xaml` to style FlowDocument elements to match our tokens:

```xml
<Style TargetType="Paragraph">
    <Setter Property="Foreground" Value="{DynamicResource TextDefaultBrush}"/>
    <Setter Property="FontSize"   Value="14"/>
    <Setter Property="LineHeight" Value="22"/>
    <Setter Property="Margin"     Value="0,0,0,8"/>
</Style>
<Style TargetType="Run" x:Key="InlineCodeRun">
    <Setter Property="Background" Value="{DynamicResource SurfaceMutedBrush}"/>
    <Setter Property="FontFamily" Value="JetBrains Mono, Cascadia Code, Consolas"/>
    <Setter Property="FontSize"   Value="12.5"/>
</Style>
<!-- Code blocks: wrap with a Border + CornerRadius=10 BlockUIContainer -->
```

For ColorCode, switch the highlighter palette to harmonize with the dark code block surface
(`#0F1729`). Use the "VS Dark" preset as base; tweak keyword color to `#7AA3F5` (our dark-mode
accent) so it ties into the brand.

---

## Step 7 — Dark mode wiring

WPF-UI exposes `ApplicationThemeManager.Apply(ApplicationTheme.Dark)`. Hook it from the settings
page and, in the same callback, swap our token dictionary:

```csharp
public static void ApplyPiaTheme(ApplicationTheme theme)
{
    ApplicationThemeManager.Apply(theme);

    var src = theme == ApplicationTheme.Dark
        ? "Resources/Theme/PiaTokens.Dark.xaml"
        : "Resources/Theme/PiaTokens.Light.xaml";

    var dicts = Application.Current.Resources.MergedDictionaries;
    var existing = dicts.FirstOrDefault(d =>
        d.Source?.OriginalString.Contains("PiaTokens") == true);
    if (existing != null) dicts.Remove(existing);

    dicts.Add(new ResourceDictionary
    {
        Source = new Uri(src, UriKind.Relative)
    });
}
```

---

## Acceptance checklist (Phase 1)

- [ ] Window background is `#F4F6FA` (light) / `#0B1220` (dark).
- [ ] Selected sidebar item shows soft accent pill, no left vertical bar.
- [ ] User bubble corners are `14 14 4 14`, drop-shadow visible.
- [ ] Assistant messages render with the small "P" persona avatar.
- [ ] Action card has a header stripe with overline label + circular icon chip.
- [ ] Input bar is a single elevated card; Send is a labeled primary button.
- [ ] Status text uses pulsing dots, not italic.
- [ ] Dark mode toggles cleanly without losing token brushes.
- [ ] No view-model files were modified.

When all boxes are checked, **stop and ask for review** before moving to Phase 2
(`02-modern-pro-controls.md`).
