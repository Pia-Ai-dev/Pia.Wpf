namespace Pia.Models;

/// <summary>
/// What an exchange row IS, and which writer produced it. PERSISTED (<c>AgentToolExchanges.Kind</c>) →
/// <b>APPEND-ONLY</b>: never renumber, never reuse an ordinal.
/// </summary>
public enum AgentToolExchangeKind
{
    /// <summary>Never written by this build; the value an older/newer DB's row reads back as.</summary>
    Unknown = 0,

    /// <summary>A <c>FunctionCallContent</c> as the MODEL saw it — tokenized when tokenization is on.</summary>
    Call = 1,

    /// <summary>A <c>FunctionResultContent</c> as the MODEL saw it — tokenized when tokenization is on.</summary>
    Result = 2,

    /// <summary>The call the gate parked, DETOKENIZED and replayable. Can hold real user content.</summary>
    ParkedCall = 3,

    /// <summary>A same-round call withheld behind the park, DETOKENIZED and replayable.</summary>
    WithheldCall = 4,
}

/// <summary>
/// How <c>ResultText</c> is encoded. PERSISTED (<c>AgentToolExchanges.ResultKind</c>) → <b>APPEND-ONLY</b>.
/// </summary>
public enum AgentToolExchangeResult
{
    /// <summary>The row carries no result: a call row, or a result whose value was null.</summary>
    None = 0,

    /// <summary>The tool returned a string, stored verbatim.</summary>
    Text = 1,

    /// <summary>The tool returned an object, stored as JSON and rehydrated as a <c>JsonElement</c>.</summary>
    Json = 2,
}

/// <summary>
/// One persisted tool-exchange row. <b>PAYLOAD-BEARING</b>, the inverse of <see cref="AgentTimelineEvent"/>'s
/// metadata-only contract: device-local, purged with the run, never logged outside <c>SensitiveDebug</c>.
/// </summary>
/// <param name="MessageSeq">Rows sharing it rebuild into ONE <c>ChatMessage</c>, so two parallel calls in one
/// assistant message come back in one message.</param>
/// <param name="Seq">Per RUN over content rows, and the only ordering. Store-assigned; a caller-built row
/// carries 0.</param>
/// <param name="CallId">Verbatim from the message, EMPTY STRING when the provider gave none. Only the replay
/// synthesizes one, so a row never disagrees with the audit row for the same call.</param>
/// <param name="ArgsOmitted">The arguments exceeded the row cap and were dropped. <see cref="Kind"/>
/// <see cref="AgentToolExchangeKind.Call"/> only — an oversize parked call is refused whole.</param>
/// <param name="Chars">The persisted argument and result lengths, so the per-run cap is a SUM rather than a
/// scan over 512 K blobs.</param>
/// <param name="AnchorMessageId">The chat message this group precedes on the re-seed; null until sealed.</param>
public sealed record AgentToolExchangeRow(
    Guid Id,
    Guid RunId,
    Guid? StepId,
    long MessageSeq,
    long Seq,
    int? Round,
    string Role,
    AgentToolExchangeKind Kind,
    string CallId,
    string? ToolName,
    Guid? PluginId,
    string? ArgumentsJson,
    bool ArgsOmitted,
    string? DisplayArgs,
    AgentToolExchangeResult ResultKind,
    string? ResultText,
    int Chars,
    Guid? AnchorMessageId,
    DateTime CreatedAt,
    DateTime? ReplayedAt,
    DateTime? SupersededAt)
{
    /// <summary>Row shape version, so a future shape change is detectable rather than misread.</summary>
    public int SchemaVersion { get; init; } = 1;
}
