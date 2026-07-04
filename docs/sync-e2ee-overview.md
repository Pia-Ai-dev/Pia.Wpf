# Pia.Wpf -> Pia.Server Sync — What's Synced & E2EE Capability

## How E2EE works here (3-5 sentences)
Each user has a random 32-byte User Master Key (UMK) that only their devices hold; every synced record is encrypted with a fresh per-record DEK (AES-256-GCM, AAD-bound to user/entity-type/entity-id), and the DEK is wrapped under the UMK. The server stores only two opaque base64 blobs per record — `EncryptedPayload` + `WrappedDek` — and can never decrypt them: it never sees the UMK, any DEK, or a device private key (device keys are non-exportable CNG; the UMK travels only wrapped via device-ECDH or an Argon2id-stretched recovery code). When E2EE is OFF, everything travels and is stored in plaintext on the server, protected only by TLS in transit (exception: provider API keys, which no longer sync at all without E2EE — they are device-local). Server-side verification: confidentiality-at-rest genuinely holds (opaque storage, plaintext columns nulled, mixed payloads rejected) — and since 2026-07-03 enforcement is **server-side**: once an account is E2EE-enabled (`PiaUser.IsE2EEEnabled`, one-way), the server rejects plaintext pushes and plaintext chat PUTs with `403 e2ee_required`. E2EE is permanent per account; the only way out is `/api/sync/reset`, which wipes all synced data *and* the account's E2EE state and key material.

## At a glance

| Item | What it carries | Direction | Status | E2EE verdict | Leaks to server even with E2EE on |
|---|---|---|---|---|---|
| Templates | Prompt-optimization templates (name, prompt, description, style) | both | active | Full | Id, created/modified timestamps, ciphertext size |
| Providers | AI provider configs **incl. API key**, endpoint, model | both | active | Full | Id, timestamps only — vendor/key/endpoint all encrypted |
| Sessions (optimization history) | User's original + optimized text, template/provider used | both | active | Full | Id, CreatedAt (usage cadence), ciphertext size |
| Memories | Memory Type/Label/Data (JSON body) | both | active | Full | Id, 3 timestamps (LastAccessedAt **day-truncated**), memory count, ciphertext size |
| Scheduled Jobs | Job name, query/prompt, granted write-tools, **full cadence** | both | active | Full | Id, OwnerDeviceId (deliberate), timestamps — not even the schedule leaks |
| Settings | Sync subset of AppSettings (theme, languages, mode/provider/persona defaults) | both | active | Full | ModifiedAt (LWW timestamp) only |
| Assistant Chats | Full conversations: title, provider, all messages incl. reasoning | out-of-band (`/api/v1/chats`) | active | Partial | Id, 3 timestamps (LastAccessedAt now **day-truncated**), WindowMode (constant) — ExtensionData now rides inside the ciphertext |
| Personas | Name, tagline, system prompt, guardrails + structural config | both | active | Partial | Archetype, ToolScope, PreferredProviderId, ReasoningEffort, Emoji, AccentColor |
| Todos | Title, notes, priority, status, due dates, kanban membership | both | active | Partial | Id, timestamps, SortOrder — ColumnId now encrypted-only (plaintext duplicate removed) |
| Kanban Columns | Column name + board structure flags | both | active | Partial | Column count, SortOrder, default/closed flags, timestamps (Name encrypted) |
| Plugin Preferences | PluginId + IsEnabled toggle | push only | active | None (necessity) | Which catalog plugins the user enables (behavioral signal; server must persist it) |
| Plugin Definitions | Server-authored plugin catalog (manifest, version, cab hash) | pull only | active | None (necessity) | Nothing user-authored — server is the author |
| Trusted Certificates | Public code-signing certs for plugin verification | pull only | active | None (necessity) | Public key material only, by design |
| Auth (login/refresh/logout) | Email + **plaintext password**, JWTs | push | active | None (necessity) | Server must verify the credential; password is NOT the E2EE root |
| E2EE key material | Device public keys, wrapped UMK, recovery blob | both | active | N/A (key material) | Device name (= machine name), OS/app version, public-key fingerprints |
| Vault files | Whole vault file (path + content) per memory row | both | scaffolded (cut-over deferred) | Full | Id + ciphertext size only — even the file path is encrypted |
| Research Sessions | Vestigial DTO slot; never populated | both (dead) | vestigial | N/A (vestigial) | Nothing — push is always empty, pull ignored |

## Notes that matter

- **Fully protected content** — Templates, Providers (including the API key), Optimization Sessions, Memories, Scheduled Jobs (including the entire fire schedule), Settings, and the deferred Vault-file mapper are Full E2EE: every content field rides inside `EncryptedPayload`; only ids/timestamps/ciphertext size remain visible.

- **Partial — metadata the server still sees**
  - **Personas**: Archetype (what kinds of AI roles the user builds), ToolScope (how much write authority each persona gets), and PreferredProviderId (persona→provider relationship graph) are plaintext by design; the actual system prompt/guardrails are encrypted.
  - **Todos**: ~~plaintext ColumnId~~ **fixed 2026-07-03** — ColumnId now rides only inside the encrypted payload (pull already preferred the decrypted copy; plaintext stays as legacy fallback for old payloads). SortOrder remains plaintext (not duplicated in the payload yet — "Phase 2" would move todo SortOrder + kanban structural flags into the payloads, deferred). Old rows keep stale plaintext ColumnId until re-pushed; a one-time full push (e.g. re-running the E2EE migration) scrubs them.
  - **Kanban Columns**: column names are encrypted, but column count, ordering, and default/closed flags reveal board layout.
  - **Assistant Chats**: all message content (incl. chain-of-thought) is encrypted. **Fixed 2026-07-03**: `LastAccessedAt` is now day-truncated on the wire (retention still works; pull keeps the newer of local/remote), and `ExtensionData` now serializes inside the encrypted payload — plaintext extension keys are stripped by the server and dropped by the client, which also fixes the inverted forward-compat bug (ciphertext-borne unknown fields used to be discarded). Residual: day-level read signal, admin UI "Last Accessed" column, and the not-yet-synced `WorkingDirectory` field would need the same treatment if it ever syncs.

