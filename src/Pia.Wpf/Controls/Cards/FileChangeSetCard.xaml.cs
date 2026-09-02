using System.Windows.Controls;

namespace Pia.Controls.Cards;

/// <summary>
/// One folded header over the accepted file diffs of a run step, so a twenty-file step reads as a
/// single line. Purely presentational — its DataContext is the hosting
/// <see cref="Pia.Models.FileChangeSet"/>.
/// </summary>
public partial class FileChangeSetCard : UserControl
{
    public FileChangeSetCard()
    {
        InitializeComponent();
    }
}
