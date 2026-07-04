# Assistant Chat History — Server Contract

Spec for the server endpoints, schemas, and behavior required to support the
**Assistant Chat History** feature in `Pia.Wpf` (`feature/assistant_history`).

The client persists every assistant conversation to a local SQLite store and
opportunistically syncs it with the Pia cloud. The cloud copy is the
cross-device backup; the client is authoritative for retention policy.

This document is the contract the server must implement. Anything not listed
here is a client-internal concern.

---

## 1. Compatibility goals

These are hard requirements — implementations that break either of them will
be rejected by the client.

1. **Old server, new client** — a server that does not implement these
   endpoints must not cause the client to fail. The client detects support via
   the capability probe (§3) and silently falls back to local-only mode.
2. **New server, old client** — adding chat-history endpoints must not change
   the behavior of any existing endpoint (notably `/api/ai/chat`). Old clients
   never call the new endpoints and must continue to work unchanged.

### Forward-compatibility rules for the schema

- All chat objects carry an integer `schemaVersion` (currently `1`).
- Schema changes are **additive only**. New optional fields may be added.
  Existing fields must never be removed, renamed, or have their semantics
  changed.
- Unknown fields received from a client **must be stored and returned
  verbatim** on subsequent reads. The server is a transport for the chat
  document, not its interpreter.
- **E2EE exception**: for encrypted chats (`encryptedPayload` set), unknown
  fields round-trip **inside the ciphertext**, not as plaintext top-level
  keys — plaintext extension keys would bypass E2EE. The server strips
  top-level unknown keys from encrypted documents, and the client drops any
  plaintext extension keys it receives on an encrypted chat.
- The server may set its own server-owned fields (e.g. `serverReceivedAt`).
  These must be prefixed `server_` to avoid collisions with future client
  fields.

---

## 2. Authentication

Same scheme as the existing `/api/ai/chat` endpoint:

- Bearer token in the `Authorization` header.
- `401 Unauthorized` → client surfaces "Authentication required" and stops
  syncing for the session.

No additional scopes or claims are required for v1.

---

## 3. Capability probe

```
GET /api/capabilities
```

**Response (200, JSON):**

```json
{
  "chats": true,
  "chatsSchemaVersion": 1
}
```

- If the endpoint returns `404`, the client assumes an old server and runs in
  local-only mode for chats.
- If `chats` is missing or `false`, same fallback.
- If `chatsSchemaVersion` is **greater** than the client's known version, the
  client still syncs but warns the user that some fields may be hidden until
  the client is updated.
- The probe is called once per app session and cached.

Adding new capability flags to this document over time is expected; clients
ignore unknown ones.

---

## 4. Endpoints

All endpoints below live under `/api/v1/chats`. Versioning the path lets us
break the schema later by minting `/api/v2/chats` without disturbing v1
clients.

### 4.1 List / pull

```
GET /api/v1/chats?since={ISO8601}&limit={N}&cursor={opaque}
```

Returns chats updated at or after `since` (inclusive), ordered by `updatedAt`
ascending. `limit` defaults to `100`, capped at `500`. Pagination uses an
opaque cursor returned by the server.

**Query parameters:**

| Name     | Required | Notes                                                       |
|----------|----------|-------------------------------------------------------------|
| `since`  | no       | If omitted, returns the most recent `limit` chats.          |
| `limit`  | no       | `1..500`, default `100`.                                    |
| `cursor` | no       | Opaque string returned in a previous response.              |

**Response (200):**

```json
{
  "chats": [ /* AssistantChat objects, see §5 */ ],
  "nextCursor": "eyJ1cGRhdGVkQXQiOi4uLn0=",
  "hasMore": true
}
```

`nextCursor` is `null` when `hasMore` is `false`.

The client uses this on startup and after reconnect to refresh its local
store. It is allowed but not required for clients to omit `since` on a clean
install to bulk-pull all chats.

### 4.2 Get a single chat

```
GET /api/v1/chats/{id}
```

Returns the full `AssistantChat` document including messages. `404` if the
chat does not exist (or is not owned by the authenticated user — the server
must not leak existence across users).

