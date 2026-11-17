using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;

namespace Pia.ViewModels.Models;

public sealed partial class ChatSession
{
    private Func<AssistantMessage, IDictionary<string, object?>?, Task<ProposePlanOutcome>>? _proposePlan;

    private async Task<object?> TryHandleProposePlanAsync(
        FunctionCallContent toolCall, AssistantMessage message, bool tokenizationEnabled, ToolDispatchContext dispatch)
    {
        if (_proposePlan is not { } propose
            || !string.Equals(toolCall.Name, ProposePlanTool.Name, StringComparison.Ordinal))
        {
            return null;
        }

        var arguments = tokenizationEnabled ? Detokenize(toolCall.Arguments) : toolCall.Arguments;
        var outcome = await propose(message, arguments);
        if (!outcome.EndsTurn)
            return outcome.Result;

        // Without a lead-in the turn would end as an empty response.
        if (string.IsNullOrWhiteSpace(message.Content))
            message.Content = _localizationService["ProposePlan_LeadIn"];
        dispatch.Stop?.RequestStop();
        return outcome.Result;
    }

    // The plan is stored and shown to the user, so its text must carry real names, not placeholders.
    private Dictionary<string, object?>? Detokenize(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
            return null;
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(arguments);
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(TokenMap.Detokenize(json));
        }
        catch (System.Text.Json.JsonException)
        {
            // A restored name with a quote in it breaks the document; the placeholders are better than no plan.
            return new Dictionary<string, object?>(arguments);
        }
    }
}
