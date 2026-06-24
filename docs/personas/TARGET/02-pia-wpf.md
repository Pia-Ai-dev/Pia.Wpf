# 02 — Pia.Wpf implementation plan

**Repo:** `/Users/marcoaltmann/Documents/GitHub/Pia.Wpf` · **Stack:** WPF (net10.0-windows), MVVM
(CommunityToolkit), WPF-UI, raw-SQLite persistence (`SqliteContext`) + `JsonPersistenceService`.

This codebase **owns `Pia.Shared`**, so the DTO work here (§1) is the prerequisite for the server.

## Files to create / modify

| Action | File | What |
|--------|------|------|
| **Create** | `src/Pia.Shared/Models/SyncPersona.cs` | The wire DTO (contract §1). |
| Modify | `src/Pia.Shared/Sync/SyncPushRequest.cs` | `SyncEntityChanges<SyncPersona> Personas`. |
| Modify | `src/Pia.Shared/Sync/SyncPullResponse.cs` | `SyncEntityChanges<SyncPersona> Personas`. |
| Modify | `src/Pia.Shared/Models/SyncSettings.cs` | `Dictionary<int,Guid> ModePersonaDefaults`. |
| **Create** | `src/Pia.Shared/BuiltInPersonas.cs` | Static catalog (fixed GUIDs + prompts, contract §4). |
| **Create** | `src/Pia.Wpf/Models/Persona.cs` | Local model (+ `IsBuiltIn`, `ToolScope` enum, `Archetype`). |
| **Create** | `src/Pia.Wpf/Services/Interfaces/IPersonaService.cs` | CRUD + merge + active resolution. |
| **Create** | `src/Pia.Wpf/Services/PersonaService.cs` | `JsonPersistenceService<List<Persona>>`, merges built-ins. |
| Modify | `src/Pia.Wpf/Services/SyncMapper.cs` | `ToSyncPersona` / `FromSyncPersona` + `ModePersonaDefaults` merge. |
| Modify | `src/Pia.Wpf/Services/SyncClientService.cs` | Push build + pull merge for personas. |
| Modify | `src/Pia.Wpf/Infrastructure/SqliteContext.cs` | `Personas` table in `EnsureSchema` + `MigrateSchema`. |
| Modify | `src/Pia.Wpf/Models/AppSettings.cs` | `ModePersonaDefaults` + `Get/SetPersonaForMode`. |
| **Create** | `src/Pia.Wpf/ViewModels/Models/PersonaEditModel.cs` | `ObservableValidator` edit model + AI-assist command. |
| **Create** | `src/Pia.Wpf/Views/Dialogs/PersonaEditContentDialog.xaml(.cs)` | The single rich edit dialog. |
| Modify | `src/Pia.Wpf/Services/Interfaces/IDialogService.cs` + `Services/DialogService.cs` | `ShowPersonaEditDialogAsync`. |
| **Create** | `src/Pia.Wpf/ViewModels/PersonaSettingsViewModel.cs` + a settings View | Manage personas (list / add / edit / delete / duplicate). |
| Modify | `src/Pia.Wpf/ViewModels/AssistantViewModel.cs` | Resolve active persona; `BuildSystemPrompt` injection; provider/effort override; tool gating; picker. |
| Modify | `src/Pia.Wpf/ViewModels/FirstRunWizardViewModel.cs` | On finish, set `ModePersonaDefaults[Assistant]` from `UserOperatingMode`. |
| Modify | `src/Pia.Wpf/Bootstrapper.cs` | Register `IPersonaService`. |

---

## 1. `Pia.Shared` (land + tag first — server depends on it)

