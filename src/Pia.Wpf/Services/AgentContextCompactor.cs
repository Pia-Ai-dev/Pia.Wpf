// MAAI001: every public type in Microsoft.Agents.AI.Compaction is [Experimental], and MAAI001 fires
// at declarations as well as at call sites. Containment therefore depends on ONE rule, enforced by
// review and by the 0-warning build bar: no compaction type may appear in a Pia signature, field,
// property, or return type. Inside this file the types live only in method-local scope, so this
// single pragma is the only suppression in the solution and a project-wide <NoWarn> is not needed
// (which would silently hide experimental-API adoption everywhere else).
#pragma warning disable MAAI001

using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Models;

namespace Pia.Services;

/// <summary>
/// Shrinks an outgoing agent request so an accumulated transcript cannot overflow the model's
/// context window and fail a step.
/// <para>
/// Static on purpose: both call sites already have a provider and a logger in scope, so this needs
/// no DI registration, no constructor parameter on HeadlessTurnExecutor or ChatSession, and no edits
/// to their test construction sites. Precedent for static classes in Pia.Services: ProviderFingerprint,
/// GuardrailMarker, TaskAmbient, TokenMapAmbient, StreamThinkTagParser, WebCitationExtractor.
/// </para>
/// <para>
/// HARD GUARDRAIL: this operates on the request copy only. It takes an IReadOnlyList and returns a
/// new List; it never touches a persisted transcript. On the Headless path the executor's _messages
/// (List&lt;ChatMessage&gt;) and _persisted (List&lt;SyncAssistantChatMessage&gt;) are different
/// types with no aliasing, and the only route from executor state to the DB is BuildChatSnapshot's
/// <c>Messages = [.. _persisted]</c> — so a pass over a ChatMessage list is type-incapable of
/// reaching persistence, and a resume still replays the full history.
/// </para>
/// </summary>
internal static class AgentContextCompactor
{
    /// <summary>
    /// Fraction of the input budget at which tool-result eviction triggers. The library default is
    /// 0.5; we act 10% earlier because the library scores context as bytes/4, which under-counts
    /// dense JSON tool results. Lowering the threshold rather than scaling the window down to ~70%
    /// buys the same conservatism on a knob that cannot throw: the strategy constructor rejects
    /// maxOutputTokens &gt;= maxContextWindowTokens, so a scaled window turns an ordinary
    /// 8k-window / 8k-max-output settings typo into a failed step — the outcome this file exists to
    /// remove. It also keeps the input budget honest.
    /// </summary>
    internal const double ToolEvictionThreshold = 0.45;

    /// <summary>
    /// Fraction of the input budget at which truncation triggers (library default 0.8). Must stay
    /// at or above <see cref="ToolEvictionThreshold"/> or the constructor throws.
    /// </summary>
    internal const double TruncationThreshold = 0.70;

    /// <summary>
    /// The point below which compaction cannot do anything: the library short-circuits when there
    /// is at most one included non-system group and floors at its minimum-preserved-groups count,
    /// so a first-step [system, goal, instruction] list is a verified no-op even far over budget.
    /// Bail explicitly rather than paying for the index build.
    /// </summary>
    private const int MinimumCompactableMessageCount = 4;

    /// <summary>
    /// Returns a possibly smaller copy of <paramref name="messages"/> that fits the budget, or the
    /// list unchanged when there is nothing to do, no budget configured, or compaction faulted.
    /// </summary>
    /// <remarks>
    /// A compaction fault NEVER fails a step. The whole degrade path lives in this one method —
    /// including the strategy construction, because the constructor throws ArgumentOutOfRangeException
    /// on bad numbers and a provider-dialog typo must not become a failed run. Cancellation is
    /// rethrown: it is not a fault, and swallowing it would mask a stop.
    /// </remarks>
    internal static async Task<List<ChatMessage>> CompactAsync(
        IReadOnlyList<ChatMessage> messages,
        AgentContextBudget? budget,
        ILogger logger,
        CancellationToken ct)
    {
        if (budget is not { } contextBudget || messages.Count < MinimumCompactableMessageCount)
            return [.. messages];

        // Pin the leading system run plus the first following user message — the step goal.
        // Verified empirically: ContextWindowCompactionStrategy has no pin/protect hook, and over an
        // agent-step-shaped list the FIRST casualty of an over-budget step is the user message
        // stating what the step was asked to do. Splicing it out of the compacted range is the only
        // way to keep it.
        var pinnedCount = 0;
        while (pinnedCount < messages.Count && messages[pinnedCount].Role == ChatRole.System)
            pinnedCount++;

        var systemCount = pinnedCount;
        if (pinnedCount < messages.Count && messages[pinnedCount].Role == ChatRole.User)
            pinnedCount++;

        var pinned = new List<ChatMessage>(pinnedCount);
        for (var i = 0; i < pinnedCount; i++)
            pinned.Add(messages[i]);

        // The system messages are handed to the library as well, so its grouping still sees a System
        // group and never counts it removable. Only the pinned goal is withheld.
        var toCompact = new List<ChatMessage>(messages.Count - pinnedCount + systemCount);
        for (var i = 0; i < systemCount; i++)
            toCompact.Add(messages[i]);
        for (var i = pinnedCount; i < messages.Count; i++)
            toCompact.Add(messages[i]);

        // The pinned messages still cost tokens on the wire, so charge them against the window
        // rather than letting the library budget as if they were free. Text length / 4 approximates
        // the library's own bytes/4 accounting closely enough for a prefix that is text in practice;
        // a pinned image attachment is under-charged here, which errs toward compacting rather than
        // toward silently overflowing.
        var pinnedCost = 0;
        foreach (var message in pinned)
            pinnedCost += (message.Text?.Length ?? 0) / 4;

        var window = contextBudget.WindowTokens - pinnedCost;
        if (window <= contextBudget.MaxOutputTokens)
        {
            // The pinned prefix alone eats the input budget. Nothing safe left to decide; send it.
            logger.LogDebug(
                "Context compaction skipped: the pinned prefix leaves no input budget ({MessageCount} messages)",
                messages.Count);
            return [.. messages];
        }

        try
        {
            var strategy = new ContextWindowCompactionStrategy(
                window,
                contextBudget.MaxOutputTokens,
                ToolEvictionThreshold,
                TruncationThreshold);

            // Materialize at the boundary: CompactAsync returns a deferred query over a still-mutable
            // index, and Pia's consumers enumerate their request list more than once (the
            // tool-disabled retry in AiClientService re-reads it).
            var kept = (await CompactionProvider
                .CompactAsync(strategy, toCompact, logger, ct)
                .ConfigureAwait(false))
                .ToList();

            if (kept.Count >= toCompact.Count)
                return [.. messages]; // Nothing was evicted — preserve the caller's order and instances.

            var compacted = new List<ChatMessage>(pinned.Count + kept.Count);
            compacted.AddRange(pinned);
            foreach (var message in kept)
            {
                if (message.Role != ChatRole.System)
                    compacted.Add(message);
            }

            // Counts only: this line lands in a support-attachable log, so no message content.
            logger.LogDebug(
                "Context compaction reduced the request from {BeforeCount} to {AfterCount} messages ({PinnedCount} pinned)",
                messages.Count,
                compacted.Count,
                pinned.Count);

            return compacted;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Context compaction failed; sending the uncompacted request ({MessageCount} messages)",
                messages.Count);
            return [.. messages];
        }
    }
}

#pragma warning restore MAAI001
