namespace Pia.Models;

/// <summary>
/// Represents enterprise policy configuration loaded from %ProgramData%/Pia.Wpf/policy.json.
/// </summary>
public class PolicySettings
{
    /// <summary>
    /// Default values applied when a user setting has not been explicitly set.
    /// Users can override these.
    /// </summary>
    public AppSettings? Defaults { get; set; }

    /// <summary>
    /// Enforced values that override user settings and are read-only in the UI.
    /// </summary>
    public AppSettings? Enforce { get; set; }
}
