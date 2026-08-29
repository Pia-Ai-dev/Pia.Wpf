using Pia.Models;
using Pia.Shared.Models;

namespace Pia.Services.Interfaces;

/// <summary>Ratings and complaints about Pia Cloud answers, sent to the connected server's <c>/api/ai-feedback</c>.</summary>
public interface IAiFeedbackService
{
    /// <summary>
    /// Assembles the report. Comment and answer text pass through the same PII tokenization as outgoing
    /// prompts when the privacy setting is on, so the server never sees data the chat itself withheld.
    /// </summary>
    Task<AiFeedbackRequest> BuildRequestAsync(
        AssistantMessage message, Guid? chatId, string rating, string? comment, bool includeAnswer);

    /// <summary>False when no Pia Cloud server is configured, the user is signed out, or the server refused.</summary>
    Task<bool> SendAsync(AiFeedbackRequest request, CancellationToken ct = default);
}
