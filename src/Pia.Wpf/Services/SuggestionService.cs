using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class SuggestionService : ISuggestionService
{
    private const int MaxSuggestions = 4;
    private const int MinSuggestions = 1;
    private const int MaxSuggestionLength = 40;

    private const string Prompt = """
        Given the user's last message and your reply, propose 2-3 follow-up
        actions the user is most likely to take next. Each label MUST be a
        terse action phrase of 2-5 words — like a button caption, not a
        sentence. No question marks, no pronouns, no greetings, no trailing
        punctuation. Match the user's language. Reply with ONLY a JSON array
        of strings — no prose, no markdown, no code fences, no <think> tags.
        Example: ["Save as recipe", "Add to weekly plan", "Shorter variant", "Make gluten-free"]
        """;

    private static readonly Regex ThinkBlockRegex = new(
        @"<think\b[^>]*>[\s\S]*?</think>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CodeFenceRegex = new(
        @"```(?:json)?\s*([\s\S]*?)\s*```",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IAiClientService _aiClientService;
    private readonly ILogger<SuggestionService> _logger;

    public SuggestionService(IAiClientService aiClientService, ILogger<SuggestionService> logger)
    {
        _aiClientService = aiClientService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> SuggestFollowupsAsync(
        AiProvider provider,
        string userMessage,
        string assistantReply,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || string.IsNullOrWhiteSpace(assistantReply))
            return [];

        var transcript = $"User: {userMessage}\n\nAssistant: {assistantReply}";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompt),
            new(ChatRole.User, transcript)
        };

        var buffer = new StringBuilder();
        try
        {
            await foreach (var chunk in _aiClientService.StreamChatCompletionAsync(
                messages, provider, mode: nameof(WindowMode.Assistant), cancellationToken))
            {
                buffer.Append(chunk);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Follow-up suggestion request failed (provider={ProviderName})", provider.Name);
            return [];
        }

        var text = buffer.ToString();
        _logger.SensitiveDebug("Follow-up suggestion raw response ({Length} chars): {Text}", text.Length, text);

        var picks = ParseSuggestions(text);
        _logger.LogInformation("Follow-up suggestion parsed {Count} picks (rawLength={Length})", picks.Count, text.Length);
        return picks;
    }

    internal static IReadOnlyList<string> ParseSuggestions(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var stripped = ThinkBlockRegex.Replace(raw, string.Empty);

        var fenceMatch = CodeFenceRegex.Match(stripped);
        var candidate = fenceMatch.Success ? fenceMatch.Groups[1].Value : stripped;

        var start = candidate.IndexOf('[');
        if (start < 0) return [];

        var end = candidate.LastIndexOf(']');
        List<string>? items = null;
        if (end > start)
        {
            try
            {
                items = JsonSerializer.Deserialize<List<string>>(candidate[start..(end + 1)]);
            }
            catch (JsonException)
            {
                // Fall back to lenient extraction below.
            }
        }

        items ??= ExtractCompleteQuotedStrings(candidate[(start + 1)..]);

        if (items.Count == 0) return [];

        var cleaned = items
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Select(s => s.Length > MaxSuggestionLength ? s[..MaxSuggestionLength] : s)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .ToList();

        return cleaned.Count >= MinSuggestions ? cleaned : [];
    }

    // Recovers complete "..."-quoted strings from a possibly-truncated JSON
    // array body. Models occasionally cut the stream off mid-element, so we
    // accept whatever fully-closed strings were emitted before that point.
    private static List<string> ExtractCompleteQuotedStrings(string s)
    {
        var result = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && s[i] != '"') i++;
            if (i >= s.Length) break;
            i++;

            var sb = new StringBuilder();
            var closed = false;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    var next = s[i + 1];
                    sb.Append(next switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '"' => '"',
                        '\\' => '\\',
                        '/' => '/',
                        _ => next,
                    });
                    i += 2;
                }
                else if (c == '"')
                {
                    closed = true;
                    i++;
                    break;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            if (closed) result.Add(sb.ToString());
        }
        return result;
    }
}
