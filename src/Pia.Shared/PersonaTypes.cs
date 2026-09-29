namespace Pia.Shared;

/// <summary>
/// Suggested persona routing types, shared by the client's picker and the server's admin pages so a type
/// offered on one is routable from the other. A hint list, never a validation set — the proxy honours any
/// free-form type a persona declares.
/// </summary>
public static class PersonaTypes
{
    /// <summary>The type a persona with a blank type routes as, on the client and the server.</summary>
    public const string Default = "general";

    public static readonly IReadOnlyList<string> Suggested = [Default, "fast", "code", "private"];
}
