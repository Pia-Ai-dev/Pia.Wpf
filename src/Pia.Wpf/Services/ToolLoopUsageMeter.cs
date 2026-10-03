using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Models;

namespace Pia.Services;

/// <summary>Counts what one tool loop sends, so the tool-output settings can be compared from a release log.</summary>
internal sealed class ToolLoopUsageMeter(AiProviderType providerType, string mcpCap, string promptCache)
{
    private readonly Dictionary<string, int> _charsByCallId = new(StringComparer.Ordinal);

    internal int Requests { get; private set; }
    internal int Results { get; private set; }
    internal long ResultChars { get; private set; }
    internal int LargestResultChars { get; private set; }
    internal string? LargestResultTool { get; private set; }

    /// <summary>Tool-result chars summed over every request, so a result re-sent on five rounds counts five times.</summary>
    internal long ToolResultCharsSent { get; private set; }

    internal long InputTokens { get; private set; }
    internal long CachedInputTokens { get; private set; }
    internal long CacheWriteTokens { get; private set; }

    public void RecordRequest(IEnumerable<ChatMessage> request)
    {
        Requests++;
        foreach (var message in request)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent result)
                    ToolResultCharsSent += CharsOf(result);
            }
        }
    }

    public int RecordResult(string toolName, string callId, object? result)
    {
        var chars = SizeOf(result);
        _charsByCallId[callId] = chars;
        Results++;
        ResultChars += chars;
        if (chars > LargestResultChars)
        {
            LargestResultChars = chars;
            LargestResultTool = toolName;
        }

        return chars;
    }

    public void RecordUsage(UsageDetails usage)
    {
        InputTokens += usage.InputTokenCount ?? 0;
        CachedInputTokens += usage.CachedInputTokenCount ?? 0;

        // The Anthropic adapter reports cache writes only here, under its own property name.
        if (usage.AdditionalCounts?.TryGetValue("CacheCreationInputTokens", out var writes) == true)
            CacheWriteTokens += writes;
    }

    public void LogSummary(ILogger logger) =>
        logger.LogInformation(
            "Tool loop usage: provider={ProviderType}, requests={Requests}, results={Results}, resultChars={ResultChars}, "
            + "largest={LargestChars} ({LargestTool}), toolResultCharsSent={ToolResultCharsSent}, inputTokens={InputTokens}, "
            + "cachedInputTokens={CachedInputTokens}, cacheWriteTokens={CacheWriteTokens}, mcpCap={McpCap}, promptCache={PromptCache}",
            providerType, Requests, Results, ResultChars,
            LargestResultChars, LargestResultTool ?? "none", ToolResultCharsSent, InputTokens,
            CachedInputTokens, CacheWriteTokens, mcpCap, promptCache);

    /// <summary>Strings are measured as-is; anything else as the compact JSON the tokenizing decorator sends.</summary>
    internal static int SizeOf(object? result)
    {
        switch (result)
        {
            case null:
                return 0;
            case string s:
                return s.Length;
            case JsonElement element:
                return element.GetRawText().Length;
        }

        try
        {
            return JsonSerializer.Serialize(result).Length;
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            return 0;
        }
    }

    // Results carried in from an earlier step were never dispatched here, but they are strings by then.
    private int CharsOf(FunctionResultContent result) =>
        result.Result is string s ? s.Length
        : _charsByCallId.TryGetValue(result.CallId, out var chars) ? chars
        : SizeOf(result.Result);
}
