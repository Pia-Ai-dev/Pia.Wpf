namespace Pia.Shared.Models;

/// <summary>
/// An admin-authored persona published to the user's group. Mirrors <see cref="SyncPersona"/> minus the
/// E2EE fields — managed personas are plaintext-only by design (a shared row cannot be wrapped with any
/// single user's UMK) — minus <c>PreferredProviderId</c>, and plus <see cref="IsManaged"/>. Pull-only:
/// never pushed.
/// <para>
/// No <c>PreferredProviderId</c>, deliberately: <see cref="SyncPersona.PreferredProviderId"/> is a soft
/// reference to a Guid-keyed provider row that exists only in the OWNING USER's local store and syncs
/// per-user over the <c>providers</c> channel. An admin cannot know any member's provider ids, and the
/// same Guid denotes a different provider on every machine — so on a shared row the field could only ever
/// be null or a cross-user mis-reference. A managed persona resolves to the member's mode default,
/// exactly as a user persona with no preference does.
/// </para>
/// </summary>
public class SyncManagedPersona
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Tagline { get; set; }
    public string? SystemPrompt { get; set; }
    public string? Guardrails { get; set; }
    public string? OutputFormat { get; set; }
    public List<string>? Expertise { get; set; }
    public string? Archetype { get; set; }
    public string? Emoji { get; set; }
    public string? AccentColor { get; set; }
    public int ToolScope { get; set; } = 2;
    public int? ReasoningEffort { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Always true. Mirrors the client's local Persona.IsBuiltIn convention so the editor
    /// lock survives store merges without inferring provenance from which channel a row arrived on.</summary>
    public bool IsManaged { get; set; } = true;
}
