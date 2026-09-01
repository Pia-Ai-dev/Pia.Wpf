# File-drop attachments — checklist

**Status:** In progress — Round 1 (parsers) landed
**Owner:** Marco Altmann
**Written:** 2026-08-31
**Origin:** [2026-08-31-file-drop-attachments-plan.md](2026-08-31-file-drop-attachments-plan.md)

Tick a box in the commit that lands the step.

## Scales

**Effort** — `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface · `L` a week or more, a new subsystem.

**Value** — `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little standalone value, unblocks a High.

## Decision gates

| Gate | Question it answers | Blocks |
|---|---|---|
| G0 | **CLOSED 2026-08-31 — out of scope for this round.** Does a mail dragged **straight out of Outlook's message list** reach `OnDrop`? No: Outlook offers `FileGroupDescriptorW`/`FileContents`, not `CF_HDROP`, and `FileDropBehavior` gates on `DataFormats.FileDrop` in both arms. The request names ".msg or .eml **files** (sample files in artifacts)", i.e. files on disk, so this round covers drop-from-Explorer and the Attach-file picker. The `FileGroupDescriptorW` materializer stays *not planned* and is raised as an open question to the owner. | nothing |
| A2 | **CLOSED 2026-08-31 — yes, both.** Two fixtures are committed under `tests/Pia.Wpf.Tests/TestData/`, written by the `cfb` npm package (an implementation independent of this repo, so they are an oracle rather than a self-check). `sample-mail.msg` (11,264 B) carries a 182-byte PR_BODY in the **mini** stream and a 6,098-byte stream in the **normal** FAT, a trailing-NUL PR_DISPLAY_TO, a zero-length PR_DISPLAY_CC, a recipient sub-storage, and a `FLAT-SCAN-BUG-SENTINEL` PR_HTML under `__nameid_version1.0` that traps a flat directory scan. `sample-mail-html-only.msg` (3,584 B) has **no** PR_BODY and a PR_HTML body with a CPID record. See that folder's README. | — |
| B1 | Does `AttachedFileContext` on `AssistantMessage` reach the model on a plain send with no `@`-command (the parameterless `ToChatMessage` path)? A no here invalidates every step below it. | B2–B9 |
| C1 | After a live run, is the interactive context growth from three file-bearing turns tolerable on the owner's smallest-window provider? A no means a per-session cap, not a bigger per-message cap. | C6 |

---

## Round 0 — the thirty-second question

- [x] **G0 · Scope of the Outlook drag.** Answered from the code rather than by experiment: `FileDropBehavior` accepts only `DataFormats.FileDrop`, and Outlook's message list publishes `FileGroupDescriptorW`/`FileContents`, so a direct drag cannot reach `OnDrop`. This round ships drop-from-Explorer plus the Attach-file picker, which is what the request describes; the materializer is an open question for the owner. *Deps:* — · *Effort:* XS · *Value:* High

## Round 1 — parsers and unit tests, no UI

