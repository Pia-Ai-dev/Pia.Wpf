using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Wiki;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Vault;

public class AssistantFolderRelocationServiceTests : IDisposable
{
    private readonly string _profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string _baseDir;
    private readonly string _old;
    private readonly string _new;
    private readonly AppSettings _settings = new();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly IVaultIndexer _indexer = Substitute.For<IVaultIndexer>();
    private readonly VaultPathProvider _paths;
    private readonly VaultWatcher _watcher;
    private readonly RecordingIngestService _ingest = new();
    private readonly AutoIngestService _autoIngest;
    private readonly AssistantFolderRelocationService _svc;

    public AssistantFolderRelocationServiceTests()
    {
        _baseDir = Path.Combine(_profile, "pia-reloc-" + Guid.NewGuid().ToString("N"));
        _old = Path.Combine(_baseDir, "old");
        _new = Path.Combine(_baseDir, "new");
        Directory.CreateDirectory(Path.Combine(_old, "Vault", "memory"));
        File.WriteAllText(Path.Combine(_old, "Vault", "memory", "m.md"), "---\nid: 1\n---\nhi");
        File.WriteAllText(Path.Combine(_old, "doc.txt"), "hello");

        _settings.AssistantFilesFolder = _old;
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_settings));
        _settingsService.SaveSettingsAsync(Arg.Any<AppSettings>()).Returns(Task.CompletedTask);

        _paths = new VaultPathProvider(AssistantWorkspace.VaultRootFor(_old));
        _watcher = new VaultWatcher(_indexer, _paths, NullLogger<VaultWatcher>.Instance);

        // AutoIngestService is sealed/concrete, so a REAL instance over a temp-db state store and a
        // recording ingest stub — the history.db lives at _baseDir root so the move never touches it.
        var providers = Substitute.For<IProviderService>();
        providers.GetDefaultProviderAsync().Returns(
            new AiProvider { Name = "stub", Endpoint = "http://localhost" });
        _autoIngest = new AutoIngestService(
            _ingest,
            new IngestStateStore($"Data Source={Path.Combine(_baseDir, "history.db")}"),
            new VaultStore(_paths.VaultRoot, new MarkdownVaultParser()),
            providers,
            _settingsService,
            _paths,
            NullLogger<AutoIngestService>.Instance);

        _svc = new AssistantFolderRelocationService(
            _settingsService, _paths, _watcher, _autoIngest, _indexer, new VaultWriteGate(),
            NullLogger<AssistantFolderRelocationService>.Instance);
    }

    public void Dispose()
    {
        _autoIngest.Dispose();
        _watcher.Dispose();
        TempPath.Remove(_baseDir);
    }

    /// <summary>Records ingest calls; always succeeds touching one topic page.</summary>
    private sealed class RecordingIngestService : IIngestService
    {
        public List<string> IngestCalls { get; } = [];

        public Task<IngestResult> IngestAsync(
            string sourceRelativePath, DateOnly date, CancellationToken ct = default)
        {
            lock (IngestCalls)
            {
                IngestCalls.Add(sourceRelativePath);
            }

            return Task.FromResult(new IngestResult(
                sourceRelativePath,
                [$"memory/topics/{Path.GetFileNameWithoutExtension(sourceRelativePath)}.md"]));
        }

        public Task RemoveContributionsAsync(
            string sourceRef, IReadOnlyList<string> pages, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> RebuildPageAsync(string pagePath, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<string>> ListTopicPagesAsync()
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> MergeTopicPagesAsync(string keeperPath, string loserPath, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    [Fact]
    public async Task Move_relocates_repoints_provider_and_saves_setting()
    {
        var result = await _svc.MoveAsync(_new, null, CancellationToken.None);

        Assert.Equal(RelocationOutcome.Success, result.Outcome);
        Assert.False(Directory.Exists(_old));
        Assert.True(File.Exists(Path.Combine(_new, "Vault", "memory", "m.md")));
        Assert.Equal(AssistantWorkspace.VaultRootFor(_new), _paths.VaultRoot);
        Assert.Equal(_new, _settings.AssistantFilesFolder);
        await _indexer.Received().RebuildAllAsync();

        // Auto-ingest was stopped for the move and restarted on the NEW root: a file dropped into
        // <newVault>/sources/ must reach the recording stub (3 s debounce; poll up to 15 s).
        File.WriteAllText(
            Path.Combine(AssistantWorkspace.VaultRootFor(_new), "sources", "dropped.txt"), "hello");
        for (var i = 0; i < 150 && _ingest.IngestCalls.Count == 0; i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(["sources/dropped.txt"], _ingest.IngestCalls);
    }

    [Fact]
    public async Task Move_outside_profile_is_ValidationFailed_and_changes_nothing()
    {
        var outside = Path.Combine(Path.GetTempPath(), "pia-outside-" + Guid.NewGuid().ToString("N"));
        // Only meaningful when TEMP is not under the profile. Skip if it is.
        if (outside.StartsWith(_profile, StringComparison.OrdinalIgnoreCase)) return;

        var result = await _svc.MoveAsync(outside, null, CancellationToken.None);

        Assert.Equal(RelocationOutcome.OutsideUserProfile, result.Outcome);
        Assert.True(Directory.Exists(_old));
        Assert.Equal(AssistantWorkspace.VaultRootFor(_old), _paths.VaultRoot);
        await _indexer.DidNotReceive().RebuildAllAsync();
    }

    [Fact]
    public async Task Move_to_same_folder_is_NoChange()
    {
        var result = await _svc.MoveAsync(_old, null, CancellationToken.None);

        Assert.Equal(RelocationOutcome.NoChange, result.Outcome);
        Assert.True(Directory.Exists(_old));
        await _indexer.DidNotReceive().RebuildAllAsync();
    }
}
