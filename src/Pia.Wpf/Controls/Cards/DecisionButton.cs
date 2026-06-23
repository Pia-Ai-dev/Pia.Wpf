using System.Windows.Input;

namespace Pia.Controls.Cards;

/// <summary>
/// A view-side descriptor for one button in a <see cref="CardDecisionBar"/>. The host builds
/// the list; the bar renders it. Strictly presentational — not a domain decision, holds only a
/// label, an emphasis and the command to run. There is intentionally no parameter member
/// (neither consumer needs one; see design §4).
/// </summary>
public sealed class DecisionButton
{
    public string Label { get; init; } = string.Empty;

    public DecisionEmphasis Emphasis { get; init; }

    public ICommand? Command { get; init; }
}