### 4.3 Upsert

```
PUT /api/v1/chats/{id}
Content-Type: application/json

{ /* AssistantChat object */ }
```

Idempotent. Conflict rules in §6.

**Responses:**

- `200 OK` — existing chat replaced. Body: stored `AssistantChat`.
- `201 Created` — new chat written. Body: stored `AssistantChat`.
- `409 Conflict` — server's `updatedAt` is newer than the body's. Body:
  current server-side `AssistantChat`. Client reconciles per §6.

### 4.4 Delete

```
DELETE /api/v1/chats/{id}
```

- `204 No Content` — deleted, or already absent (idempotent).
- The server should soft-delete with a tombstone retained for at least 30
  days so that out-of-order syncs across clients converge. Tombstones do not
  need to be exposed in the list endpoint for v1; clients drive deletes from
  their own retention policy.

### 4.5 (Optional) Bulk delete

Not required for v1. A future addition could be `POST /api/v1/chats:batchDelete`
accepting a list of IDs. Listed here only so it does not collide with future
endpoints.

---

## 5. AssistantChat schema

```json
{
  "id": "8f1c0a3e-d3f1-4b6e-9a40-9a8b1a2b3c4d",
  "schemaVersion": 1,
  "title": "How do I unit test ICommand bindings?",
  "createdAt": "2026-05-19T08:12:34Z",
  "updatedAt": "2026-05-19T08:18:02Z",
  "lastAccessedAt": "2026-05-19T08:18:02Z",
  "windowMode": "Assistant",
  "providerId": "1c2d3e4f-5678-4abc-9def-0123456789ab",
  "messages": [
    {
      "id": "0e8a...",
      "role": "user",
      "content": "How do I unit test ICommand bindings?",
      "timestamp": "2026-05-19T08:12:34Z"
    },
    {
      "id": "1f9b...",
      "role": "assistant",
      "content": "...",
      "thinkingContent": "...",
      "timestamp": "2026-05-19T08:12:39Z",
      "tokens": 942,
      "modelName": "gpt-5"
    }
  ]
}
```

### Field reference

| Field            | Type            | Required | Notes |
|------------------|-----------------|----------|-------|
| `id`             | UUID (string)   | yes      | Client-generated. Path `{id}` MUST equal body `id`. |
| `schemaVersion`  | int             | yes      | Currently `1`. |
| `title`          | string \| null  | no       | Up to 200 chars. May be null until the client auto-titles. |
| `createdAt`      | ISO8601 UTC     | yes      | Immutable after first write. |
| `updatedAt`      | ISO8601 UTC     | yes      | Client sets on every change; used for conflict resolution. |
| `lastAccessedAt` | ISO8601 UTC     | yes      | Client-owned. Server stores and returns. Used by client for retention. Clients send it **day-truncated** (read-activity privacy); on pull the client keeps the newer of local/remote. |
| `windowMode`     | string          | yes      | Matches the existing `X-Pia-Mode` values — PascalCase enum names (`"Assistant"`, `"Optimize"`, `"Research"`). |
| `providerId`     | UUID (string) \| null | no | Provider used. UUID matching a client-side provider configuration. |
| `messages`       | array<Message\> | yes      | Ordered oldest → newest. May be empty for a freshly-titled chat. |
| `encryptedPayload` | string \| null | no      | Base64 AES-GCM ciphertext (nonce‖ciphertext‖tag) of the chat body. Present only when E2EE is active; when present, plaintext content fields will be null. Server stores and returns verbatim. |
| `wrappedDek`     | string \| null  | no       | Base64 AES-GCM wrapping of the per-chat DEK with the user's UMK (nonce‖wrapped-DEK‖tag). Present only when E2EE is active. Server stores and returns verbatim. |

### Message reference