- [x] **A1 · Normalized mail record and the shared MIME decoding layer.** Add `src/Pia.Wpf/Helpers/Email/EmailMessage.cs` (no `ReplyTo` member — plan §7.5) and `MimeDecoding.cs` (RFC 2047 with the adjacent-encoded-word whitespace drop, quoted-printable with soft breaks, base64, the hard-coded charset table, the HTML strip, and the RFC 5322 CFWS comment strip the fixture’s own `Date:` needs). *Deps:* G0 · *Effort:* S · *Value:* Enabler
- [x] **A2 · Tracked test fixtures.** `tests/Pia.Wpf.Tests/TestData/` now holds `sample-mail.msg` (both allocators, trailing-NUL, zero-length stream, recipient sub-storage, flat-scan sentinel) and `sample-mail-html-only.msg` (PR_HTML body, no PR_BODY, CPID record), plus a README recording each trap. Still owed: the `<None Update="TestData\*.msg"><CopyToOutputDirectory>` item group the test project does not have today. *Deps:* — · *Effort:* S · *Value:* Enabler
- [x] **A3 · The CFB container reader.** Add `src/Pia.Wpf/Helpers/Email/CompoundFile.cs` — header validation, DIFAT including continuation sectors, FAT and mini-FAT chains, directory sectors walked from the FAT chain, the red-black sibling tree, and the `streamSize < 4096` allocator dispatch. *Deps:* A2 · *Effort:* M · *Value:* Enabler
- [x] **A4 · The MSG reader.** Add `MsgReader.cs` — the root-scoped MAPI tag table, the `__properties_version1.0` record loop for PR_CLIENT_SUBMIT_TIME, recipient sub-storages with SMTP-over-X.500 address selection, attachment names, the root-scoped PR_HTML body fallback, and the three mandatory string post-processing rules. PR_CLIENT_SUBMIT_TIME goes through `DateTime.FromFileTimeUtc`, never `DateTimeOffset.FromFileTime`; PR_INTERNET_CPID comes from the property record, not a `__substg1.0_` stream. *Deps:* A3 · *Effort:* M · *Value:* High
- [x] **A5 · The EML reader.** Add `EmlReader.cs` — line-ending normalization, header unfolding, the whitelist, the recursive multipart walk with a depth cap, and text/plain-preferred part selection with the HTML-strip fallback. *Deps:* A1 · *Effort:* M · *Value:* High
- [x] **A6 · `FileKind.Email` and `ReadEmailAsync`.** Extend `DroppedFileReader` with the enum member, the two extension rows, the reader that renders the five-field whitelist plus body — date as `yyyy/MM/dd HH:mm zzz` under `InvariantCulture`, slashes because the PII tokenizer eats the hyphenated form — a `===` rule between the header block and the body so the PII phone pattern cannot span into it, and the `MaxTextBytes` cap applied to the rendered text with a looser `MaxTextBytes * 8` ceiling on the file itself; add the `Email` case to `DroppedFileImporter`'s switch. *Deps:* A4, A5 · *Effort:* XS · *Value:* Enabler
- [x] **A7 · Parser unit tests.** Add `EmlReaderTests.cs` (23 methods) and `MsgReaderTests.cs` (18 methods) per the plan's §15.2/§15.3, plus the three new `[InlineData]` rows in `DroppedFileReaderClassifyTests.cs`. *Deps:* A6 · *Effort:* M · *Value:* High
- [x] **A8 · Local sample probe — DROPPED, not deferred.** It would assert `artifacts/sample.msg` and `artifacts/sample.eml`, which are gitignored and untracked, so it can only ever run on one machine and reports `Not Run` everywhere else — a test that rots silently the moment those files move. The values it would pin are already asserted by the tracked-fixture tests and were measured independently twice against a third-party CFB implementation; both real samples were re-measured through the shipped `ReadEmailAsync` after every fix round. *Deps:* A7 · *Effort:* XS · *Value:* Med

## Round 2 — attachment model, ViewModel, XAML, localization, automation id, DEBUG bypass

