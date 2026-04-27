using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;

namespace Pia.Services;

public sealed record MeetingSummaryDeliverable(
    string Summary,
    string TranscriptPath,
    string OriginalFilename,
    string Date,
    string[] Speakers,
    string ChosenKey,
    Func<Task<bool>> SaveAsMemoryAsync);

public class MeetingToolHandler : IMeetingToolHandler
{
    private readonly IAiClientService _ai;
    private readonly IProviderService _providerService;
    private readonly IMemoryService _memoryService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<MeetingToolHandler> _logger;

    public MeetingToolHandler(
        IAiClientService ai,
        IProviderService providerService,
        IMemoryService memoryService,
        ILocalizationService localizationService,
        ILogger<MeetingToolHandler> logger)
    {
        _ai = ai;
        _providerService = providerService;
        _memoryService = memoryService;
        _localizationService = localizationService;
        _logger = logger;
    }

    public IList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(SummarizeMeetingTranscriptSchema, "summarize_meeting_transcript",
            "Summarize a saved meeting transcript file. Reads the file, prompts the user to choose a " +
            "summarization style (clean / bulleted / text), and delivers the summary directly to the " +
            "user as a dedicated message together with an inline action card asking whether to save " +
            "it as a meeting_summary memory. The assistant must NOT repeat, paraphrase, comment on, " +
            "or acknowledge the summary in chat — output nothing further after this tool call."),

