using Pia.Models;

namespace Pia.ViewModels.Models;

public class ProviderDisplayItem
{
    public AiProvider Provider { get; init; } = null!;
    public bool IsActive { get; init; }
    public bool IsDefaultForAnyMode { get; init; }
    public bool ShowFailBadge => IsDefaultForAnyMode && !IsActive;
    public string? FailReason { get; init; }
}
