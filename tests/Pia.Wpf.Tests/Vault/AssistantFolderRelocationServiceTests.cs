using System;
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
        _svc = new AssistantFolderRelocationService(
            _settingsService, _paths, _watcher, _indexer, new VaultWriteGate(),
            NullLogger<AssistantFolderRelocationService>.Instance);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        try { Directory.Delete(_baseDir, true); } catch { }
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
    }

    [Fact]
    public async Task Move_outside_profile_is_ValidationFailed_and_changes_nothing()
    {
        var outside = Path.Combine(Path.GetTempPath(), "pia-outside-" + Guid.NewGuid().ToString("N"));
        // Only meaningful when TEMP is not under the profile. Skip if it is.
        if (outside.StartsWith(_profile, StringComparison.OrdinalIgnoreCase)) return;

        var result = await _svc.MoveAsync(outside, null, CancellationToken.None);

        Assert.Equal(RelocationOutcome.ValidationFailed, result.Outcome);
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
