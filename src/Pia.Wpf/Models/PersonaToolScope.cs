namespace Pia.Models;

/// <summary>
/// How many tools a persona may use. See docs/personas/TARGET/00-shared-contract.md §5.
/// </summary>
public enum PersonaToolScope
{
    /// <summary>No tools at all; the no-tools prompt path is used and no tools are passed to the model.</summary>
    None = 0,

    /// <summary>Only read/query tools (reserved — treated as <see cref="Full"/> in v1).</summary>
    ReadOnly = 1,

    /// <summary>All enabled tools (current behaviour).</summary>
    Full = 2,
}
