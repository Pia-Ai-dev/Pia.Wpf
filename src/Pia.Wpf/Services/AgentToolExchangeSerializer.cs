using System.Text.Json;
using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services;

/// <summary>
/// The <c>ChatMessage</c>-to-row codec. Two accepted lossy edges: a raw CLR <c>string</c> argument comes back as
/// a <c>JsonElement</c> (identical on the wire), and <c>FunctionCallContent.Exception</c> is not persisted.
/// </summary>
internal static class AgentToolExchangeSerializer
{
    /// <summary>Per-row payload ceiling. A <c>write_file</c> content argument at its 512 K cap fits well inside.</summary>
    internal const int MaxRowChars = 1_048_576;

    /// <summary>Per-value cap on a REPLAYED call's arguments as the model is shown them.</summary>
    internal const int MaxSeedValueChars = 400;

    /// <summary>A truncated JSON is not parseable, so an oversize object result is downgraded to text.</summary>
    private const string TruncationSuffix = "\n[truncated]";

    private const string AssistantRole = "assistant";
    private const string ToolRole = "tool";

    /// <summary>
    /// One round's messages as rows. <paramref name="seqFrom"/> / <paramref name="messageSeqFrom"/> are the
    /// maxima already in the table, and the increments happen HERE alone so no caller re-allocates.
    /// </summary>
    internal static IReadOnlyList<AgentToolExchangeRow> ToRows(
        Guid runId,
        Guid? stepId,
        int round,
        long seqFrom,
        long messageSeqFrom,
        IReadOnlyList<ChatMessage> messages,
        DateTime now)
    {
        var rows = new List<AgentToolExchangeRow>();
        var seq = seqFrom;
        var messageSeq = messageSeqFrom;

        foreach (var message in messages)
        {
            var toolContents = message.Contents
                .Where(c => c is FunctionCallContent or FunctionResultContent)
                .ToList();
            if (toolContents.Count == 0)
                continue;

            messageSeq++;
            var role = string.IsNullOrEmpty(message.Role.Value) ? AssistantRole : message.Role.Value;

            foreach (var content in toolContents)
            {
                seq++;
                rows.Add(content switch
                {
                    FunctionCallContent call => CallRow(runId, stepId, round, messageSeq, seq, role, call, now),
                    _ => ResultRow(runId, stepId, round, messageSeq, seq, role, (FunctionResultContent)content, now),
                });
            }
        }

        return rows;
    }

    private static AgentToolExchangeRow CallRow(
        Guid runId, Guid? stepId, int round, long messageSeq, long seq, string role,
        FunctionCallContent call, DateTime now)
    {
        var json = SerializeArguments(call.Arguments);
        var omitted = json is not null && json.Length > MaxRowChars;
        if (omitted) json = null;

        return new AgentToolExchangeRow(
            Id: Guid.NewGuid(),
            RunId: runId,
            StepId: stepId,
            MessageSeq: messageSeq,
            Seq: seq,
            Round: round,
            Role: role,
            Kind: AgentToolExchangeKind.Call,
            CallId: call.CallId ?? string.Empty,
            ToolName: call.Name,
            PluginId: null,
            ArgumentsJson: json,
            ArgsOmitted: omitted,
            DisplayArgs: null,
            ResultKind: AgentToolExchangeResult.None,
            ResultText: null,
            Chars: json?.Length ?? 0,
            AnchorMessageId: null,
            CreatedAt: now,
            ReplayedAt: null,
            SupersededAt: null);
    }

    private static AgentToolExchangeRow ResultRow(
        Guid runId, Guid? stepId, int round, long messageSeq, long seq, string role,
        FunctionResultContent result, DateTime now)
    {
        var (resultKind, text) = SerializeResult(result.Result);

        return new AgentToolExchangeRow(
            Id: Guid.NewGuid(),
            RunId: runId,
            StepId: stepId,
            MessageSeq: messageSeq,
            Seq: seq,
            Round: round,
            Role: role,
            Kind: AgentToolExchangeKind.Result,
            CallId: result.CallId ?? string.Empty,
            ToolName: null,
            PluginId: null,
            ArgumentsJson: null,
            ArgsOmitted: false,
            DisplayArgs: null,
            ResultKind: resultKind,
            ResultText: text,
            Chars: text?.Length ?? 0,
            AnchorMessageId: null,
            CreatedAt: now,
            ReplayedAt: null,
            SupersededAt: null);
    }

