# Plan — Assistant chat history

**Goal:** persist every assistant conversation to a local SQLite store, let the
user browse / resume / search past chats, optionally sync them to the Pia
cloud, and auto-expire stale ones.

**Why:** today `AssistantViewModel.Messages` is in-memory only and lost on
`ClearConversation` or app close. Users have asked to come back to a previous
thread. Reusing the existing `HistoryView` pattern (already proven for
Optimize and Research) keeps the lift small and the UX consistent.

**Server contract:** see [`docs/server/assistant-chat-history.md`](../server/assistant-chat-history.md).
This plan is the client side only.

---

## Deliverables

1. New SQLite tables + service for persisting chats locally.
2. New shared DTOs in `Pia.Shared` (also used as the cloud wire format).
3. Header chip + Wpf-UI flyout on `AssistantView` to switch between recent
   chats.
4. `Ctrl+H` quick switcher overlay across the assistant window.
5. New `AssistantHistoryView` (full list + inspector), mirroring
   `HistoryView`.
6. Nav entry "Chat history" visible **only when window mode = assistant**.
7. Retention policy in settings (default 30 days, max 365) with cleanup job.
8. Auto-title (opt-in, default off) and dumb-title fallback.
9. Cloud sync via the new `/api/v1/chats` endpoints, gated on the capability
   probe.

---

## Data model

### Shared DTO — `Pia.Shared`

New file `src/Pia.Shared/AssistantChatDto.cs`:

```csharp
public class SyncAssistantChat
{
    public Guid Id { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public string? Title { get; set; }
    public DateTime CreatedAt { get; set; }    // UTC
    public DateTime UpdatedAt { get; set; }    // UTC
    public DateTime LastAccessedAt { get; set; } // UTC
    public string WindowMode { get; set; } = "Assistant";
    public Guid? ProviderId { get; set; }
    public List<SyncAssistantChatMessage> Messages { get; set; } = [];
    // E2EE fields (EncryptedPayload, WrappedDek) for parity with other Sync* DTOs.
}

public class SyncAssistantChatMessage
{
    public Guid Id { get; set; }
    public string Role { get; set; } = "user";   // "user" | "assistant"
    public string Content { get; set; } = string.Empty;
    public string? ThinkingContent { get; set; }
    public DateTime Timestamp { get; set; }
    public int? Tokens { get; set; }
    public string? ModelName { get; set; }
}
```

(Matches the existing `Sync*` convention in `Pia.Shared/Models/`.)

This is the wire format **and** the persistence-mapping shape. The existing
`Pia.Models.AssistantMessage` stays unchanged; we map between it and
`AssistantChatMessageDto` in the service.

JSON serialization uses the default `System.Text.Json` options the cloud
client already uses (camelCase, ISO 8601 UTC). Round-trip unknown fields by
using `JsonExtensionData` on the DTOs — required by the server contract.

---

## Storage

Extend `SqliteContext.EnsureSchema()` with two tables + an FTS5 index:

```sql
CREATE TABLE IF NOT EXISTS AssistantChats (
    Id              TEXT PRIMARY KEY,
    SchemaVersion   INTEGER NOT NULL,
    Title           TEXT,
    CreatedAt       TEXT NOT NULL,
    UpdatedAt       TEXT NOT NULL,
    LastAccessedAt  TEXT NOT NULL,
    WindowMode      TEXT NOT NULL,
    ProviderId      TEXT,
    ExtraJson       TEXT             -- unknown round-tripped fields
);

CREATE INDEX IF NOT EXISTS IX_AssistantChats_LastAccessedAt
    ON AssistantChats (LastAccessedAt);
CREATE INDEX IF NOT EXISTS IX_AssistantChats_UpdatedAt
    ON AssistantChats (UpdatedAt);

CREATE TABLE IF NOT EXISTS AssistantChatMessages (
    Id              TEXT PRIMARY KEY,
    ChatId          TEXT NOT NULL,
    Ordinal         INTEGER NOT NULL,
    Role            TEXT NOT NULL,
    Content         TEXT NOT NULL,
    ThinkingContent TEXT,
    Timestamp       TEXT NOT NULL,
    PromptTokens    INTEGER,
    CompletionTokens INTEGER,
    ModelName       TEXT,
    FOREIGN KEY (ChatId) REFERENCES AssistantChats(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_AssistantChatMessages_ChatId_Ordinal
    ON AssistantChatMessages (ChatId, Ordinal);

CREATE VIRTUAL TABLE IF NOT EXISTS AssistantChatsFts USING fts5(
    title, content, content=''
);
-- triggers to keep AssistantChatsFts in sync with both tables.
```

