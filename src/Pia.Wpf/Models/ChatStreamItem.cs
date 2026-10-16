using Microsoft.Extensions.AI;

namespace Pia.Models;

public abstract record ChatStreamItem;

public sealed record TextDelta(string Text) : ChatStreamItem;

/// <summary>A chunk of model reasoning / "thinking" content, surfaced separately from the
/// visible answer. Funnels every provider's reasoning channel (M.E.AI
/// <see cref="Microsoft.Extensions.AI.TextReasoningContent"/>, OpenRouter's <c>reasoning</c>
/// field, inline <c>&lt;think&gt;</c> tags) into <c>AssistantMessage.ThinkingContent</c>.</summary>
public sealed record ReasoningDelta(string Text) : ChatStreamItem;

/// <param name="ToolRoundsExhausted">True when the in-step tool loop ran out of rounds before the model
/// stopped calling tools on its own.</param>
/// <param name="Provider">Display label of the model's provider; null for Pia Cloud (see <see cref="AnswerProvenance"/>).</param>
public sealed record Finished(
    UsageDetails? Usage, string Model, string? Provider = null, bool Protected = false, bool ToolRoundsExhausted = false) : ChatStreamItem;

/// <summary>A tool call was just dispatched; the next <see cref="TextDelta"/> starts a fresh model turn
/// built on the tool result, not a continuation of the text streamed before the call.</summary>
public sealed record ToolRoundCompleted : ChatStreamItem;

/// <summary>One tool round's appended messages, emitted after the dispatch; already tokenized.</summary>
/// <param name="Messages">Calls and capped results only — the shape that is persisted and resumed.</param>
/// <param name="Sent">The round exactly as the next request sends it, so a later step can extend that request.</param>
public sealed record ToolRoundExchange(
    int Round, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ChatMessage> Sent) : ChatStreamItem;

/// <summary>The final round's assistant message(s) as received, without reasoning; absent after a re-ask or
/// wrap-up round, whose nudge the next step would not carry.</summary>
public sealed record FinalAnswer(IReadOnlyList<ChatMessage> Messages) : ChatStreamItem;