    /// <summary>
    /// Rows back into messages. Grouped by <c>MessageSeq</c>, so two parallel calls in one assistant message
    /// come back in one message — the shape the tool loop produced, with no regrouping.
    /// </summary>
    internal static IReadOnlyList<ChatMessage> ToMessages(IEnumerable<AgentToolExchangeRow> rows)
    {
        var messages = new List<ChatMessage>();

        foreach (var group in rows.GroupBy(r => r.MessageSeq))
        {
            var contents = new List<AIContent>();
            foreach (var row in group)
            {
                switch (row.Kind)
                {
                    case AgentToolExchangeKind.Call:
                    case AgentToolExchangeKind.ParkedCall:
                    case AgentToolExchangeKind.WithheldCall:
                        contents.Add(new FunctionCallContent(
                            row.CallId, row.ToolName ?? string.Empty, DeserializeArguments(row.ArgumentsJson)));
                        break;
                    case AgentToolExchangeKind.Result:
                        contents.Add(new FunctionResultContent(
                            row.CallId, DeserializeResult(row.ResultKind, row.ResultText)));
                        break;
                }
            }

            if (contents.Count == 0)
                continue;

            var first = group.First();
            var role = string.IsNullOrEmpty(first.Role)
                ? (first.Kind == AgentToolExchangeKind.Result ? ToolRole : AssistantRole)
                : first.Role;
            messages.Add(new ChatMessage(new ChatRole(role), contents));
        }

        return messages;
    }

    internal static string? SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return null;

        try { return JsonSerializer.Serialize(arguments); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { return null; }
    }

    internal static Dictionary<string, object?>? DeserializeArguments(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (parsed is null) return null;

            var arguments = new Dictionary<string, object?>(parsed.Count, StringComparer.Ordinal);
            foreach (var (key, value) in parsed)
                arguments[key] = value;

            return arguments;
        }
        catch (JsonException) { return null; }
    }

    internal static (AgentToolExchangeResult Kind, string? Text) SerializeResult(object? result)
    {
        switch (result)
        {
            case null:
                return (AgentToolExchangeResult.None, null);
            case string s:
                // Never truncated here: Capture already caps a string result long before this ceiling.
                return (AgentToolExchangeResult.Text, s);
        }

        string json;
        try { json = JsonSerializer.Serialize(result); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return (AgentToolExchangeResult.Text, result.ToString());
        }

        return json.Length > MaxRowChars
            ? (AgentToolExchangeResult.Text, json[..MaxRowChars] + TruncationSuffix)
            : (AgentToolExchangeResult.Json, json);
    }

    internal static object? DeserializeResult(AgentToolExchangeResult kind, string? text)
    {
        if (kind == AgentToolExchangeResult.None || text is null)
            return null;

        if (kind == AgentToolExchangeResult.Text)
            return text;

        try { return JsonSerializer.Deserialize<JsonElement>(text); }
        catch (JsonException) { return text; }
    }

    /// <summary>
    /// A replayed call's arguments as the MODEL is shown them: the real value went to the tool, and a 512 K
    /// file body in the seeded call would cost the whole context window.
    /// </summary>
    internal static Dictionary<string, object?> CapForSeed(IDictionary<string, object?> arguments)
    {
        var capped = new Dictionary<string, object?>(arguments.Count, StringComparer.Ordinal);
        foreach (var (key, value) in arguments)
        {
            var text = value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
                _ => null,
            };

            capped[key] = text is { } t && t.Length > MaxSeedValueChars
                ? t[..MaxSeedValueChars] + "…"
                : value;
        }

        return capped;
    }
}
