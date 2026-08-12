using Pia.Shared.Operators;

namespace Pia.Services.Operators;

/// <summary>
/// Whether the background-assignment surface exists for this user at all, and what it offers. The gate is
/// deliberately coarse: no server, no licence, no grant and an empty skill list all mean "hide it", because a
/// disabled-looking button the user cannot explain is worse than no button.
/// </summary>
public sealed record AssignmentSurface(bool Available, IReadOnlyList<AssignmentSkill> Skills)
{
    public static readonly AssignmentSurface Hidden = new(false, []);
}

/// <summary>
/// One local record the user may choose to send, WITHOUT its text. The split is the consent boundary in type
/// form: a picker builds these, and the content is only read once a receipt exists.
/// </summary>
/// <param name="CharCount">Counted while listing so the consent screen can show it and so an over-cap record
/// can be refused before the user affirms anything.</param>
public sealed record AssignmentScopeItem(
    string EntityType,
    Guid EntityId,
    string Title,
    int CharCount,
    DateTime? UpdatedAt)
{
    /// <summary>A record larger than the server's per-item cap cannot be sent, and is shown as such rather
    /// than being quietly cut down — a user who affirms sending a record and sends a fifth of it was not
    /// asked the question they answered.</summary>
    public bool ExceedsItemCap => CharCount > AssignmentInput.MaxItemChars;
}

/// <summary>
/// Evidence that a human affirmed this exact set of records, in this session, before anything was read or
/// sent. It is a REQUIRED argument of the start path rather than a flag checked inside it, so a background
/// caller cannot reach the send by omitting a step — it would have to forge a consent record first, and that
/// record is itself the audit trail.
/// </summary>
public sealed record AssignmentConsentReceipt(
    Guid RecordId,
    string SkillName,
    IReadOnlyList<AssignmentScopeItem> Items,
    DateTime GrantedAtUtc);

/// <summary>What the user asked for, plus the records they ticked. Carries no text: the coordinator resolves
/// that from the local stores after checking the receipt.</summary>
public sealed record AssignmentRequest(
    string SkillName,
    string Prompt,
    IReadOnlyList<AssignmentScopeItem> Items);

public enum AssignmentStartStatus
{
    Started,

    /// <summary>No receipt, or one this session's consent log did not write. Nothing was read and nothing
    /// was sent.</summary>
    ConsentMissing,

    /// <summary>The selection or prompt is over a published cap. Refused locally against the same constants
    /// the server enforces, so the round trip never happens.</summary>
    TooLarge,

    /// <summary>The server refused it, or could not be reached. The reason is logged, not shown as a code.</summary>
    Refused,
}

public sealed record AssignmentStartOutcome(AssignmentStartStatus Status, Guid? AssignmentId = null)
{
    public static AssignmentStartOutcome ConsentMissing => new(AssignmentStartStatus.ConsentMissing);
    public static AssignmentStartOutcome TooLarge => new(AssignmentStartStatus.TooLarge);
    public static AssignmentStartOutcome Refused => new(AssignmentStartStatus.Refused);
}

/// <summary>
/// A run this device started and has not yet finished collecting. Persisted, because the app closing
/// mid-run must not lose the artifact: the server drops the plaintext within
/// <c>Operators:PlaintextRetentionHours</c> whether or not anyone came back for it.
/// </summary>
/// <param name="ChatId">Minted BEFORE the run starts, so writing the artifact is idempotent — a resumed
/// pull overwrites its own chat instead of creating a second one.</param>
public sealed record PendingAssignment(
    Guid AssignmentId,
    Guid ChatId,
    string SkillName,
    string Prompt,
    DateTime StartedAtUtc);
