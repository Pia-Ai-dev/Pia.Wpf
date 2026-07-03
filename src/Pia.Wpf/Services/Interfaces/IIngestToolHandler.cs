using Microsoft.Extensions.AI;

namespace Pia.Services.Interfaces;

/// <summary>
/// Assistant tool surface for the ingest pipeline (Task 7.1), mirroring the existing <c>*ToolHandler</c>
/// pattern. Exposes a single <c>ingest(source_ref)</c> tool that compiles a RAW source under
/// <c>sources/</c> into <c>memory/topics/</c> wiki pages. Ingest runs inline (no pending-action /
/// confirmation card), so <see cref="HandleToolCallAsync"/> performs the work and returns the result
/// directly.
/// </summary>
public interface IIngestToolHandler
{
    IList<AITool> GetTools();

    Task<object?> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);
}
