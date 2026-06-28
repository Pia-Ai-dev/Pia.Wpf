using OpenAI.Chat;
using OpenAI.Responses;
using Pia.Models;

namespace Pia.Services.Providers;

/// <summary>
/// Maps the cross-provider <see cref="ReasoningEffort"/> enum onto the OpenAI
/// SDK's reasoning-effort enums. Returns null when the parameter should be
/// omitted (tool-using turns, or effort configured to None) so callers avoid
/// sending an unsupported value to models that don't accept "none".
/// </summary>
internal static class ReasoningEffortMapping
{
#pragma warning disable OPENAI001
    public static ChatReasoningEffortLevel? ToOpenAi(ReasoningEffort? effort, bool hasTools)
    {
        if (!ShouldSend(effort, hasTools)) return null;

        return effort switch
        {
            ReasoningEffort.Minimal or ReasoningEffort.Low => ChatReasoningEffortLevel.Low,
            ReasoningEffort.Medium => ChatReasoningEffortLevel.Medium,
            _ => ChatReasoningEffortLevel.High,
        };
    }

    // The Responses API supports reasoning alongside tool calls, and the assistant always
    // sends a tool schema — so reasoning is gated only on the configured effort, NOT on the
    // presence of tools. (The Chat Completions path above keeps the tool gate.) Without this,
    // reasoning and its summary would never surface in the tool-enabled assistant.
    public static ResponseReasoningEffortLevel? ToOpenAiResponses(ReasoningEffort? effort)
    {
        if (effort is null or ReasoningEffort.None) return null;

        return effort switch
        {
            ReasoningEffort.Minimal or ReasoningEffort.Low => ResponseReasoningEffortLevel.Low,
            ReasoningEffort.Medium => ResponseReasoningEffortLevel.Medium,
            _ => ResponseReasoningEffortLevel.High,
        };
    }

    // Omit the parameter when tools are present, when effort is unset, or when
    // effort is None — not all models accept "none" as a valid value.
    private static bool ShouldSend(ReasoningEffort? effort, bool hasTools)
        => !hasTools && effort is not null and not ReasoningEffort.None;
#pragma warning restore OPENAI001
}
