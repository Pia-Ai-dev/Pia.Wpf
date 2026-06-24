# Persona System — Implementation Plan (WPF · Mac · Server)

> Status: **Plan / not yet implemented.** Target: Assistant-mode personas, synced, with
> app-shipped defaults + user-authored personas. Council & Brainstorming are **out of scope**
> for this iteration but the schema is designed so they can reference personas later.
>
> **Update (post-plan):** an `OutputFormat` field was added so the prompt's formatting section is
> per-persona (Pia built-ins keep the historical text; others ship tailored formats). It is a
> sensitive textual field (encrypted under E2EE). **Implemented on WPF;** server (`ServerPersona`
> entity/migration/validation) and Mac (`WireSyncPersona`, GRDB column, edit sheet) need the parallel
> change. See contract §1/§3/§4/§8.

## 1. What we're building

A **persona** is a reusable bundle of *identity + voice + role + expertise* that shapes how the
assistant answers. It is the Assistant-mode analogue of an `OptimizationTemplate` (which shapes
Optimize mode). A persona:

- **fully replaces** the assistant's identity/personality block in the system prompt (the
  substrate — date, language, tools, privacy, web-search — stays intact);
- can optionally **override the model/provider** it runs on (soft reference + reasoning effort);
- declares a **tool scope** (none / read-only / full);
- carries **visual identity** (emoji + accent colour) for attribution in the picker and, later,
  Council cards.

There is **always** an active persona. The hardcoded *"You are Pia, a helpful personal assistant"*
identity stops being hardcoded and becomes **data**, shipped as two built-in personas
(**Pia · Personal**, **Pia · Business**). The first-run `UserOperatingMode` (Personal/Business)
picks which one is active by default.

## 2. Decisions locked in

| # | Decision | Choice |
|---|----------|--------|
| 1 | Built-in personas | **Read-only.** Shipped in each client binary, never synced, not editable. |
| 2 | Authoring UX | **Single rich edit dialog** (no wizard), with an AI-assist "draft from a description" button. |
| 3 | Per-persona model | **Yes** — optional soft `PreferredProviderId` + `ReasoningEffort` override; falls back to the mode default if missing/deleted. |
| 4 | Scope of iteration | **Personas first.** No Council code; schema keeps `Archetype` + `AccentColor` for future Council. |
| 5 | Prompt composition | A persona **replaces** the identity block entirely; functional substrate is appended unchanged and gated by `ToolScope`. |
| 6 | Two base personalities | Pia · Personal and Pia · Business, seeded from the current identity text, selected by `UserOperatingMode`. |

**Open / recommended-yes (not blocking):** offer a *"Duplicate"* action on a built-in that creates
a new **user** persona seeded from the built-in's content. This does not "customise the built-in"
(it stays read-only) — it's just a creation shortcut. Recommended, flagged for confirmation.

## 3. The three codebases

| Codebase | Path | Role | Shared-DTO relationship |
|----------|------|------|--------------------------|
| **Pia.Wpf** | `/Users/marcoaltmann/Documents/GitHub/Pia.Wpf` | Windows client | **Owns** `Pia.Shared` (the C# DTOs). |
| **Pia (Server)** | `/Users/marcoaltmann/Documents/GitHub/Pia` | ASP.NET Core + EF Core sync backend | Consumes `Pia.Shared` via **git submodule** `lib/Pia.Wpf` (`ProjectReference`). |
| **Pia.Mac** | `/Users/marcoaltmann/Documents/GitHub/Pia.Mac` | macOS/iOS client (Swift/SwiftUI + `PiaKit`) | **Re-declares** the wire shape by hand in Swift (`WireSync*`); must match the JSON contract. |

Per-platform plans:

- [`00-shared-contract.md`](00-shared-contract.md) — **the canonical schema, JSON wire contract,
  built-in GUIDs, built-in prompts, prompt-composition spec, E2EE rules.** Read this first; the
  other three implement it.
- [`01-pia-server.md`](01-pia-server.md) — Pia server (entity, migration, sync push/pull, quota, validation).
- [`02-pia-wpf.md`](02-pia-wpf.md) — Pia.Wpf (DTO, model, service, sync wiring, edit dialog, prompt integration).
- [`03-pia-mac.md`](03-pia-mac.md) — Pia.Mac (model, GRDB migration, wire DTO/mapper, sync, edit sheet, prompt integration).

## 4. Cross-cutting contract (must agree across all three)

1. **Built-in persona GUIDs are a fixed constant** shared byte-for-byte between WPF and Mac
   (server never stores them). Source of truth: [`00-shared-contract.md`](00-shared-contract.md) §4.
2. **Built-ins are never pushed** (`Where(!IsBuiltIn)` on push) and **never overwritten on pull**
   (skip-if-built-in on merge), mirroring templates.
3. **Active-persona selection syncs** as a per-mode setting (`ModePersonaDefaults`) inside the
   existing `SyncSettings` — even when the selected persona is a built-in (its GUID is identical
   on every device).
4. **E2EE field split** is identical everywhere: textual fields encrypted into
   `EncryptedPayload`/`WrappedDek`; structural/config fields stay plaintext. See contract §3.
5. **`SchemaVersion`** is sent on every persona; clients ignore unknown fields and tolerate older
   versions (additive evolution only).
6. **Wire JSON casing** matches the existing `SyncTemplate`/`SyncTodo` convention. Mac mirrors it.

## 5. Recommended rollout order

Because of the submodule dependency, the order matters:

1. **Pia.Wpf — `Pia.Shared` DTOs first** (`SyncPersona`, add to `SyncPushRequest`/`SyncPullResponse`,
   add `ModePersonaDefaults` to `SyncSettings`). Land + tag a new `Pia.Wpf` release.
2. **Server** — bump the `lib/Pia.Wpf` submodule pointer to that tag, then add `ServerPersona`,
   migration, sync push/pull, quota, validation. Deploy (additive & backward-compatible).
3. **Pia.Wpf — client feature** (model, `PersonaService`, sync wiring, edit dialog, settings UI,
   `BuildSystemPrompt` integration, built-ins, first-run default).
4. **Pia.Mac** — mirror the wire DTOs in Swift, GRDB migration, service, sync wiring, edit sheet,
   settings, prompt integration, built-ins, first-run default.

Steps 3 and 4 are independent of each other once steps 1–2 are deployed.

## 6. Backward / forward compatibility

- **Additive sync.** Old clients that don't send/read `Personas` are unaffected; the server's
  `Personas` arrays are simply absent/ignored. The server returning `Personas` to an old client is
  harmless (deserializer ignores the unknown property).
- **Identity refactor is behaviour-preserving:** Pia · Personal's prompt is seeded from the current
  identity wording, so existing Personal-mode users see no behavioural change after upgrade.
- **New users:** first-run wizard sets the default active persona from `UserOperatingMode`.

## 7. Privacy & logging (applies to all C# code; mirror intent on Mac)

Persona **name** and **prompt/tagline/guardrails** are user-named content. Per `CLAUDE.md`:

- Never log persona text at non-debug levels. Use `_logger.SensitiveDebug(...)` for any line that
  includes a name/prompt/tagline; log only `Id`/counts at `LogInformation`.
- Persona text is E2EE-eligible content → it goes into `EncryptedPayload` when E2EE is active.