- Add `SyncPersona.cs` exactly as contract §1 (copy `SyncTemplate.cs`'s JSON attribute conventions).
- `SyncPushRequest.cs` (next to `Templates`, ~line 24) and `SyncPullResponse.cs` (~line 16):
  ```csharp
  public SyncEntityChanges<SyncPersona> Personas { get; set; } = new();
  ```
- `SyncSettings.cs`: add `public Dictionary<int, Guid> ModePersonaDefaults { get; set; } = new();`
  next to `ModeProviderDefaults`.
- `BuiltInPersonas.cs`: a static class (mirror `BuiltInTemplates`) exposing
  `IReadOnlyList<Persona> All` with the seven fixed-GUID entries from contract §4. Keep the GUIDs in
  a `public static readonly Guid` per persona (e.g. `PiaPersonalId`) so callers reference them
  symbolically. *(If you prefer `BuiltInPersonas` to depend only on `Pia.Shared` types, model it as
  a record like `BuiltInTemplate` and convert to `Persona` in the WPF service; either is fine —
  match whatever `BuiltInTemplates` does.)*

➡ Land these, tag a Pia.Wpf release, then proceed to the server (`01-pia-server.md` §0).

## 2. Local model — `Persona.cs`

Mirror `OptimizationTemplate.cs`. Include `bool IsBuiltIn`, a `PersonaToolScope` enum
(`None=0, ReadOnly=1, Full=2`), `Archetype` (string or enum), `Expertise` (`List<string>`),
`Emoji`, `AccentColor`, `PreferredProviderId`, `ReasoningEffort?`, `SchemaVersion`,
`CreatedAt`, `UpdatedAt`.

## 3. Persistence — `SqliteContext.cs`

Personas sync, so store in SQLite (not a loose JSON file). Add to `EnsureSchema` (mirror the `Todos`
table ~lines 92–104) and a presence check in `MigrateSchema` (mirror the `Todos.ColumnId` add
~lines 260–292 — create the table if `PRAGMA table_info(Personas)` is empty):

```sql
CREATE TABLE IF NOT EXISTS Personas (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Tagline TEXT,
    SystemPrompt TEXT NOT NULL,
    Guardrails TEXT,
    Archetype TEXT,
    Expertise TEXT,                  -- JSON array
    Emoji TEXT,
    AccentColor TEXT,
    ToolScope INTEGER NOT NULL DEFAULT 2,
    PreferredProviderId TEXT,
    ReasoningEffort INTEGER,
    SchemaVersion INTEGER NOT NULL DEFAULT 1,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    OutputFormat TEXT                -- per-persona "Output Format" section body; null ⇒ substrate default
);
CREATE INDEX IF NOT EXISTS IX_Personas_UpdatedAt ON Personas(UpdatedAt);
```

> Only **user** personas are persisted here. Built-ins are merged in-memory from
> `BuiltInPersonas.All` by the service (they are not rows).
>
> `OutputFormat` was added after the table shipped: `MigrateSchema` runs the table-presence check
> above, then a column-presence check (`PRAGMA table_info(Personas)`) that issues
> `ALTER TABLE Personas ADD COLUMN OutputFormat TEXT` when missing. It is appended **last** in the
> `SELECT` column list so existing reader ordinals stay stable.

## 4. Service — `IPersonaService` / `PersonaService`

Mirror `TemplateService` (`src/Pia.Wpf/Services/TemplateService.cs`) — but persist via
`SqliteContext` rather than a JSON file (follow `TodoService`'s SQLite usage). Interface:

```csharp
public interface IPersonaService
{
    event EventHandler? PersonasChanged;
    Task<IReadOnlyList<Persona>> GetPersonasAsync();        // built-ins ∪ user, built-ins first
    Task<Persona?> GetPersonaAsync(Guid id);
    Task<Persona> AddPersonaAsync(Persona persona);
    Task UpdatePersonaAsync(Persona persona);               // no-op/throw if IsBuiltIn
    Task DeletePersonaAsync(Guid id);                       // tracks delete; no-op if IsBuiltIn
    Task<Persona> ResolveActiveAsync(WindowMode mode, UserOperatingMode operatingMode); // never null
}
```

- `GetPersonasAsync`: start from `BuiltInPersonas.All` (`IsBuiltIn = true`), then add user rows whose
  `Id` isn't a built-in GUID (mirror `TemplateService.GetTemplatesAsync` merge ~lines 27–53).
- `Add/Update/Delete`: write to SQLite, bump `UpdatedAt`, raise `PersonasChanged`. `Update`/`Delete`
  guard against built-in GUIDs. `Delete` calls `_deleteTracker.TrackDeletion("personas", id)`.
- `ResolveActiveAsync`: `settings.GetPersonaForMode(mode)` → look up; if missing/unknown, fall back
  to `UserOperatingMode`-mapped Pia built-in (contract §7). Always returns a non-null `Persona`.
- **Logging:** never log persona text above debug — use `SensitiveDebug`; log only `Id`/counts at
  `LogInformation`.

Register in `Bootstrapper.cs` (~line 248): `services.AddSingleton<IPersonaService, PersonaService>();`

## 5. Sync wiring

**SyncMapper.cs** — add `ToSyncPersona(Persona, userId)` / `FromSyncPersona(SyncPersona, userId)`
mirroring the Template methods (~lines 37–103):

- `To…`: if E2EE active, encrypt the textual fields (Name, Tagline, SystemPrompt, Guardrails,
  OutputFormat, Expertise) into `EncryptedPayload`/`WrappedDek` (E2EE key `"persona"`), null the
  plaintext; keep structural fields plaintext. Else plaintext, null blob.
- `From…`: reverse; always set `IsBuiltIn = false`.
- Add `MergeModePersonaDefaults(IDictionary<int,Guid>, AppSettings)` mirroring
  `MergeModeProviderDefaults`, and call it from both the E2EE and plaintext settings paths; populate
  `SyncSettings.ModePersonaDefaults` in `ToSyncSettings`.

**SyncClientService.cs** — mirror Templates in both directions:

- *Push build* (~lines 412–420): 
  ```csharp
  Personas = new SyncEntityChanges<SyncPersona>
  {
      Upserted = (await _personaService.GetPersonasAsync())
          .Where(p => !p.IsBuiltIn)
          .Where(p => p.UpdatedAt.ToUniversalTime() >= lastSync)
          .Select(p => _mapper.ToSyncPersona(p, userId)).ToList(),
      Deleted = pendingDeletes.GetValueOrDefault("personas", [])
  }
  ```
- *Pull merge* (~lines 629–687): for each upserted persona, `FromSyncPersona`; if an existing local
  persona is built-in → skip; else last-write-wins by `UpdatedAt`; insert if new; wrap in
  `try/catch (CryptographicException)` like templates. Process `Personas.Deleted` →
  `DeletePersonaAsync`.

`SyncDeleteTrackerService` needs **no change** — entity types are free-form strings; just use
`"personas"`.

## 6. Settings — `AppSettings.cs`

Add next to `ModeProviderDefaults` (~line 78):

```csharp
public Dictionary<WindowMode, Guid> ModePersonaDefaults { get; set; } = new();
public Guid? GetPersonaForMode(WindowMode mode) =>
    ModePersonaDefaults.TryGetValue(mode, out var id) ? id : null;
public void SetPersonaForMode(WindowMode mode, Guid? id)
{
    if (id.HasValue) ModePersonaDefaults[mode] = id.Value;
    else ModePersonaDefaults.Remove(mode);
}
```

## 7. Edit dialog (single rich form)

- **`PersonaEditModel : ObservableValidator`** (mirror `TemplateEditModel`): `[ObservableProperty]`
  + `[Required]` on `Name`, `SystemPrompt`; properties for `Tagline`, `Guardrails`, `OutputFormat`
  (multiline editor; blank ⇒ substrate default), `Archetype`, `Expertise` (comma/string editor),
  `Emoji`, `AccentColor`, `ToolScope`, `PreferredProviderId` (bound to a provider picker incl. an
  "(Use mode default)" null entry), `ReasoningEffort`. Add `FromPersona`/`ToPersona` factories and a
  `CanSave => !HasErrors && !string.IsNullOrWhiteSpace(Name)`.
- **AI-assist command** (mirror `TemplateEditModel.GeneratePromptCommand`): user types a short
  description → call the assistant provider to draft `Name`, `Tagline`, `SystemPrompt`, `Guardrails`,
  `OutputFormat`, `Emoji`, `AccentColor`, `Expertise`, `Archetype`; populate the fields for the user
  to edit (prefilling only the values the user hasn't already set).
- **`PersonaEditContentDialog.xaml(.cs)`** (mirror `TemplateEditContentDialog`): WPF-UI
  `ContentDialog`, validate in `OnClosing` (block close if `!CanSave`).
- **DialogService**: add to `IDialogService` and `DialogService` (~lines 44–51 pattern):
  ```csharp
  public async Task<bool> ShowPersonaEditDialogAsync(PersonaEditModel persona)
  {
      var host = _contentDialogService.GetDialogHostEx() ?? throw new InvalidOperationException("No dialog host");
      return await new PersonaEditContentDialog(host, persona).ShowAsync() == ContentDialogResult.Primary;
  }
  ```

## 8. Settings management view

`PersonaSettingsViewModel` + a View under `Views/SettingsViews/`: list `GetPersonasAsync()`,
built-ins shown read-only (no edit/delete; optional **Duplicate** → seed a new user `Persona` from
the built-in and open the edit dialog), user personas editable/deletable. "Add Persona" opens an
empty edit dialog. Add the section to the existing settings navigation.

## 9. Assistant integration — `AssistantViewModel.cs`

1. **Resolve once per turn** in `ExecuteSendMessage` (before building the prompt):
   `var persona = await _personaService.ResolveActiveAsync(WindowMode.Assistant, settings.UserOperatingMode);`
2. **Provider/effort override** (contract §6): if `persona.PreferredProviderId` resolves to a usable
   provider use it, else the existing `GetDefaultProviderForModeAsync(Assistant)`. If
   `persona.ReasoningEffort` is set, build chat options from a shallow copy of the provider with
   `ReasoningEffort` overridden (don't mutate the stored provider).
3. **Tool gating** (contract §5): use tools only if `provider.SupportsToolCalling` **and**
   `persona.ToolScope == Full`. If `ToolScope == None`, take the existing no-tools path and pass no
   tools.
4. **`BuildSystemPrompt`** and **`BuildSystemPromptNoTools`**: take a `Persona activePersona`
   parameter; the identity comes from `BuildIdentityBlock(activePersona)` (SystemPrompt + optional
   Guardrails + date line). The old hardcoded `## Principles` block is renamed **`## Output Format`**
   and its body is `ResolveOutputFormat(activePersona)` — the persona's `OutputFormat`, or the
   `DefaultOutputFormat` constant when blank. The substrate-owned "don't retry a declined action"
   rule is appended after it **only in the tools path** (it isn't formatting). Composition per
   contract §8.
5. **Picker UI**: a persona selector in the Assistant view (chip showing `Emoji` + `Name`); on
   change, `settings.SetPersonaForMode(Assistant, id)` + save (it syncs via `SyncSettings`). Decide
   whether changing mid-conversation applies from the next turn (recommended) — note it in the UI.

## 10. First-run default — `FirstRunWizardViewModel.cs`

On finish (where it already writes `UserOperatingMode` / `DefaultTemplateId`), set:
`settings.SetPersonaForMode(WindowMode.Assistant, operatingMode == Business ? BuiltInPersonas.PiaBusinessId : BuiltInPersonas.PiaPersonalId);`

## 11. Tests (`tests/Pia.Wpf.Tests`)

- `PersonaService`: merge built-ins + user; built-ins not deletable/updatable; delete tracks
  `"personas"`; `ResolveActiveAsync` falls back to the operating-mode Pia built-in.
- `SyncMapper`: persona round-trip plaintext **and** E2EE (textual incl. `OutputFormat` encrypted,
  structural plaintext).
- `BuildSystemPrompt`: identity replaced by persona; guardrails appended; `ToolScope.None` omits the
  tool-selection section and routes to the no-tools path.
- `ResolveOutputFormat`: uses the persona's `OutputFormat` when set, else `DefaultOutputFormat`; the
  Pia built-ins' `OutputFormat` is pinned byte-identical to `DefaultOutputFormat`.
- Provider override resolution incl. dangling `PreferredProviderId` → falls back to mode default.
