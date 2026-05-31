namespace Pia.Models;

/// <summary>
/// A reusable bundle of identity + voice + role + expertise that shapes how the assistant answers —
/// the Assistant-mode analogue of an <see cref="OptimizationTemplate"/>. Built-in personas set
/// <see cref="IsBuiltIn"/> = true (read-only, never synced); user personas set it to false and sync.
/// See docs/personas/TARGET/00-shared-contract.md §1/§2.
/// </summary>
public class Persona
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Tagline { get; set; }
    public required string SystemPrompt { get; set; }
    public string? Guardrails { get; set; }

    /// <summary>assistant | analyst | creative | visionary | explainer | custom. Default "custom".</summary>
    public string Archetype { get; set; } = "custom";

    /// <summary>Domain tags (small list, ≤ 16).</summary>
    public List<string> Expertise { get; set; } = [];

    public string? Emoji { get; set; }

    /// <summary>Hex <c>#RRGGBB</c> for the chip/attribution.</summary>
    public string? AccentColor { get; set; }

    public PersonaToolScope ToolScope { get; set; } = PersonaToolScope.Full;

    /// <summary>Soft reference to an <see cref="AiProvider"/>; <c>null</c> ⇒ use the mode default.</summary>
    public Guid? PreferredProviderId { get; set; }

    /// <summary>Optional per-turn reasoning-effort override; <c>null</c> ⇒ provider default.</summary>
    public ReasoningEffort? ReasoningEffort { get; set; }

    public int SchemaVersion { get; set; } = 1;

    /// <summary>Local-only flag (not a wire field): derived from membership in the built-in catalog.</summary>
    public bool IsBuiltIn { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC. Conflict key for last-write-wins sync (mirrors <c>SyncTodo</c>, not <c>ModifiedAt</c>).</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
