using System.Windows.Controls;

namespace Pia.Controls.Cards;

/// <summary>
/// A GitHub-style diff view for the write_file approval card: header stripe (path + ±stats + collapse
/// chevron) over a dual line-number gutter with hunk folding. Purely presentational — its DataContext
/// is the hosting <see cref="Pia.Models.ActionCardInfo"/>.
/// </summary>
public partial class FileDiffCard : UserControl
{
    public FileDiffCard()
    {
        InitializeComponent();
    }
}