`ExtraJson` is the unknown-fields catch-all required by the server contract
(forward-compat).

---

## Service layer

New file `src/Pia.Wpf/Services/AssistantChatService.cs` implementing
`IAssistantChatService` (interface in
`src/Pia.Wpf/Services/Interfaces/`). Pattern follows `HistoryService` (raw
`SqliteConnection`, no ORM).

```csharp
public interface IAssistantChatService
{
    event EventHandler? ChatsChanged;

    Task<AssistantChatDto> SaveAsync(AssistantChatDto chat, CancellationToken ct);
    Task<AssistantChatDto?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<AssistantChatDto>> SearchAsync(
        string? text, DateTime? from, DateTime? to,
        string? providerId, int offset, int limit,
        CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task TouchLastAccessedAsync(Guid id, CancellationToken ct);
    Task<int> EvictOlderThanAsync(DateTime cutoffUtc, CancellationToken ct);
}
```

`SaveAsync` is upsert by `Id`. It also calls into the cloud sync path
(fire-and-forget) — see §sync.

Register as scoped in `Bootstrapper.cs` next to `HistoryService`.

---

## Sync

### Capability probe

Add `ICloudCapabilityService` with one method `Task<bool> ChatsSupportedAsync(CancellationToken)`.
Implementation: `GET /api/capabilities` once per app session, cache the
result. On `404` or network error → `false`. Treat any non-`true` value as
local-only.

### Sync worker

New `AssistantChatSyncService` (hosted service registered in
`Bootstrapper`) with a single in-process queue:

- On `IAssistantChatService.ChatsChanged` (for an insert/update), enqueue a
  `PUT /api/v1/chats/{id}` with the DTO body.
- On delete, enqueue `DELETE /api/v1/chats/{id}`.
- On startup, if cloud chats are supported, call
  `GET /api/v1/chats?since={maxLocalUpdatedAt}` and merge incoming chats
  into the local store (last-writer-wins per the server contract).
- 409 Conflict → re-fetch the server copy, merge any local-only trailing
  messages, retry once.
- All HTTP calls go through the existing auth + retry helpers used by
  `PiaCloudChatClient`.

**Failure mode:** any exception in the sync worker is logged and swallowed.
Local store is always authoritative for the client.

Privacy logging: chat titles and message content are user-named/payload
content. Use `SensitiveDebug` for them; log only IDs at `Information` level.
URLs go through `SafeUrl.Format`.

---

## UX

### Title chip + flyout (on `AssistantView`)

Add a new top row (above the messages `ScrollViewer`) with a Wpf-UI
`Button` styled as a chip showing the current chat title (or "New chat"
when empty). Clicking opens a `ContextFlyout` containing:

- Search box at the top (debounced 300 ms).
- "+ New chat" item.
- Up to 10 recent chats grouped by date (Today / Yesterday / Earlier),
  using the same date-bucket logic as `HistoryViewModel`.
- "Show all chats…" → navigates to `AssistantHistoryView`.

The chip and flyout live in `Controls/Assistant/PiaChatTitleChip.xaml`.
DataContext is a small `ChatTitleChipViewModel` injected via
`AssistantViewModel` (single-source list).

### Ctrl+H quick switcher

`AssistantView.InputBindings` gets `<KeyBinding Modifiers="Control" Key="H"
Command="{Binding OpenQuickSwitcherCommand}" />`. The command opens a
modal overlay (`Controls/Assistant/PiaChatQuickSwitcher.xaml`) bound to the
same `ChatTitleChipViewModel` but rendered as a centered command-palette:
text input with focus, then a vertical list of fuzzy-matched chat titles +
preview snippet. Enter resumes the selected chat. `Esc` dismisses.

(Confirm `Ctrl+H` is unbound elsewhere in `AssistantView`; if anything in
the app intercepts it globally, switch to `Ctrl+Shift+H`.)

### Full `AssistantHistoryView`

New files:

