using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Produces a short chat title (3–8 words) from the first user/assistant
/// exchange via the Assistant-mode default provider, then sanitizes the model
/// output (strips wrapping quotes, trailing punctuation, caps length). The
/// caller owns when to invoke this and how to persist/display the result.
/// </summary>
public sealed class ChatTitleService : IChatTitleService
{
    private readonly IAiClientService _aiClientService;
    private readonly IProviderService _providerService;
    private readonly ILogger<ChatTitleService> _logger;

    public ChatTitleService(
        IAiClientService aiClientService,
        IProviderService providerService,
        ILogger<ChatTitleService> logger)
    {
        _aiClientService = aiClientService;
        _providerService = providerService;
        _logger = logger;
    }

    public async Task<string?> GenerateAsync(string userContent, string assistantContent, CancellationToken cancellationToken = default)
    {
        var provider = await _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant);
        if (provider is null)
        {
            _logger.LogWarning("Auto-title skipped: no Assistant-mode provider");
            return null;
        }

        const int snippetMax = 1000;
        var userSnippet = userContent.Length > snippetMax ? userContent[..snippetMax] : userContent;
        var assistantSnippet = assistantContent.Length > snippetMax ? assistantContent[..snippetMax] : assistantContent;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You write very short chat titles (3-8 words, no quotes, no trailing punctuation). Respond with only the title."),
            new(ChatRole.User,
                $"Summarize this conversation in 3-8 words:\nUser: {userSnippet}\nAssistant: {assistantSnippet}"),
        };

        _logger.SensitiveDebug("Auto-title prompt: user={User} assistant={Assistant}", userSnippet, assistantSnippet);

        var response = await _aiClientService.GetChatResponseAsync(
            messages, provider, tools: null, mode: nameof(WindowMode.Assistant), cancellationToken: cancellationToken);

        var rawTitle = response.Text ?? string.Empty;
        var title = SanitizeGeneratedTitle(rawTitle);
        if (string.IsNullOrEmpty(title))
        {
            _logger.LogWarning("Auto-title generation returned empty title");
            return null;
        }

        _logger.SensitiveDebug("Auto-title result: {Title}", title);
        return title;
    }

    private static string SanitizeGeneratedTitle(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0) return text;

        if (text.Length >= 2 &&
            ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
        {
            text = text[1..^1].Trim();
        }

        text = text.TrimEnd('.', '!', '?').TrimEnd();

        const int max = 80;
        if (text.Length > max) text = text[..max].TrimEnd() + "…";
        return TextFormatting.CollapseWhitespace(text);
    }
}
