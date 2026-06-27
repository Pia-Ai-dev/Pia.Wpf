using System.Windows;
using System.Windows.Controls;
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
            new PropertyMetadata(null));

    public static readonly DependencyProperty FileNameProperty =
        DependencyProperty.Register(nameof(FileName), typeof(string), typeof(PiaFileChip),
            new PropertyMetadata(null));

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(FileRefKind), typeof(PiaFileChip),
            new PropertyMetadata(FileRefKind.Read));

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

    public PiaFileChip() => InitializeComponent();

    private void OnOpenClick(object sender, RoutedEventArgs e) => ShellLauncher.OpenFile(AbsolutePath);

    private void OnRevealClick(object sender, RoutedEventArgs e) => ShellLauncher.RevealInExplorer(AbsolutePath);
}
