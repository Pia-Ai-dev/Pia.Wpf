using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Vault;

public class VaultIndexerTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly SqliteContext _ctx;
    private readonly MarkdownVaultParser _parser = new();
    private readonly VaultStore _store;

    public VaultIndexerTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _vaultRoot = Path.Combine(_tmpDir, "vault");
        Directory.CreateDirectory(_vaultRoot);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _store = new VaultStore(_vaultRoot, _parser);
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

    // A deterministic embedding service used where we only care about row state (not call counts).
    private sealed class StubEmbeddingService : IEmbeddingService
    {
        private static readonly float[] Fixed = { 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f };

        public bool IsModelAvailable => true;

        public Task<bool> DownloadModelAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> EnsureAvailableAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult((float[])Fixed.Clone());

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

    private static readonly float[] FixedVector = { 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f };

    // A real FloatsToBytes/BytesToFloats wired onto a substitute so we can assert call counts.
    private static IEmbeddingService NewCountingEmbeddings()
    {
        var real = new StubEmbeddingService();
        var sub = Substitute.For<IEmbeddingService>();
        sub.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult((float[])FixedVector.Clone()));
        sub.FloatsToBytes(Arg.Any<float[]>()).Returns(ci => real.FloatsToBytes(ci.Arg<float[]>()));
        sub.BytesToFloats(Arg.Any<byte[]>()).Returns(ci => real.BytesToFloats(ci.Arg<byte[]>()));
        return sub;
    }

    private const string ProfileFixture =
        "---\n" +
        "pia: managed\n" +
        "id: 6f9c0b3e-7c1a-4f2e-9a8b-000000000001\n" +
        "type: profile\n" +
        "title: Profile\n" +
        "schemaVersion: 1\n" +
        "---\n" +
        "## Preferences\n" +
        "- likes coffee\n" +
        "\n" +
        "## Goals\n" +
        "- ship the vault\n";

    private const string ContactsFixture =
        "---\n" +
        "pia: managed\n" +
        "id: 6f9c0b3e-7c1a-4f2e-9a8b-000000000002\n" +
        "type: contact_list\n" +
        "title: Contacts\n" +
        "schemaVersion: 1\n" +
        "---\n" +
        "## John Smith\n" +
        "- email: john@example.com\n";

    private long CountChunks()
    {
        var connection = _ctx.GetConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Chunks;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private long CountChunksFor(string filePath)
    {
        var connection = _ctx.GetConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Chunks WHERE FilePath = $p;";
        cmd.Parameters.AddWithValue("$p", filePath);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ---- (a) RebuildAllAsync over a 2-file vault yields chunk count == total section count ----

    [Fact]
    public async Task RebuildAll_indexes_every_section_in_every_file()
    {
        await _store.WriteAtomicAsync("profile.md", ProfileFixture);
        await _store.WriteAtomicAsync("contacts.md", ContactsFixture);

        var indexer = new VaultIndexer(_ctx, _store, _parser, new StubEmbeddingService(), NullLogger<VaultIndexer>.Instance);
        await indexer.RebuildAllAsync();

        // Profile has 2 sections, Contacts has 1 -> 3 total.
        Assert.Equal(3, CountChunks());
    }

    // ---- (b) Re-indexing an UNCHANGED file does NOT re-embed (content-hash skip) ----

    [Fact]
    public async Task Reindexing_unchanged_file_does_not_reembed()
    {
        await _store.WriteAtomicAsync("profile.md", ProfileFixture);

        var embeddings = NewCountingEmbeddings();
        var indexer = new VaultIndexer(_ctx, _store, _parser, embeddings, NullLogger<VaultIndexer>.Instance);

        await indexer.IndexFileAsync("profile.md");
        embeddings.ClearReceivedCalls();

        // Second pass over the byte-identical file: every section's content hash matches.
        await indexer.IndexFileAsync("profile.md");

        await embeddings.DidNotReceive().GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(2, CountChunksFor("profile.md"));
    }

    // ---- (c) Editing one section re-embeds ONLY that section ----

    [Fact]
    public async Task Editing_one_section_reembeds_only_that_section()
    {
        await _store.WriteAtomicAsync("profile.md", ProfileFixture);

        var embeddings = NewCountingEmbeddings();
        var indexer = new VaultIndexer(_ctx, _store, _parser, embeddings, NullLogger<VaultIndexer>.Instance);

        await indexer.IndexFileAsync("profile.md");
        embeddings.ClearReceivedCalls();

        // Change ONLY the Goals body via a byte-range splice; Preferences is untouched.
        await _store.SpliceSectionAsync("profile.md", "goals", "- ship the indexer\n");

        await indexer.IndexFileAsync("profile.md");

        // Exactly one embed call, for the changed Goals section.
        await embeddings.Received(1).GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await embeddings.Received(1).GenerateEmbeddingAsync(
            Arg.Is<string>(s => s.Contains("ship the indexer")), Arg.Any<CancellationToken>());
        await embeddings.DidNotReceive().GenerateEmbeddingAsync(
            Arg.Is<string>(s => s.Contains("likes coffee")), Arg.Any<CancellationToken>());
        Assert.Equal(2, CountChunksFor("profile.md"));
    }

    // ---- (d) RemoveFileAsync drops that file's chunk rows ----

    [Fact]
    public async Task RemoveFile_drops_that_files_chunk_rows()
    {
        await _store.WriteAtomicAsync("profile.md", ProfileFixture);
        await _store.WriteAtomicAsync("contacts.md", ContactsFixture);

        var indexer = new VaultIndexer(_ctx, _store, _parser, new StubEmbeddingService(), NullLogger<VaultIndexer>.Instance);
        await indexer.RebuildAllAsync();
        Assert.Equal(3, CountChunks());

        await indexer.RemoveFileAsync("profile.md");

        Assert.Equal(0, CountChunksFor("profile.md"));
        Assert.Equal(1, CountChunksFor("contacts.md"));
        Assert.Equal(1, CountChunks());
    }
}
