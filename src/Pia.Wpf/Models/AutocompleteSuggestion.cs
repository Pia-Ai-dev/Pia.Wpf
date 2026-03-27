using Wpf.Ui.Controls;

namespace Pia.Models;

public class AutocompleteSuggestion
{
    public required string DisplayText { get; init; }
    public SymbolRegular Icon { get; init; }
    public AtCommandDomain? Domain { get; init; }
    public Guid? ItemId { get; init; }
    public bool IsTier1 { get; init; }
}
