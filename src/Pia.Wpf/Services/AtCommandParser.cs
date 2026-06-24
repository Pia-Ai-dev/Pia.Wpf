using System.Text.RegularExpressions;
using Pia.Models;

namespace Pia.Services;

/// <summary>
/// Pure static parser for @-commands in chat input text.
/// Supports: @Memory, @Todo:Title, @Reminder:"Multi word title"
/// </summary>
public static partial class AtCommandParser
{
    /// <summary>
    /// Single source of truth for the canonical keyword of every <see cref="AtCommandDomain"/>.
    /// When you add a new enum value, add a row here — the round-trip test enforces it.
    /// </summary>
    private static readonly (AtCommandDomain Domain, string Keyword)[] Domains =
    [
        (AtCommandDomain.Memory, "Memory"),
        (AtCommandDomain.Todo, "Todo"),
        (AtCommandDomain.Reminder, "Reminder"),
        (AtCommandDomain.Research, "Research"),
        (AtCommandDomain.Files, "Files")
    ];

    private static readonly Dictionary<string, AtCommandDomain> DomainMap =
        Domains.ToDictionary(d => d.Keyword, d => d.Domain, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<AtCommandDomain, string> KeywordMap =
        Domains.ToDictionary(d => d.Domain, d => d.Keyword);

    public static string GetKeyword(AtCommandDomain domain) =>
        KeywordMap.TryGetValue(domain, out var keyword)
            ? keyword
            : throw new ArgumentOutOfRangeException(nameof(domain), domain,
                $"No at-command keyword registered for domain {domain}. Add a row to AtCommandParser.Domains.");

    // Matches @Domain, @Domain:SingleWord, or @Domain:"Quoted title"
    // Must be at start-of-string or after whitespace
    [GeneratedRegex("""(?:^|(?<=\s))@(\w+(?::(?:"[^"]*"|\w*))?)(?=\s|$)""", RegexOptions.Multiline)]
    private static partial Regex CommandPattern();

    /// <summary>
    /// Determines if autocomplete should be shown based on caret position.
    /// Returns the trigger fragment (text after @) if applicable.
    /// </summary>
    public static bool ShouldShowAutocomplete(string text, int caretIndex, out string triggerFragment)
    {
        triggerFragment = string.Empty;

        if (string.IsNullOrEmpty(text) || caretIndex <= 0 || caretIndex > text.Length)
            return false;

        // Walk backwards from caret to find @
        int atIndex = -1;
        for (int i = caretIndex - 1; i >= 0; i--)
        {
            char c = text[i];
            if (c == '@')
            {
                atIndex = i;
                break;
            }
            // Allow word chars, colon, quotes, and spaces (inside quotes) in the fragment
            if (!char.IsLetterOrDigit(c) && c != ':' && c != '_' && c != '"')
            {
                // Space is allowed if we're inside quotes
                if (c == ' ' || c == '\n' || c == '\r')
                {
                    // Check if we're inside a quoted section
                    var textBeforeCaret = text.Substring(0, caretIndex);
                    var lastAt = textBeforeCaret.LastIndexOf('@');
                    if (lastAt >= 0)
                    {
                        var fragment = textBeforeCaret[(lastAt + 1)..];
                        var colonPos = fragment.IndexOf(':');
                        if (colonPos >= 0)
                        {
                            var afterColon = fragment[(colonPos + 1)..];
                            // Inside an open quote — allow spaces
                            if (afterColon.StartsWith('"') && afterColon.Count(c => c == '"') == 1)
                            {
                                continue;
                            }
                        }
                    }
                    break;
                }
                return false;
            }
        }

        if (atIndex < 0)
            return false;

        // Check that @ is preceded by whitespace, newline, or is at start of text
        if (atIndex > 0)
        {
            char preceding = text[atIndex - 1];
            if (preceding != ' ' && preceding != '\n' && preceding != '\r' && preceding != '\t')
                return false;
        }

        // Extract the fragment between @ and caret
        triggerFragment = text.Substring(atIndex + 1, caretIndex - atIndex - 1);
        return true;
    }

    /// <summary>
    /// Parses a trigger fragment into domain and item filter.
    /// "Memory:Proj" -> (Memory, "Proj")
    /// "Memory:\"Fav" -> (Memory, "Fav") — inside quoted filter
    /// "Mem" -> (null, "Mem") for tier-1 filtering
    /// "Memory:" -> (Memory, "") for tier-2 with no filter
    /// </summary>
    public static (AtCommandDomain? Domain, string? ItemFilter) ParseTriggerFragment(string fragment)
    {
        if (string.IsNullOrEmpty(fragment))
            return (null, null);

        int colonIndex = fragment.IndexOf(':');
        if (colonIndex >= 0)
        {
            string domainPart = fragment[..colonIndex];
            string itemFilter = fragment[(colonIndex + 1)..];

            // Strip surrounding quotes from filter
            itemFilter = itemFilter.Trim('"');

            if (DomainMap.TryGetValue(domainPart, out var domain))
                return (domain, itemFilter);

            // Invalid domain with colon — no suggestions
            return (null, null);
        }

        // No colon — check if it exactly matches a domain
        if (DomainMap.TryGetValue(fragment, out var exactDomain))
            return (exactDomain, null);

        // Partial domain name — tier-1 filtering
        return (null, fragment);
    }

    /// <summary>
    /// Extracts all completed @-commands from the full input text.
    /// </summary>
    public static IReadOnlyList<AtCommand> ExtractAllCommands(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var commands = new List<AtCommand>();
        var matches = CommandPattern().Matches(text);

        foreach (Match match in matches)
        {
            var content = match.Groups[1].Value;
            int colonIndex = content.IndexOf(':');

            string domainPart;
            string? itemTitle = null;

            if (colonIndex >= 0)
            {
                domainPart = content[..colonIndex];
                var rawTitle = content[(colonIndex + 1)..].Trim().Trim('"');
                if (!string.IsNullOrEmpty(rawTitle))
                    itemTitle = rawTitle;
            }
            else
            {
                domainPart = content;
            }

            if (DomainMap.TryGetValue(domainPart, out var domain))
            {
                commands.Add(new AtCommand
                {
                    Domain = domain,
                    ItemTitle = itemTitle,
                    StartIndex = match.Index,
                    Length = match.Length
                });
            }
        }

        return commands;
    }

    /// <summary>
    /// Removes all @-commands from text, returning clean user message.
    /// </summary>
    public static string StripCommands(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var result = CommandPattern().Replace(text, "");
        // Clean up extra whitespace left behind
        result = Regex.Replace(result, @"  +", " ").Trim();
        return result;
    }

    /// <summary>
    /// Gets the start index of the current @-trigger for popup positioning.
    /// </summary>
    public static int GetTriggerStartIndex(string text, int caretIndex)
    {
        for (int i = caretIndex - 1; i >= 0; i--)
        {
            if (text[i] == '@')
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Formats a title for insertion into the input text. Titles containing anything
    /// other than word characters (spaces, path separators like <c>/</c> or <c>\</c>,
    /// dots, hyphens) are wrapped in quotes so they round-trip through the extraction
    /// regex's quoted branch — the unquoted branch only captures <c>\w*</c> and would
    /// otherwise truncate a file path such as <c>notes/todo.md</c> at the first slash.
    /// </summary>
    public static string FormatItemTitle(string title)
    {
        // A token made entirely of word characters (letters, digits, underscore) is safe
        // to insert bare; everything else must be quoted.
        bool needsQuoting = title.Length == 0 || title.Any(c => !char.IsLetterOrDigit(c) && c != '_');
        return needsQuoting ? $"\"{title}\"" : title;
    }
}
