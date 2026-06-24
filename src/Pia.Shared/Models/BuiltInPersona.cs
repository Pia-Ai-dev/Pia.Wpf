namespace Pia.Shared.Models;

/// <summary>
/// Definition of a built-in (app-shipped) persona. Built-ins are read-only, never synced, and
/// hardcoded byte-for-byte in each client (the <paramref name="Id"/> values are the fixed GUIDs in
/// docs/personas/TARGET/00-shared-contract.md §4). The WPF/Mac clients convert these into their
/// local <c>Persona</c> model with <c>IsBuiltIn = true</c>.
/// </summary>
/// <param name="Id">Fixed GUID (string form) — must match across clients.</param>
/// <param name="Name">Display name.</param>
/// <param name="Tagline">One-liner for the picker / Council cards.</param>
/// <param name="SystemPrompt">Identity/voice block that replaces the assistant identity.</param>
/// <param name="Guardrails">Optional constraints appended after the identity.</param>
/// <param name="OutputFormat">Response-format guidance (the per-persona body of the prompt's
/// "Output Format" section). <c>null</c> ⇒ the client falls back to its substrate default.</param>
/// <param name="Archetype">assistant | analyst | creative | visionary | explainer | custom.</param>
/// <param name="Expertise">Domain tags (small list).</param>
/// <param name="Emoji">Single emoji for the chip.</param>
/// <param name="AccentColor">Hex <c>#RRGGBB</c> for the chip/attribution.</param>
/// <param name="ToolScope">0 = none, 1 = read-only (reserved), 2 = full.</param>
public record BuiltInPersona(
    string Id,
    string Name,
    string? Tagline,
    string SystemPrompt,
    string? Guardrails,
    string? OutputFormat,
    string Archetype,
    IReadOnlyList<string> Expertise,
    string? Emoji,
    string? AccentColor,
    int ToolScope);
