using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>A CLASS because <see cref="ToolDispatchContext"/> is by-value: a flag set on the handler's copy
/// could never reach the loop, and <c>readonly</c> forbids a setter outright.</summary>
public sealed class ToolLoopStopSignal
{
    public bool IsStopRequested { get; private set; }

    public void RequestStop() => IsStopRequested = true;
}

/// <summary>What produced a parked picture, so the consumed placeholder can name it.</summary>
public enum ToolLoopImageSource
{
    ScreenCapture,
    ImageFile,
}

/// <summary>One picture a tool produced for the model, parked until every result of its round is appended.</summary>
public sealed record ToolLoopImage(
    string CallId, byte[] Bytes, string MediaType, int Width, int Height, string Caption,
    ToolLoopImageSource Source = ToolLoopImageSource.ScreenCapture);

/// <summary>Marks an injected image message so the swap and the compactor find it without reading its text.</summary>
public sealed record ToolLoopImageTag(
    string CallId, int Width, int Height,
    ToolLoopImageSource Source = ToolLoopImageSource.ScreenCapture);

/// <summary>Where a handler parks a picture a tool result cannot carry — a result has no image slot and any
/// non-string one is JSON-serialized — for the loop to append after the round's last result.</summary>
public sealed class ToolLoopImageChannel
{
    public const string MessageTagKey = "pia.toolImage";

    /// <summary>Per ROUND, which is per flight: <c>Consume</c> withdraws a round's images before the next
    /// one, so no turn-wide cap is needed. Four is what the compactor's image allowance affords.</summary>
    public const int MaxImagesPerRound = 4;

    private static readonly AsyncLocal<ToolLoopImageChannel?> _current = new();

    private readonly List<ToolLoopImage> _parked = [];

    public ToolLoopImageChannel(AiProviderType providerType) => ProviderType = providerType;

    /// <summary>The channel of the dispatch on this logical flow; null means no loop, so no way to hand a
    /// picture over, so the handler must refuse before it captures anything.</summary>
    public static ToolLoopImageChannel? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    /// <summary>Where this round's frame would go — the fact only the loop holds.</summary>
    public AiProviderType ProviderType { get; }

    public int Count
    {
        get { lock (_parked) return _parked.Count; }
    }

    /// <summary>Uncapped: the one caller is <c>screen_capture</c>, one frame per approval card the user
    /// already clicked through. A tool the model can call unattended uses <see cref="TryPark"/>.</summary>
    public void Park(ToolLoopImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        lock (_parked) _parked.Add(image);
    }

    /// <summary>False when this round is already full, so the caller can say so in its tool result.</summary>
    public bool TryPark(ToolLoopImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        // Inside the lock: two handlers in one round dispatch sequentially today, but nothing in this
        // type says they must, and a check-then-add outside it would let both past a full channel.
        lock (_parked)
        {
            if (_parked.Count >= MaxImagesPerRound) return false;
            _parked.Add(image);
            return true;
        }
    }

    public IReadOnlyList<ToolLoopImage> Drain()
    {
        lock (_parked)
        {
            if (_parked.Count == 0) return [];
            var drained = _parked.ToArray();
            _parked.Clear();
            return drained;
        }
    }
}

/// <summary>
/// What the tool LOOP knows about a dispatch that the handler cannot work out for itself. Today: the round.
/// <para>
/// A record STRUCT rather than a bare <c>int</c> parameter, so the next thing the loop needs to
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
/// <param name="Stop">The loop's stop flag, or null when the caller runs no loop that could stop.</param>
public readonly record struct ToolDispatchContext(int Round, ToolLoopStopSignal? Stop = null);

/// <summary>
/// The tool-dispatch callback the tool loop invokes for one <c>FunctionCallContent</c>. A NAMED delegate
/// rather than the raw <c>Func&lt;...&gt;</c> it replaced: with ~140 references across src and tests, a named
/// type makes every future shape change to the dispatch contract a change in ONE place instead of a
/// find-and-replace across the suite.
/// </summary>
public delegate Task<object?> ToolCallHandler(FunctionCallContent call, ToolDispatchContext context);

public interface IAiClientService
{
    /// <param name="mode">
    /// Relayed to the Pia Cloud transport as <c>X-Pia-Mode</c> so the server can pick the right model
    /// catalog. Null ⇒ header omitted, which is what a caller with no window-mode context passes.
    /// </param>
    Task<AiCompletionResult> SendRequestAsync(
        AiProvider provider, string prompt, CancellationToken cancellationToken = default, string? mode = null);

    IAsyncEnumerable<string> StreamChatCompletionAsync(
        IList<ChatMessage> messages,
        AiProvider provider,
        string? mode = null,
        CancellationToken cancellationToken = default);

    /// <param name="managedPersonaId">
    /// The persona driving this turn, relayed to the Pia Cloud transport as <c>X-Pia-Persona</c>.
    /// See <see cref="GetChatCompletionWithToolsAsync"/>.
    /// </param>
    /// <param name="personaModelType">
    /// The model-routing hint, relayed as <c>metadata.pia_persona_type</c>.
    /// See <see cref="GetChatCompletionWithToolsAsync"/>.
    /// </param>
    Task<ChatResponse> GetChatResponseAsync(
        IList<ChatMessage> messages,
        AiProvider provider,
        IList<AITool>? tools = null,
        string? mode = null,
        Guid? managedPersonaId = null,
        string? personaModelType = null,
        CancellationToken cancellationToken = default);

    /// <param name="managedPersonaId">
    /// The persona driving this turn, relayed to the Pia Cloud transport as <c>X-Pia-Persona</c> so the
    /// server can scope persona-bound KBs/connectors. Null ⇒ header omitted, which is what every
    /// non-assistant caller (title generation, optimize, planner, verifier) passes. It was inserted
    /// after <paramref name="mode"/> to mirror how <c>mode</c> is threaded end-to-end rather than to keep
    /// call sites compiling — inserting it here shifts <paramref name="cancellationToken"/> and
    /// <paramref name="contextBudget"/>, so every positional call site was updated to name them.
    /// </param>
    /// <param name="personaModelType">
    /// The persona's model-routing hint, relayed to the Pia Cloud transport as
    /// <c>metadata.pia_persona_type</c> so the server can route Assistant-mode chat to the group's
    /// catalog provider for that type. Null ⇒ the key is omitted, which is what every caller without
    /// a resolved persona passes. Ignored by every non-Pia-Cloud provider.
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
        string? personaModelType = null,
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
