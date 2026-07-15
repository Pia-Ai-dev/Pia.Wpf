using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pia.Helpers;
using Pia.Models;

namespace Pia.Controls.Chat;

/// <summary>
/// Chip for a local file a chat touched (read / created / updated / exported / @File-referenced).
/// The local-file analogue of <see cref="PiaSourceChip"/>: the name region opens the file with its
/// default app; the trailing folder button reveals it in Explorer. Opens are best-effort (see
/// <see cref="ShellLauncher"/>) so a since-deleted file never throws.
/// </summary>
public partial class PiaFileChip : UserControl
{
    public static readonly DependencyProperty AbsolutePathProperty =
        DependencyProperty.Register(nameof(AbsolutePath), typeof(string), typeof(PiaFileChip),
            new PropertyMetadata(null, OnAbsolutePathChanged));

    public static readonly DependencyProperty FileNameProperty =
        DependencyProperty.Register(nameof(FileName), typeof(string), typeof(PiaFileChip),
            new PropertyMetadata(null));

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(FileRefKind), typeof(PiaFileChip),
            new PropertyMetadata(FileRefKind.Read));

    // Whether to surface the "open in VS Code" button: VS Code installed AND the file is a supported
    // (code/script/config/text) type. Recomputed whenever AbsolutePath changes.
    public static readonly DependencyProperty ShowVsCodeButtonProperty =
        DependencyProperty.Register(nameof(ShowVsCodeButton), typeof(bool), typeof(PiaFileChip),
            new PropertyMetadata(false));

    // The VS Code app icon extracted from the install, or null when extraction failed (the XAML falls
    // back to a generic glyph so a null icon never hides an otherwise-usable button).
    public static readonly DependencyProperty VsCodeIconProperty =
        DependencyProperty.Register(nameof(VsCodeIcon), typeof(ImageSource), typeof(PiaFileChip),
            new PropertyMetadata(null));

    public string? AbsolutePath
    {
        get => (string?)GetValue(AbsolutePathProperty);
        set => SetValue(AbsolutePathProperty, value);
    }

    public string? FileName
    {
        get => (string?)GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    public FileRefKind Kind
    {
        get => (FileRefKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool ShowVsCodeButton
    {
        get => (bool)GetValue(ShowVsCodeButtonProperty);
        private set => SetValue(ShowVsCodeButtonProperty, value);
    }

    public ImageSource? VsCodeIcon
    {
        get => (ImageSource?)GetValue(VsCodeIconProperty);
        private set => SetValue(VsCodeIconProperty, value);
    }

    public PiaFileChip() => InitializeComponent();

    private static void OnAbsolutePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PiaFileChip)d).UpdateVsCodeAffordance();

    private void UpdateVsCodeAffordance()
    {
        ShowVsCodeButton = VsCodeLauncher.IsAvailable && VsCodeLauncher.IsSupportedFile(AbsolutePath);
        VsCodeIcon = ShowVsCodeButton ? VsCodeLauncher.TryGetIcon() : null;
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => ShellLauncher.OpenFile(AbsolutePath);

    private void OnRevealClick(object sender, RoutedEventArgs e) => ShellLauncher.RevealInExplorer(AbsolutePath);

    private void OnOpenInVsCodeClick(object sender, RoutedEventArgs e) => VsCodeLauncher.Open(AbsolutePath);
}
