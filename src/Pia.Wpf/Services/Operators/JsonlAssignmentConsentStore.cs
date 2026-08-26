using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Paths;

namespace Pia.Services.Operators;

/// <summary>
/// The local, append-only record of every time a user let content leave the encrypted plane, and the only
/// source of an <see cref="AssignmentConsentReceipt"/>.
/// </summary>
public interface IAssignmentConsentStore
{
    /// <summary>
    /// Writes the record and only then hands back a receipt. AWAITED, unlike the speaker-consent log's
    /// fire-and-forget append: this record is the evidence that the send was consented, so a send whose
    /// record never reached disk must not happen at all.
    /// </summary>
    /// <param name="grantedBy">Who authorised it — <see cref="AssignmentGranter"/>. Required, and required
    /// before <paramref name="ct"/> on purpose: no caller can mint a receipt without naming a granter.</param>
    /// <param name="promptChars">The prompt's LENGTH. The prompt itself never enters this file.</param>
    Task<AssignmentConsentReceipt> RecordAsync(
        string skillName, string mode, IReadOnlyList<AssignmentScopeItem> items,
        string grantedBy, int promptChars, CancellationToken ct = default);

    /// <summary>Whether THIS process wrote that record. Deliberately session-scoped: a receipt is evidence
    /// that a human affirmed a specific selection a moment ago, and one that outlived the process it was
    /// granted in is evidence of nothing.</summary>
    bool WasRecorded(Guid recordId);
}

/// <inheritdoc cref="IAssignmentConsentStore"/>
public sealed class JsonlAssignmentConsentStore : IAssignmentConsentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly string _path;
    private readonly ILogger<JsonlAssignmentConsentStore> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly HashSet<Guid> _written = [];

    public JsonlAssignmentConsentStore(string path, ILogger<JsonlAssignmentConsentStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>Its own file beside the speaker-consent trail rather than inside it: different subject,
    /// different lifecycle, and mixing them would make either one harder to read.</summary>
    public static JsonlAssignmentConsentStore CreateDefault(ILogger<JsonlAssignmentConsentStore> logger)
    {
        var directory = PiaPaths.ConsentAuditDirectory;
        Directory.CreateDirectory(directory);
        return new JsonlAssignmentConsentStore(Path.Combine(directory, "assignments.jsonl"), logger);
    }

    public async Task<AssignmentConsentReceipt> RecordAsync(
        string skillName, string mode, IReadOnlyList<AssignmentScopeItem> items,
        string grantedBy, int promptChars, CancellationToken ct = default)
    {
        var recordId = Guid.NewGuid();
        var grantedAt = DateTime.UtcNow;

        // Metadata only, the same rule the speaker-consent trail follows: the entity id is what lets the app
        // resolve a record's title locally when the user asks what they sent, so the title itself — user
        // content — never needs to be in the file.
        var entry = new
        {
            recordId,
            grantedAt,
            skill = skillName,
            mode,
            itemCount = items.Count,
            totalChars = items.Sum(i => i.CharCount),
            grantedBy,
            promptChars,
            items = items.Select(i => new { i.EntityType, i.EntityId, i.CharCount }).ToArray(),
        };

        await _writeGate.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(
                _path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine, Encoding.UTF8, ct);
            _written.Add(recordId);
        }
        finally
        {
            _writeGate.Release();
        }

        _logger.LogInformation(
            "Recorded consent {RecordId} to send {ItemCount} record(s) to the '{Skill}' skill, granted by {GrantedBy}.",
            recordId, items.Count, skillName, grantedBy);

        return new AssignmentConsentReceipt(recordId, skillName, items, grantedAt);
    }

    public bool WasRecorded(Guid recordId)
    {
        _writeGate.Wait();
        try
        {
            return _written.Contains(recordId);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
