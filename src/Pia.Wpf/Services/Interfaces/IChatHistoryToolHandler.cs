using Microsoft.Extensions.AI;

namespace Pia.Services.Interfaces;

/// <summary>Read-only search_chats/read_chat tools over past conversations — inline only, so no
/// pending-action type or tuple return.</summary>
public interface IChatHistoryToolHandler
{
    /// <summary>False when the user has turned the pack off; the tools are then not offered at all.</summary>
    bool IsAvailable { get; }

    IList<AITool> GetTools();

    Task<object?> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);
}
