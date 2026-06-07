using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Integration;

[Trait("Category", "Integration")]
public class MemoryToolIntegrationTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly SqliteContext _ctx;
    private readonly MarkdownVaultParser _parser = new();
    private readonly VaultStore _store;
    private readonly StubEmbeddingService _embeddings = new();
    private readonly SyncDeleteTrackerService _deleteTracker;
    private readonly SectionUpsertService _upsert;
    private readonly ILocalizationService _localization;

    public MemoryToolIntegrationTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _vaultRoot = Path.Combine(_tmpDir, "vault");
        Directory.CreateDirectory(_vaultRoot);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _store = new VaultStore(_vaultRoot, _parser);
        _deleteTracker = new SyncDeleteTrackerService(_tmpDir, NullLogger<SyncDeleteTrackerService>.Instance);
        _upsert = new SectionUpsertService(_embeddings);

        _localization = Substitute.For<ILocalizationService>();
        _localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        _localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => string.Format(ci.ArgAt<string>(0), ci.ArgAt<object[]>(1)));
    }

    public void Dispose()
    {
        _ctx.Dispose();
        try
        {
            if (Directory.Exists(_tmpDir))
            {
                Directory.Delete(_tmpDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of the temp dir.
        }
    }

    private MemoryService BuildMemoryService()
        => new(_ctx, NullLogger<MemoryService>.Instance, _embeddings, _deleteTracker, _store, _upsert);

    private MemoryToolHandler BuildHandler(IMemoryService memory)
        => new(memory, _embeddings, _localization, NullLogger<MemoryToolHandler>.Instance);

    private static FunctionCallContent RememberCall(string type, string subject, string content)
        => new(
            callId: Guid.NewGuid().ToString(),
            name: "remember",
            arguments: new Dictionary<string, object?>
            {
                ["type"] = type,
                ["subject"] = subject,
                ["content"] = content,
            });

    // DEDUP PROOF: two remember tool-calls for the SAME subject must collapse into ONE "## John Smith"
    // section with a merged body. This runs with NO API key — we call HandleToolCallAsync directly with a
    // FunctionCallContent for "remember", then ExecutePendingActionAsync on the returned pending action,
    // over a REAL MemoryService (real SectionUpsertService + StubEmbeddingService) writing to a temp vault.
    [Fact]
    public async Task Remember_SameSubjectTwice_DedupsIntoOneSection()
    {
        var memory = BuildMemoryService();
        var handler = BuildHandler(memory);

        // First call: creates the section. Resolution band is Create -> pending action -> execute (write).
        var (firstResult, firstPending) = await handler.HandleToolCallAsync(
            RememberCall("contact_list", "John Smith", "- email: a@x"));
        Assert.Null(firstResult);
        Assert.NotNull(firstPending);
        Assert.Equal("remember", firstPending!.ToolName);
        await handler.ExecutePendingActionAsync(firstPending);

        // Second call, SAME subject: resolution band is Edit -> pending action -> execute (merge, no dup).
        var (secondResult, secondPending) = await handler.HandleToolCallAsync(
            RememberCall("contact_list", "John Smith", "- phone: 5"));
        Assert.Null(secondResult);
        Assert.NotNull(secondPending);
        await handler.ExecutePendingActionAsync(secondPending);

        // Assert: contacts.md has EXACTLY ONE "## John Smith" section with a merged body.
        var doc = await _store.ReadAsync("memory/contacts.md");
        Assert.NotNull(doc);

        var sections = doc!.Sections.Where(s => s.Heading == "John Smith").ToList();
        Assert.Single(sections);

        Assert.Contains("a@x", sections[0].Body);
        Assert.Contains("phone: 5", sections[0].Body);
    }

    // recall returns hits immediately (no pending action). After remembering a contact, recalling its
    // subject must surface a RecallHit for that section.
    [Fact]
    public async Task Recall_AfterRemember_ReturnsImmediateHits()
    {
        var memory = BuildMemoryService();
        var handler = BuildHandler(memory);

        var (_, pending) = await handler.HandleToolCallAsync(
            RememberCall("contact_list", "John Smith", "- email: a@x"));
        Assert.NotNull(pending);
        await handler.ExecutePendingActionAsync(pending!);

        // Index the vault so the Chunks-backed recall has something to match.
        await memory.RecallAsync("John Smith");

        var recallCall = new FunctionCallContent(
            callId: Guid.NewGuid().ToString(),
            name: "recall",
            arguments: new Dictionary<string, object?> { ["query"] = "John Smith" });

        var (result, recallPending) = await handler.HandleToolCallAsync(recallCall);

        // recall is immediate: a result object, never a pending action.
        Assert.Null(recallPending);
        Assert.NotNull(result);
        Assert.IsAssignableFrom<IReadOnlyList<RecallHit>>(result);
    }

    // forget returns a pending action whose Execute removes the addressed section.
    [Fact]
    public async Task Forget_PendingAction_RemovesSection()
    {
        var memory = BuildMemoryService();
        var handler = BuildHandler(memory);

        var (_, rememberPending) = await handler.HandleToolCallAsync(
            RememberCall("contact_list", "John Smith", "- email: a@x"));
        Assert.NotNull(rememberPending);
        await handler.ExecutePendingActionAsync(rememberPending!);

        var forgetCall = new FunctionCallContent(
            callId: Guid.NewGuid().ToString(),
            name: "forget",
            arguments: new Dictionary<string, object?> { ["reference"] = "memory/contacts.md#John Smith" });

        var (forgetResult, forgetPending) = await handler.HandleToolCallAsync(forgetCall);
        Assert.Null(forgetResult);
        Assert.NotNull(forgetPending);
        Assert.Equal("forget", forgetPending!.ToolName);
        await handler.ExecutePendingActionAsync(forgetPending);

        var doc = await _store.ReadAsync("memory/contacts.md");
        Assert.NotNull(doc);
        Assert.DoesNotContain(doc!.Sections, s => s.Heading == "John Smith");
    }

    // Distinct text -> well-spread near-orthogonal unit vectors so unrelated subjects do NOT collide into
    // an Edit/Ambiguous band; identical text round-trips to an identical vector. (Mirrors MemoryWriteTests.)
    private sealed class StubEmbeddingService : IEmbeddingService
    {
        private const int Dim = 16;

        public bool IsModelAvailable => true;

        public Task<bool> DownloadModelAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> EnsureAvailableAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            var vec = new float[Dim];
            var h = Fnv1a(text);
            for (var i = 0; i < Dim; i++)
            {
                h = (h ^ (uint)(i * 0x9e3779b9)) * 16777619u;
                vec[i] = ((h & 0xffff) / 32767.5f) - 1f;
            }
            return Task.FromResult(vec);
        }

        private static uint Fnv1a(string s)
        {
            uint h = 2166136261u;
            foreach (var c in s)
            {
                h = (h ^ c) * 16777619u;
            }
            return h;
        }

        public byte[] FloatsToBytes(float[] embedding)
        {
            var bytes = new byte[embedding.Length * sizeof(float)];
            Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        public float[] BytesToFloats(byte[] bytes)
        {
            var floats = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return floats;
        }
    }
}
