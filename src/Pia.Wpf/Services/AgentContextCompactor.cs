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
    /// Tokens charged for ONE pinned image-bearing turn. A deliberate BOUND, not a measurement: Pia
    /// re-encodes every attachment through <c>ImageAttachmentProcessor</c>, whose
    /// <c>MaxLongEdge = 1568</c> scales the long edge down, so the largest image that can reach a
    /// provider is 1568x1568 — about 3278 tokens at Anthropic's w*h/750 — and 3500 rounds that up
    /// rather than down. Bounding beats measuring for the same reason
    /// <see cref="ToolEvictionThreshold"/> is a threshold rather than a scaled window: the real
    /// per-provider figure is unknowable from here, and the two errors are NOT symmetric —
    /// over-charging compacts a little harder, while under-charging subtracts less from the window,
    /// leaves a larger input budget, and overflows the context.
    /// </summary>
    internal const int ImageTokenCharge = 3500;

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

        // The WITHHELD pinned messages still cost tokens on the wire, so charge them against the
        // window rather than letting the library budget as if they were free. Text length / 4
        // approximates the library's own bytes/4 accounting closely enough for a prefix that is text
        // in practice; an image-bearing turn is charged a flat ImageTokenCharge on top, and the
        // DIRECTION is the whole point: pinnedCost is SUBTRACTED from the window below, so
        // under-charging leaves a LARGER input budget and therefore LESS compaction — it errs toward
        // silently overflowing the context, not (as an earlier comment here claimed, backwards) toward
        // compacting. Charging the image properly makes the window smaller, so a request that used to
        // fit only because the attachment was counted at ~0 now compacts.
        //
        // Charged ONCE: the system messages are NOT included here because they are inside toCompact,
        // where the library counts them itself. Charging them in both places made a 2000-token system
        // prompt cost 4000 — measured, that evicted 1 of 11 messages at 2810 real input tokens of an
        // 8000 window, and 7 of 11 once the history reached ~2000 tokens.
        //
        // The head and instruction pins are charged UNCONDITIONALLY: they ship whether or not they
        // carry an image, so there is nothing to decide about them. Only the mid-list turns below are
        // admitted or refused.
        var pinnedCost = 0;
        for (var i = systemCount; i < headCount; i++)
            pinnedCost += ChargeFor(messages[i]);
        if (instruction is not null)
            pinnedCost += ChargeFor(instruction);

        // PIN IMAGE-BEARING TURNS. Without this an image attachment is the FIRST casualty of an
        // over-budget request: the library scores a message at raw bytes/4, so a 300 KB JPEG reports
        // ~75k phantom tokens and the whole turn carrying it is evicted while the goal that REFERS to
        // the image stays pinned — measured on the Live path as in=7 -> out=6 with no DataContent
        // surviving, i.e. the step confabulates about a screenshot it cannot see. Note the UNIT:
        // AssistantMessage.ToChatMessage fuses [TextContent, DataContent] into ONE ChatMessage, so what
        // is withheld here is a whole TURN that contains an image, never a bare image message.
        //
        // Admitted NEWEST-FIRST under a sub-cap, because every pinned token is subtracted from the
        // window: an unbounded pinned set drives 'window' below MaxOutputTokens, trips the early return
        // below and sends the request UNCOMPACTED — a provider 400 instead of a shrink. The cap is half
        // of the input budget left after the text pins, floored at ONE image: the half guarantees the
        // compacted history keeps at least as much room as the images take, and the floor keeps the
        // ordinary single-attachment case from being refused by integer halving on a small window (an
        // 8000/2000 provider halves to 2994, which is under one ImageTokenCharge). The second
        // condition is the hard stop the floor cannot cross: an image that would push 'window' to or
        // below MaxOutputTokens is NOT pinned, because a request that still compacts and loses the
        // image beats one that declines and fails outright. Tool content is never admitted, so lifting
        // a turn out of the compacted range can never separate a function call from its result.
        var imageAllowance = Math.Max(
            ImageTokenCharge,
            (contextBudget.WindowTokens - contextBudget.MaxOutputTokens - pinnedCost) / 2);

        HashSet<int>? pinnedImageIndices = null;
        var pinnedImageCost = 0;
        for (var i = messages.Count - 1; i >= headCount; i--)
        {
            if (i == instructionIndex || !HasImageContent(messages[i]) || HasToolContent(messages[i]))
                continue;

            var charged = pinnedImageCost + ImageTokenCharge;
            if (charged > imageAllowance
                || contextBudget.WindowTokens - (pinnedCost + charged) <= contextBudget.MaxOutputTokens)
            {
                break;
            }

            pinnedImageCost = charged;
            (pinnedImageIndices ??= []).Add(i);
        }

        pinnedCost += pinnedImageCost;

        // The system messages are handed to the library as well, so its grouping still sees a System
        // group and never counts it removable. Only the pinned goal, the pinned instruction and the
        // pinned image-bearing turns are withheld.
        var toCompact = new List<ChatMessage>(messages.Count);
        for (var i = 0; i < systemCount; i++)
            toCompact.Add(messages[i]);

        // ASCENDING, so the withheld images come out of this pass in the caller's original relative
        // order and re-attaching them needs no second sort.
        List<ChatMessage>? pinnedImages = null;
        for (var i = headCount; i < messages.Count; i++)
        {
            if (i == instructionIndex)
                continue;
            if (pinnedImageIndices is not null && pinnedImageIndices.Contains(i))
            {
                (pinnedImages ??= []).Add(messages[i]);
                continue;
            }
            toCompact.Add(messages[i]);
        }

        // Newly reachable now that whole TURNS can be withheld: [goal, image, image, instruction] pins
        // every non-system message, leaving the library an empty (or system-only) list. Handing that to
        // the strategy would at best round-trip to the same early return below, and at worst throw into
        // the degrade path and log a "compaction failed" warning for a request that had nothing to
        // compact. NoSystemMessage_StillPinsTheGoal exists because this shape class is real.
        if (toCompact.Count <= systemCount)
            return [.. messages];

        var window = contextBudget.WindowTokens - pinnedCost;
        if (window <= contextBudget.MaxOutputTokens)
        {
            // The pinned prefix alone eats the input budget. Nothing safe left to decide; send it —
            // and that send is very likely a provider 400, which is why this is a WARNING now rather
            // than the Debug line it was. The level is fixed at COMPILE TIME: Bootstrapper sets both
            // the minimum level and the file sink from IsDevMode, which is '#if DEBUG', and there is no
            // AddFilter or AddConfiguration anywhere in src and no Logging section in appsettings.json
            // — so in a release build a Debug line is unrecoverable and the user cannot raise the level
            // to get it back. Numbers only, never message content, because users attach this log to
            // support tickets. Both windows are reported: the configured one, which is what the user
            // can change, and what is left of it after the pins, which is what this message is about.
            logger.LogWarning(
                "Context compaction skipped: the pinned prefix leaves no input budget ({MessageCount} messages, "
                + "pinned {PinnedTokens} tokens of {WindowTokens}, {RemainingTokens} left against {MaxOutputTokens} reserved output)",
                messages.Count,
                pinnedCost,
                contextBudget.WindowTokens,
                window,
                contextBudget.MaxOutputTokens);
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
            //
            // The pinned image-bearing turns go back immediately BEFORE that instruction, in their
            // original relative order, so the model reads the image and then the sentence telling it
            // what to do with it. SHAPE: an image turn is User-role in practice, so this can place two
            // user messages next to each other. Accepted, because it introduces NOTHING NEW — every
            // first-step agent request Pia already sends is [system, goal(User), "Execute step 1"(User)]
            // and goes out uncompacted exactly like that (the fixture in
            // ShortList_IsNeverCompacted_EvenFarOverBudget is that shape). The alternative is the one
            // that would do damage: splicing an image back at its original index, whose neighbours may
            // have been evicted, is how call/result adjacency gets broken. Nothing withheld here is
            // tool content (the pin loop refuses it), so every surviving FunctionCallContent still sits
            // next to its FunctionResultContent.
            var pinnedCount = head.Count + (instruction is null ? 0 : 1) + (pinnedImages?.Count ?? 0);
            // The pinned system run is in head AND was handed to the library inside toCompact (so its
            // grouping still saw a System group), so it comes back in 'kept' and has to be skipped here or
            // it ships twice. Skipped BY IDENTITY, not by role: a role filter also deleted every OTHER
            // system message the library returned — one it synthesized, or a non-leading system message a
            // caller placed after a user message, which the head loop above never reaches, IS inside
            // toCompact, and must survive. ReferenceEqualityComparer keeps ChatMessage value equality from
            // making two distinct-but-equal messages indistinguishable. The pinned goal and instruction
            // need no entry: they were withheld from toCompact, so the library cannot hand them back. Left
            // null when there is no leading system run at all — nothing to exclude, nothing to allocate.
            HashSet<ChatMessage>? pinnedSystem = null;
            if (systemCount > 0)
            {
                pinnedSystem = new HashSet<ChatMessage>(systemCount, ReferenceEqualityComparer.Instance);
                for (var i = 0; i < systemCount; i++)
                    pinnedSystem.Add(messages[i]);
            }

            var compacted = new List<ChatMessage>(pinnedCount + kept.Count);
            compacted.AddRange(head);
            foreach (var message in kept)
            {
                if (pinnedSystem is null || !pinnedSystem.Contains(message))
                    compacted.Add(message);
            }
            if (pinnedImages is not null)
                compacted.AddRange(pinnedImages);
            if (instruction is not null)
                compacted.Add(instruction);

            // Counts only: this line lands in a support-attachable log, so no message content.
            // INFORMATION, not Debug: the level is fixed at compile time (see the skip path above), so
            // a Debug line means a release build can never tell anyone whether compaction ran at all —
            // and this log is the only evidence a step was shrunk. Not free either: the in-step tool
            // loop can reach it once per round, which is why it stays one short counts-only line.
            logger.LogInformation(
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

    /// <summary>
    /// What ONE pinned message costs against the window: the same text bytes/4 estimate the library
    /// uses, plus a flat <see cref="ImageTokenCharge"/> when the turn carries an image — bytes/4 is
    /// wrong in both directions on an attachment (~75k for a 300 KB JPEG, ~0 for the text fronting it).
    /// Used for the head AND the instruction pin so the two cannot drift apart.
    /// </summary>
    private static int ChargeFor(ChatMessage message) =>
        (message.Text?.Length ?? 0) / 4 + (HasImageContent(message) ? ImageTokenCharge : 0);

    /// <summary>
    /// True when the message carries an image attachment. The media type is matched exactly the way
    /// PiaCloudChatClient's outbound converter matches it, so the pin and the wire agree on what counts
    /// as an image.
    /// </summary>
    private static bool HasImageContent(ChatMessage message)
    {
        foreach (var content in message.Contents)
        {
            if (content is DataContent data
                && data.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the message is part of a tool exchange. Such a turn is never withheld from the
    /// compacted range: lifting it out would separate a function call from its result, and a provider
    /// rejects that request outright.
    /// </summary>
    private static bool HasToolContent(ChatMessage message)
    {
        foreach (var content in message.Contents)
        {
            if (content is FunctionCallContent or FunctionResultContent)
                return true;
        }

        return false;
    }
}

#pragma warning restore MAAI001
