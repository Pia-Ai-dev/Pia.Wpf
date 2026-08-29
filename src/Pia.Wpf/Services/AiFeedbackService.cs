using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services;

public sealed class AiFeedbackService : IAiFeedbackService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISettingsService _settingsService;
    private readonly IAuthService _authService;
    private readonly Func<ITokenMapService> _tokenMapFactory;
    private readonly ILogger<AiFeedbackService> _logger;

    public AiFeedbackService(
        IHttpClientFactory httpClientFactory,
        ISettingsService settingsService,
        IAuthService authService,
        Func<ITokenMapService> tokenMapFactory,
        ILogger<AiFeedbackService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settingsService = settingsService;
        _authService = authService;
        _tokenMapFactory = tokenMapFactory;
        _logger = logger;
    }

    public async Task<AiFeedbackRequest> BuildRequestAsync(
        AssistantMessage message, Guid? chatId, string rating, string? comment, bool includeAnswer)
    {
        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        var hasText = includeAnswer || !string.IsNullOrWhiteSpace(comment);

        ITokenMapService? tokenMap = null;
        if (hasText && settings.Privacy.TokenizationEnabled)
        {
            // A fresh map, initialized from the same persisted store the chats use, so a name the chat
            // sent as [Person_1] leaves here as [Person_1] too.
            tokenMap = _tokenMapFactory();
            await tokenMap.InitializeAsync().ConfigureAwait(false);
        }

        string? Guard(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var trimmed = text.Trim();
            return tokenMap is null ? trimmed : tokenMap.TokenizeStructuredResult(trimmed);
        }

        return new AiFeedbackRequest
        {
            MessageId = message.Id,
            ChatId = chatId,
            Rating = rating,
            Comment = Guard(comment),
            AnswerText = includeAnswer ? Guard(message.Content) : null,
            PiiTokenized = hasText && tokenMap is not null,
            Model = message.Stats?.Model,
            AnsweredAt = message.Timestamp.ToUniversalTime(),
            ReportedAt = DateTime.UtcNow,
            AppVersion = AppVersionInfo.Version,
            Locale = CultureInfo.CurrentUICulture.Name,
        };
    }

    public async Task<bool> SendAsync(AiFeedbackRequest request, CancellationToken ct = default)
    {
        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        var serverUrl = settings.ServerUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(serverUrl))
        {
            _logger.LogWarning("AI feedback not sent: no Pia Cloud server configured");
            return false;
        }

        var token = await _authService.GetAccessTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("AI feedback not sent: not signed in to Pia Cloud");
            return false;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var http = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/api/ai-feedback");
            http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            http.Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(http, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AI feedback rejected with status {Status}", (int)response.StatusCode);
                return false;
            }

            // Ids and flags only: the comment and the answer are user content.
            _logger.LogInformation("AI feedback sent for message {MessageId} (rating={Rating}, withAnswer={WithAnswer})",
                request.MessageId, request.Rating, request.AnswerText is not null);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "AI feedback could not be sent");
            return false;
        }
    }
}
