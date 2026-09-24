using Microsoft.Extensions.AI;

namespace Pia.Services.Interfaces;

/// <summary>Read-only pia_help/pia_settings tools that let the assistant answer questions about Pia
/// itself — inline only, so no pending-action type or tuple return.</summary>
public interface IHelpToolHandler
{
    IList<AITool> GetTools();

    Task<object?> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);
}
