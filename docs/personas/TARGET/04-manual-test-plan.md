# 04 — Manual Test Plan (Pia.Wpf client + server update)

> Use this after the **Pia.Wpf persona client feature** lands and the **server persona sync**
> (`01-pia-server.md`) is deployed. It targets what automated tests *cannot* cover: the live UI,
> the cross-device sync round-trip (incl. E2EE), and behaviour-preservation for existing users.
>
> The WPF client is `net10.0-windows` — **build and run on Windows**. Automated unit tests
> (`dotnet test`) cover SyncMapper round-trip, `PersonaService`, and prompt composition; run them
> first, then work through this plan.

## 0. Prerequisites

- [ ] WPF client built `Release` on Windows from this branch; `dotnet test` green.
- [ ] Server deployed with persona sync (entity, migration applied, push/pull, quota, validation).
- [ ] **Two** client devices (A and B) signed in to the **same** sync account — needed for §J–§L.
      A clean profile on at least one (no prior `%LOCALAPPDATA%\Pia\history.db`) to exercise migration.
- [ ] A non-PiaCloud provider configured (e.g. OpenAI/Ollama) **and** PiaCloud, to exercise both
      AI-assist paths and the provider override.
- [ ] Know how to read logs: `%LOCALAPPDATA%\Pia\Logs\pia-*.log` (used in §N).

## 1. How to read this plan

Each row is **action → expected**. Anything marked **🔒 privacy** must be re-checked in a Release
build. Anything marked **🔁 sync** needs both devices. Record pass/fail + notes per row.

---

## A. Built-in personas present & read-only

- [ ] Open Settings → **Personas**. All 7 built-ins are listed first: Pia · Personal, Pia · Business,
      Experienced Coder, Marketing Writer, Financial Expert, Worldwide Company CEO, Explain It Simply.
- [ ] Each built-in shows its emoji + a "Built-in" badge; **no Edit / Delete** buttons on built-ins.
- [ ] **Duplicate** is available on a built-in → opens the edit dialog seeded from the built-in with a
      new name ("… (copy)"); saving creates a **new user** persona (the built-in stays unchanged).
