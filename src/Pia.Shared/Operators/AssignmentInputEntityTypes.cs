namespace Pia.Shared.Operators;

/// <summary>
/// The closed vocabulary of record kinds an assignment's input may carry. Shared because the client's scoping
/// picker and the server's refusal must name the same strings: as inline literals on each side, a typo would
/// present as an unexplained <c>400</c> on one entity type and nothing else.
///
/// Matched EXACTLY, ordinally. A wrong-cased value is refused rather than accepted, for the same reason a
/// closed set is used at all — silently normalising input into a security-relevant vocabulary hides the
/// mistake instead of surfacing it. Clients use these constants and never their own literals.
///
/// This is <b>user-authored content only</b>. Three families are excluded, each for its own reason, recorded
/// here so none of them later reads as an oversight:
/// <list type="bullet">
/// <item><description><b>Providers, settings, trusted certificates, plugins and plugin preferences</b> carry
/// credentials and configuration. Excluded by construction, and must stay excluded — no skill has a reason to
/// read them.</description></item>
/// <item><description><b>Kanban columns</b> are containers, not content: a column is a name, a sort order and
/// two flags. The user's prose lives in the todos inside it, which are in the vocabulary. An entity no picker
/// can usefully offer is dead surface in a security boundary.</description></item>
/// <item><description><b>Research sessions</b> are no longer produced by the client; the sync DTO survives only
/// for wire-contract stability.</description></item>
/// </list>
///
/// Adding a member is a deliberate edit to a named constant in this repo, which is the review point. Weigh it
/// on the test that actually applies — is this prose the user authored, and does it hold no credential?
/// </summary>
public static class AssignmentInputEntityTypes
{
    /// <summary>A turn or thread from the user's own assistant conversations.</summary>
    public const string AssistantChat = "assistantChat";

    /// <summary>A saved chat session.</summary>
    public const string Session = "session";

    /// <summary>A memory entry the user wrote or accepted.</summary>
    public const string Memory = "memory";

    /// <summary>A todo, including its notes — where a kanban board's actual prose lives.</summary>
    public const string Todo = "todo";

    /// <summary>
    /// A CUSTOM prompt template. In rather than out despite looking config-shaped: a template is a name,
    /// prompt and description the user wrote, it holds no credential, and only custom templates sync at all
    /// (built-ins are client-side constants). It is prompt scaffolding the user authored, so it falls on the
    /// content side of the line.
    /// </summary>
    public const string Template = "template";

    /// <summary>
    /// Every legal value. Ordinal by design — see the type's own remarks on why a wrong-cased value is refused
    /// rather than normalised.
    /// </summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { AssistantChat, Session, Memory, Todo, Template };

    /// <summary>Whether <paramref name="entityType"/> is in the vocabulary. The server's first of two checks;
    /// the second is whether the target skill declared it.</summary>
    public static bool IsKnown(string? entityType) => entityType is not null && All.Contains(entityType);
}
