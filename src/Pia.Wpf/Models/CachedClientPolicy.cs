using Pia.Shared.Policy;

namespace Pia.Models;

public class CachedClientPolicy
{
    public string Document { get; set; } = ClientPolicyContract.EmptyDocument;

    public DateTime? UpdatedAt { get; set; }

    /// <summary>Per-key JSON of the last default this mechanism applied, so a later admin change re-applies
    /// while a value the user has since changed wins.</summary>
    public Dictionary<string, string> AppliedDefaults { get; set; } = new();
}
