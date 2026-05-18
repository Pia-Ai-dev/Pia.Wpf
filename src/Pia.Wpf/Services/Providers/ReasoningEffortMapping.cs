using OpenAI.Chat;
using Pia.Models;

namespace Pia.Services.Providers;

/// <summary>
/// Maps the cross-provider <see cref="ReasoningEffort"/> enum onto the OpenAI
/// SDK's <see cref="ChatReasoningEffortLevel"/>. Tool-using turns force None
/// regardless of the configured effort — tool calls should not burn reasoning
/// tokens.
/// </summary>
internal static class ReasoningEffortMapping
{
#pragma warning disable OPENAI001
    public static ChatReasoningEffortLevel ToOpenAi(ReasoningEffort? effort, bool hasTools)
    {
        if (hasTools) return ChatReasoningEffortLevel.None;

        return effort switch
        {
            ReasoningEffort.None => ChatReasoningEffortLevel.None,
            ReasoningEffort.Minimal => ChatReasoningEffortLevel.Low,
            ReasoningEffort.Low => ChatReasoningEffortLevel.Low,
            ReasoningEffort.Medium => ChatReasoningEffortLevel.Medium,
            ReasoningEffort.High => ChatReasoningEffortLevel.High,
            ReasoningEffort.XHigh => ChatReasoningEffortLevel.High,
            _ => ChatReasoningEffortLevel.None,
        };
    }
#pragma warning restore OPENAI001
}