- [ ] **B1 · `AttachedFileContext` and the single `ToChatMessage` builder.** Add the observable field to `AssistantMessage` and collapse both overloads onto one private `BuildChatMessage(string)` that appends it, with the tests that prove the no-image path carries it. *Deps:* — · *Effort:* S · *Value:* High
- [ ] **B2 · Thread the context through the turn.** Add the optional `attachedFileContext` parameter to `IChatSessionManager.StartTurnAsync` and its implementation, set it on the minted user message, and add it to `TryAnswerParkedRunAsync`'s refusal list beside `attachment`. Add a sixth `Arg.Any<string?>()` to the three existing NSubstitute setups (`AssistantViewModelLeverTests.cs:517-518`, `:530-531`, `AssistantViewModelRegenerateTests.cs:38-40`) in this same step — a five-arg matcher silently returns a null `Task` once anything drives a non-null context. Tests: `StartTurnAsync_SetsAttachedFileContextOnTheUserMessage` and `StartTurnAsync_RunParkedForClarification_WithAnAttachedFile_StartsAnOrdinaryTurn` (plan §15.8a). *Deps:* B1 · *Effort:* S · *Value:* High
- [ ] **B3 · `PendingFileAttachment` and the wrapper renderer.** Add the model with its BMP-safe `SymbolRegular Icon`, and `AssistantPromptComposer.BuildAttachedFileBlock` reusing the existing private `EscapeAttr`. *Deps:* A6 · *Effort:* S · *Value:* Enabler
- [ ] **B4 · `DroppedFileAttachmentImporter`.** Add the sibling staging API with the dedup, count, per-file and per-message caps, and the per-file snackbars, leaving `DroppedFileImporter.TryImportAsync`'s contract untouched. *Deps:* B3 · *Effort:* S · *Value:* High
- [ ] **B5 · Localization keys.** Add nine keys — seven in the `MessageStrings` family, two in `ViewStrings` — across all three locales each, plus the two-placeholder `[InlineData]` row and the two `\blocalizationService\.` regex patterns in `LocalizationTests` (its five existing patterns all require the underscore-prefixed field, so the new importer’s five keys are unchecked without them; run that widened test once on a clean tree first, since it newly scans every parameter-injected call site). *Deps:* — · *Effort:* XS · *Value:* Enabler
- [ ] **B6 · ViewModel: the collection, its notification wiring, and the send path.** Add `PendingFiles`, `HasPendingFiles`, `RemovePendingFileCommand`, the `CollectionChanged` subscription and its teardown, the widened `CanExecuteSendMessage`, clear-on-send, restore-on-refusal, clear in `StartFreshChat`, and the `RegenerateCore` capture. *Deps:* B2, B4 · *Effort:* M · *Value:* High
- [ ] **B6a · Refuse both detach paths, and explain it once.** Two blocks and one hint (plan §8.4.1–8.4.3): add `PendingFiles.Count == 0` to `CanExecuteRunInBackground`, **and** force `planned: false` in `ExecuteSendMessage` while a chip is attached — an approved plan resumes through `HeadlessRunLauncher`, which rebuilds from persisted rows and cannot see `AttachedFileContext`, and the planner only ever sees the goal string. Leave the Agent lever itself alone. *Deps:* B6 · *Effort:* XS · *Value:* High
- [ ] **B6b · The composer hint and its precedence.** Add `PendingFilesBlockRunHintVisible`, recomputed from `OnPendingFilesChanged` **and** from the `AssistantViewModel.cs:851-854` name filter, undebounced; settle the three-way order `GoalTooShort` → `PendingFilesBlockRun` → `AgentMode` in the two `partial void On…Changed` hooks; add the `TextBlock` beside the other two. Without the collection-side recompute the button goes dead with no explanation — the exact state the hint exists to prevent. *Deps:* B6a, B5 · *Effort:* XS · *Value:* High
- [ ] **B7 · Replace the drop handler and the picker branch.** Rewrite `ExecuteHandleFilesDropped` to route per file, add the `IsStreaming` guard to `ExecuteHandleImageAttached`, apply the first-image-wins rule, and delete the duplicated `Count == 1 && Image` branch in `AssistantView.xaml.cs`. *Deps:* B6 · *Effort:* S · *Value:* High
- [ ] **B8 · The chip strip and the accepted-extension lists.** Add the `ItemsControl` + `WrapPanel` Border above the image preview with the per-item `StringFormat` AutomationId and the full-path tooltip, append `.eml,.msg` to both views' `AcceptedExtensions`, and switch the Assistant's overlay hint to its own loc key. *Deps:* B5, B6 · *Effort:* S · *Value:* High
- [ ] **B9 · The DEBUG bypass.** Declare `DebugDropFilesEnvVar` in `Bootstrapper`, read it through a `#if DEBUG`-gated helper in `AssistantView.xaml.cs` that stands in for the picker, and add the row to the UI-automation playbook's bypass table. *Deps:* B7 · *Effort:* XS · *Value:* Enabler
- [ ] **B10 · Bump the AutomationId row.** Move `AssistantView`'s `[InlineData]` from `19, 2` to `20, 3` after measuring the real counts by overshooting the floor and reading the failure message. *Deps:* B8 · *Effort:* XS · *Value:* Med
- [ ] **B11 · ViewModel and composer tests.** Add `AssistantViewModelPendingFilesTests.cs` (including the two hint tests, the precedence test and `AgentModeSendWithAPendingFile_IsNotPlanned`), `DroppedFileAttachmentImporterTests.cs`, `AssistantPromptComposerAttachedFileTests.cs`, `AssistantMessageAttachedFileContextTests.cs`, `PendingFileAttachmentTests.cs`, and the two named rows in `AssistantViewParseTests.cs` (plan §15.11). *Deps:* B6, B6b, B7 · *Effort:* M · *Value:* High
- [ ] **B12 · Clear the gate.** `dotnet test` at `failed: 0`, then `dotnet build -t:Rebuild -v:n` in Debug **and** Release at `0 Warning(s)` / `0 Error(s)`. *Deps:* B11 · *Effort:* XS · *Value:* Enabler

