using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IAiClientService
{
    Task<AiCompletionResult> SendRequestAsync(AiProvider provider, string prompt, CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> StreamChatCompletionAsync(
        IList<ChatMessage> messages,
        AiProvider provider,
        string? mode = null,
        CancellationToken cancellationToken = default);

    /// <param name="managedPersonaId">
    /// The persona driving this turn, relayed to the Pia Cloud transport as <c>X-Pia-Persona</c>.
    /// See <see cref="GetChatCompletionWithToolsAsync"/>.
    /// </param>
    Task<ChatResponse> GetChatResponseAsync(
        IList<ChatMessage> messages,
        AiProvider provider,
        IList<AITool>? tools = null,
        string? mode = null,
        Guid? managedPersonaId = null,
        CancellationToken cancellationToken = default);

    /// <param name="managedPersonaId">
    /// The persona driving this turn, relayed to the Pia Cloud transport as <c>X-Pia-Persona</c> so the
    /// server can scope persona-bound KBs/connectors. Null ⇒ header omitted, which is what every
    /// non-assistant caller (title generation, optimize, planner, verifier) passes. It was inserted
    /// after <paramref name="mode"/> to mirror how <c>mode</c> is threaded end-to-end rather than to keep
    /// call sites compiling — inserting it here shifts <paramref name="cancellationToken"/> and
    /// <paramref name="contextBudget"/>, so every positional call site was updated to name them.
    /// </param>
    /// <param name="contextBudget">
    /// Opt-in agent context budget. When non-null, the working message list is compacted between tool
    /// rounds so a long in-step tool loop cannot overflow the model's context window and fail the
    /// step. Null — the default, and what every interactive/background caller passes — means the
    /// request list is sent exactly as today.
    /// </param>
    IAsyncEnumerable<ChatStreamItem> GetChatCompletionWithToolsAsync(
        IList<ChatMessage> messages,
        AiProvider provider,
        IList<AITool>? tools = null,
        Func<FunctionCallContent, Task<object?>>? toolHandler = null,
        string? mode = null,
        Guid? managedPersonaId = null,
        CancellationToken cancellationToken = default,
        AgentContextBudget? contextBudget = null);

    Task<bool> TestToolCallingAsync(AiProvider provider, CancellationToken cancellationToken = default);

    /// <summary>
    /// Strengthened tool-calling probe (R10): demands an actual tool call and returns true only when the
    /// provider emits a <c>FunctionCallContent</c>. Distinct from <see cref="TestToolCallingAsync"/> (which
    /// only checks the schema is accepted). Used by <c>IProviderCapabilityService</c>; never hard-blocks.
    /// </summary>
    Task<bool> TestToolCallEmittedAsync(AiProvider provider, CancellationToken cancellationToken = default);

    Task<bool> TestStreamingAsync(AiProvider provider, CancellationToken cancellationToken = default);

    Task<AiCompletionResult> OptimizeViaPiaCloudAsync(
        string text,
        Guid templateId,
        string language,
        bool isVoiceInput,
        string? mode = null,
        string? customPrompt = null,
        string? customTemplateName = null,
        CancellationToken cancellationToken = default);

    Task<string> GeneratePromptViaPiaCloudAsync(
        string styleDescription,
        string? mode = null,
        CancellationToken cancellationToken = default);

    Task TestPiaCloudConnectionAsync(CancellationToken cancellationToken = default);
}
