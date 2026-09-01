# Persisted attachments: save into the workdir, keep the names after send

**Status:** Implemented — pending real-app confirmation
**Owner:** Marco Altmann
**Written:** 2026-09-01
**Origin:** Owner request 2026-09-01 ("files we drag and drop or select into Assistant chats are only stored in memory, not shown after sending the message and vanish in history. 1. user needs an intuitive option to persist this data into the current workdir before adding 2. after sending the message the ui needs to include the names in the ui — it is very confusing that they disappear"), building on [2026-08-31-file-drop-attachments-plan.md](2026-08-31-file-drop-attachments-plan.md) and [2026-09-01-outlook-virtual-file-drop.md](2026-09-01-outlook-virtual-file-drop.md).

Companion tracking surface: [2026-09-01-persisted-attachments-checklist.md](2026-09-01-persisted-attachments-checklist.md).

---

## 1. What was wrong

The chip round shipped staging: a dropped `.txt` / `.docx` / `.xlsx` / `.msg` is read to text at drop time into
`PendingFileAttachment.Text` and shown as a composer chip. Nothing else happened to the file.

On send, `AssistantViewModel.ExecuteSendMessage` folded that text into `attachedFileContext`, cleared
`PendingFiles`, and the chips disappeared. `AttachedFileContext` is not in `AssistantMessageMapper`, so a
reloaded chat showed only the typed text with no trace that a file was ever involved.
`PendingFileAttachment.FullPath` was captured but had exactly two consumers — a tooltip and the dedupe check —
and for an Outlook virtual drop the source under `%LOCALAPPDATA%\Pia\DropCache` is swept after two minutes.

The two requests are one feature: **copying the file into the working directory is what makes the history chip
openable.** A name-only chip is inert; a saved chip can be reopened and is reachable by the file tools in a
later turn.

## 2. Decisions

| Question | Answer |
|---|---|
| Shape of the persist affordance | A per-chip save button beside the existing remove button. One click, per file, reversible before send. Not a modal on every drop, and not an all-or-nothing composer toggle. |
| What a chip on a sent message can do | Open + reveal **only** when the file was saved. An unsaved one is an inert name pill — no dead buttons. |
| Agent mode / Run in background | Still blocked by attachments. Unchanged. |
| File types | Unchanged: Text/Docx/Xlsx/Email plus images. PDF/ZIP stay unsupported. |
| What is persisted | The file name always; the sandbox-relative path only when the file was copied in. **Never the original absolute path.** |

That last row is the load-bearing one. `SyncAssistantChatMessage` carries an explicit text-only/no-attachments
contract and syncs under an E2EE wrapper precisely because its text fields are sensitive. A file name is
metadata; a full source path is a user-named item that leaks the sender's local folder layout.

## 3. What was built

**New service.** `IAttachedFileStore` / `AttachedFileStore` (`src/Pia.Wpf/Services/`), registered beside
`IWorkingDirectoryService` in `Bootstrapper`. Two operations:

- `SaveIntoWorkingDirectory(sourcePath, workingDirectory)` — the copy's sandbox-relative path, or null.
- `ResolveAbsolute(relativePath)` — an absolute path, **composed and not probed**.

It reuses the sandbox machinery rather than reimplementing it: `ISettingsService.AssistantFilesFolder` for the
root (read per call, mirroring `WorkingDirectoryService`), `IWorkingDirectoryService.EnsureSubfolder` to create
the target, `SafeFolderPath.TryResolveInside` / `TryResolveInsideAllowingAbsolute` for containment,
`SensitivePathGuard.IsBlocked`, and `AssistantWorkspace.IsAtOrInsideVaultOf` to reject the vault — mirroring
`WorkingDirectoryService.EnsureSubfolder`, whose comment notes the vault is deliberately *not* in the guard.

Behaviour at the edges: a name collision is suffixed (`notes (2).txt`) and an existing file is **never**
overwritten; a source already inside the sandbox is not duplicated, its own relative path comes back; an
unconfigured folder, a missing source, an escaping working directory and the vault all return null.

`ResolveAbsolute` returns null **only** for an unconfigured sandbox or a path that fails containment — never
because the file is missing. A chip saved before an `AssistantFolderRelocationService` move therefore keeps its
buttons and the click silently no-ops through `ShellLauncher`'s best-effort contract. It does not degrade to an
inert pill: that would need a disk probe per history row, and the pill/buttons split stays a pure function of
`SavedRelativePath`.

