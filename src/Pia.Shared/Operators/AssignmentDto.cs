using System.Text.Json.Serialization;

namespace Pia.Shared.Operators;

/// <summary>
/// Wire request to create an assignment. No caller-supplied id and no idempotency key: calling twice with an
/// identical body yields two assignments, each with its own run.
///
/// <see cref="InputJson"/> stays a string on the wire and its content is an <see cref="AssignmentInput"/>
/// envelope. There is deliberately no <c>Mode</c> field — the mode is a compile-time property of the skill,
/// and a caller-supplied one would fail SILENTLY: the run would lose its knowledge-base grounding, produce an
/// ungrounded artifact, and still bill the tokens.
/// </summary>
public sealed record CreateAssignmentRequest(string SkillName, string InputJson);

/// <summary>
/// Wire projection of an assignment. Deliberately excludes the owning user, the operator row id and the
/// workflow id — none is this API's business, and the workflow id is an orchestration detail the product
/// surface never exposes.
///
/// User-scoped ONLY. It carries the artifact and the error message, both free text, which is why the
/// cross-user admin roll-up has its own narrower projection rather than reusing this one.
/// </summary>
/// <param name="TokensAbandoned">
/// Spend from attempts the run had already closed by the time they finished. Nonzero means
/// <paramref name="TokensSpent"/> alone under-reports what the assignment cost; the true bill is the sum. Kept
/// separate rather than folded in, because a folded total would contradict the step count and the artifact it
/// is read alongside.
/// </param>
/// <param name="ArtifactJson">
/// The raw upstream completion. Prefer <paramref name="ArtifactText"/> for anything user-facing.
/// </param>
/// <param name="ArtifactText">
/// The artifact's assistant text, normalised server-side. Extracted there rather than here because parsing a
/// provider response is logic, and this assembly carries none — a second parser on the client would be a
/// second thing to get wrong. Absent on the list projection.
/// </param>
/// <param name="PlaintextDroppedAt">
/// When the server redacted this assignment's stored plaintext. Non-null means the input and artifact are gone
/// server-side and only the client's own re-encrypted copy remains. Absent on the list projection.
/// </param>
/// <param name="Events">
/// The progress log — polled, not pushed. <c>null</c>, and therefore ABSENT from the wire, on the list route:
/// an empty array there would read as "this assignment has no progress", which is a lie, since a row always
/// has at least the event written in the same transaction that created it. The list route does not load them
/// and must not, or a full page would drag every event of every row with it.
/// </param>
public sealed record AssignmentDto(
    Guid Id,
    string SkillName,
    // property:, not a bare attribute — on a positional record an attribute with no target binds to the
    // PARAMETER, where the serializer never looks. These are what keep a null absent rather than emitted:
    // the server has a global "omit nulls" option, this type's other consumer does not, and the absent-vs-null
    // distinction is load-bearing for Events below.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Mode,
    string Status,
    int StepCount,
    int TokensSpent,
    int TokensAbandoned,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTime? StartedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTime? CompletedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ArtifactJson,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorMessage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ArtifactText = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTime? PlaintextDroppedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<AssignmentEventDto>? Events = null);

/// <summary>
/// One entry in an assignment's progress log. This is the only place the event kinds become observable to a
/// caller, and that matters: a row force-failed by the server's own reconcile pass and one killed by an admin
/// both land on the same terminal status, so the kind is what tells them apart.
/// </summary>
/// <param name="Kind">
/// The lifecycle kind. Treat unknown values as informational rather than an error: kinds are added over time,
/// and a client that switches exhaustively will break on the next one.
/// </param>
/// <param name="Message">
/// Free text — a step summary, a de-authorisation detail, an admin's reason. Absent on kinds that carry none.
/// </param>
/// <param name="DetailJson">
/// The machine-readable half of the same event: the step index, that step's token delta, the tools it
/// resolved, and on a de-authorisation which of them were lost. Absent on the kinds nothing structured applies
/// to, so treat it as optional rather than as a schema.
/// </param>
public sealed record AssignmentEventDto(
    Guid Id,
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DetailJson,
    DateTime CreatedAt);