## Round 3 — UI round

- [ ] **C1 · Drive the real app, and measure the context growth.** Record a WinWright script that launches with `PIA_DEBUG_DROP_FILES` pointed at a `.txt` and a `.msg`, clicks Attach-file, asserts both chips, removes one, sends, and asserts the answer refers to the mail. Then send **three** file-bearing turns in one session and read the resulting request size off the log — that is what gate C1 asks, and one send cannot answer it. *Deps:* B12 · *Effort:* S · *Value:* High
- [ ] **C2 · Mixed-drop cosmetics.** Judge the two stacked Borders when an image and files are staged together, and merge or restyle them if it reads badly. *Deps:* C1 · *Effort:* XS · *Value:* Med
- [ ] **C3 · Overflow and truncation legibility.** Check five chips at the narrowest composer width, and decide whether a truncated file needs a visible marker beyond the one-shot snackbar. *Deps:* C1 · *Effort:* XS · *Value:* Med
- [ ] **C4 · Chat-switch behaviour.** Try both keeping and clearing chips across a chat switch and settle open question 1 in the plan. *Deps:* C1 · *Effort:* XS · *Value:* Med
- [ ] **C4a · Settle the Agent-mode downgrade.** With a chip attached and the Agent lever on, judge whether the silent downgrade plus its hint reads right, or whether Send should refuse outright the way Run-in-background does — plan open question 4. *Deps:* C1 · *Effort:* XS · *Value:* Med
- [ ] **C5 · Mail-quality pass on real mail.** Drop a handful of the owner's own `.msg` and `.eml` files — including one HTML-only and one relayed through Gmail (for the `(UTC)` date suffix) — confirm the header whitelist and body extraction read well to the model, and fix whatever the samples did not exercise. *Deps:* C1 · *Effort:* S · *Value:* High
- [ ] **C6 · Per-session attachment budget.** Only if C1 shows the per-message caps are not enough: cap the *total* attachment text alive in one session rather than raising `MaxTotalChars`. Strike this step if C1 says the caps hold. *Deps:* C1 · *Effort:* S · *Value:* Med

## Not yet planned

- PDF attachments (`FileKind.Pdf` is classified but has no reader and is absent from `AcceptedExtensions`).
- Persisting `AttachedFileContext` so a reopened chat keeps its files — would need the `SyncAssistantChatMessage.ExtensionData` escape hatch and a server-contract conversation.
- Extracting `.msg`/`.eml` attachment **bytes** rather than names.
- Exposing mail parsing to the assistant's `read_file` tool via `FilesToolHandler`. The extension chain at `src/Pia.Wpf/Services/FilesToolHandler.cs:743-759` has `.docx` and `.xlsx` branches but no `.msg`, so the model asking to open a `.msg` from disk gets CFB binary as mojibake rather than a clean refusal. That is the status quo, but users who start dropping mail will start asking the model to open one.

## Suggested order

Cheapest decisive work first, then the vertical slices.

0. **G0** — thirty seconds against today’s build, and it can redraw the whole feature. Nothing else starts first.
1. **B1** — one file, and it settles gate B1: if `AttachedFileContext` does not reach the model on a plain send, nothing else is worth building.
2. **A2** — the fixture question (gate A2) decides how the whole MSG parser gets tested; answer it before writing the parser.
3. **B5** — loc keys are free and unblock B8 without depending on anything.
4. **A1 → A5** — the EML reader is the cheaper of the two parsers and proves the shared decoding layer.
5. **A3 → A4** — the CFB reader and the MSG reader, the single largest and riskiest block.
6. **A6 → A7 → A8** — wire the readers in and lock them down.
7. **B2 → B3 → B4** — the send-path plumbing and the staging API, all headless and testable.
8. **B6 → B6a → B6b → B7 → B8 → B9 → B10** — the first vertical slice a human can see: chips appear, files ride the turn, a script can drive it.
9. **B11 → B12** — tests and the gate.
10. **C1 → C6** — the UI round, driven by what the real app actually looks like.
