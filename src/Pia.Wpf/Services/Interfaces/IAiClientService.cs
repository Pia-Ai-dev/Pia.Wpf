using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// What the tool LOOP knows about a dispatch that the handler cannot work out for itself. Today: the round.
/// <para>
/// A record STRUCT with one field rather than a bare <c>int</c> parameter, so the next thing the loop needs to
/// tell a gate costs no further churn across the ~140 references to
/// <see cref="ToolCallHandler"/>. Named <c>ToolDispatchContext</c>, not <c>ToolCallContext</c>, to stay clear
/// of the ambient <c>TaskAmbient.TaskContext</c> that <c>IAgentTimelineService</c>'s remarks explicitly reject
/// as an id carrier: this is an EXPLICIT parameter on one call, not ambient state.
/// </para>
/// <para>
/// It deliberately does NOT carry the call id: <c>FunctionCallContent.CallId</c> is already on the
/// <c>FunctionCallContent</c> every handler receives, so duplicating it here would create two spellings of
/// one fact and a way for them to disagree.
/// </para>
/// </summary>
/// <param name="Round">The provider tool-loop round this call is being dispatched in, <b>1-based</b> — the
/// same number every log line inside that loop prints (<c>round + 1</c>), so an audit row and a log line agree
/// without an off-by-one caveat.</param>
public readonly record struct ToolDispatchContext(int Round);

/// <summary>
/// The tool-dispatch callback the tool loop invokes for one <c>FunctionCallContent</c>. A NAMED delegate
/// rather than the raw <c>Func&lt;...&gt;</c> it replaced: with ~140 references across src and tests, a named
/// type makes every future shape change to the dispatch contract a change in ONE place instead of a
/// find-and-replace across the suite.
/// </summary>
public delegate Task<object?> ToolCallHandler(FunctionCallContent call, ToolDispatchContext context);

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
        ToolCallHandler? toolHandler = null,
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
