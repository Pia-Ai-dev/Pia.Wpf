# Pia.Wpf — Modern Pro (Phase 2: Long-form Chat Controls)

> **Prerequisite:** Phase 1 (`01-migration-guide.md`) is shipped and approved.
>
> Goal: render Pia answers as rich documents — headings, lists, code blocks, callouts, sources,
> response toolbar, follow-up suggestions. Same tokens, same look, but built from **reusable Controls**
> analogous to your existing `ActionCard` and `CodeBlock`.

---

## Design principle

Every semantic unit of a Pia answer is its own `UserControl` (or `Style`). Markdown enters the system
as **one string** on `AssistantMessageViewModel.Markdown`; everything else happens in the view.

No specialisation in the ViewModel layer. No new view-model classes (only additive properties on
the existing assistant message VM — see Step 10).

---

## Control inventory

All under `Pia.Wpf/Controls/Chat/`.

| Control | File | Role |
| --- | --- | --- |
| **PiaAssistantMessage** | `PiaAssistantMessage.xaml` | Wrapper: Avatar + meta-header + `PiaMarkdownView` + `PiaAnswerToolbar` + `PiaSuggestionChips`. One per assistant turn in the chat `ItemsControl`. |
| **PiaMarkdownView** | `PiaMarkdownView.xaml` | Converts a Markdown string → `FlowDocument` via Markdig. Styles paragraphs/lists/headings/inline-code using our tokens. Recognizes code fences and renders them as `PiaCodeBlock`. |
| **PiaCodeBlock** | `PiaCodeBlock.xaml` | Modernized code-block control. Header with language + filename + Copy button, ColorCode body, rounded corners. |
| **PiaCallout** | `PiaCallout.xaml` | Info / Warn / Success / Tip box with left accent bar and icon. `:::tip` Markdig extension renders to this. |
| **PiaSourceChip** | `PiaSourceChip.xaml` | Pill with numbered badge, source type (Profil / Vorrat / Web), meta text. Click opens details. |
| **PiaAnswerToolbar** | `PiaAnswerToolbar.xaml` | Footer toolbar per answer: Copy · Speak · Regenerate · 👍 👎. Right: token/model display. |
| **PiaSuggestionChips** | `PiaSuggestionChips.xaml` | `ItemsControl` with `WrapPanel`. Follow-up questions / quick actions as pills with a small accent dot. |

---

## Step 1 — Markdig pipeline

Markdig has an official WPF renderer (`Markdig.Wpf`). Use the default pipeline + a few extensions
and replace the code-block renderer with our own.

```csharp
// Pia.Wpf/Services/MarkdownService.cs
public sealed class MarkdownService
{
    private readonly MarkdownPipeline _pipeline;

    public MarkdownService()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseSupportedExtensions()       // tables, task lists, autolinks, emphasis_extras
            .UseEmojiAndSmiley()
            .UsePiaCallouts()               // custom :::tip / :::warn / :::info syntax → PiaCallout
            .UseSoftlineBreakAsHardlineBreak()
            .Build();
    }

    public FlowDocument Render(string markdown, IServiceProvider sp)
    {
        var doc = Markdown.Parse(markdown ?? "", _pipeline);
        var flow = new FlowDocument
        {
            FontFamily  = (FontFamily)Application.Current.FindResource("PiaBodyFont"),
            FontSize    = 14,
            PagePadding = new Thickness(0),
        };

        using var renderer = new WpfRenderer(flow);
        // Replace built-in fenced code renderer
        renderer.ObjectRenderers.RemoveAll(r => r is CodeBlockRenderer);
        renderer.ObjectRenderers.Add(new PiaCodeBlockRenderer(sp));
        // Replace heading renderer to use our typography
        renderer.ObjectRenderers.RemoveAll(r => r is HeadingRenderer);
        renderer.ObjectRenderers.Add(new PiaHeadingRenderer());

        _pipeline.Setup(renderer);
        renderer.Render(doc);
        return flow;
    }
}
```

> ⚠ `FlowDocument` is **not** an element tree XAML Styles reach automatically. Set brushes/fonts
> directly on the `Paragraph` / `Run` / `BlockUIContainer` instances in the renderer, or maintain a
> central `FlowDocumentStyle.xaml` and reference resources by key.

---

## Step 2 — PiaMarkdownView

