using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services;

/// <summary>
/// Maps an Open WebUI "Export All Chats" file onto Pia's chat DTO. Only the active path through the
/// message tree is imported, so regenerated branches are dropped — Pia's transcript has no branch
/// model to put them in.
/// </summary>
public static partial class OpenWebUiChatConverter
{
    private const int MaxTitleLength = 120;

    /// <summary>Below this an epoch value is read as seconds; at or above it, as milliseconds.</summary>
    private const double MillisecondEpochThreshold = 1e12;

    public static bool LooksLikeOpenWebUiExport(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var record in root.EnumerateArray())
        {
            return record.ValueKind == JsonValueKind.Object
                && (record.TryGetProperty("chat", out _) || record.TryGetProperty("messages", out _));
        }

        // An empty array is a degenerate export rather than a foreign file; let it import as zero chats.
        return true;
    }

    public static OpenWebUiConversion Convert(JsonElement root)
    {
        var chats = new List<SyncAssistantChat>();
        var tally = new ConversionTally();

        if (root.ValueKind != JsonValueKind.Array)
            return tally.ToConversion(chats);

        foreach (var record in root.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object)
            {
                tally.SkippedEmpty++;
                continue;
            }

            var chat = ConvertChat(record, tally);
            if (chat is null)
                tally.SkippedEmpty++;
            else
                chats.Add(chat);
        }

        return tally.ToConversion(chats);
    }

    /// <summary>Running counts a whole-file conversion accumulates across chats and messages.</summary>
    private sealed class ConversionTally
    {
        public int SkippedEmpty;
        public int DroppedAttachments;
        public int RecoveredFromTree;

        public OpenWebUiConversion ToConversion(List<SyncAssistantChat> chats) =>
            new(chats, SkippedEmpty, DroppedAttachments, RecoveredFromTree);
    }

    private static SyncAssistantChat? ConvertChat(JsonElement record, ConversionTally tally)
    {
        var inner = record.TryGetProperty("chat", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : record;

        var updatedAt = ReadEpoch(record, "updated_at")
            ?? ReadEpoch(inner, "timestamp")
            ?? ReadEpoch(record, "created_at")
            ?? DateTime.UtcNow;
        var createdAt = ReadEpoch(record, "created_at") ?? updatedAt;
        if (createdAt > updatedAt)
            createdAt = updatedAt;

        var path = ReadActivePath(inner);
        tally.RecoveredFromTree += Math.Max(0, path.Count - CachedMessageCount(inner));

        var messages = new List<SyncAssistantChatMessage>(path.Count);
        foreach (var element in path)
        {
            var message = ConvertMessage(element, updatedAt, tally);
            if (message is not null)
                messages.Add(message);
        }

        if (messages.Count == 0)
            return null;

        return new SyncAssistantChat
        {
            Id = ReadIdentifier(record, "id") ?? ReadIdentifier(inner, "id") ?? Guid.NewGuid(),
            SchemaVersion = 1,
            Title = SanitizeTitle(ReadString(record, "title") ?? ReadString(inner, "title")),
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            LastAccessedAt = updatedAt,
            WindowMode = "Assistant",
            // Open WebUI model ids are pipeline-mangled and resolve to no Pia provider, so the chat
            // adopts whichever provider is active when the user resumes it.
            ProviderId = null,
            WorkingDirectory = null,
            Messages = messages,
        };
    }

    /// <summary>
    /// The conversation as Open WebUI last rendered it: <c>history.currentId</c> walked back up
    /// <c>parentId</c>. The flat <c>chat.messages</c> array is only a cache of that walk, and newer
    /// builds no longer keep it in sync — it can hold a lone <c>{role, content}</c> stub, an assistant
    /// turn with an empty body, or a stale regenerated branch — so it is the fallback, not the source.
    /// </summary>
    private static List<JsonElement> ReadActivePath(JsonElement inner)
    {
        var path = WalkMessageTree(inner);
        if (path.Count > 0)
            return path;

        return inner.TryGetProperty("messages", out var cached) && cached.ValueKind == JsonValueKind.Array
            ? [.. cached.EnumerateArray()]
            : [];
    }

    private static List<JsonElement> WalkMessageTree(JsonElement inner)
    {
        var path = new List<JsonElement>();
        if (!inner.TryGetProperty("history", out var history)
            || history.ValueKind != JsonValueKind.Object
            || !history.TryGetProperty("messages", out var nodes)
            || nodes.ValueKind != JsonValueKind.Object)
        {
            return path;
        }

        // A hand-edited export could point a parentId back into the path; without the visited set that
        // walk never terminates.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var id = ReadString(history, "currentId");
        while (id is { Length: > 0 }
            && visited.Add(id)
            && nodes.TryGetProperty(id, out var node)
            && node.ValueKind == JsonValueKind.Object)
        {
            path.Add(node);
            id = ReadString(node, "parentId");
        }

        path.Reverse();
        return path;
    }

    private static int CachedMessageCount(JsonElement inner) =>
        inner.TryGetProperty("messages", out var cached) && cached.ValueKind == JsonValueKind.Array
            ? cached.GetArrayLength()
            : 0;

    private static SyncAssistantChatMessage? ConvertMessage(
        JsonElement element,
        DateTime fallbackTimestamp,
        ConversionTally tally)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (element.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            tally.DroppedAttachments += files.GetArrayLength();

        var content = ReadString(element, "content") ?? string.Empty;
        if (BuildSourceFootnote(element) is { } footnote)
            content = content.Length == 0 ? footnote.TrimStart() : content + footnote;

        if (content.Length == 0)
            return null;

        var role = string.Equals(ReadString(element, "role"), "user", StringComparison.OrdinalIgnoreCase)
            ? "user"
            : "assistant";

        return new SyncAssistantChatMessage
        {
            Id = ReadIdentifier(element, "id") ?? Guid.NewGuid(),
            Role = role,
            Content = content,
            Timestamp = ReadEpoch(element, "timestamp") ?? fallbackTimestamp,
            Tokens = ReadTokens(element),
            ModelName = ReadString(element, "model") ?? ReadString(element, "modelName"),
        };
    }

    /// <summary>
    /// Renders Open WebUI's retrieval citations as a trailing text block. The cited documents
    /// themselves stay behind — the chat store is text-only and one citation can carry a 50 KB body.
    /// </summary>
    private static string? BuildSourceFootnote(JsonElement element)
    {
        if (!element.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            return null;

        var labels = new List<string>();
        foreach (var entry in sources.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var source = entry.TryGetProperty("source", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : entry;

            var name = ReadString(source, "name")
                ?? ReadString(source, "filename")
                ?? ReadString(source, "collection_name");
            var url = ReadString(source, "url");
            // File citations reuse `url` for the upload's opaque id, so only real links become links.
            var isLink = url is not null
                && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

            var label = (name, isLink) switch
            {
                ({ Length: > 0 }, true) => $"[{name}]({url})",
                ({ Length: > 0 }, false) => name!,
                (_, true) => url!,
                _ => null,
            };

            if (label is not null && !labels.Contains(label, StringComparer.Ordinal))
                labels.Add(label);
        }

        if (labels.Count == 0)
            return null;

        var builder = new StringBuilder("\n\n---\nSources:\n");
        foreach (var label in labels)
            builder.Append("- ").Append(label).Append('\n');
        return builder.ToString();
    }

    private static int? ReadTokens(JsonElement element)
    {
        if (!element.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in new[] { "completion_tokens", "total_tokens" })
        {
            if (usage.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var tokens)
                && tokens > 0)
            {
                return tokens;
            }
        }

        return null;
    }

    /// <summary>
    /// An Open WebUI title can be the whole first prompt — thousands of characters with embedded
    /// newlines — which would wreck the history row layout and the search index body.
    /// </summary>
    private static string? SanitizeTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var collapsed = WhitespaceRun().Replace(raw, " ").Trim();
        if (collapsed.Length == 0)
            return null;

        return collapsed.Length <= MaxTitleLength
            ? collapsed
            : collapsed[..MaxTitleLength].TrimEnd() + "…";
    }

    private static string? ReadString(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Keeps the foreign id so re-importing the same file updates instead of duplicating.</summary>
    private static Guid? ReadIdentifier(JsonElement owner, string name)
    {
        var raw = ReadString(owner, name);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return Guid.TryParse(raw, out var parsed) && parsed != Guid.Empty
            ? parsed
            : DeterministicGuid.FromString(raw);
    }

    private static DateTime? ReadEpoch(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var value))
            return null;

        double raw;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDouble(out raw))
                return null;
        }
        else if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var parsed))
        {
            raw = parsed;
        }
        else
        {
            return null;
        }

        if (double.IsNaN(raw) || raw <= 0)
            return null;

        // Most builds write seconds, some milliseconds; guessing wrong lands the chat in 1970.
        var milliseconds = raw >= MillisecondEpochThreshold ? raw : raw * 1000;
        if (milliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            return null;

        return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds).UtcDateTime;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
