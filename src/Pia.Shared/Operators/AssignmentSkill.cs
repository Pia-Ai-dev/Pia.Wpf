namespace Pia.Shared.Operators;

/// <summary>
/// One skill the calling user may enqueue an assignment for — what the client's scoping UI renders from.
/// Resolved through the caller's own group grants, so this list is per-user and not a server-wide catalogue.
/// </summary>
/// <param name="Name">The value to send as <see cref="CreateAssignmentRequest.SkillName"/>.</param>
/// <param name="DisplayName">
/// Presentation only, never read by any check. Sourced from the operator row's admin-editable name, falling
/// back to <paramref name="Name"/>. It deliberately does NOT come from the skill's server-side descriptor:
/// that type exists precisely so a config row cannot supply its members, and putting an admin-editable string
/// on it would undermine that for a label.
/// </param>
/// <param name="Mode">
/// The chat mode the skill runs under, for display. A compile-time property of the skill, never a request
/// field — a caller cannot steer an assignment into a different mode, and a wrong one fails silently.
/// </param>
/// <param name="DeclaredInputTypes">
/// Which <see cref="AssignmentInputEntityTypes"/> members this skill accepts. The picker offers only these,
/// and the server REFUSES anything outside them, so the two halves cannot drift into a scope the skill never
/// declared.
///
/// <b>Empty is meaningful, not missing data.</b> It means the skill takes a prompt and nothing else — true of
/// every pod-hosted skill, which has no server-side class to declare on and therefore declares nothing rather
/// than being trusted to declare for itself. A client that treats empty as "unknown, offer everything" inverts
/// the gate.
/// </param>
public sealed record AssignmentSkill(
    string Name,
    string DisplayName,
    string Mode,
    IReadOnlyList<string> DeclaredInputTypes);