A `RichTextBox` (read-only, focusable=false) hosting the FlowDocument. The outer chat list scrolls,
not the bubble.

```xml
<!-- PiaMarkdownView.xaml -->
<UserControl x:Class="Pia.Wpf.Controls.Chat.PiaMarkdownView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <RichTextBox x:Name="DocHost"
                 IsReadOnly="True"
                 IsDocumentEnabled="True"
                 BorderThickness="0"
                 Background="Transparent"
                 Padding="0"
                 Focusable="False"
                 Foreground="{DynamicResource TextDefaultBrush}"
                 FontSize="14">
        <RichTextBox.Resources>
            <!-- These styles propagate into the FlowDocument -->
            <Style TargetType="Paragraph">
                <Setter Property="Margin"     Value="0,0,0,10"/>
                <Setter Property="LineHeight" Value="22"/>
                <Setter Property="Foreground" Value="{DynamicResource TextDefaultBrush}"/>
            </Style>
            <Style TargetType="List">
                <Setter Property="Margin"  Value="0,0,0,10"/>
                <Setter Property="Padding" Value="0"/>
            </Style>
            <Style TargetType="ListItem">
                <Setter Property="Padding" Value="0,0,0,2"/>
            </Style>
        </RichTextBox.Resources>
    </RichTextBox>
</UserControl>
```

```csharp
// PiaMarkdownView.xaml.cs
public partial class PiaMarkdownView : UserControl
{
    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(PiaMarkdownView),
            new PropertyMetadata(null, OnMarkdownChanged));

    public string? Markdown
    {
        get => (string?)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (PiaMarkdownView)d;
        var md   = App.Services.GetRequiredService<MarkdownService>();
        view.DocHost.Document = md.Render((string?)e.NewValue, App.Services);
    }

    public PiaMarkdownView() => InitializeComponent();
}
```

---

## Step 3 — PiaCodeBlock + Renderer

The renderer turns `FencedCodeBlock` nodes into a `BlockUIContainer` hosting our `PiaCodeBlock`.
ColorCode.Wpf runs **inside** the UserControl.

```csharp
// PiaCodeBlockRenderer.cs
public sealed class PiaCodeBlockRenderer : WpfObjectRenderer<FencedCodeBlock>
{
    private readonly IServiceProvider _sp;
    public PiaCodeBlockRenderer(IServiceProvider sp) => _sp = sp;

    protected override void Write(WpfRenderer renderer, FencedCodeBlock node)
    {
        var code = string.Join("\n",
            node.Lines.Lines.Take(node.Lines.Count).Select(l => l.ToString()));

        var lang = node.Info ?? "";
        // Optional: "json · shopping-list.json" syntax
        string? fileName = null;
        if (node.Arguments?.Contains('·') == true)
        {
            var parts = node.Arguments.Split('·');
            lang     = parts[0].Trim();
            fileName = parts[1].Trim();
        }

        var block = new PiaCodeBlock
        {
            Code     = code,
            Language = lang,
            FileName = fileName,
        };
        renderer.WriteBlock(new BlockUIContainer(block)
        {
            Margin = new Thickness(0, 6, 0, 12)
        });
    }
}
```

```xml
<!-- PiaCodeBlock.xaml -->
<UserControl ...>
    <Border CornerRadius="10"
            Background="{DynamicResource CodeBlockBgBrush}"
            BorderBrush="{DynamicResource CodeBlockBgBrush}"
            BorderThickness="1">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
            </Grid.RowDefinitions>

            <!-- Header -->
            <Border Grid.Row="0" Padding="12,7"
                    BorderBrush="#1A2A3F" BorderThickness="0,0,0,1">
                <Grid>
                    <TextBlock VerticalAlignment="Center"
                               FontFamily="JetBrains Mono, Cascadia Code, Consolas"
                               FontSize="10.5" FontWeight="SemiBold"
                               Foreground="#6B7790">
                        <Run Text="{Binding Language, RelativeSource={RelativeSource AncestorType=UserControl}, Converter={StaticResource UpperCase}}"/>
                        <Run Text=" · "/>
                        <Run Text="{Binding FileName, RelativeSource={RelativeSource AncestorType=UserControl}}"/>
                    </TextBlock>
                    <ui:Button HorizontalAlignment="Right"
                               Appearance="Transparent"
                               Padding="8,2"
                               Icon="{ui:SymbolIcon Symbol=Copy20}"
                               Content="Kopieren"
                               Foreground="#E6EAF2"
                               Command="{Binding CopyCommand,
                                         RelativeSource={RelativeSource AncestorType=UserControl}}"/>
                </Grid>
            </Border>

            <!-- Body: ColorCode renders syntax-highlighted Inlines into this RichTextBox -->
            <RichTextBox Grid.Row="1"
                         x:Name="CodeHost"
                         IsReadOnly="True"
                         Background="Transparent"
                         BorderThickness="0"
                         Padding="14,12"
                         FontFamily="JetBrains Mono, Cascadia Code, Consolas"
                         FontSize="12"
                         Foreground="{DynamicResource CodeFgBrush}"/>
        </Grid>
    </Border>
</UserControl>
```

