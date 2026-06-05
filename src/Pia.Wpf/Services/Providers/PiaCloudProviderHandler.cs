using System.Net.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services.Providers;

public sealed class PiaCloudProviderHandler : IAiProviderHandler
{
    private readonly IAuthService _authService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<PiaCloudProviderHandler> _logger;

    public PiaCloudProviderHandler(
        IAuthService authService,
        ISettingsService settingsService,
        ILogger<PiaCloudProviderHandler> logger)
    {
        _authService = authService;
        _settingsService = settingsService;
        _logger = logger;
    }

    public AiProviderType ProviderType => AiProviderType.PiaCloud;

    public async Task<IChatClient> CreateChatClientAsync(
        AiProvider provider,
        string? apiKey,
        HttpClient httpClient,
        string? mode,
        CancellationToken cancellationToken)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var serverUrl = settings.ServerUrl?.TrimEnd('/');

        if (string.IsNullOrEmpty(serverUrl))
            throw new InvalidOperationException("Pia Cloud server URL is not configured. Set it in Settings > Sync.");

        _logger.LogInformation("PiaCloud: creating PiaCloudChatClient with endpoint={ServerUrl}/api/ai/chat",
            SafeUrl.Format(serverUrl));

        // The chat client fetches a valid (refreshed) token per-request via the auth service,
        // and can force a refresh on 401 retry.
        return new PiaCloudChatClient(httpClient, serverUrl, _authService.GetAccessTokenAsync, _logger, mode);
    }

    public ChatOptions CreateChatOptions(AiProvider provider, bool hasTools)
        => new();
}
