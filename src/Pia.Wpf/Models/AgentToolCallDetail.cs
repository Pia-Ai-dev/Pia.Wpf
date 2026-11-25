namespace Pia.Models;

/// <summary>Whether a call's input or result is kept in full, and why not when it is not.</summary>
public enum AgentToolDetailState
{
    Kept = 0,

    /// <summary>A result cut at the per-result cap (about 4,000 chars); the kept text is the start.</summary>
    Truncated = 1,

    /// <summary>Arguments over the 1 MB row cap, dropped.</summary>
    Omitted = 2,

    /// <summary>The call has no result row: the result was never recorded.</summary>
    Missing = 3,
}

/// <summary>One tool call of a step, as the step-detail view shows it. Texts carry real names.</summary>
/// <param name="CallId">The provider's call id; empty when the provider gave none.</param>
/// <param name="Arguments">The arguments as JSON; null when there were none or <paramref name="ArgumentsState"/>
/// says they were not kept.</param>
public sealed record AgentToolCallDetail(
    string CallId,
    string? ToolName,
    int? Round,
    DateTime CreatedAt,
    string? Arguments,
    AgentToolDetailState ArgumentsState,
    string? Result,
    AgentToolDetailState ResultState);

/// <summary>A step's recorded calls, in call order.</summary>
/// <param name="NotRecordedRounds">Rounds of this step the per-run cap (500 rows or four million chars) refused;
/// their calls have no detail.</param>
public sealed record AgentStepCallDetails(
    IReadOnlyList<AgentToolCallDetail> Calls,
    IReadOnlyList<int> NotRecordedRounds)
{
    public static readonly AgentStepCallDetails Empty = new([], []);
}
