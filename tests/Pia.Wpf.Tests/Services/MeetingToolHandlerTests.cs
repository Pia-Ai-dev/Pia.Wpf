using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class MeetingToolHandlerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "pia-meeting-tests-" + Guid.NewGuid().ToString("N"));

    public MeetingToolHandlerTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }

    [Fact]
    public async Task SummarizeMeetingTranscript_ReturnsMultiChoiceCard()
    {
        var path = WriteSampleTranscript("transcript-x.md");
        var handler = NewHandler();

        var (result, pending) = await handler.HandleToolCallAsync(Call("summarize_meeting_transcript", new { filePath = path }));

        Assert.Null(result);
        Assert.NotNull(pending);
        Assert.NotNull(pending!.Choices);
        Assert.Equal(3, pending.Choices!.Count);
        Assert.Contains(pending.Choices, c => c.Key == "clean");
        Assert.Contains(pending.Choices, c => c.Key == "bulleted");
        Assert.Contains(pending.Choices, c => c.Key == "text");
    }

    [Fact]
    public async Task SummarizeMeetingTranscript_RunsAiAndReturnsSummary_OnExecute()
    {
        var path = WriteSampleTranscript("transcript-x.md");
        var handler = NewHandler();

        var (_, pending) = await handler.HandleToolCallAsync(Call("summarize_meeting_transcript", new { filePath = path }));

        var execResult = await pending!.Execute("clean");
        var deliverable = Assert.IsType<Pia.Services.MeetingSummaryDeliverable>(execResult);
        Assert.Contains("CANNED-SUMMARY", deliverable.Summary);
    }

    [Fact]
    public async Task SummarizeMeetingTranscript_ReturnsErrorWhenFileMissing()
    {
        var handler = NewHandler();
        var (result, pending) = await handler.HandleToolCallAsync(
            Call("summarize_meeting_transcript", new { filePath = Path.Combine(_tempDir, "missing.md") }));

        Assert.Null(pending);
        Assert.IsType<string>(result);
        Assert.Contains("not found", (string)result!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryMeetingSummaries_FiltersByDate()
    {
        var memory = new FakeMemoryService();
        memory.Seed("Topic A", "2026-04-01", new[] { "You", "Alice" });
        memory.Seed("Topic B", "2026-04-15", new[] { "You", "Bob" });
        memory.Seed("Topic C", "2026-04-27", new[] { "You" });

        var handler = NewHandler(memory: memory);
        var (result, _) = await handler.HandleToolCallAsync(
            Call("query_meeting_summaries", new { from = "2026-04-10", to = "2026-04-20" }));

        var text = (string)result!;
        Assert.DoesNotContain("Topic A", text);
        Assert.Contains("Topic B", text);
        Assert.DoesNotContain("Topic C", text);
    }

    [Fact]
    public async Task QueryMeetingSummaries_FiltersBySpeaker_CaseInsensitive()
    {
        var memory = new FakeMemoryService();
        memory.Seed("With Alice",   "2026-04-01", new[] { "You", "Alice" });
        memory.Seed("With Bob",     "2026-04-02", new[] { "You", "Bob" });
        memory.Seed("With aLiCe-2", "2026-04-03", new[] { "You", "ALICE" });

        var handler = NewHandler(memory: memory);
        var (result, _) = await handler.HandleToolCallAsync(
            Call("query_meeting_summaries", new { speaker = "alice" }));

        var text = (string)result!;
        Assert.Contains("With Alice", text);
        Assert.Contains("With aLiCe-2", text);
        Assert.DoesNotContain("With Bob", text);
    }

    [Fact]
    public async Task QueryMeetingSummaries_ReturnsNoneMessage_WhenEmpty()
    {
        var memory = new FakeMemoryService();
        var handler = NewHandler(memory: memory);
        var (result, _) = await handler.HandleToolCallAsync(
            Call("query_meeting_summaries", new { speaker = "nobody" }));

        Assert.Contains("no meetings", ((string)result!).ToLowerInvariant());
    }

    private static FunctionCallContent Call(string name, object args)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var p in args.GetType().GetProperties())
            dict[p.Name] = p.GetValue(args);
        return new FunctionCallContent(callId: Guid.NewGuid().ToString("N"), name: name, arguments: dict);
    }

    private string WriteSampleTranscript(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, """
            ---
            schema: pia-meeting-transcript/v1
            start: 2026-04-27T10:30:00+02:00
            speakers:
              - You
              - Alice
            originalFilename: transcript-x.md
            ---
            # Live Transcription — 2026-04-27 10:30

            **Alice** _10:30:01_

            Hello world.
            """);
        return path;
    }

    private MeetingToolHandler NewHandler(FakeMemoryService? memory = null)
    {
        var ai = Substitute.For<IAiClientService>();
        ai.GetChatResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
          .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "CANNED-SUMMARY"))));

        var providers = Substitute.For<IProviderService>();
        providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>())
                 .Returns(Task.FromResult<AiProvider?>(new AiProvider { Id = Guid.NewGuid(), Name = "fake", Endpoint = "http://localhost" }));

        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);

        return new MeetingToolHandler(
            ai: ai,
            providerService: providers,
            memoryService: memory ?? new FakeMemoryService(),
            localizationService: loc,
            logger: NullLogger<MeetingToolHandler>.Instance);
    }
}

