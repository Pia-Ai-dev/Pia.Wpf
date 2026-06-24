# 03 — Pia.Mac implementation plan

**Repo:** `/Users/marcoaltmann/Documents/GitHub/Pia.Mac` · **Stack:** Swift, SwiftUI + AppKit,
business logic in the `PiaKit` SPM package, persistence via **GRDB** (SQLite) + JSON settings,
manual DI in `DependencyContainer.swift`, services as `actor`s, view-models as
`@Observable @MainActor`.

Pia.Mac does **not** share C# DTOs — it re-declares the wire shape in Swift (`WireSync…`). Those must
match the JSON produced/consumed by the server (and WPF). Build this **after** the server is
deployed (`01-pia-server.md`).

## Files to create / modify

**Create**

| File | What |
|------|------|
| `Packages/PiaKit/Sources/PiaKit/Models/Persona.swift` | Local model (`Codable`, GRDB `FetchableRecord/PersistableRecord`, `isBuiltIn`). |
| `Packages/PiaKit/Sources/PiaKit/Models/BuiltInPersonas.swift` | The 7 fixed-GUID built-ins (contract §4) — copy GUIDs & prompts verbatim. |
| `Packages/PiaKit/Sources/PiaKit/Services/Protocols/PersonaService.swift` | Protocol. |
| `Packages/PiaKit/Sources/PiaKit/Services/Implementations/LivePersonaService.swift` | `actor`, CRUD + merge built-ins + delete tracking. |
| `Pia.Mac/Features/Settings/Sections/PersonaEditViewModel.swift` | Edit-form state + AI-assist. |
| `Pia.Mac/Features/Settings/Sections/PersonaEditSheet.swift` | The single edit sheet. |
| `Pia.Mac/Features/Settings/Sections/PersonaSettingsSection.swift` | List / add / edit / delete / duplicate. |
| `Pia.Mac/Features/Settings/Sections/PersonaSettingsViewModel.swift` | Backing VM for the section. |

**Modify**

| File | What |
|------|------|
| `Packages/PiaKit/Sources/PiaKit/Database/DatabaseContext.swift` | New migration `vN_personas` creating the `personas` table. |
| `Packages/PiaKit/Sources/PiaKit/Sync/SyncDTOs.swift` | `WireSyncPersona` + `personas` on `SyncPushRequest`/`SyncPullResponse`; `modePersonaDefaults` on `WireSyncSettings`. |
| `Packages/PiaKit/Sources/PiaKit/Sync/SyncMapper.swift` | `toSyncPersona` / `fromSyncPersona`. |
| `Packages/PiaKit/Sources/PiaKit/Services/Implementations/LiveSyncClientService.swift` | Push build + pull merge for personas. |
| `Packages/PiaKit/Sources/PiaKit/Models/AppSettings.swift` | `modePersonaDefaults: [String: UUID]` + helpers. |
| `Pia.Mac/DependencyContainer.swift` | Instantiate `LivePersonaService`, inject into sync. |
| `Pia.Mac/Features/Assistant/AssistantViewModel.swift` + AI client | Resolve active persona; build system prompt; provider/effort override; tool gating. |
| `Pia.Mac/Features/Wizard/WizardView.swift` (+ its VM) | On finish set `modePersonaDefaults["assistant"]` from `userOperatingMode`. |
| `Pia.Mac/Features/Settings/SettingsView.swift` | Embed the personas section. |
| `Pia.Mac/Localizable.xcstrings` | Strings (en/de/fr): Personas, New/Edit/Delete Persona, field labels, tool-scope labels. |

---

## 1. Local model — `Persona.swift`

Mirror `OptimizationTemplate.swift`. `struct Persona: Identifiable, Codable, Hashable, Sendable,
FetchableRecord, PersistableRecord`. Fields: `id: UUID`, `name`, `tagline: String?`,
`systemPrompt`, `guardrails: String?`, `archetype: String`, `expertise: [String]`,
`emoji: String?`, `accentColor: String?`, `toolScope: Int`, `preferredProviderId: UUID?`,
`reasoningEffort: Int?`, `schemaVersion: Int`, `isBuiltIn: Bool`, `createdAt: Date`,
`updatedAt: Date`. Define `databaseTableName = "personas"` and a `Columns` enum.

