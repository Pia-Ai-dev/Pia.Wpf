using Pia.Models;

namespace Pia.Services.Interfaces;

public interface ISuggestionService
{
    Task<IReadOnlyList<string>> SuggestFollowupsAsync(
        AiProvider provider,
        string userMessage,
        string assistantReply,
        CancellationToken cancellationToken = default);
}