internal sealed class FakeMemoryService : IMemoryService
{
    private readonly List<MemoryObject> _objects = new();

    public void Seed(string topic, string date, IEnumerable<string> speakers)
    {
        var data = JsonSerializer.Serialize(new
        {
            topic,
            date,
            speakers = speakers.ToArray(),
            originalFilename = $"transcript-{date.Replace("-", "")}-000000.md",
            summaryKind = "bulleted",
            content = "..."
        });
        _objects.Add(new MemoryObject { Type = MemoryObjectTypes.MeetingSummary, Label = topic, Data = data });
    }

    public Task<IReadOnlyList<MemoryObject>> GetObjectsByTypeAsync(string type)
        => Task.FromResult<IReadOnlyList<MemoryObject>>(_objects.Where(o => o.Type == type).ToList());

    public Task<MemoryObject> CreateObjectAsync(string type, string label, string jsonData) => throw new NotImplementedException();
    public Task<MemoryObject> ImportObjectAsync(MemoryObject memory) => throw new NotImplementedException();
    public Task<MemoryObject?> GetObjectAsync(Guid id) => throw new NotImplementedException();
    public Task UpdateObjectAsync(Guid id, string jsonMergePatch) => throw new NotImplementedException();
    public Task UpdateObjectDataAsync(Guid id, string label, string jsonData) => throw new NotImplementedException();
    public Task AppendToListAsync(Guid id, string jsonEntry) => throw new NotImplementedException();
    public Task DeleteObjectAsync(Guid id) => throw new NotImplementedException();
    public Task<IReadOnlyList<MemoryObject>> GetAllObjectsAsync() => throw new NotImplementedException();
    public Task<IReadOnlyList<MemoryObject>> SearchAsync(string query) => throw new NotImplementedException();
    public Task<IReadOnlyList<MemoryObject>> FullTextSearchAsync(string query) => throw new NotImplementedException();
    public Task<IReadOnlyList<MemoryObject>> VectorSearchAsync(float[] queryEmbedding, int topK = 5, float threshold = 0.3f) => throw new NotImplementedException();
    public Task<IReadOnlyList<MemoryObject>> HybridSearchAsync(string query, float[]? queryEmbedding = null, int topK = 10) => throw new NotImplementedException();
    public Task UpdateEmbeddingAsync(Guid id, byte[] embedding) => throw new NotImplementedException();
    public Task TouchAccessTimeAsync(Guid id) => throw new NotImplementedException();
    public Task<int> GetObjectCountAsync() => throw new NotImplementedException();
    public Task<long> GetStorageSizeAsync() => throw new NotImplementedException();
    public Task<IReadOnlyList<MemoryObject>> GetStaleObjectsAsync(TimeSpan staleThreshold) => throw new NotImplementedException();
    public Task<string> ExportAllAsync() => throw new NotImplementedException();
    public Task<IReadOnlyList<MemorySummary>> GetMemorySummariesAsync(string? typeFilter = null) => throw new NotImplementedException();
}
