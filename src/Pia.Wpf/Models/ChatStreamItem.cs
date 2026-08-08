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
public sealed record Finished(UsageDetails? Usage, string Model, bool Protected = false, bool ToolRoundsExhausted = false) : ChatStreamItem;