```csharp
// PiaCodeBlock.xaml.cs
public partial class PiaCodeBlock : UserControl
{
    public static readonly DependencyProperty CodeProperty =
        DependencyProperty.Register(nameof(Code), typeof(string), typeof(PiaCodeBlock),
            new PropertyMetadata(null, (d, _) => ((PiaCodeBlock)d).Refresh()));
    public static readonly DependencyProperty LanguageProperty =
        DependencyProperty.Register(nameof(Language), typeof(string), typeof(PiaCodeBlock),
            new PropertyMetadata(null, (d, _) => ((PiaCodeBlock)d).Refresh()));
    public static readonly DependencyProperty FileNameProperty =
        DependencyProperty.Register(nameof(FileName), typeof(string), typeof(PiaCodeBlock));

    public string?  Code     { get => (string?)GetValue(CodeProperty);     set => SetValue(CodeProperty, value); }
    public string?  Language { get => (string?)GetValue(LanguageProperty); set => SetValue(LanguageProperty, value); }
    public string?  FileName { get => (string?)GetValue(FileNameProperty); set => SetValue(FileNameProperty, value); }
    public ICommand CopyCommand { get; }

    private static readonly StyleDictionary PiaDark = new()
    {
        [ScopeName.Keyword]   = new Style(ScopeName.Keyword)   { Foreground = "#7AA3F5" },
        [ScopeName.String]    = new Style(ScopeName.String)    { Foreground = "#86E1A0" },
        [ScopeName.Comment]   = new Style(ScopeName.Comment)   { Foreground = "#6B7790" },
        [ScopeName.Number]    = new Style(ScopeName.Number)    { Foreground = "#F5A872" },
        [ScopeName.ClassName] = new Style(ScopeName.ClassName) { Foreground = "#E6EAF2" },
        [ScopeName.PlainText] = new Style(ScopeName.PlainText) { Foreground = "#E6EAF2" },
        [ScopeName.JsonKey]   = new Style(ScopeName.JsonKey)   { Foreground = "#7AA3F5" },
        [ScopeName.JsonValue] = new Style(ScopeName.JsonValue) { Foreground = "#86E1A0" },
    };

    private void Refresh()
    {
        if (string.IsNullOrEmpty(Code)) return;
        var fmt  = new RichTextBoxFormatter(PiaDark);
        var lang = Languages.FindById(Language ?? "") ?? Languages.PlainText;
        CodeHost.Document.Blocks.Clear();
        fmt.FormatRichTextBox(Code, lang, CodeHost);
    }
}
```

> ✓ With a single `StyleDictionary` (`PiaDark`), every code block in the app looks identical.
> For Light/Dark theme variants, expose two dictionaries and pick at construction time.

---

## Step 4 — PiaCallout (:::tip syntax)

A simple Markdig extension block: `:::tip\nText\n:::` → `PiaCallout`.
Four kinds: `tip`, `info`, `warn`, `success`.