| Field             | Type           | Required | Notes |
|-------------------|----------------|----------|-------|
| `id`              | UUID (string)  | yes      | Stable per message. |
| `role`            | string         | yes      | `"user"` or `"assistant"`. Future: `"system"`, `"tool"`. |
| `content`         | string         | yes      | May be empty. UTF-8. |
| `thinkingContent` | string \| null | no       | Extended-thinking output. |
| `timestamp`       | ISO8601 UTC    | yes      |       |
| `tokens`          | int \| null    | no       | Total tokens consumed by this message (combined prompt+completion). |
| `modelName`       | string \| null | no       | Model that produced this message. |

All timestamps are UTC ISO 8601 with a trailing `Z`. The server must not
rewrite client-supplied timestamps.

### Text-only payloads

Message `content` and `thinkingContent` are UTF-8 text only. Binary
attachments (images, PDFs, audio, arbitrary files) MUST NOT be inlined as
base64 or appended in additional fields. If the client gains the ability
to attach files to LLM requests in a future version, those payloads stay
in a separate transport and are never mirrored into the chat-history sync.
The server may reject any message that violates this with `400`.

---

## 6. Conflict resolution

Per-chat last-writer-wins on `updatedAt`. Concretely:

1. Client `PUT`s a chat with `updatedAt = T_client`.
2. Server compares `T_client` to `T_server` (the `updatedAt` currently stored).
3. If `T_client >= T_server` (or no record exists), the write succeeds. Reply
   `200` or `201` with the stored chat.
4. If `T_client < T_server`, reply `409 Conflict` with the current server
   chat as the body. The client merges (by replaying any local-only messages
   onto the server's version) and retries the `PUT` with a fresh
   `updatedAt`.

`createdAt` is immutable after the first write — if a client tries to change
it, the server keeps the original silently.

There is no per-message merge. Messages are append-only in normal operation;
the chat document as a whole is the unit of conflict.

---

## 7. Retention (server side)

The **client owns retention policy** (user-configurable, default 30 days
without access, max 365). The server's role:

- Store chats indefinitely until the client explicitly `DELETE`s them.
- Tombstones (§4.4) should live at least 30 days so multi-device deletes
  converge.
- Server-side cleanup of orphaned tombstones is implementation-defined.

The server **must not** delete chats based on its own retention policy
without an explicit `DELETE` from a client. Doing so would re-create the
chats on the next client sync.

---

## 8. Error contract

| Status | When                                           | Client action |
|--------|------------------------------------------------|---------------|
| 400    | Malformed body, schema validation failure      | Log + drop. Do not retry. |
| 401    | Missing / invalid bearer                       | Surface "Authentication required". Stop syncing. |
| 404    | Unknown chat ID on GET / DELETE                | Treat DELETE as success; GET as "no longer exists". |
| 409    | Conflict on PUT                                | Merge per §6, retry once. |
| 429    | Rate limit                                     | Back off honoring `Retry-After`. |
| 5xx    | Transient                                      | Exponential backoff, retry up to 3 times per chat per session. |

Error bodies should be JSON: `{ "error": "...", "message": "..." }`. The
client logs the `error` code but only shows generic UI for non-fatal codes.

---

## 9. Out of scope for v1

The following are intentionally **not** in the spec. Adding them later must
follow the additive rule in §1.

- Real-time push / WebSocket notifications.
- Server-side full-text search.
- Sharing chats between users.
- Per-message tool-call / source / suggestion arrays — these are reconstructed
  or discarded on the client and not persisted.
- Pinning (planned for a later client version; the field will be added
  additively).

---

## 10. Implementation checklist

- [ ] `GET /api/capabilities` returns `{ "chats": true, "chatsSchemaVersion": 1 }`.
- [ ] `GET /api/v1/chats` with `since` / `limit` / `cursor` pagination.
- [ ] `GET /api/v1/chats/{id}`.
- [ ] `PUT /api/v1/chats/{id}` with conflict handling.
- [ ] `DELETE /api/v1/chats/{id}` with tombstones.
- [ ] Unknown JSON fields round-trip untouched.
- [ ] `createdAt` is immutable.
- [ ] Auth shared with existing `/api/ai/chat`.
- [ ] No change in behavior of `/api/ai/chat` or any other existing endpoint.
