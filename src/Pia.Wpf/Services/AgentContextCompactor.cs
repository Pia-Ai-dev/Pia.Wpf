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
        // Checked HERE, before any early return, because the "cancellation is rethrown" contract below is
        // unconditional but the catch-and-rethrow could only honour it when the library happened to observe
        // the token — and it only gets the chance on a request that is actually over budget. An under-budget
        // request (or one short-circuited by the two early returns) did the whole pin/index pass on a run
        // that had already been stopped and reported success. A cancelled run should not be compacting at
        // all, and every caller propagates OperationCanceledException already.
        ct.ThrowIfCancellationRequested();

        if (budget is not { } contextBudget || messages.Count < MinimumCompactableMessageCount)
            return [.. messages];

        // Pin BOTH ends of the instruction pair: the leading system run plus the first following user
        // message (the run goal), and the most recent user message (the step instruction).
        // Verified empirically: ContextWindowCompactionStrategy has no pin/protect hook, and over an
        // agent-step-shaped list the FIRST casualty of an over-budget step is a user message stating
        // what the step was asked to do. Splicing those out of the compacted range is the only way to
        // keep them.
        var systemCount = 0;
        while (systemCount < messages.Count && messages[systemCount].Role == ChatRole.System)
            systemCount++;

        var headCount = systemCount;
        if (headCount < messages.Count && messages[headCount].Role == ChatRole.User)
            headCount++;

        // The TAIL pin. Pinning only the head kept the goal and dropped "Execute step N: <intent>.
        // Expected: <artifact>" as soon as the in-step tool loop appended a few rounds behind it —
        // measured against Microsoft.Agents.AI 1.15.0: an 8-prior-step request at window 8000/2000
        // came back as [system, goal, assistant, tool, assistant, tool] with the instruction gone, so
        // the model was asked to keep going with no statement of which step or artifact it was
        // producing and answered against the whole run goal instead (AgentVerifier then fails the
        // ExpectedArtifact check). The newest user message IS that instruction on both executor
        // paths, and on an ordinary chat request it is the user's latest turn — pinnable in either
        // reading. -1 when the newest user message is the already-pinned goal (or there is none).
        var instructionIndex = -1;
        for (var i = messages.Count - 1; i >= headCount; i--)
        {
            if (messages[i].Role == ChatRole.User)
            {
                instructionIndex = i;
                break;
            }
        }

        var head = new List<ChatMessage>(headCount);
        for (var i = 0; i < headCount; i++)
            head.Add(messages[i]);

        var instruction = instructionIndex >= 0 ? messages[instructionIndex] : null;

        // The system messages are handed to the library as well, so its grouping still sees a System
        // group and never counts it removable. Only the pinned goal and instruction are withheld.
        var toCompact = new List<ChatMessage>(messages.Count);
        for (var i = 0; i < systemCount; i++)
            toCompact.Add(messages[i]);
        for (var i = headCount; i < messages.Count; i++)
        {
            if (i != instructionIndex)
                toCompact.Add(messages[i]);
        }

        // The WITHHELD pinned messages still cost tokens on the wire, so charge them against the
        // window rather than letting the library budget as if they were free. Text length / 4
        // approximates the library's own bytes/4 accounting closely enough for a prefix that is text
        // in practice; a pinned image attachment is under-charged here, which errs toward compacting
        // rather than toward silently overflowing.
        //
        // Charged ONCE: the system messages are NOT included here because they are inside toCompact,
        // where the library counts them itself. Charging them in both places made a 2000-token system
        // prompt cost 4000 — measured, that evicted 1 of 11 messages at 2810 real input tokens of an
        // 8000 window, and 7 of 11 once the history reached ~2000 tokens.
        var pinnedCost = 0;
        for (var i = systemCount; i < headCount; i++)
            pinnedCost += (messages[i].Text?.Length ?? 0) / 4;
        if (instruction is not null)
            pinnedCost += (instruction.Text?.Length ?? 0) / 4;

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

            // The pinned instruction goes back LAST. When the caller's list already ended with it — every
            // step request the executors build — that reproduces the original order exactly. When the
            // in-step tool loop had appended rounds behind it, the instruction moves from the middle to the
            // end of the request, which is the deliberate choice: it is a valid shape (a user turn after a
            // tool result), it keeps the surviving call/result pairs adjacent, and it states the step the
            // model must finish as the most recent thing it was told.
            var pinnedCount = head.Count + (instruction is null ? 0 : 1);
            var compacted = new List<ChatMessage>(pinnedCount + kept.Count);
            compacted.AddRange(head);
            foreach (var message in kept)
            {
                if (message.Role != ChatRole.System)
                    compacted.Add(message);
            }
            if (instruction is not null)
                compacted.Add(instruction);

            // Counts only: this line lands in a support-attachable log, so no message content.
            logger.LogDebug(
                "Context compaction reduced the request from {BeforeCount} to {AfterCount} messages ({PinnedCount} pinned)",
                messages.Count,
                compacted.Count,
                pinnedCount);

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
