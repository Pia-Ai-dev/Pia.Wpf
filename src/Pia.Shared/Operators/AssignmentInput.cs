using System.Text.Json.Serialization;

namespace Pia.Shared.Operators;

/// <summary>
/// The decrypt-in envelope: what a client sends as an assignment's input once it has decrypted the items the
/// user consented to. Carried as the JSON *content* of <see cref="CreateAssignmentRequest.InputJson"/>, which
/// stays a string on the wire.
///
/// This envelope is REQUIRED — there is no opaque-JSON path. The server enforces that every
/// <see cref="AssignmentInputItem.EntityType"/> is both in <see cref="AssignmentInputEntityTypes"/> and in the
/// target skill's own declaration, and an opaque body would be a one-line bypass of that enforcement.
///
/// Everything here is PLAINTEXT by the time it leaves the client, and that is the whole point of the gate: the
/// interactive plane stays end-to-end encrypted, and this one route is the declared, consented crossing. The
/// guarantee on the far side is tenant isolation plus a bounded retention window, not encryption.
/// </summary>
/// <param name="SchemaVersion">
/// Always <see cref="CurrentSchemaVersion"/> for a new client. Present so the server can refuse a shape it
/// does not understand instead of half-reading it: a missing or unknown value is a <c>400</c>, never a
/// best-effort parse.
/// </param>
/// <param name="Prompt">What the user is asking for, in their own words. Bounded by
/// <see cref="MaxPromptChars"/>.</param>
/// <param name="Items">
/// The decrypted records the user explicitly ticked. Never implicit, never derived — a client that infers
/// "related" content and includes it has broken the consent boundary, whatever the server accepts.
/// </param>
public sealed record AssignmentInput(
    int SchemaVersion,
    string Prompt,
    IReadOnlyList<AssignmentInputItem> Items)
{
    /// <summary>The only version any shipped client sends or server accepts. Bump deliberately, and only
    /// alongside a server that accepts both.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Bounds shared with the server so a client can refuse before a round trip rather than reading a
    /// <c>400</c> back. The SERVER is the enforcement point regardless — these are the numbers it enforces,
    /// published here so the two cannot disagree, not a client-side substitute for it.
    ///
    /// They exist because the assignment input column is unbounded, so without them a single multi-megabyte
    /// item is accepted, spliced verbatim into a prompt, and burns the per-assignment token ceiling mid-run
    /// with the tokens already billed. A refusal is the only cheap place to stop that.
    /// </summary>
    public const int MaxItems = 20;

    /// <inheritdoc cref="MaxItems"/>
    public const int MaxItemChars = 8_000;

    /// <inheritdoc cref="MaxItems"/>
    public const int MaxTotalItemChars = 32_000;

    /// <inheritdoc cref="MaxItems"/>
    public const int MaxPromptChars = 4_000;
}

/// <summary>
/// One consented record. Deliberately flat text rather than the sync DTO it came from: a skill reads prose,
/// and shipping the full entity would put fields the user never saw named — and in some cases wrapped keys —
/// across the boundary.
/// </summary>
/// <param name="EntityType">One of <see cref="AssignmentInputEntityTypes"/>, matched exactly.</param>
/// <param name="EntityId">
/// The client-side id of the record this text came from. Round-tripped for the client's own benefit — so it
/// can show the user which of their records an artifact was built from — and read by no server-side check.
/// </param>
/// <param name="Title">Optional label for the item, shown to the model as a heading.</param>
/// <param name="Text">The decrypted content. Bounded by <see cref="AssignmentInput.MaxItemChars"/>.</param>
/// <param name="UpdatedAt">
/// When the source record last changed, if the client knows. Lets a skill say how stale its grounding is.
/// </param>
public sealed record AssignmentInputItem(
    string EntityType,
    Guid EntityId,
    // property:, not a bare attribute — on a positional record an attribute with no target lands on the
    // PARAMETER, where the serializer never looks at it. This is the one that has to be right: the server's
    // global "omit nulls" option does not travel with the type, and the client's serializer has no such
    // global, so the attribute is what keeps the wire shape the same in both directions.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Title,
    string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTime? UpdatedAt);
