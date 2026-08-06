# Persona-Type Model Routing — WPF Client Handoff

> Server-side implementation: `docs/plans/2026-08-06-persona-type-model-routing.md` (shipped on
> `feature/connector-abstraction-phase1` in the Pia repo). This document is the client half, for the
> Pia.Wpf repo (`C:\projects\Pia.Wpf`). Line numbers verified 2026-08-06 against that checkout — treat
> them as approximate.

## What the server already does

- `SyncPersona.ModelType` (`lib/Pia.Wpf/src/Pia.Shared/Models/SyncPersona.cs`) exists — added in the
  Pia repo's submodule working tree. **It is not committed yet**: the change is backed up at
  `%LOCALAPPDATA%\Temp\opencode\pia-wpf-syncpersona-modeltype.patch` and must be committed in the
  Pia.Wpf repo (then the Pia repo's submodule pointer bumped) as part of this client work.
- Sync round-trips `ModelType` as a plaintext structural field (both E2EE and plaintext modes);
  push validation rejects values over 50 chars with 400.
- `POST /api/ai/chat` accepts an optional `metadata` object. Reserved key `pia_persona_type`:
  Assistant-mode chat is routed to the group's `PersonaTypeProviderIds[type]` catalog provider.
- Behavior contract (frozen server-side, tested):
  - Assistant mode **only** — any other mode ignores the key.
  - The group's per-mode mapping (`ModeProviderIds["Assistant"]`) **wins** over persona-type routing.
  - Unknown/unmapped types, blank values, and values over 50 chars fall through silently — never a 400.
  - `metadata` is never forwarded to the upstream provider and never persisted server-side.

## Client work

### 1. `Persona.ModelType`

`src/Pia.Wpf/Models/Persona.cs` — add `public string? ModelType { get; set; }` (free-form string,
≤50 chars, `null` = no persona-type routing). Map it both ways in the persona sync mapping
(`SyncPersona.ModelType` already exists — see above).

### 2. Picker in `PersonaEditContentDialog`

`src/Pia.Wpf/Views/Dialogs/PersonaEditContentDialog.xaml(.cs)` — add a model-type field to the
structural section (next to Archetype): an editable combo box (free text allowed) with suggested
values `general`, `fast`, `code`. Empty = null. This is a routing hint, not a validated enum — the
server falls through on anything unmapped, so do not restrict input.

### 3. Send `metadata` on chat requests

`src/Pia.Wpf/Services/PiaCloudChatClient.cs` `BuildRequestBody` (~L295-320) — add:

```csharp
if (modelType is not null)
    body["metadata"] = new JsonObject { ["pia_persona_type"] = modelType };
```

Omit the key entirely when `ModelType` is null (no empty `metadata` object on the wire). Thread the
value from the resolved persona at the three resolution sites:

- `src/Pia.Wpf/ViewModels/Models/ChatSessionManager.cs` (~L719, `ResolveActiveAsync`)
- `src/Pia.Wpf/ViewModels/AssistantViewModel.cs` (~L593 and ~L1629)
- `src/Pia.Wpf/Services/BackgroundAssistantTurnRunner.cs` (~L115)

`BuildRequestBody` currently receives only messages + `ChatOptions`; pass the persona's `ModelType`
down alongside (a small optional parameter on the chat methods is enough — no public contract beyond
this repo consumes them).

## Notes

- `ModelType` is plaintext even under E2EE (structural field, like `Archetype`) — it must never go
  into the encrypted persona payload.
- Privacy-first logging applies: a persona's model type is a user-named setting — no logging needed
  at all here; if any is added, use the `Pia.Logging` helpers.
- Zero-warning policy: verify `dotnet build -t:Rebuild -v:n` and `-c Release` both report
  `0 Warning(s)` before committing in Pia.Wpf.