```xml
<!-- PiaCallout.xaml -->
<UserControl ...>
    <Border CornerRadius="0,8,8,0"
            BorderThickness="3,0,0,0"
            BorderBrush="{Binding AccentBrush, RelativeSource={RelativeSource AncestorType=UserControl}}"
            Background="{Binding SoftBrush,    RelativeSource={RelativeSource AncestorType=UserControl}}"
            Padding="14,12">
        <StackPanel Orientation="Horizontal">
            <ui:SymbolIcon Symbol="{Binding IconSymbol, RelativeSource={RelativeSource AncestorType=UserControl}}"
                           FontSize="16" VerticalAlignment="Top"
                           Foreground="{Binding AccentBrush, RelativeSource={RelativeSource AncestorType=UserControl}}"/>
            <StackPanel Margin="10,0,0,0">
                <TextBlock Text="{Binding Title, RelativeSource={RelativeSource AncestorType=UserControl}}"
                           FontWeight="SemiBold" FontSize="13"
                           Foreground="{DynamicResource TextDefaultBrush}"
                           Visibility="{Binding Title, RelativeSource={RelativeSource AncestorType=UserControl},
                                        Converter={StaticResource NullToVisibility}}"/>
                <ContentPresenter Content="{Binding Body, RelativeSource={RelativeSource AncestorType=UserControl}}"/>
            </StackPanel>
        </StackPanel>
    </Border>
</UserControl>
```

`AccentBrush` / `SoftBrush` / `IconSymbol` are derived in code-behind from `Kind` — a dictionary
lookup over our token brushes.

---

## Step 5 — PiaSourceChip

```xml
<!-- PiaSourceChip.xaml -->
<UserControl ...>
    <Border CornerRadius="999"
            Background="{DynamicResource SurfaceBrush}"
            BorderBrush="{DynamicResource BorderBrush_}"
            BorderThickness="1"
            Padding="3,3,10,3">
        <StackPanel Orientation="Horizontal">
            <Border Width="20" Height="20" CornerRadius="10"
                    Background="{DynamicResource PiaAccentSoftBrush}">
                <TextBlock Text="{Binding Number, RelativeSource={RelativeSource AncestorType=UserControl}}"
                           HorizontalAlignment="Center" VerticalAlignment="Center"
                           FontSize="10.5" FontWeight="Bold"
                           Foreground="{DynamicResource PiaAccentBrush}"/>
            </Border>
            <TextBlock Margin="7,0,4,0" VerticalAlignment="Center"
                       FontSize="12" FontWeight="SemiBold"
                       Text="{Binding Source, RelativeSource={RelativeSource AncestorType=UserControl}}"/>
            <TextBlock VerticalAlignment="Center"
                       FontSize="12"
                       Foreground="{DynamicResource TextMutedBrush}"
                       Text="{Binding Meta, RelativeSource={RelativeSource AncestorType=UserControl}}"/>
        </StackPanel>
    </Border>
</UserControl>
```

Multiple chips go into an `ItemsControl` with a `WrapPanel` — no separate container control.

---

## Step 6 — PiaAnswerToolbar

```xml
<!-- PiaAnswerToolbar.xaml -->
<UserControl ...>
    <StackPanel Orientation="Horizontal">
        <ui:Button Appearance="Transparent" Padding="7,4"
                   Icon="{ui:SymbolIcon Symbol=Copy20}" Content="Kopieren"
                   Command="{Binding CopyCommand,       RelativeSource={RelativeSource AncestorType=UserControl}}"/>
        <ui:Button Appearance="Transparent" Padding="7,4"
                   Icon="{ui:SymbolIcon Symbol=Speaker220}" Content="Vorlesen"
                   Command="{Binding SpeakCommand,      RelativeSource={RelativeSource AncestorType=UserControl}}"/>
        <ui:Button Appearance="Transparent" Padding="7,4"
                   Icon="{ui:SymbolIcon Symbol=ArrowClockwise20}" Content="Neu generieren"
                   Command="{Binding RegenerateCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"/>
        <Separator Width="1" Margin="6,4" Background="{DynamicResource BorderBrush_}"/>
        <ui:Button Appearance="Transparent" Padding="6,4"
                   Icon="{ui:SymbolIcon Symbol=ThumbLike20}"
                   Command="{Binding RateCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                   CommandParameter="Up"/>
        <ui:Button Appearance="Transparent" Padding="6,4"
                   Icon="{ui:SymbolIcon Symbol=ThumbDislike20}"
                   Command="{Binding RateCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                   CommandParameter="Down"/>
        <TextBlock HorizontalAlignment="Right" VerticalAlignment="Center"
                   Margin="auto,0,4,0"
                   FontSize="11"
                   Foreground="{DynamicResource TextSubtleBrush}"
                   Text="{Binding Stats.Summary, RelativeSource={RelativeSource AncestorType=UserControl}}"/>
    </StackPanel>
</UserControl>
```

---

