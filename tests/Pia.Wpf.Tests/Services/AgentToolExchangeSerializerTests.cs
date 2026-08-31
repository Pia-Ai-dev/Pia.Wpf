using System.Text.Json;
using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The <c>ChatMessage</c>-to-row codec: what survives the round trip byte-for-byte, and the two lossy edges
/// the store's design accepts.
/// </summary>
public class AgentToolExchangeSerializerTests
{
    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Arguments_RoundTrip_ReproducesTheExactJsonTheProviderSent()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["path"] = Element("\"C:/notes/report.md\""),
            ["rows"] = Element("42"),
            ["options"] = Element("{\"overwrite\":true,\"encoding\":\"utf-8\"}"),
            ["columns"] = Element("[\"a\",\"b\",\"c\"]"),
            ["plain"] = "a raw CLR string",
            ["missing"] = null,
        };

        var first = AgentToolExchangeSerializer.SerializeArguments(arguments);
        Assert.NotNull(first);

        var rehydrated = AgentToolExchangeSerializer.DeserializeArguments(first);
        Assert.NotNull(rehydrated);
        var second = AgentToolExchangeSerializer.SerializeArguments(rehydrated);

        Assert.Equal(first, second);
        Assert.Equal(arguments.Keys.OrderBy(k => k, StringComparer.Ordinal),
            rehydrated!.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void ARawStringArgument_WidensToAJsonElement_WhichBothArgumentReadersStillAccept()
    {
        // Lossy edge 1: a CLR string comes back as a JsonElement of ValueKind.String. Benign, because every
        // argument reader in the codebase switches on both shapes.
        var call = new FunctionCallContent("c0", "read_file", new Dictionary<string, object?>
        {
            ["path"] = "C:/notes/therapy.md",
        });

        var rows = AgentToolExchangeSerializer.ToRows(
            Guid.NewGuid(), null, 1, 0, 0, [new ChatMessage(ChatRole.Assistant, [call])], DateTime.UtcNow);
        var rebuilt = AgentToolExchangeSerializer.ToMessages(rows);
        var rebuiltCall = rebuilt[0].Contents.OfType<FunctionCallContent>().Single();

        Assert.IsType<JsonElement>(rebuiltCall.Arguments!["path"]);

        var described = ToolApprovalArguments.Describe(rebuiltCall);
        Assert.Equal("path=C:/notes/therapy.md", described);

        // The carryover's placeholder reads the same argument through its own switch, so it must still name
        // the path after the widening.
        var cleared = AgentToolCarryover.ClearOldResults(
        [
            new ChatMessage(ChatRole.Assistant, [rebuiltCall]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c0", "body")]),
            .. Enumerable.Range(0, AgentToolCarryover.KeptResults).SelectMany(i => new[]
            {
                new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("k" + i, "read_file", null)]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("k" + i, "later")]),
            }),
        ]);

        var placeholder = cleared[1].Contents.OfType<FunctionResultContent>().Single().Result as string;
        Assert.NotNull(placeholder);
        Assert.StartsWith("[result cleared", placeholder, StringComparison.Ordinal);
        Assert.Contains("C:/notes/therapy.md", placeholder, StringComparison.Ordinal);
    }

    [Fact]
    public void Result_AStringIsText_AnObjectIsJson_AndAnOversizeJsonResultDowngradesToTruncatedText()
    {
        var (textKind, text) = AgentToolExchangeSerializer.SerializeResult("plain body");
        Assert.Equal(AgentToolExchangeResult.Text, textKind);
        Assert.Equal("plain body", text);
        Assert.Equal("plain body", AgentToolExchangeSerializer.DeserializeResult(textKind, text));

        var (jsonKind, json) = AgentToolExchangeSerializer.SerializeResult(new { ok = true, rows = 3 });
        Assert.Equal(AgentToolExchangeResult.Json, jsonKind);
        Assert.Equal("{\"ok\":true,\"rows\":3}", json);
        var rehydrated = Assert.IsType<JsonElement>(AgentToolExchangeSerializer.DeserializeResult(jsonKind, json));
        Assert.Equal(3, rehydrated.GetProperty("rows").GetInt32());

        Assert.Equal(AgentToolExchangeResult.None, AgentToolExchangeSerializer.SerializeResult(null).Kind);

        // A truncated JSON is not parseable, so the oversize arm changes the KIND as well as the text.
        var oversize = new { blob = new string('x', AgentToolExchangeSerializer.MaxRowChars + 1000) };
        var (bigKind, bigText) = AgentToolExchangeSerializer.SerializeResult(oversize);
        Assert.Equal(AgentToolExchangeResult.Text, bigKind);
        Assert.NotNull(bigText);
        Assert.Equal(AgentToolExchangeSerializer.MaxRowChars + "\n[truncated]".Length, bigText!.Length);
        Assert.EndsWith("[truncated]", bigText, StringComparison.Ordinal);
    }

    [Fact]
    public void ToMessages_GroupsByMessageSeq_KeepingParallelCallsInOneAssistantMessage()
    {
        var round = new List<ChatMessage>
        {
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("c0", "read_file", new Dictionary<string, object?> { ["path"] = "a.md" }),
                new FunctionCallContent("c1", "read_file", new Dictionary<string, object?> { ["path"] = "b.md" }),
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("c0", "body a")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "body b")]),
        };

        var rows = AgentToolExchangeSerializer.ToRows(Guid.NewGuid(), null, 2, 0, 0, round, DateTime.UtcNow);
        Assert.Equal(4, rows.Count);
        Assert.Equal(new long[] { 1, 1, 2, 3 }, rows.Select(r => r.MessageSeq).ToArray());
        Assert.Equal(new long[] { 1, 2, 3, 4 }, rows.Select(r => r.Seq).ToArray());

        var rebuilt = AgentToolExchangeSerializer.ToMessages(rows);

        Assert.Equal(3, rebuilt.Count);
        Assert.Equal(ChatRole.Assistant, rebuilt[0].Role);
        Assert.Equal(ChatRole.Tool, rebuilt[1].Role);
        Assert.Equal(ChatRole.Tool, rebuilt[2].Role);
        Assert.Equal(2, rebuilt[0].Contents.OfType<FunctionCallContent>().Count());

        var callIds = rebuilt.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId).ToList();
        var resultIds = rebuilt.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(c => c.CallId).ToList();
        Assert.Equal(callIds.OrderBy(i => i, StringComparer.Ordinal), resultIds.OrderBy(i => i, StringComparer.Ordinal));
    }

    [Fact]
    public void ABlankCallId_IsStoredAsAnEmptyString_SoTheCallAndResultStillPairBlankToBlank()
    {
        // Only the replay synthesizes an id; a recorded row keeps what the message carried.
        var round = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent(string.Empty, "read_file", null)]),
            new(ChatRole.Tool, [new FunctionResultContent(string.Empty, "body")]),
        };

        var rows = AgentToolExchangeSerializer.ToRows(Guid.NewGuid(), null, 1, 0, 0, round, DateTime.UtcNow);

        Assert.All(rows, r => Assert.Equal(string.Empty, r.CallId));
    }

    [Fact]
    public void CapForSeed_CapsEachValue_AndLeavesNonStringsAlone()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["content"] = new string('x', AgentToolExchangeSerializer.MaxSeedValueChars + 500),
            ["path"] = Element("\"short.md\""),
            ["rows"] = Element("7"),
        };

        var capped = AgentToolExchangeSerializer.CapForSeed(arguments);

        var content = Assert.IsType<string>(capped["content"]);
        Assert.Equal(AgentToolExchangeSerializer.MaxSeedValueChars + 1, content.Length);
        Assert.Equal("short.md", Assert.IsType<JsonElement>(capped["path"]).GetString());
        Assert.Equal(JsonValueKind.Number, Assert.IsType<JsonElement>(capped["rows"]).ValueKind);
    }
}