- `src/Pia.Wpf/Views/AssistantHistoryView.xaml` + `.xaml.cs`
- `src/Pia.Wpf/ViewModels/AssistantHistoryViewModel.cs`
- `src/Pia.Wpf/Controls/AssistantHistory/PiaAssistantChatGroupCard.xaml`
- `src/Pia.Wpf/Controls/AssistantHistory/PiaAssistantChatRow.xaml`
- `src/Pia.Wpf/Controls/AssistantHistory/PiaAssistantChatInspector.xaml`

XAML mirrors `HistoryView.xaml` 1:1: header, search bar (reuse
`PiaHistorySearchBar` — it's already generic over query + date range),
two-column grid (groups list ↑ inspector ↓), status bar. The inspector
right pane renders the conversation read-only (reuse existing
`PiaAssistantMessage` control) plus a "Resume this chat" button at the
top of the pane.

`AssistantHistoryViewModel` mirrors `HistoryViewModel` (debounced search,
expand/collapse persistence, `Delete` key binding). Filters: search text,
date range, provider.

### Nav entry — assistant mode only

`MainWindowViewModel` switches nav items by window mode. Add a case
`"AssistantHistory"` next to `"ResearchHistory"` (~line 311), and gate the
**menu item itself** so it only renders when the current window mode is
assistant. (Same gating that hides `ResearchHistory` outside Research mode
today — mirror that.)

Label the menu item "Chat history" so it does not collide with the
existing "History" (Optimize).

---

## Settings

In `AppSettings.cs`:

```csharp
public bool ChatHistoryEnabled { get; set; } = true;
public int  ChatHistoryRetentionDays { get; set; } = 30;   // clamp [1, 365]
public bool ChatAutoTitleEnabled { get; set; } = false;    // opt-in
```

Surface in `AssistantSettingsViewModel`:

- Toggle: "Save chat history."
- Slider/numeric: "Delete chats not opened for N days." (1–365, default 30,
  disabled when above toggle is off).
- Toggle: "Auto-generate chat titles (uses tokens)."
- Button: "Delete all chat history now." → confirm dialog reusing the
  `History_DeleteAllConfirm*` resx patterns; new keys
  `AssistantHistory_DeleteAllConfirmTitle` / `…Body`.

When `ChatHistoryEnabled` flips off, the service should also delete any
existing local chats (with a second confirm). Pending sync deletes are
flushed.

---

## Retention / cleanup

New `AssistantChatRetentionService` (hosted):

- On app startup, run cleanup once.
- Then a `System.Threading.PeriodicTimer` every 24 h.
- Cleanup: `DateTime.UtcNow - settings.ChatHistoryRetentionDays` →
  `EvictOlderThanAsync(cutoff)`. The service emits a `DELETE` to the cloud
  per evicted chat (best-effort, queue into the sync worker).

`LastAccessedAt` is bumped:

- When the user opens a chat (resumes it from the flyout, quick switcher,
  or inspector).
- When a new message is sent in that chat.

Not bumped by background sync pulls.

---

## Localization

New keys in `MessageStrings.resx` (and `.de.resx`, `.fr.resx` siblings):

- `AssistantHistory_Title`
- `AssistantHistory_EmptyState`
- `AssistantHistory_EmptyStateHint`
- `AssistantHistory_Resume`
- `AssistantHistory_DeleteAllConfirmTitle`
- `AssistantHistory_DeleteAllConfirmBody`
- `AssistantChat_TitlePlaceholder_NewChat`
- `AssistantChat_QuickSwitcher_Placeholder`
- `Settings_Chat_HistoryEnabled`
- `Settings_Chat_RetentionDays`
- `Settings_Chat_AutoTitle`
- `Settings_Chat_DeleteAllNow`

German + French translations land in the same PR (Pia's existing
convention — do not ship English-only keys).

---

## Privacy logging (per `CLAUDE.md`)

- Chat titles → user-named. `SensitiveDebug` only.
- Message content / thinking content → payload. `SensitiveDebug` only.
- Search query text → user input. `SensitiveDebug` only.
- Chat `Id`, `CreatedAt`, `UpdatedAt`, message counts → safe at
  `Information`.
- All cloud URLs through `SafeUrl.Format`.
- Capability probe response: safe (no user data).

---

## Sequencing (PR-sized chunks)

Each step is meant to ship green / shippable on its own.

1. **DTOs + storage** — `AssistantChatDto`(s), schema migration, service +
   tests. No UI yet. Wire up so `AssistantViewModel` persists messages
   on send (but no resume flow yet).
2. **Resume + flyout** — title chip on `AssistantView`, recent-chats
   flyout, "+ New chat", "Resume" loads chat back into the VM.
3. **Quick switcher** — `Ctrl+H` overlay with fuzzy search.
4. **Full history view** — `AssistantHistoryView` + nav entry gated to
   assistant mode.
5. **Settings + retention** — settings UI, retention service, "Delete all"
   button.
6. **Cloud sync** — capability probe, sync worker, conflict handling.
   Local-only mode stays the default UX path; sync layers on top.
7. **Auto-title (opt-in)** — small completion call after first user
   turn when setting is on.

Steps 1–3 give a usable feature; 4–7 round it out.

### Post-step cleanup pass

**After every step above, before opening the PR**, run the `simplify` skill
in a fresh agent session scoped to the files touched by that step. The
intent is to catch reuse opportunities (e.g. should this duplicate the
history-grouping logic or extract a shared helper?), dead/unreachable
branches, and over-eager abstraction before the diff is reviewed by a
human.

Concrete recipe per step:

1. Collect the file list: `git diff --name-only main...HEAD` on the
   step's branch.
2. Spawn a new agent session (general-purpose subagent is fine) and
   invoke the `simplify` skill with the file list as the explicit scope.
   Tell it the step number and a one-line summary of what shipped so it
   has context for "what's necessary vs. what's accidental."
3. Apply the suggestions you agree with, push, then open the PR.

Do **not** skip this on small steps — step 1 (DTOs + storage) is exactly
where extracting a helper or trimming an interface method is cheap, and
the cost compounds in steps 2+.

---

## Acceptance

- Send a message in Assistant mode → close + reopen app → chat is visible
  in the title-chip flyout's "Today" group; clicking it restores the
  conversation in `AssistantView`.
- `Ctrl+H` opens the quick switcher; typing filters; Enter resumes;
  `Esc` dismisses with no side effects.
- Open `AssistantHistoryView` from "Show all chats…" → date-grouped list,
  search by content, filter by date range, click a row to see messages in
  the inspector, "Resume" loads it back.
- Nav entry "Chat history" is **only** visible when the current window
  mode is assistant.
- Set retention to 7 days, manually backdate a chat's `LastAccessedAt` to
  10 days ago, restart app → chat is gone locally and a `DELETE` was sent
  to the cloud (verify via test stub).
- With auto-title off (default) the first user message becomes the chat
  title (first 40 chars). Toggle it on, start a new chat → after the first
  assistant reply, the title is replaced by the model-generated summary.
- Point the client at a stubbed old server (`/api/capabilities` → 404):
  feature still works, no errors surfaced beyond a single startup log
  line.
- `pia-*.log` from a release build contains **no** chat titles, message
  content, or full URLs. Grep the log to verify.

---

## Risks / notes

- **FTS5 weight** — message content can be long. Cap stored message content
  at the 256 KiB-per-message limit from the server contract; truncate on
  the way in with a visible "(truncated)" marker.
- **`Ctrl+H` collision** — verify against existing `InputBindings` across
  `AssistantView` and `MainWindow`. Fallback: `Ctrl+Shift+H`.
- **Schema migration** — `EnsureSchema` is idempotent; the new tables can
  ship without a version bump. If a future field needs a non-additive
  change to the *local* schema, introduce a versioning row at that point.
- **Sync worker ordering** — within a single chat, deletes must follow
  puts. Use a per-chat-ID queue, not a global FIFO. Simpler: short-circuit
  by checking the latest desired state before each request.
- **Resume-then-edit divergence** — if a user resumes an old chat and adds
  a turn, the server PUT must include the full chat (server contract is
  document-level replacement). The DTO already encodes that.
- **AnswerStats persistence** — `AnswerStats` exists today but isn't always
  populated. Persist it when present, leave null otherwise. Don't fail a
  save because stats are missing.
- **Existing `HistoryView` naming** — current "History" nav entry is
  Optimize history. Consider renaming its label to "Optimize history" in
  the same PR as adding "Chat history" to avoid user confusion, but only
  if a translator is available for the corresponding resx changes.
