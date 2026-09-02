using System.Windows;
using System.Windows.Controls;

namespace Pia.Controls.Cards;

/// <summary>
/// A GitHub-style diff view for the write_file approval card: header stripe (path + ±stats + collapse
/// chevron) over a dual line-number gutter with hunk folding. Purely presentational — its DataContext
/// is the hosting <see cref="Pia.Models.ActionCardInfo"/>.
/// </summary>
public partial class FileDiffCard : UserControl
{
    /// <summary>Renders as a bare row instead of its own card, for use inside a <c>FileChangeSetCard</c>.</summary>
    public static readonly DependencyProperty ChromelessProperty =
        DependencyProperty.Register(nameof(Chromeless), typeof(bool), typeof(FileDiffCard),
            new PropertyMetadata(false));

    public bool Chromeless
    {
        get => (bool)GetValue(ChromelessProperty);
        set => SetValue(ChromelessProperty, value);
    }

    public FileDiffCard()
    {
        InitializeComponent();
    }
}
