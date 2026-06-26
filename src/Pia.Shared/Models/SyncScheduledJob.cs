using System.Text.Json.Serialization;

namespace Pia.Shared.Models;

/// <summary>
/// Sync DTO for a recurring scheduled job (e.g. periodic research).
/// Execution state (NextFireAt, LastFiredAt, ConsecutiveFailures, LastResultEntryId) is intentionally
/// not synced — only the configured owner device fires the job, so each device tracks its own state.
/// </summary>
public class SyncScheduledJob
{
    public Guid Id { get; set; }

    /// <summary>
    /// Device that owns the firing schedule. Other devices see the job in the UI but do not fire it.
    /// Plaintext on the wire so the server can support a future "transfer ownership" flow.
    /// </summary>
    public Guid? OwnerDeviceId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string? Name { get; set; }
    public string? Query { get; set; }
    public int? Kind { get; set; }

    /// <summary>Dormant: legacy research answer-length. No longer produced by the client; kept for wire-contract stability.</summary>
    public int? AnswerLength { get; set; }

    /// <summary>Write-tool names this job may execute as a background assistant turn (reads always allowed).</summary>
    public List<string>? GrantedTools { get; set; }

    public Guid? ProviderId { get; set; }
    public int? Recurrence { get; set; }
    public TimeOnly? TimeOfDay { get; set; }
    public int? DayOfWeek { get; set; }
    public int? DayOfMonth { get; set; }
    public int? Month { get; set; }
    public DateTime? SpecificDate { get; set; }
    public int? Status { get; set; }

    /// <summary>
    /// Base64: AES-GCM encrypted entity payload (nonce‖ciphertext‖tag).
    /// Non-null when E2EE is active; plaintext content fields will be null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedPayload { get; set; }

    /// <summary>
    /// Base64: DEK wrapped with UMK via AES-GCM (nonce‖wrapped-DEK‖tag).
    /// Non-null when E2EE is active.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WrappedDek { get; set; }
}
