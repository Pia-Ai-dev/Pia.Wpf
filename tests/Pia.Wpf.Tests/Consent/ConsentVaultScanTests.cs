using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure.Vault;
using Pia.Services.Consent;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Consent;

public sealed class ConsentVaultScanTests : IDisposable
{
    private const string SessionA = "3f2a9c1e7b4d4e0f8a6b5c4d3e2f1a0b";
    private const string SessionB = "9b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pia-consent-scan-{Guid.NewGuid():N}");

    public ConsentVaultScanTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TempPath.Remove(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ConsentVaultScan Build(string? root = null) =>
        new(new VaultStore(root ?? _root, new MarkdownVaultParser()), NullLogger.Instance);

    private string Note(string relativePath, string text)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static string Naming(params string[] sessionIds) =>
        $"---\nschema: pia-meeting/v1\nconsentSessions: [{string.Join(", ", sessionIds)}]\n---\n\n# Meeting\n";

    [Fact]
    public async Task EachSession_MapsToTheNotesNamingIt_WithForwardSlashes()
    {
        Note(Path.Combine("sources", "a.md"), Naming(SessionA));
        Note(Path.Combine("notes", "moved", "b.md"), Naming(SessionA, SessionB));
        Note("plain.md", "# No front matter\n");

        var scan = await Build().ScanAsync(Ct);

        Assert.True(scan.IsComplete);
        Assert.Equal(3, scan.NotesScanned);
        Assert.Equal(["notes/moved/b.md", "sources/a.md"], scan.NotesBySession[SessionA]);
        Assert.Equal(["notes/moved/b.md"], scan.NotesBySession[SessionB]);
        Assert.Equal(2, scan.NotesBySession.Count);
    }

    [Fact]
    public async Task AnAbsentVault_IsIncomplete()
    {
        var scan = await Build(Path.Combine(_root, "missing")).ScanAsync(Ct);

        Assert.False(scan.IsComplete);
        Assert.Empty(scan.NotesBySession);
    }

    [Fact]
    public async Task ANoteThatCannotBeRead_MakesTheScanIncomplete_AndTheOthersStillCount()
    {
        Note("a.md", Naming(SessionA));
        var locked = Note("b.md", Naming(SessionB));

        ConsentVaultScanResult scan;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            scan = await Build().ScanAsync(Ct);
        }

        Assert.False(scan.IsComplete);
        Assert.Equal(1, scan.NotesUnreadable);
        Assert.Equal(["a.md"], scan.NotesBySession[SessionA]);
        Assert.False(scan.NotesBySession.ContainsKey(SessionB));
    }
}