        AIFunctionFactory.Create(QueryMeetingSummariesSchema, "query_meeting_summaries",
            "Search saved meeting summaries (meeting_summary memory type) by date range and/or speaker. " +
            "Use when the user asks about past meetings."),
    ];

    public async Task<(object? Result, MeetingToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall, CancellationToken cancellationToken = default)
    {
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();
        return toolCall.Name switch
        {
            "summarize_meeting_transcript" => PrepareSummarize(args),
            "query_meeting_summaries"      => (await HandleQuery(args), null),
            _ => ((object?)$"Unknown tool: {toolCall.Name}", null),
        };
    }

    private (object? Result, MeetingToolCall? PendingAction) PrepareSummarize(IDictionary<string, object?> args)
    {
        var rawPath = GetStringArg(args, "filePath");
        var path = PathShortener.Expand(rawPath);

        if (!File.Exists(path))
            return ($"Error: meeting transcript not found at {rawPath}.", null);

        var choices = new[]
        {
            new ActionCardChoice("clean",    _localizationService["MeetingTool_Choice_Clean"]),
            new ActionCardChoice("bulleted", _localizationService["MeetingTool_Choice_Bulleted"]),
            new ActionCardChoice("text",     _localizationService["MeetingTool_Choice_Text"]),
        };

        var pending = new MeetingToolCall(
            ToolName: "summarize_meeting_transcript",
            Description: _localizationService["MeetingTool_Desc_PickKind"],
            Details: rawPath,
            Choices: choices,
            Execute: async chosenKey =>
            {
                if (string.IsNullOrEmpty(chosenKey)) return "User cancelled.";
                try
                {
                    var markdown = await File.ReadAllTextAsync(path);
                    var body = MeetingTranscriptWriter.StripFrontMatter(markdown);
                    MeetingTranscriptWriter.TryParseFrontMatter(markdown, out var frontMatter);
                    var prompt = chosenKey switch
                    {
                        "clean"    => _localizationService["MeetingTool_Prompt_Clean"],
                        "bulleted" => _localizationService["MeetingTool_Prompt_Bulleted"],
                        "text"     => _localizationService["MeetingTool_Prompt_Text"],
                        _          => _localizationService["MeetingTool_Prompt_Bulleted"],
                    };

                    var provider = await _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant);
                    if (provider is null) return "Error: no AI provider configured.";

                    var messages = new List<ChatMessage>
                    {
                        new(ChatRole.System, prompt),
                        new(ChatRole.User,   body),
                    };

                    var response = await _ai.GetChatResponseAsync(messages, provider);
                    var summary = response.Messages
                        .SelectMany(m => m.Contents)
                        .OfType<TextContent>()
                        .Aggregate(new StringBuilder(), (sb, t) => sb.Append(t.Text))
                        .ToString();

                    if (string.IsNullOrWhiteSpace(summary))
                        summary = response.Messages.FirstOrDefault()?.Text ?? "";

                    if (string.IsNullOrWhiteSpace(summary))
                        return "Error: summarization returned empty result.";

                    return new MeetingSummaryDeliverable(
                        Summary: summary,
                        TranscriptPath: path,
                        OriginalFilename: frontMatter?.OriginalFilename ?? Path.GetFileName(path),
                        Date: frontMatter?.Date ?? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        Speakers: frontMatter?.Speakers?.ToArray() ?? Array.Empty<string>(),
                        ChosenKey: chosenKey,
                        SaveAsMemoryAsync: () => SaveSummaryAsMemoryAsync(summary, frontMatter, chosenKey, path));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Summarization failed for {Path}", path);
                    return $"Error: summarization failed: {ex.Message}";
                }
            });

        return (null, pending);
    }

    private async Task<bool> SaveSummaryAsMemoryAsync(
        string summary, MeetingFrontMatter? frontMatter, string chosenKey, string transcriptPath)
    {
        try
        {
            var topic = DeriveTopic(summary, frontMatter);
            var date = frontMatter?.Date ?? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var speakers = frontMatter?.Speakers ?? (IReadOnlyList<string>)Array.Empty<string>();
            var originalFilename = frontMatter?.OriginalFilename ?? Path.GetFileName(transcriptPath);

            var data = new
            {
                topic,
                date,
                speakers,
                originalFilename,
                summaryKind = chosenKey,
                content = summary,
            };
            var json = JsonSerializer.Serialize(data);

            await _memoryService.CreateObjectAsync(MemoryObjectTypes.MeetingSummary, topic, json);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save meeting summary memory for {Path}", transcriptPath);
            return false;
        }
    }

    private static string DeriveTopic(string summary, MeetingFrontMatter? frontMatter)
    {
        foreach (var rawLine in summary.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('#').Trim();
            if (line.Length == 0) continue;
            return line.Length > 80 ? line[..80] : line;
        }
        var date = frontMatter?.Date ?? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"Meeting summary {date}";
    }

    private async Task<object?> HandleQuery(IDictionary<string, object?> args)
    {
        var fromStr = GetStringArg(args, "from");
        var toStr   = GetStringArg(args, "to");
        var speaker = GetStringArg(args, "speaker");

        DateTime? from = DateTime.TryParseExact(fromStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;
        DateTime? to   = DateTime.TryParseExact(toStr,   "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

        var summaries = await _memoryService.GetObjectsByTypeAsync(MemoryObjectTypes.MeetingSummary);
        var matches = new List<(MemoryObject Obj, string Topic, string Date, string[] Speakers)>();

        foreach (var obj in summaries)
        {
            string topic = obj.Label;
            string date = "";
            string[] speakers = Array.Empty<string>();
            try
            {
                using var doc = JsonDocument.Parse(obj.Data);
                if (doc.RootElement.TryGetProperty("topic", out var t1))    topic    = t1.GetString() ?? topic;
                if (doc.RootElement.TryGetProperty("date", out var d))      date     = d.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("speakers", out var s) && s.ValueKind == JsonValueKind.Array)
                    speakers = s.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
            }
            catch (JsonException) { /* tolerate malformed records */ }

            if (from.HasValue && DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1) && d1 < from.Value) continue;
            if (to.HasValue   && DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2) && d2 > to.Value)   continue;
            if (!string.IsNullOrWhiteSpace(speaker)
                && !speakers.Any(sp => sp.Contains(speaker, StringComparison.OrdinalIgnoreCase))) continue;

            matches.Add((obj, topic, date, speakers));
        }

        if (matches.Count == 0)
            return "No meetings found matching those criteria.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {matches.Count} meeting summary(s):");
        foreach (var (obj, topic, date, speakers) in matches.OrderBy(m => m.Date))
            sb.AppendLine($"\n[ID: {obj.Id}] {topic} ({date}) — speakers: {string.Join(", ", speakers)}");
        return sb.ToString();
    }

    [Description("Summarize a saved meeting transcript file")]
    private static string SummarizeMeetingTranscriptSchema(
        [Description("Path to the transcript markdown file. Environment variables like %APPDATA% are expanded.")] string filePath) => "";

    [Description("Search saved meeting summaries by date range and/or speaker name")]
    private static string QueryMeetingSummariesSchema(
        [Description("Optional ISO date (yyyy-MM-dd); inclusive lower bound")] string? from = null,
        [Description("Optional ISO date (yyyy-MM-dd); inclusive upper bound")] string? to = null,
        [Description("Optional speaker name (case-insensitive substring match)")] string? speaker = null) => "";

    private static string GetStringArg(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return string.Empty;
        if (value is JsonElement el)
            return el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" :
                   el.ValueKind == JsonValueKind.Null   ? "" : el.GetRawText();
        return value.ToString() ?? string.Empty;
    }
}