## Step 7 — PiaSuggestionChips

```xml
<!-- PiaSuggestionChips.xaml -->
<UserControl ...>
    <ItemsControl ItemsSource="{Binding ItemsSource,
                                RelativeSource={RelativeSource AncestorType=UserControl}}">
        <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate>
                <WrapPanel Orientation="Horizontal" ItemHeight="32"/>
            </ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
            <DataTemplate>
                <Button Margin="0,0,6,6" Padding="10,0"
                        Background="{DynamicResource SurfaceBrush}"
                        BorderBrush="{DynamicResource BorderBrush_}"
                        BorderThickness="1"
                        Foreground="{DynamicResource TextDefaultBrush}"
                        Command="{Binding DataContext.ItemClickCommand,
                                  RelativeSource={RelativeSource AncestorType=UserControl}}"
                        CommandParameter="{Binding}">
                    <Button.Template>
                        <ControlTemplate TargetType="Button">
                            <Border CornerRadius="999"
                                    Background="{TemplateBinding Background}"
                                    BorderBrush="{TemplateBinding BorderBrush}"
                                    BorderThickness="{TemplateBinding BorderThickness}"
                                    Padding="{TemplateBinding Padding}">
                                <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                                    <Ellipse Width="5" Height="5" Margin="0,0,6,0"
                                             Fill="{DynamicResource PiaAccentBrush}"/>
                                    <ContentPresenter VerticalAlignment="Center"/>
                                </StackPanel>
                            </Border>
                        </ControlTemplate>
                    </Button.Template>
                </Button>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
</UserControl>
```

---

## Step 8 — PiaAssistantMessage (composer)

This is the **only** component the chat `ItemsControl`'s `DataTemplate` renders for assistant turns.
It assembles everything and binds to `AssistantMessageViewModel`.

```xml
<!-- PiaAssistantMessage.xaml -->
<UserControl ...>
    <Grid Margin="0,0,0,18">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>

        <!-- Avatar (Style: PiaAvatarStyle, defined in PiaStyles.xaml) -->
        <Border Grid.Column="0" Style="{StaticResource PiaAvatarStyle}"
                Width="28" Height="28" Margin="0,2,12,0"/>

        <StackPanel Grid.Column="1" MaxWidth="640" HorizontalAlignment="Left">
            <!-- Meta strip -->
            <StackPanel Orientation="Horizontal" Margin="0,0,0,6">
                <TextBlock Text="Pia" FontSize="12.5" FontWeight="SemiBold"
                           Foreground="{DynamicResource TextDefaultBrush}"/>
                <TextBlock Text="{Binding Meta.Timing}" Margin="6,0,0,0"
                           FontSize="11" Foreground="{DynamicResource TextSubtleBrush}"/>
                <Border Margin="8,0,0,0" Padding="6,1" CornerRadius="5"
                        Background="{DynamicResource SuccessSoftBrush}"
                        Visibility="{Binding Meta.ProfileLabel,
                                     Converter={StaticResource NullToVisibility}}">
                    <TextBlock Text="{Binding Meta.ProfileLabel}"
                               FontSize="11" FontWeight="SemiBold"
                               Foreground="{DynamicResource SuccessBrush}"/>
                </Border>
            </StackPanel>

            <!-- Markdown body -->
            <chat:PiaMarkdownView Markdown="{Binding Markdown}"/>

            <!-- Sources (optional) -->
            <ItemsControl ItemsSource="{Binding Sources}"
                          Margin="0,12,0,0"
                          Visibility="{Binding HasSources, Converter={StaticResource BoolToVisibility}}">
                <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <chat:PiaSourceChip Number="{Binding Number}"
                                            Source="{Binding Source}"
                                            Meta="{Binding Meta}"
                                            Margin="0,0,6,6"/>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>

            <!-- Toolbar -->
            <chat:PiaAnswerToolbar Margin="-6,8,0,0"
                CopyCommand="{Binding CopyCommand}"
                SpeakCommand="{Binding SpeakCommand}"
                RegenerateCommand="{Binding RegenerateCommand}"
                RateCommand="{Binding RateCommand}"
                Stats="{Binding Stats}"/>

            <!-- Suggestions -->
            <chat:PiaSuggestionChips Margin="0,8,0,0"
                ItemsSource="{Binding Suggestions}"
                ItemClickCommand="{Binding SuggestionCommand}"/>
        </StackPanel>
    </Grid>
</UserControl>
```