**Models.** `PendingFileAttachment` became an `ObservableObject` with `SavedRelativePath` + `IsSaved`, so the
composer chip re-renders when the copy lands. `AttachedFileRef(FileName, SavedRelativePath)` joined
`ChatMessageExtras.cs`, and `AssistantMessage` gained `AttachedFiles` + `HasAttachedFiles`.

`FileRefKind` was deliberately **not** extended. Its declaration order *is* its precedence in
`AssistantMessage.AddOrUpgradeFileRef`: inserting mid-list would silently reorder existing kinds, and appending
would make an attachment outrank `Exported`.

**Send path.** `ExecuteSendMessage` already captured the chips before `PendingFiles.Clear()`; it now also
projects them to `AttachedFileRef[]` and passes them as a new trailing `attachedFiles` parameter on
`StartTurnAsync`, which sets them on the user `AssistantMessage` where `AttachedFileContext` is already set.
`SavePendingFileCommand` performs the copy; `CanSavePendingFiles` hides the button when no store is available.

**Persistence.** One JSON `TEXT` column, `AttachedFiles`, on `AssistantChatMessages` — at most five
display-only items per message that are never queried, so a child table was not worth it;
`AssistantChats.ExtraJson` is the in-repo precedent. Note `AssistantChatMessages` has no `ExtraJson`, so the
message-level `[JsonExtensionData]` is dropped on local persist: this genuinely needed a real column. The
migration copies the `hasProviderName` block in `SqliteContext` verbatim. The column is **appended last** in
both the INSERT and the SELECT, because `GetMessagesAsync` reads positionally. A corrupted column loses the
chips, not the message.

The migration itself is covered by `AssistantChatMessagesAttachedFilesMigrationTests`, which hand-builds a
pre-change database and opens it through `SqliteContext` — `CREATE TABLE IF NOT EXISTS` leaves the seeded
table alone, so the `ALTER TABLE` branch is genuinely what runs. A round-trip test against a fresh database
would have passed vacuously.

**E2EE needed no change.** `SyncMapper.ToSyncAssistantChat` moves the whole `Messages` list into
`EncryptedPayload` rather than nulling an enumerated field list, so a new property on
`SyncAssistantChatMessage` rides inside the ciphertext automatically. There is no allowlist that could have
left file names in the clear.

**UI.** New `PiaAttachedFileChip` (`src/Pia.Wpf/Controls/Chat/`), a sibling of `PiaFileChip` following the
one-control-per-chip-kind pattern. It holds no absolute path: `SavedRelativePath` decides between the
open/reveal buttons and the inert pill.

Deliberately **not** `PiaChipOverflowPanel`: `MaxPendingFiles` is 5 against `InlineSlots = 3`, so overflow could
only ever hold two chips, and the panel re-hosts spill-over inside a `Popup` whose separate visual-tree root
makes a `FindAncestor` walk out to the view unreliable — which is exactly why `PiaAssistantMessage` has to name
an explicit `AncestorType={x:Type chat:PiaAssistantMessage}` for its own chips. A plain `ItemsControl` +
`WrapPanel` hosts them instead.

The two user bubbles are hand-duplicated and already diverged, so this is two edit sites with **different**
ancestor bindings, each matching its own file's convention:

- `Views/AssistantView.xaml` — `AncestorType=UserControl`, resolving to `AssistantView`, like the user-bubble
  copy button 20 lines below.
- `Controls/AssistantHistory/PiaAssistantChatInspector.xaml` — `AncestorType=views:AssistantHistoryView`, like
  every other command binding in that file. `AncestorType=UserControl` would wrongly resolve to the inspector
  itself, whose `DataContext` is not the view model.

AutomationIds use a fresh `AttachedFileChip_` prefix. Reusing `FileChip_` would have made a script targeting
the assistant-side chips start hitting user-message chips under `automationId*=` prefix matching.

## 4. Deliberately out of scope

- **Images.** `PendingAttachment` / `ImageAttachment` is a separate path and also absent from the mapper, so a
  sent image renders in the live bubble but vanishes from history. Same class of bug, different transport
  (bytes, not a path).
- **Regenerate on a resumed chat** stays broken for attachment turns: `AttachedFileContext` is not persisted, so
  the guard in `AssistantViewModel` still rejects it. Re-reading a *saved* file to rebuild the context is the
  natural follow-up.
- **FTS.** `ReplaceFtsRowAsync` indexes only message content; appending file names would make "the chat where I
  attached quote.docx" searchable.
- **Agent mode / background runs.** Once a file is a real file in the workdir, the comments justifying the block
  ("a detached run has no way to carry one", "the planner sees only the goal string") no longer hold. Left
  blocked on purpose.
