using OpenAI.Chat;
using Pia.Models;

namespace Pia.Services.Providers;

/// <summary>
/// Maps the cross-provider <see cref="ReasoningEffort"/> enum onto the OpenAI
/// SDK's <see cref="ChatReasoningEffortLevel"/>. Returns null when the parameter
/// should be omitted (tool-using turns, or effort configured to None) so callers
/// avoid sending an unsupported value to models that don't accept "none".
/// </summary>
internal static class ReasoningEffortMapping
{
#pragma warning disable OPENAI001
    public static ChatReasoningEffortLevel? ToOpenAi(ReasoningEffort? effort, bool hasTools)
    {
        // Omit the parameter entirely when there are tools, when effort is unset,
        // or when effort is None — not all models accept "none" as a valid value.
        if (hasTools || effort is null or ReasoningEffort.None) return null;

        return effort switch
        {
            ReasoningEffort.Minimal or ReasoningEffort.Low => ChatReasoningEffortLevel.Low,
            ReasoningEffort.Medium => ChatReasoningEffortLevel.Medium,
            _ => ChatReasoningEffortLevel.High,
        };
    }
#pragma warning restore OPENAI001
}
