using System.Net.Http;
using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Providers;

public interface IAiProviderHandler
{
    AiProviderType ProviderType { get; }

    Task<IChatClient> CreateChatClientAsync(
        AiProvider provider,
        string? apiKey,
        HttpClient httpClient,
        string? mode,
        CancellationToken cancellationToken);

    ChatOptions CreateChatOptions(AiProvider provider, bool hasTools);
}