`BuiltInPersonas.all: [Persona]` — the 7 entries from contract §4, `isBuiltIn = true`. **The UUIDs
must equal the WPF GUIDs exactly** (e.g. `UUID(uuidString: "0000000A-0000-0000-0000-000000000001")!`).

## 2. GRDB migration — `DatabaseContext.swift`

Add `migrator.registerMigration("vN_personas")` (N = next version after the current latest) creating
the `personas` table with columns matching `Persona` (store `expertise` as JSON text), plus an index
on `updatedAt`. Built-ins are **not** rows — they're merged in memory by the service.

## 3. Service — `PersonaService` / `LivePersonaService`

Protocol mirrors `TemplateService`:

```swift
public protocol PersonaService: Sendable {
    func getPersonas() async throws -> [Persona]          // built-ins ∪ user (built-ins first)
    func getPersona(_ id: UUID) async throws -> Persona?
    func addPersona(_ p: Persona) async throws
    func updatePersona(_ p: Persona) async throws         // guard isBuiltIn
    func deletePersona(_ id: UUID) async throws           // guard isBuiltIn; track delete
    func importItem(_ p: Persona) async throws            // for pull (upsert)
    func resolveActive(mode: AppMode, operatingMode: UserOperatingMode) async throws -> Persona
}
```

`LivePersonaService` (`actor`): GRDB read/write like `LiveTodoService`; `getPersonas()` merges
`BuiltInPersonas.all` with user rows (drop user rows whose id is a built-in); `deletePersona`
calls `deleteTracker?.trackDeletion(entityType: "personas", id: id)`; `resolveActive` reads
`modePersonaDefaults` then falls back to the operating-mode Pia built-in (contract §7).

Wire it in `DependencyContainer.swift`: instantiate after the other services, store the property,
and pass it into `LiveSyncClientService`'s initializer.

## 4. Sync DTOs & mapper — `SyncDTOs.swift`, `SyncMapper.swift`

- `WireSyncPersona: Codable, Sendable` with the contract §1 fields. **Match the JSON casing of the
  existing `WireSyncTemplate`/`WireSyncTodo`** (verify how the others encode — same
  `JSONEncoder`/`CodingKeys` strategy) so the server accepts it. Include `encryptedPayload` /
  `wrappedDek` and the structural fields.
- Add `personas: SyncEntityChanges<WireSyncPersona>` to both `SyncPushRequest` and
  `SyncPullResponse` (update their `init` and `CodingKeys`).
- Add `modePersonaDefaults: [String: UUID]` to `WireSyncSettings` (mirror `modeProviderDefaults`).
- `SyncMapper`: `toSyncPersona(_:)` / `fromSyncPersona(_:)` mirroring `toSyncTemplate`/`fromSyncTemplate`
  — including the **E2EE split** (encrypt textual fields into `encryptedPayload`, keep structural
  plaintext; contract §3). `fromSyncPersona` sets `isBuiltIn = false`.

## 5. Sync push/pull — `LiveSyncClientService.swift`

- *Push* (`buildPushRequest`): 
  ```swift
  let personas = try await personaService.getPersonas()
      .filter { !$0.isBuiltIn }
      .map { mapper.toSyncPersona($0) }
  request.personas = SyncEntityChanges(upserted: personas,
                                       deleted: deleteTracker?.getPendingDeletes()["personas"] ?? [])
  ```
  (Apply the same "changed since last sync" filter the other entities use, if present.)
- *Pull* (`pullChanges`): for each `upserted` → `fromSyncPersona` → `importItem` (skip if an existing
  local persona `isBuiltIn`; else last-write-wins by `updatedAt`); for each `deleted` →
  `deletePersona`. Mirror the template handling already there.

