# Persisted attachments — checklist

**Status:** Implemented — every step below landed in one change; real-app confirmation outstanding
**Owner:** Marco Altmann
**Written:** 2026-09-01
**Origin:** [2026-09-01-persisted-attachments-plan.md](2026-09-01-persisted-attachments-plan.md)

Tick a box in the commit that lands the step.

## Scales

**Effort** — `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface · `L` a week or more, a new subsystem.

**Value** — `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little standalone value, unblocks a High.

## Decision gates

| Gate | Question it answers | Blocks |
|---|---|---|
| D1 | **CLOSED 2026-09-01 — per-chip save button.** How is the persist option offered? Not a modal on every drop (interrupts the common one-off paste) and not an all-or-nothing composer toggle (hides which file went where). | A1, C1 |
| D2 | **CLOSED 2026-09-01 — name always, relative path only when saved; never the absolute path.** What of an attachment may enter `SyncAssistantChatMessage`, whose contract is text-only and whose fields are E2EE-wrapped because they are sensitive? | B1–B4 |
| D3 | **CLOSED 2026-09-01 — no.** Does saving a file into the workdir unblock Agent mode / Run in background? The comments justifying the block no longer hold once the file is real, but the owner scoped it out. | — |
| D4 | **CLOSED 2026-09-01 — no.** Do PDF/ZIP become attachable as save-only? Would require widening the XAML extension list, `ReadableKinds`, and `FileDropAcceptedExtensionsTests` in lockstep. | — |

## Steps

- [x] **A1 — `AttachedFileStore`.** New `IAttachedFileStore` / `AttachedFileStore` copying a staged file into the chat's working directory and mapping the stored relative path back to an absolute one, reusing `IWorkingDirectoryService.EnsureSubfolder`, `SafeFolderPath`, `SensitivePathGuard` and `AssistantWorkspace.IsAtOrInsideVaultOf`. Registered in `Bootstrapper`. *Deps:* — · *Effort:* S · *Value:* Enabler
- [x] **A2 — Model state for a saved chip.** `PendingFileAttachment` becomes an `ObservableObject` with `SavedRelativePath` / `IsSaved`; `AttachedFileRef` joins `ChatMessageExtras.cs`; `AssistantMessage` gains `AttachedFiles` / `HasAttachedFiles`. `FileRefKind` is left alone — its order is its precedence. *Deps:* — · *Effort:* XS · *Value:* Enabler
- [x] **B1 — Sync DTO.** `SyncMessageAttachedFile { FileName, RelativePath }` and `SyncAssistantChatMessage.AttachedFiles`, with the text-only contract comment extended to say what may and may not go in. *Deps:* A2 · *Effort:* XS · *Value:* Enabler
- [x] **B2 — Schema + migration.** `AttachedFiles TEXT` in the `CREATE TABLE`, plus a `PRAGMA table_info` presence flag and `ALTER TABLE ... ADD COLUMN` copying the `hasProviderName` block verbatim. *Deps:* B1 · *Effort:* XS · *Value:* Enabler
- [x] **B3 — Read/write.** The column appended **last** in the INSERT and the SELECT, because `GetMessagesAsync` reads positionally; serialize/deserialize helpers beside the `ExtensionData` pair, with a corrupted column losing the chips rather than the message. *Deps:* B2 · *Effort:* XS · *Value:* Enabler
- [x] **B4 — Mapper.** `AssistantMessageMapper.ToDto` / `FromDto`, which covers both the live-resume path and the history inspector. Empty maps to null so the column stays `NULL`. *Deps:* B1 · *Effort:* XS · *Value:* Enabler
- [x] **C1 — Save command.** `SavePendingFileCommand` + `CanSavePendingFiles` on `AssistantViewModel`, with localized success/failure snackbars. *Deps:* A1, A2 · *Effort:* XS · *Value:* High
- [x] **C2 — Names ride the send.** A trailing `attachedFiles` parameter on `StartTurnAsync`; `ExecuteSendMessage` projects the already-captured chips onto it; `ChatSessionManager` sets them on the user message. A refused send still restores the composer. *Deps:* A2 · *Effort:* XS · *Value:* High
- [x] **C3 — `PiaAttachedFileChip`.** New control: open + reveal when saved, an inert name pill when not. A fresh `AttachedFileChip_` AutomationId prefix, disjoint from `FileChip_`. *Deps:* A2 · *Effort:* S · *Value:* High
- [x] **C4 — Both user bubbles.** The chip strip in `AssistantView.xaml` and `PiaAssistantChatInspector.xaml`, each with the ancestor binding its own file already uses. A plain `ItemsControl` + `WrapPanel`, not `PiaChipOverflowPanel`. *Deps:* C3 · *Effort:* XS · *Value:* High
- [x] **C5 — Composer save button.** The button on the pending chip, hidden once saved or when no store is available, with a tick in its place. *Deps:* C1 · *Effort:* XS · *Value:* High
- [x] **E1 — Strings.** Five new keys across `ViewStrings` and `MessageStrings`, in neutral / `.de` / `.fr`. `LocalizationTests` enforces parity. *Deps:* C1, C3 · *Effort:* XS · *Value:* Enabler
- [x] **E2 — Tests.** `AttachedFileStoreTests` (collision, already-inside, relative-source, vault, escape, missing source, unconfigured, resolve); mapper round-trip; service round-trip + `NULL` + corrupted column; VM save/send/refusal; chip visibility; the `ViewAutomationIdTests` rows. *Deps:* every step above · *Effort:* S · *Value:* High
- [x] **E3 — Migration test.** `AssistantChatMessagesAttachedFilesMigrationTests` hand-builds a pre-change database so the `ALTER TABLE` branch actually runs — the one path where a mistake corrupts history a user already has, and one a fresh-database round-trip would cover only vacuously. *Deps:* B2 · *Effort:* XS · *Value:* High
- [ ] **F1 — Confirm in the real app.** Save one of two chips, send, check both bubbles, reopen from History, and verify the copy landed in the chat's working directory. The migration no longer rides on this pass — see E3. *Deps:* E2, E3 · *Effort:* XS · *Value:* High

## Not yet planned

- Persisting the image attachment, which vanishes from history the same way through a different transport.
- Rebuilding `AttachedFileContext` on a resumed chat by re-reading a saved file, which would also un-break regenerate for attachment turns.
- Indexing attached file names into `AssistantChatsFts`.
- Letting saved files unblock Agent mode and Run in background (gate D3).

## Suggested order

Cheapest decisive work first: A2 then A1 (the model shape decides the service surface), then the B group as one
vertical slice — it is inert until C2 feeds it. C1/C5 and C3/C4 are two independent user-visible slices; either
can land first. E1 rides along with whichever of them lands first, E2 last, F1 in the real app.
