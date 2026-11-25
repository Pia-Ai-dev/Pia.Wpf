using Microsoft.Extensions.AI;

namespace Pia.Services.Interfaces;

/// <summary>What a conversation turn gets while its chat has an unfinished chat job; never stored.</summary>
public sealed record ChatJobTurn(Guid RunId, string Block, string PromptAddition, IReadOnlyList<AITool> Tools);

/// <summary>Lets a conversation turn see and steer the chat job running next to it.</summary>
public interface IChatJobToolService
{
    /// <summary>Null with the unified flow off, or when the chat has no unfinished chat job.</summary>
    Task<ChatJobTurn?> PrepareTurnAsync(Guid? chatId, CancellationToken ct = default);

    Task<AgentRunLookup?> FindUnfinishedChatJobAsync(Guid? chatId, CancellationToken ct = default);

    /// <summary>True when a message typed while this run is parked should be an ordinary turn.</summary>
    Task<bool> TypedMessageIsTurnAsync(Guid runId, CancellationToken ct = default);

    bool Handles(string toolName);

    Task<string> HandleAsync(Guid chatId, string toolName, IDictionary<string, object?>? arguments,
        CancellationToken ct = default);
}

public sealed record AgentRunLookup(Guid RunId, Models.AgentRunState State, bool Executing);
