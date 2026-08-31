using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// A run workspace never gets the vault copied in and the promote walk drops anything under it, so a
/// <c>Vault/</c> write there is silently discarded at teardown — hence the refusal, and only there.
/// </summary>
public sealed class FilesToolHandlerVaultTargetTests : IDisposable
{
    private const string AbsoluteMarker = "<absolute-run-vault-path>";

    private readonly string _runRoot;
    private readonly string _interactiveRoot;
    private readonly FilesToolHandler _handler;

    public FilesToolHandlerVaultTargetTests()
    {
        _runRoot = NewDir("pia-vault-target-run-");
        _interactiveRoot = NewDir("pia-vault-target-interactive-");

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _interactiveRoot });
        _handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);

        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), WorkingSubpath: null, OnFileTouched: null, WorkspaceRoot: _runRoot);
    }

    public void Dispose()
    {
        TaskAmbient.Current = null;
        foreach (var d in new[] { _runRoot, _interactiveRoot })
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>FilesToolHandler's result records are private, so members are read by their wire names (<c>success</c>, <c>error</c>).</summary>
    private static T Prop<T>(object obj, string name)
    {
        var p = obj.GetType().GetProperty(name);
        Assert.NotNull(p);
        return (T)p!.GetValue(obj)!;
    }

    private Task<(object? Result, FilesToolCall? PendingAction)> WriteAsync(string path, string content = "body")
        => _handler.HandleToolCallAsync(
            new FunctionCallContent("w", "write_file", new Dictionary<string, object?> { ["path"] = path, ["content"] = content }),
            TestContext.Current.CancellationToken);

    private static string Under(string root, string relative)
        => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Write_UnderVault_InARunWorkspace_IsRefused_AndNamesBothMemoryTools()
    {
        var (result, pending) = await WriteAsync("Vault/sources/urlaub.md");

        Assert.Null(pending);
        Assert.NotNull(result);
        Assert.False(Prop<bool>(result!, "success"));

        var error = Prop<string?>(result!, "error")!;
        Assert.Contains("create_source('sources/urlaub.md'", error, StringComparison.Ordinal);
        Assert.Contains("update_source", error, StringComparison.Ordinal);

        Assert.False(File.Exists(Under(SafeFolderPath.NormalizeWorkspaceRoot(_runRoot), "Vault/sources/urlaub.md")));
    }

    [Fact]
    public async Task Write_UnderVault_Interactive_IsStillWritten()
    {
        TaskAmbient.Current = null;
        Directory.CreateDirectory(Under(_interactiveRoot, "Vault/sources"));

        var (result, pending) = await WriteAsync("Vault/sources/urlaub.md", "vault content");

        Assert.Null(result);
        Assert.NotNull(pending);
        var executed = await pending!.Execute();
        Assert.True(Prop<bool>(executed!, "success"));

        var full = Under(_interactiveRoot, "Vault/sources/urlaub.md");
        Assert.True(File.Exists(full));
        Assert.Contains("vault content", File.ReadAllText(full));
    }

    [Theory]
    [InlineData("Vault Backups/x.md")]
    [InlineData("docs/Vault/x.md")]
    [InlineData("VaultNotes.md")]
    [InlineData("deliverable.md")]
    public async Task Write_ToAVaultLookalike_InARunWorkspace_IsAllowed(string path)
    {
        var (result, pending) = await WriteAsync(path, "working file");

        Assert.Null(result);
        Assert.NotNull(pending);
        var executed = await pending!.Execute();
        Assert.True(Prop<bool>(executed!, "success"));

        Assert.True(File.Exists(Under(SafeFolderPath.NormalizeWorkspaceRoot(_runRoot), path)));
    }

    [Theory]
    [InlineData("vault/x.md", "sources/x.md")]
    [InlineData("Vault\\sources\\x.md", "sources/x.md")]
    [InlineData("./Vault/x.md", "sources/x.md")]
    [InlineData(AbsoluteMarker, "sources/x.md")]
    [InlineData("Vault", "sources/<name>.md")]
    public async Task Write_VaultSpellingVariants_AreRefused(string path, string expectedSuggestion)
    {
        if (path == AbsoluteMarker)
            path = Under(SafeFolderPath.NormalizeWorkspaceRoot(_runRoot), "Vault/x.md");

        var (result, pending) = await WriteAsync(path);

        Assert.Null(pending);
        Assert.NotNull(result);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains($"create_source('{expectedSuggestion}'", Prop<string?>(result!, "error")!, StringComparison.Ordinal);

        Assert.False(Directory.Exists(Under(SafeFolderPath.NormalizeWorkspaceRoot(_runRoot), "Vault")));
    }

    [Fact]
    public async Task Write_UnderVaultSources_SuggestsTheExactCall()
    {
        var (result, _) = await WriteAsync("Vault/sources/urlaub/2026.md");

        Assert.NotNull(result);
        Assert.Contains("create_source('sources/urlaub/2026.md'", Prop<string?>(result!, "error")!, StringComparison.Ordinal);
    }

    /// <summary>Deliberate asymmetry: there is no vault-source delete tool the refusal could name.</summary>
    [Fact]
    public async Task Delete_UnderVault_InARunWorkspace_IsNotGivenTheVaultRefusal()
    {
        var target = Under(SafeFolderPath.NormalizeWorkspaceRoot(_runRoot), "Vault/sources/x.md");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "staged");

        var (present, pending) = await DeleteAsync("Vault/sources/x.md");
        Assert.Null(present);
        Assert.NotNull(pending);

        File.Delete(target);

        var (absent, noPending) = await DeleteAsync("Vault/sources/x.md");
        Assert.Null(noPending);
        var message = Assert.IsType<string>(absent);
        Assert.Contains("not found", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("create_source", message, StringComparison.Ordinal);
    }

    private Task<(object? Result, FilesToolCall? PendingAction)> DeleteAsync(string path)
        => _handler.HandleToolCallAsync(
            new FunctionCallContent("d", "delete_file", new Dictionary<string, object?> { ["path"] = path }),
            TestContext.Current.CancellationToken);

    private static string NewDir(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}