- [ ] Emoji/accent render correctly (🟣 #7C4DFF, 🔵 #2962FF, 💻, ✍️, 📈, 🌐, 🧒).

## B. Persona CRUD

- [ ] **Add Persona** → empty dialog → fill Name + System Prompt → Save → appears in the list, editable.
- [ ] **Edit** a user persona → change fields → Save → list reflects changes.
- [ ] **Delete** a user persona → it disappears; confirm it does not reappear after app restart.
- [ ] Try to **Save** with an empty Name or empty System Prompt → blocked with a validation message.
- [ ] Expertise entered as comma-separated text round-trips (re-open the persona → tags preserved).
- [ ] **Output Format**: enter custom guidance (e.g. "- Answer only in haiku.") → Save → re-open → it
      round-trips. Leave it **blank** on another persona → Save → re-open → still blank (the substrate
      default applies at prompt time, not stored as text).
- [ ] Restart the app → all user personas persist (SQLite); built-ins still present and first.

## B′. Output-format behaviour (per-persona)

- [ ] With **Pia · Personal/Business** active, replies stay short/plain (unchanged from before this
      feature — the historical formatting block).
- [ ] Switch to **Experienced Coder** → answers lead with the recommendation and use fenced code blocks;
      switch to **Explain It Simply** → tiny paragraphs, plain words, no headings/tables/code.
- [ ] A persona with a **custom Output Format** visibly follows it (e.g. the haiku persona above), while a
      persona left **blank** behaves like the Pia default.
- [ ] Even with a creative/custom Output Format, the assistant still does **not** retry a declined action
      (the substrate tool-safety rule is preserved in the tools path).

## C. Edit dialog + AI-assist ("draft from a description")

- [ ] With a **non-PiaCloud** provider as the Assistant default: type a short description → **Draft with AI**
      → Name, Tagline, System Prompt, Guardrails, **Output Format**, Archetype, Emoji, Accent, Expertise get
      populated (only fields you left blank); fields remain editable.
- [ ] With **PiaCloud**: Draft with AI fills **System Prompt only** (other fields left for you) — no error.
- [ ] Pick a **Preferred Provider** ("(Use mode default)" is the first/null option) and a **Reasoning Effort**
      ("(Provider default)" is the first/null option); Save; re-open → both selections round-trip.
- [ ] Set **Tool Access** = None / Full / Read-only; Save; re-open → value round-trips.
- [ ] While drafting, the button shows a spinner and inputs are read-only; it re-enables afterward.

## D. First-run default

- [ ] Fresh profile → run the first-run wizard, choose **Personal** → finish → open Assistant → the active
      persona is **Pia · Personal**.
- [ ] Fresh profile → choose **Business** → finish → active persona is **Pia · Business**.
- [ ] **Skip** the wizard (if possible) → Assistant still has a working persona (falls back to Pia · Personal).

## E. Active-persona picker (Assistant view)

- [ ] The composer shows a persona chip (emoji + name) reflecting the active persona.
- [ ] Changing the picker updates the chip and persists (re-open Assistant / restart → selection retained).
- [ ] The change applies **from the next turn** (a request already streaming is unaffected).

## F. Prompt integration — identity (contract §8)

- [ ] **Behaviour-preserving:** with **Pia · Personal** active, the assistant behaves as before the upgrade
      (warm, concise, friendly). No regression for existing Personal-mode users.
- [ ] Switch to **Experienced Coder** → answers become senior-engineer style (edge cases, trade-offs).
- [ ] **Financial Expert** → responses include its guardrail ("general educational information only…").
- [ ] **Explain It Simply** → plain-language explanations; asks short "why?" questions when *you* explain.
- [ ] The date line, language instruction, principles, and (when enabled) privacy-token / web-search
      sections are still present regardless of persona (substrate intact).

## G. Provider / reasoning-effort override (contract §6)

- [ ] Persona with **PreferredProviderId** set to an existing provider → that turn uses that provider
      (verify via the model/provider used; check the "resolved persona" log line).
- [ ] Persona whose **PreferredProviderId points at a deleted/unknown provider** → falls back to the
      Assistant **mode default** (no crash). *(This is the dangling-reference case.)*
- [ ] Persona with **ReasoningEffort** set (e.g. High) on a reasoning-capable provider → the effort is
      applied for that turn; the **stored provider is unchanged** (open Settings → Providers → effort
      still shows the provider's own value, not the persona's).

## H. Tool gating (contract §5)

- [ ] **ToolScope = Full** + tool-capable provider → tools work (create a todo/reminder/memory via chat).
- [ ] **ToolScope = None** → the assistant uses the **no-tools** path: it will not call tools; the
      Tool-Selection section is absent from the system prompt; @-commands don't load tools.
- [ ] **ToolScope = Read-only** → behaves as Full in v1 (tools available) — documented fast-follow.
- [ ] Provider **without** tool support + ToolScope Full → still no tools (provider gates win).

## I. Voice mode

- [ ] Enter voice mode with a non-default persona active → spoken responses use the **same** persona
      identity and ToolScope as text mode.

## J. Sync — non-E2EE, two devices 🔁

> E2EE **off** on both. Use distinct, identifiable persona names.

- [ ] Create persona "SyncTest-A" on **A** → sync → on **B** it appears (correct fields, **not** built-in).
- [ ] Edit "SyncTest-A" on **B** → sync → **A** reflects the edit (last-write-wins on `UpdatedAt`).
- [ ] Edit the same persona on **both** offline, then sync → the **later** edit wins; no duplicate row.
- [ ] Delete "SyncTest-A" on **A** → sync → it's removed on **B** (deletion tracked as `personas`).
- [ ] **Built-ins never sync:** confirm built-in personas are never sent (server has no rows for the
      `0000000A-…` GUIDs) and are never overwritten on either device.
- [ ] **Active-persona selection syncs:** set Assistant persona on **A** (incl. selecting a *built-in*) →
      sync → **B**'s Assistant picker shows the same active persona (via `ModePersonaDefaults`).

## K. Sync — E2EE on 🔁🔒

> Enable E2EE and complete onboarding on both devices.

- [ ] Create/edit a persona on **A** → inspect the server payload (admin/DB): `Name`, `Tagline`,
      `SystemPrompt`, `Guardrails`, `OutputFormat`, `Expertise` are **absent/encrypted** (in
      `EncryptedPayload`), while `Archetype`, `Emoji`, `AccentColor`, `ToolScope`, `PreferredProviderId`,
      `ReasoningEffort`, `SchemaVersion`, `CreatedAt`, `UpdatedAt` are **plaintext**.
- [ ] On **B**, the persona decrypts correctly (all textual fields restored).
- [ ] A device that **cannot** decrypt a record logs a warning and **skips** it without aborting the sync
      cycle (other entities still sync).

## L. First-sync migration 🔁

- [ ] On a device with local user personas but never-synced, **sign in** → the first-sync push includes
      personas (server receives them; the "First-sync push completed (… personas: N …)" log shows N>0).
- [ ] After first sync, the other device pulls those personas.

## M. Backward / forward compatibility

- [ ] An **older client** (no persona support) syncing against the updated server is unaffected (it
      ignores `Personas` in pull responses; doesn't send them) — no errors on either side.
- [ ] The updated client against an **older server** that doesn't echo `Personas` still works (personas
      simply don't sync; local built-ins + user personas remain usable).

## N. Privacy & logging 🔒 (re-check in a Release build)

- [ ] Trigger create/update/delete/import of personas, then open `%LOCALAPPDATA%\Pia\Logs\pia-*.log`.
- [ ] At **Information** level: only persona **Ids/counts** appear — **no** Name / Tagline / SystemPrompt /
      Guardrails text. Persona text appears only via `SensitiveDebug` (erased from Release).
- [ ] Any persona-related server URL is `SafeUrl`-formatted (no full endpoint at non-debug level).

## O. Server-side checks (with the server team)

- [ ] Admin UI lists synced (user) personas; built-in GUIDs never appear server-side.
- [ ] Per-user **quota**/validation enforced (max length on Name/Prompt/etc.); oversized payloads rejected
      gracefully (client surfaces the error, doesn't crash).
- [ ] Server treats persona fields as opaque (no server-side interpretation of textual content).
- [ ] `SchemaVersion` tolerated; unknown future fields ignored (additive evolution).

## P. Regression (must be unaffected)

- [ ] Optimize mode + templates: add/edit/delete/duplicate templates and run an optimization — unchanged.
- [ ] Provider per-mode defaults and `UseSameProviderForAllModes` behave as before.
- [ ] Settings navigation: all existing tabs still open; the new **Personas** tab sits after Plugins.

---

## Sign-off

| Area | Result | Notes |
|------|--------|-------|
| A Built-ins | | |
| B CRUD | | |
| C Edit + AI-assist | | |
| D First-run | | |
| E Picker | | |
| F Identity | | |
| G Provider/effort | | |
| H Tool gating | | |
| I Voice | | |
| J Sync (plaintext) | | |
| K Sync (E2EE) | | |
| L First-sync migration | | |
| M Back/fwd compat | | |
| N Privacy/logging | | |
| O Server | | |
| P Regression | | |