- **Not end-to-end (by necessity)**
  - **Auth**: the server must see email + password to authenticate; critically, the UMK is random and never derived from the password, so a server that knows the password still cannot decrypt anything.
  - **Plugin catalog**: server-authored, admin-managed, pull-only — the server already holds every field.
  - **Trusted certificates**: distributed public key material; nothing to hide.
  - **Plugin Preferences**: the server must persist PluginId+IsEnabled to serve the toggle, and it authors the catalog behind each GUID — nothing encryptable, but it is a per-user feature-usage profile.

- **Risks / gaps to flag**
  - ~~**E2EE OFF = provider API keys in CLEARTEXT on the wire**~~ **fixed 2026-07-03**: API keys are now device-local when E2EE is off — the client never puts them on the wire and the server pull never emits them (each new device asks for one-time key re-entry). Correction to the original finding: the server never stored them in *cleartext* — it held them AES-GCM-encrypted under a server-side master key (operator-decryptable, which is why removing them from the sync path still mattered). Stored `EncryptedApiKey` values from before the fix remain server-side as dead data until purged.
  - **E2EE OFF exposes all content at rest**: full conversations (incl. reasoning), memories, todos, prompts, persona instructions — TLS-only, readable by an honest-but-curious or compromised server. (API keys are the exception, see above.)
  - ~~**Encryption is client-enforced**~~ **fixed 2026-07-03**: the server now rejects plaintext sync pushes and plaintext chat PUTs from E2EE-enabled accounts with `403 e2ee_required` (previously a plaintext push not only stored plaintext, it **nulled the existing ciphertext** — a silent downgrade-and-destroy hole). E2EE is now permanent per account: the client's "Disable E2EE" feature (which only worked by exploiting the gap) was removed; `/api/sync/reset` is the sanctioned escape hatch and now also clears the account's E2EE flag, devices, wrapped UMKs, recovery blob, and assistant chats. Clients recognize `e2ee_required` and route into the existing E2EE-onboarding flow instead of silently stalling.
  - **Verification corrections applied**: Plugin Preferences reclassified from "Partial" to **None (necessity)** (it has no encrypt path at all); Research Sessions' tag corrected from "N/A (key material)" to **N/A (vestigial)**; Persona `OutputFormat` does **not** leak under E2EE-off — the server silently dropped it (a data-loss bug, **fixed 2026-07-03**: server DTO/entity/mappers + `AddPersonaOutputFormat` migration; the drop was worse than first assessed — the originating device wiped its own local value after one push→pull cycle). Also fixed in the same pass: an E2EE-ON bug where a rotated provider API key never propagated to other devices (pull-apply clobbered the freshly-decrypted key with the stale local one).
  - **Vault-file sync is scaffolded, not live** (cut-over deferred, Task 4.3); live memory sync is still row-based via `SyncMemory`.
  - **AiProxy (live inference) necessarily sees plaintext prompts** to forward them upstream — orthogonal to at-rest E2EE; it persists only token counts and SHA-256 content hashes by default (verbatim only via opt-in `logPreview`, 200 chars).
  - **Device roster leaks host identity**: DeviceName = `Environment.MachineName` (e.g. "MARCO-LAPTOP") plus OS/app version are always server-visible. **Decision 2026-07-03: accepted as-is** — the name's UX value for device approval outweighs the leak in this deployment.

- **Key material (not a candidate for E2EE)** — wrapped-UMK, recovery, and device-key blobs are stored and relayed opaquely by the server, which holds no device private key and no recovery code and therefore can never unwrap any of them.

## Changelog

- **2026-07-03** — Hardening pass (both repos, deployed together):
  1. Server-side E2EE enforcement: `403 e2ee_required` on plaintext `/api/sync/push` and plaintext `PUT /api/v1/chats` for E2EE-enabled accounts; E2EE now permanent (client Disable feature removed); `/api/sync/reset` clears E2EE state + key material + chats and is the sanctioned escape hatch; clients route `e2ee_required` into E2EE onboarding.
  2. Provider API keys device-local when E2EE off (never on the wire, never emitted on pull); E2EE-ON rotated-key propagation fixed.
  3. Chat `ExtensionData` moved inside the encrypted payload (server strips plaintext extension keys on encrypted PUTs; forward-compat now flows through the ciphertext).
  4. Todo `ColumnId` plaintext duplicate removed under E2EE.
  5. `LastAccessedAt` day-truncated on the wire (chats + memories); chat pull keeps the newer of local/remote.
  6. Persona `OutputFormat` added to server DTO/entity/mappers + migration (data-loss fix).
  - Deferred: "Phase 2" hiding of todo/kanban SortOrder + structural flags; purge of pre-fix stored `EncryptedApiKey` rows; device-name change (accepted as-is).
