namespace Pia.Controls.Cards;

/// <summary>
/// Presentational emphasis for a <see cref="DecisionButton"/>. Maps to a WPF-UI button
/// appearance (Primary→Primary, Default→Secondary, Danger→Caution) — pure view concern,
/// not a domain concept.
/// </summary>
public enum DecisionEmphasis
{
    Primary,
    Default,
    Danger
}
