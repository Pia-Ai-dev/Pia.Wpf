using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Logging;

namespace Pia.Services.Consent;

/// <summary>Finds the vault notes whose front matter names a consent session, wherever in the vault they moved.</summary>
public sealed class ConsentVaultScan
{
    // A note that opens with a thematic break and never closes it must not be read whole.
    private const int FrontMatterLineCap = 1000;

    private const string FrontMatterDelimiter = "---";

    private readonly IVaultStore _vault;
    private readonly ILogger _logger;

    public ConsentVaultScan(IVaultStore vault, ILogger logger)
    {
        _vault = vault;
        _logger = logger;
    }

    public async Task<ConsentVaultScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var root = _vault.Root;
        // An offline or not yet derived vault shows no notes, which must not read as "every note is gone".
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return ConsentVaultScanResult.Incomplete;

        IReadOnlyList<string> notes;
        try
        {
            notes = await _vault.EnumerateAsync("*.md").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Failed to list the vault for consent sessions ({Error})", ex.GetType().Name);
            _logger.SensitiveDebug("Failed to list the vault for consent sessions: {Exception}", ex);
            return ConsentVaultScanResult.Incomplete;
        }

        var notesBySession = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var unreadable = 0;
        foreach (var note in notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reference = note.Replace('\\', '/');
                foreach (var sessionId in ConsentFrontMatter.ReadSessions(ReadFrontMatter(Path.Combine(root, note))))
                {
                    if (!notesBySession.TryGetValue(sessionId, out var references))
                        notesBySession[sessionId] = references = [];
                    references.Add(reference);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable++;
            }
        }

        if (unreadable > 0)
            _logger.LogWarning("Consent vault scan could not read {Count} notes; their sessions are kept", unreadable);

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sessionId, references) in notesBySession) result[sessionId] = references;
        return new ConsentVaultScanResult(result, notes.Count, unreadable, IsComplete: unreadable == 0);
    }

    // Shared, so a note open in an editor is still read.
    private static string? ReadFrontMatter(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var first = reader.ReadLine();
        if (first is null || first.TrimEnd() != FrontMatterDelimiter) return null;

        var text = new StringBuilder(first).Append('\n');
        for (var i = 0; i < FrontMatterLineCap && reader.ReadLine() is { } line; i++)
        {
            text.Append(line).Append('\n');
            if (line.TrimEnd() == FrontMatterDelimiter) return text.ToString();
        }
        return null;
    }
}

/// <param name="NotesBySession">Session id to the vault-relative notes naming it, with forward slashes.</param>
/// <param name="IsComplete">False when the vault is absent or a note could not be read, so a missing session proves nothing.</param>
public sealed record ConsentVaultScanResult(
    IReadOnlyDictionary<string, IReadOnlyList<string>> NotesBySession,
    int NotesScanned,
    int NotesUnreadable,
    bool IsComplete)
{
    public static ConsentVaultScanResult Incomplete { get; } =
        new(new Dictionary<string, IReadOnlyList<string>>(), 0, 0, IsComplete: false);
}
