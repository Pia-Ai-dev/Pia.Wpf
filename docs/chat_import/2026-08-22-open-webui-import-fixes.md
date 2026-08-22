# Open WebUI import: the freeze and the one-message chats

**Status:** Implemented (both reported defects fixed and verified end-to-end)
**Owner:** Marco Altmann
**Written:** 2026-08-22
**Origin:** Two defects reported against the chat-import feature while importing
`chat-export-1787290830479` (37 MB, 573 chats) from Open WebUI.

## 1. Every imported chat showed only the first prompt

### What was wrong

`OpenWebUiChatConverter` read the conversation from `chat.messages`. That array is a *cache* of the
active path through Open WebUI's message tree, and newer builds no longer keep it in sync. The tree
itself lives in `chat.history.messages`, indexed by id, with `history.currentId` naming the leaf of
the path the app last rendered.

Measured against the reported export:

| | `chat.messages` (old) | `history` walk (new) |
|---|---|---|
| Messages imported | 1708 | 1801 |
| Chats collapsed to one message | 34 | 0 |
| Assistant turns with an empty body | 7 | 1 |
| Chats where the two disagree on the shared prefix | — | 13 (tree wins) |

The 34 collapsed chats were the **34 most recent** in the file. History sorts newest first, so they
sat at the top of the list after import — which is why the defect read as "every chat".

Three distinct shapes of loss, all from the same cause:

- `chat.messages` reduced to a single `{role, content}` stub (34 chats, ranks 1–34 by recency).
- An assistant turn present but with `content: ""` while the tree held the whole answer — one was
  58 KB of text.
- A stale regenerated branch: the cache kept the discarded answer, `currentId` pointed at the kept one.

### The fix

`ReadActivePath` walks `history.currentId` back up `parentId` (cycle-guarded) and reverses. The flat
array stays as the fallback for older exports that carry no tree — its shape is still covered by the
existing fixtures. `OpenWebUiConversion.RecoveredFromTree` counts the messages the tree held that the
cache had lost, and the import log line reports it, so a future regression is visible in a support log
rather than only in a user's screenshot.

## 2. The app froze for the duration of the import

### What was wrong

Nothing in `ChatArchiveService` detached from the caller's `SynchronizationContext`, and the ViewModel
awaited it from the UI thread. So the UI thread ran:

- the 37 MB `JsonDocument` parse continuation and all of `OpenWebUiChatConverter.Convert` — pure CPU
  with no await to yield on;
- 573 × (`GetAsync` + `SaveAsync`), each awaited without `ConfigureAwait(false)`, so every one of the
  1146 completions marshalled back to the dispatcher;
- 573 `ChatsChanged` events, each posting a debounced reload whose FTS search contends for the same
  write gate the next save needs.

The MVVM defect is the second bullet: a service is not a UI component and must not capture the UI
context. The ViewModel then had no way to report progress even if it had wanted to — `ImportAsync`
returned only a final result.

### The fix

- `ImportAsync(string, IProgress<ChatImportProgress>?, CancellationToken)`. Parse and convert run
  inside `Task.Run`; every await in the service uses `ConfigureAwait(false)`.
- `ChatImportProgress(Phase, Processed, Total)` with `Reading` → `Converting` → `Storing`. Only
  `Storing` knows the chat count, so the bar is indeterminate until then. Storing reports at most once
  per percent — one report per chat would just move the flood onto the progress callback.
- The ViewModel builds the `Progress<T>` on the UI thread (so reports come back marshalled) and binds
  `IsImporting` / `ImportStatus` / `ImportProgress` / `ImportProgressIsIndeterminate` to a row in the
  history status bar.
- `OnChatsChanged` ignores events while `IsImporting`: the import reveals its whole result itself when
  it finishes, so the hundreds of reloads in between bought nothing and fought the write gate.

`AssistantChatService`'s single shared connection is safe off the UI thread — its `_gate` covers every
public method including reads, and the sync service already calls it from a pool thread.