## 6. Settings — `AppSettings.swift`

Add `var modePersonaDefaults: [String: UUID] = [:]` and helpers
`personaId(for: AppMode)` / `setPersona(_:for:)`, mirroring `modeProviderDefaults`. It serializes
into `WireSyncSettings.modePersonaDefaults` (step 4) so the selection syncs.

## 7. Assistant integration — `AssistantViewModel.swift` + `LiveAiClientService`

Today the Mac builds the system prompt from `AppSettings.assistantSettings.systemPrompt` (empty ⇒
hardcoded default). Change it to:

1. Resolve the active persona once per send:
   `let persona = try await personaService.resolveActive(mode: .assistant, operatingMode: settings.userOperatingMode)`.
2. Build the system prompt with `persona.systemPrompt` as the identity block + `persona.guardrails`,
   then append the existing substrate (date, language, tools, etc.) — same composition as contract §8.
   *(If the Mac currently has no structured substrate builder, introduce a small
   `AssistantPromptBuilder` so the persona identity and the functional sections are composed
   consistently with WPF.)*
3. Provider/effort override (contract §6): prefer `persona.preferredProviderId` if it resolves,
   else `settings.providerId(for: .assistant)`; apply `persona.reasoningEffort` to the request if set.
4. Tool gating (contract §5): attach tools only when the provider supports them **and**
   `persona.toolScope == 2` (full); `toolScope == 0` (none) ⇒ no tools + no-tools prompt.
5. Persona picker in the Assistant UI (emoji + name chip); on change
   `settings.setPersona(id, for: .assistant)` + `settingsService.save(...)`.

> The freeform `assistantSettings.systemPrompt` field can either be retired in favour of personas or
> kept as an advanced override layered after the persona identity — decide and note it; retiring it
> is cleaner but is a small behaviour change for existing Mac users.

## 8. First-run default — `WizardView` / its VM

Where the wizard already persists `userOperatingMode` + `hasCompletedFirstRunWizard`, also set
`settings.setPersona(operatingMode == .business ? BuiltInPersonas.piaBusinessId : BuiltInPersonas.piaPersonalId, for: .assistant)`.

## 9. Edit UI

Follow the `TemplateEditSheet` pattern: `Mode` enum (`.add` / `.edit(Persona)`), `@State` VM created
in `init`, `Form` with sections (Name, Tagline, System Prompt, Guardrails, Expertise, Emoji, Accent,
Tool Scope picker, Provider picker incl. "Use mode default", Reasoning Effort), `.formStyle(.grouped)`,
toolbar Cancel/Save with `Save` disabled unless `isValid`. Add an **AI-assist** button (draft from a
short description) mirroring the template's prompt-generation. Parent calls
`personaService.addPersona/updatePersona` after the sheet dismisses (the Mac convention).

`PersonaSettingsSection`: list with built-ins read-only (optional **Duplicate** to seed a user
persona), user personas editable/deletable, "Add Persona" button. Embed in `SettingsView`.

## 10. Tests (`PiaKit` tests)

- `LivePersonaService`: built-in merge, built-in immutability, delete tracking, `resolveActive`
  fallback.
- `SyncMapper`: `WireSyncPersona` round-trip plaintext + E2EE; **cross-check the encoded JSON keys
  against a `SyncTemplate`/`SyncTodo` sample** to confirm casing matches the server.
- Prompt builder: persona identity replaces default; `toolScope == 0` ⇒ no tools.

## 11. Parity checklist with the contract

- [ ] Built-in UUIDs byte-identical to WPF `BuiltInPersonas`.
- [ ] `WireSyncPersona` JSON keys match the server (same casing as other Sync DTOs).
- [ ] E2EE encrypts the same textual fields; structural fields stay plaintext.
- [ ] `modePersonaDefaults` syncs via `WireSyncSettings`.
- [ ] Active persona resolution + `UserOperatingMode` default identical to WPF.
