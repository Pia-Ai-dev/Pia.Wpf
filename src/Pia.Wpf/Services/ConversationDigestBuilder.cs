using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services;

/// <summary>What a run carries into its plan turn from the chat it was started in, and what producing it cost.</summary>
internal readonly record struct ConversationDigestResult(string? Text, UsageDetails? Usage);

/// <summary>
/// Renders the conversation an agent run was started from into one fenced block for the plan and re-plan
/// turns, which otherwise see only the goal sentence. Static like <see cref="AgentContextCompactor"/>: the
/// one caller already holds the chat, the provider, a logger and an AI client.
/// </summary>
internal static class ConversationDigestBuilder
{
    /// <summary>Hard ceiling on the excerpt: on a wide-window provider the halved budget below compacts
    /// nothing, so this is what bounds the plan turn's cost.</summary>
    internal const int MaxDigestChars = 12000;

    /// <summary>Same cap the planner puts on its own reasoning analysis — this text lands in the same message.</summary>
    private const int MaxSummaryChars = 4000;

    private const string FenceOpen =
        "--- Earlier in this conversation, before the goal above (context only — plan the goal, not this) ---";

    private const string FenceClose = "--- end of the conversation ---";

    private const string SummarySystemPrompt =
        "You summarize a conversation so another model can plan a follow-up task from it. Cover what was "
        + "decided, what was already tried or produced, and which files, names and numbers were named — keep "
        + "those exact. No preamble, no advice, no invented detail. Respond with only the summary.";

    /// <summary>The block to fold into the plan turn's user message. Never throws but for cancellation — a
    /// run must not fail because its context could not be built.</summary>
    internal static async Task<ConversationDigestResult> BuildAsync(
        SyncAssistantChat? chat,
        string goal,
        AgentContextMode mode,
        AiProvider provider,
        IAiClientService? ai,
        ILogger logger,
        CancellationToken ct)
    {
        if (mode == AgentContextMode.Off || chat is null || chat.Messages.Count == 0)
            return default;

        var prior = RowsBeforeGoal(chat.Messages, goal);
        if (prior.Count == 0)
            return default;

        var verbatim = await RenderVerbatimAsync(prior, provider, logger, ct).ConfigureAwait(false);
        if (verbatim is null)
            return default;

        logger.LogInformation(
            "Conversation digest: {RowCount} earlier rows rendered to {Chars} chars ({Mode}).",
            prior.Count, verbatim.Length, mode);
        logger.SensitiveDebug("Conversation digest excerpt:\n{Digest}", verbatim);

        if (mode != AgentContextMode.Summary || ai is null)
            return new ConversationDigestResult(Fence(verbatim), null);

        return await SummarizeAsync(verbatim, provider, ai, logger, ct).ConfigureAwait(false);
    }

    /// <summary>Everything before the run's own goal row. Searched from the END, so one rule drops both the
    /// goal and the clarification exchange the run itself posted after it.</summary>
    private static List<SyncAssistantChatMessage> RowsBeforeGoal(
        List<SyncAssistantChatMessage> rows, string goal)
    {
        var trimmedGoal = goal.Trim();
        var cut = rows.Count;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (string.Equals(rows[i].Role, "user", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rows[i].Content.Trim(), trimmedGoal, StringComparison.Ordinal))
            {
                cut = i;
                break;
            }
        }

        var kept = new List<SyncAssistantChatMessage>(cut);
        for (var i = 0; i < cut; i++)
        {
            if (!string.IsNullOrWhiteSpace(rows[i].Content))
                kept.Add(rows[i]);
        }
        return kept;
    }

    /// <summary>Prose only: "which files exist" is already answered by the working-folder listing the same
    /// message carries, and splicing the tool exchanges in would dwarf the prose.</summary>
    private static async Task<string?> RenderVerbatimAsync(
        IReadOnlyList<SyncAssistantChatMessage> rows, AiProvider provider, ILogger logger, CancellationToken ct)
    {
        var messages = rows
            .Select(r => new ChatMessage(
                string.Equals(r.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? ChatRole.Assistant
                    : ChatRole.User,
                r.Content))
            .ToList();

        // HALF the window: the same request also carries the system prompt, the goal, the working-folder
        // listing and the emit_plan schema. Zero reserved output — this pass emits nothing.
        var budget = AgentContextBudget.From(provider) is { } configured
            ? new AgentContextBudget(Math.Max(2, configured.WindowTokens / 2), 0)
            : (AgentContextBudget?)null;

        var kept = await AgentContextCompactor.CompactAsync(messages, budget, logger, ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        foreach (var message in kept)
        {
            var text = message.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                continue;
            sb.Append(message.Role == ChatRole.Assistant ? "Assistant: " : "User: ").AppendLine(text);
        }

        var rendered = sb.ToString().TrimEnd();
        if (rendered.Length == 0)
            return null;

        // From the FRONT: in a conversation the recent turns are the ones the follow-up refers to.
        if (rendered.Length > MaxDigestChars)
            rendered = "… (earlier turns omitted)\n" + rendered[^MaxDigestChars..];

        return rendered;
    }

    /// <summary>Runs over the already-rendered excerpt, so a long chat cannot make this the request that
    /// overflows. Any failure degrades to that excerpt and still reports the usage it was charged.</summary>
    private static async Task<ConversationDigestResult> SummarizeAsync(
        string verbatim, AiProvider provider, IAiClientService ai, ILogger logger, CancellationToken ct)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SummarySystemPrompt),
                new(ChatRole.User, $"Summarize this conversation:\n{verbatim}"),
            };

            var response = await ai.GetChatResponseAsync(
                messages, provider, tools: null, mode: AgentTurnRouting.Mode,
                personaModelType: AgentTurnRouting.ModelType, cancellationToken: ct).ConfigureAwait(false);

            var usage = response.Usage; // paid for regardless of what came back
            var text = response.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                logger.LogInformation("Conversation summary turn produced no text; using the verbatim excerpt.");
                return new ConversationDigestResult(Fence(verbatim), usage);
            }

            if (text.Length > MaxSummaryChars)
                text = text[..MaxSummaryChars] + "\n… (summary truncated)";

            logger.LogInformation("Conversation digest summarized to {Chars} chars.", text.Length);
            logger.SensitiveDebug("Conversation digest summary: {Summary}", text);
            return new ConversationDigestResult(Fence(text), usage);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // a genuine run cancel is never a degrade
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Conversation summary turn failed ({Error}); using the verbatim excerpt.", ex.GetType().Name);
            return new ConversationDigestResult(Fence(verbatim), null);
        }
    }

    private static string Fence(string body) => $"{FenceOpen}\n{body}\n{FenceClose}";
}
