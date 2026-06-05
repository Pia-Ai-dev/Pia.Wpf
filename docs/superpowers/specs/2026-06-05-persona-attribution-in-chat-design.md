# Persona Attribution in Chat — Design

**Date:** 2026-06-05
**Branch:** feature/personas
**Status:** Approved

## Problem

When the user chats with a persona other than the built-in Pia defaults (Pia · Personal / Pia · Business), the assistant chat (`AssistantView`) gives no indication of which persona produced which reply. Every assistant message shows the same generic Pia avatar, so a conversation that switches personas mid-stream is indistinguishable.

## Goal

Surface, per assistant message, which persona generated it:

- The **top-left avatar** shows the persona's glyph (the Pia app icon for the two built-in Pia personas, the persona's emoji otherwise).
- The persona **name** appears in the message footer, between the token count and the model: `1,234 Tokens · Marketing Writer · gpt-4o`.

Attribution must survive a history reload (the chat is saved and can be reopened later), and must degrade gracefully for messages saved before this feature existed.

## Non-Goals

- No accent-color tinting of the name (personas carry an `AccentColor`, but it is not used here).
- No name header above the message body — the avatar plus footer carry the attribution.
- No backfill/migration of historical messages — they simply show the legacy fallback.

## Design

### 1. Model — message-level snapshot

A persona can be renamed or deleted after a message is sent, so attribution is a **snapshot** taken at send time, not a live lookup.

`src/Pia.Wpf/Models/ChatMessageExtras.cs` (next to `MessageMeta` / `AnswerStats`):

```csharp
public sealed record PersonaAttribution(Guid Id, string Name, string? Emoji);
```

`AssistantMessage` (`src/Pia.Wpf/Models/AssistantMessage.cs`) gains:

```csharp
[ObservableProperty]
private PersonaAttribution? _persona;

public bool HasPersona => Persona is not null;

// Legacy messages (null Persona) fall back to the Pia icon.
public Guid PersonaGlyphId => Persona?.Id ?? BuiltInPersonas.PiaPersonalId;
public string? PersonaGlyphEmoji => Persona?.Emoji;
```

`PersonaGlyphId` raises change notification alongside `Persona` (via the generated `OnPersonaChanged` partial).

### 2. Stamp at send time

In `AssistantViewModel.ExecuteSendMessage`, immediately after the per-turn persona is resolved (~line 468):

```csharp
assistantMessage.Persona = new PersonaAttribution(persona.Id, persona.Name, persona.Emoji);
```

The persona is already resolved there for the system prompt / provider override, so no new lookup is introduced.

### 3. Persistence

`SyncAssistantChatMessage` (`src/Pia.Shared/Models/SyncAssistantChat.cs`) gains a nested, nullable snapshot sibling to `Tokens` / `ModelName`:

```csharp
public SyncMessagePersona? Persona { get; set; }   // null for legacy / user messages

public sealed class SyncMessagePersona
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Emoji { get; set; }
}
```

Old clients round-trip the unknown field via the existing `[JsonExtensionData]` bag, so the wire format stays forward-compatible.

Mapping:
- `AssistantViewModel.MapToDto` — write the snapshot when `m.Persona` is non-null.
- `AssistantViewModel.MapFromDto` **and** `AssistantHistoryViewModel.MapFromDto` — restore it. (Two duplicate mappers exist today; both must be updated.)

Legacy saved messages have no snapshot → `Persona` stays null → Pia-icon avatar, footer unchanged. No migration required.

### 4a. Avatar — `PiaPersonaAvatar`

A small reusable control that renders the existing rounded/shadowed avatar box (currently drawn by `PiaAvatarStyle`) wrapping a `PersonaGlyph`:

```xml
<chat:PiaPersonaAvatar PersonaId="{Binding PersonaGlyphId}"
                       Emoji="{Binding PersonaGlyphEmoji}" />
```

`PersonaGlyph` already chooses Pia-icon vs emoji from the id. The control replaces the static `PiaAvatarStyle` ContentControl in:
- `src/Pia.Wpf/Views/AssistantView.xaml`
- `src/Pia.Wpf/Controls/AssistantHistory/PiaAssistantChatInspector.xaml`

### 4b. Footer — `PiaAnswerToolbar`

`PiaAnswerToolbar` gains a `PersonaName` dependency property, bound from the message in `PiaAssistantMessage.xaml`:

```xml
<chat:PiaAnswerToolbar Stats="{Binding Stats}"
                       PersonaName="{Binding Persona.Name}" ... />
```

The read-only summary text is composed from up to three parts joined by ` · `, dropping empty parts, in this order: **token count, persona name, model**. Recomputed when either `Stats` or `PersonaName` changes.

| Case | Footer text |
|------|-------------|
| persona + stats | `1,234 Tokens · Marketing Writer · gpt-4o` |
| no persona (legacy) | `1,234 Tokens · gpt-4o` *(unchanged)* |
| persona, no token stats | `Marketing Writer` |

`AnswerStats.Summary` is left intact; the toolbar composes from `Stats.Tokens` / `Stats.Model` directly.

### 5. Tests (xunit)

- Stamping: after a send, the assistant message's `Persona` carries the resolved persona's id/name/emoji.
- Round-trip: `MapToDto` → `MapFromDto` preserves the snapshot (both mappers).
- Legacy: a DTO with no persona maps to `Persona == null`, `HasPersona == false`, and `PersonaGlyphId == PiaPersonalId`.
- Footer composition: the three rows in the table above produce the expected strings.

## Affected Files

| File | Change |
|------|--------|
| `Models/ChatMessageExtras.cs` | add `PersonaAttribution` record |
| `Models/AssistantMessage.cs` | add `Persona` + computed helpers |
| `ViewModels/AssistantViewModel.cs` | stamp on send; map to/from DTO |
| `ViewModels/AssistantHistoryViewModel.cs` | map from DTO |
| `Pia.Shared/Models/SyncAssistantChat.cs` | add `SyncMessagePersona` snapshot |
| `Controls/Chat/PiaPersonaAvatar.xaml(.cs)` | **new** reusable avatar control |
| `Controls/Chat/PiaAnswerToolbar.xaml(.cs)` | add `PersonaName` DP + composed summary |
| `Controls/Chat/PiaAssistantMessage.xaml` | bind `PersonaName` to the toolbar |
| `Views/AssistantView.xaml` | use `PiaPersonaAvatar` |
| `Controls/AssistantHistory/PiaAssistantChatInspector.xaml` | use `PiaPersonaAvatar` |
| `tests/Pia.Wpf.Tests/...` | stamping, round-trip, legacy, footer tests |