### Result

The reported export now imports in **2.1 s** (16:22:39.76 → 16:22:41.84), all 573 chats, with the UI
answering UIA queries throughout.

Where the time actually goes, measured on a 150 MB / 2292-chat export built by repeating the real one
with fresh GUIDs: read + convert **0.31 s**, store **21.6 s**. So the indeterminate phases are
effectively instantaneous even at four times the reported size, and the phase worth a determinate bar
is the only slow one. Caveat on the `IsIndeterminate` binding: that 0.31 s window is too short to
catch through UIA, so it is covered by the ViewModel test rather than observed in the app. A typo in
that one binding would leave the bar determinate at 0% for a third of a second.

## Verification

Both fixes were verified against the real 37 MB file, in the real app, on a throwaway profile
(`PIA_DATA_DIR` / `PIA_LOCAL_DATA_DIR`, `PIA_DEBUG_CHAT_IMPORT_FILE` to bypass the file picker):

1. Import → 573 written, 0 failed, 94 messages recovered from the tree.
2. Export all → re-read every chat through the product's own path (`GetAsync`, `ORDER BY Ordinal`).
3. Compared that archive against the source file's own tree walk: **573/573 chats match** on message
   count, role order and content. 1801 messages stored. Chats showing a single message: 40 before, 0
   after.
4. Progress row seen live on the 150 MB export: `AssistantHistory_ImportStatus` read
   `Importing chat 1716 of 2292…` and then `Importing chat 2090 of 2292…` on the next query, with the
   bar filled proportionally and the Import button disabled. Both readings were answered by the UI
   thread mid-import, which is the responsiveness claim.

Re-importing the same file is a deliberate no-op (`existing.UpdatedAt >= chat.UpdatedAt`), so a
profile that already ran the old import keeps its truncated chats. Delete them first, or the fix looks
like it did nothing.

`dotnet test`: 4476 passed, 0 failed (baseline was 4465/0). Rebuild in Debug and Release: 0 warnings.

The off-thread test was falsified before being trusted — stripping `ConfigureAwait(false)` from the
service makes `Import_DoesNotRunOnTheCallersSynchronizationContext` fail on all three observations.

## Not fixed — found while verifying

### Retention deletes an imported archive on the next launch — read this before importing

`AssistantChatRetentionService` evicts chats older than `ChatHistoryRetentionDays` (default 30) at
startup. An Open WebUI migration is almost entirely older than that, so the very next launch deleted
**561 of the 573** imported chats. Reproduced in the throwaway profile; raising the setting to 365 was
what made the verification above possible at all.

This makes a successful import look like it worked and then silently undid itself. Whatever the fix is
— exempt imported chats, prompt before a bulk eviction, or treat `CreatedAt` differently from
`LastAccessedAt` for retention — it needs a decision, not just a code change.

### Open WebUI reasoning blocks render as raw HTML

430 of 901 imported assistant turns begin with a `<details type="reasoning">…</details>` block.
`PiaMarkdownRenderer.RenderHtmlBlock` emits an HTML block as literal text, so the answer opens with
`<details type="reasoning" done="true" duration="10">`, `<summary>Thought for 10 seconds</summary>`
and a wall of `&gt;`-quoted lines before the actual reply. The answer is present and readable below
it; only the fidelity is poor. Pia already has `SyncAssistantChatMessage.ThinkingContent` for exactly
this, so the converter could lift those blocks out instead of leaving them inline.

### Pre-existing: the ViewModel builds its own file dialogs

`ExecuteImportChatsAsync`, `ExportToArchiveFileAsync` and `ExecuteExportChatAsync` construct
`OpenFileDialog` / `SaveFileDialog` directly while an `IDialogService` is already injected. Left alone
deliberately — it is pre-existing, consistent across the whole file, and untangling it does not belong
in a bugfix.
