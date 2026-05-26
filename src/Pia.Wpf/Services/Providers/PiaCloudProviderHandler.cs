using System.Net.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services.Providers;

public sealed class PiaCloudProviderHandler : IAiProviderHandler
{
    private readonly DpapiHelper _dpapiHelper;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<PiaCloudProviderHandler> _logger;

    public PiaCloudProviderHandler(
        DpapiHelper dpapiHelper,
        ISettingsService settingsService,
        ILogger<PiaCloudProviderHandler> logger)
    {
        _dpapiHelper = dpapiHelper;
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

        string? accessToken = null;
        if (!string.IsNullOrEmpty(settings.EncryptedAccessToken))
        {
            try
            {
                accessToken = _dpapiHelper.Decrypt(settings.EncryptedAccessToken);
            }
            catch
            {
                // If decryption fails, proceed without auth
            }
        }

        _logger.LogInformation("PiaCloud: creating PiaCloudChatClient with endpoint={ServerUrl}/api/ai/chat",
            SafeUrl.Format(serverUrl));

        return new PiaCloudChatClient(httpClient, serverUrl, accessToken, _logger, mode);
    }

    public ChatOptions CreateChatOptions(AiProvider provider, bool hasTools)
        => new();
}