```xml
<!-- MainWindow.xaml chat list -->
<ItemsControl ItemsSource="{Binding Messages}">
    <ItemsControl.Resources>
        <DataTemplate DataType="{x:Type vm:AssistantMessageViewModel}">
            <chat:PiaAssistantMessage/>
        </DataTemplate>
        <DataTemplate DataType="{x:Type vm:UserMessageViewModel}">
            <Border Style="{StaticResource UserBubbleStyle}">
                <TextBlock Style="{StaticResource UserBubbleTextStyle}" Text="{Binding Text}"/>
            </Border>
        </DataTemplate>
        <DataTemplate DataType="{x:Type vm:ActionMessageViewModel}">
            <chat:PiaActionCard .../>
        </DataTemplate>
    </ItemsControl.Resources>
</ItemsControl>
```

---

## Step 9 — Additional tokens

Append to `PiaTokens.Light.xaml` / `PiaTokens.Dark.xaml`:

```xml
<!-- Code-block palette (same in light + dark) -->
<Color x:Key="CodeBlockBgColor">#FF0F1729</Color>
<Color x:Key="CodeFgColor">#FFE6EAF2</Color>
<Color x:Key="CodeKeywordColor">#FF7AA3F5</Color>
<Color x:Key="CodeStringColor">#FF86E1A0</Color>
<Color x:Key="CodeNumberColor">#FFF5A872</Color>
<Color x:Key="CodeCommentColor">#FF6B7790</Color>

<SolidColorBrush x:Key="CodeBlockBgBrush" Color="{StaticResource CodeBlockBgColor}"/>
<SolidColorBrush x:Key="CodeFgBrush"      Color="{StaticResource CodeFgColor}"/>

<!-- Callout palette -->
<Color x:Key="WarnColor">#FFB45309</Color>
<Color x:Key="WarnSoftColor">#FFFEF3C7</Color>
<SolidColorBrush x:Key="WarnBrush"     Color="{StaticResource WarnColor}"/>
<SolidColorBrush x:Key="WarnSoftBrush" Color="{StaticResource WarnSoftColor}"/>
```

---

## Step 10 — ViewModel additions (additive only)

```csharp
public partial class AssistantMessageViewModel : ObservableObject
{
    [ObservableProperty] private string         _markdown    = string.Empty;
    [ObservableProperty] private MessageMeta    _meta        = new();
    [ObservableProperty] private IList<SourceRef> _sources   = Array.Empty<SourceRef>();
    [ObservableProperty] private IList<string>  _suggestions = Array.Empty<string>();
    [ObservableProperty] private AnswerStats    _stats       = new();

    public bool HasSources => Sources.Count > 0;

    public IRelayCommand          CopyCommand        { get; }
    public IRelayCommand          SpeakCommand       { get; }
    public IRelayCommand          RegenerateCommand  { get; }
    public IRelayCommand<string>  RateCommand        { get; }
    public IRelayCommand<string>  SuggestionCommand  { get; }
}

public sealed record MessageMeta(
    string  Timing       = "",      // "in 1.2 s erstellt"
    string? ProfileLabel = null);   // "nutzt Profil: vegan"

public sealed record SourceRef(int Number, string Source, string Meta);

public sealed record AnswerStats(int Tokens, string Model)
{
    public string Summary => $"{Tokens:N0} Tokens · {Model} · lokal";
}
```

> Existing `Text` property stays as a fallback. Markdown == Text for old messages means Markdig
> renders a plain paragraph — no Renderer setup needed.

---

## Build order

1. Extend tokens (Step 9).
2. `MarkdownService` + `PiaMarkdownView` — test with stock Markdig rendering first.
3. `PiaCodeBlock` + `PiaCodeBlockRenderer` — register in the pipeline.
4. `PiaCallout` + `:::tip` Markdig extension.
5. `PiaAnswerToolbar`, `PiaSuggestionChips`, `PiaSourceChip` (pure view controls).
6. `PiaAssistantMessage` composer — swap the `DataTemplate` in `MainWindow.xaml`.
7. Backend feeds `Markdown` + `Sources` instead of raw `Text`. Old messages keep working
   (Markdown == Text).

After each step: build, render a test Markdown payload, screenshot the result, pause for review.
