# Unmerged branch inventory

Taken 2026-08-16 against `feature/agent-run-spine` @ `93f8eb4b`, after `git fetch --prune`.

Companion to the cleanup pass that deleted the 13 branches already fully merged into
`feature/agent-run-spine` (those carried no unreachable commits). Everything below is the
opposite case: each branch holds commits that are **not** reachable from the spine, so deleting
one discards work. The tip SHAs are recorded here as the recovery record — `git branch <name> <sha>`
brings any of them back while the objects survive (indefinitely for anything still on the remote,
~90 days of reflog for the local-only ones).

"Ahead"/"behind" are relative to `feature/agent-run-spine`, not to `main`.

## Local + remote in sync

Deleting either side should mean deleting both.

| Branch | Ahead | Behind | Tip | Last commit |
|---|---|---|---|---|
| `claude/auto-fill-microsoft-login-name-jVKL0` | 1 | 844 | `f6836cba` | 2026-04-21 Claude |
| `claude/enhance-transcription-service-mPdYr` | 16 | 838 | `60bf0809` | 2026-04-26 Claude |
| `claude/refine-local-plan-uzT5G` | 2 | 875 | `3b1a3d32` | 2026-04-04 Claude |
| `feature/community-edition-client` | 1 | 844 | `94645e20` | 2026-04-20 Marco Altmann |

## Local + remote diverged

| Branch | Local | Remote | Note |
|---|---|---|---|
| `claude/restructure-navigation-pane-a0AuE` | ahead 6, `c8a2f100` 2026-04-22 | ahead 1, `539d9fd8` 2026-04-21 | 5 local commits never pushed |

`feature/meeting_transscription` was listed here in the first pass; it is **not** diverged. Local and
remote are both `966deaba` (ahead 102, behind 838) — the "ahead 102 / ahead 102" reading was an
ahead/behind mix-up. It belongs under "Local + remote in sync". It is checked out in the
`C:/projects/pia_meeting` worktree, which must be removed before the branch can be deleted.

## Local only, never pushed

No remote copy: deleting these leaves the reflog as the only recovery path.

| Branch | Ahead | Behind | Tip | Last commit |
|---|---|---|---|---|
| ~~`feature/dynamic-schema-ui`~~ | 11 | 881 | `3d6c1e65` | 2026-03-28 Marco Altmann — **deleted**, see below |
| ~~`merge/dynamic-schema-ui`~~ | 12 | 773 | `3906951f` | 2026-05-04 Marco Altmann — **deleted**, see below |
| `feature/right_click` | 5 | 817 | `b1ca6d98` | 2026-04-30 Marco Altmann |
| `feature/23_multi_window` | 4 | 937 | `e31da6e3` | 2026-03-16 Marco Altmann |
| `feature/38_brainstorming` | 2 | 896 | `6193a6a6` | 2026-03-20 Marco Altmann |

## Remote only, no local copy

| Branch | Ahead | Behind | Tip | Last commit |
|---|---|---|---|---|
| `origin/managed-personas-dtos` | 1 | 425 | `f6b6bbf0` | 2026-08-01 Pia-Ai-dev |
| `origin/feature/suggestions` | 1 | 423 | `0a174834` | 2026-07-13 Pia-Ai-dev |
| `origin/claude/pia-policy-settings-docs-2rlLT` | 1 | 844 | `90757da8` | 2026-04-21 Claude |
| `origin/claude/provider-specific-setup-options-0eTN6` | 1 | 896 | `4d811868` | 2026-03-23 Claude |
| `origin/claude/prompt-logging-transparency-AkHvh` | 1 | 937 | `3669db80` | 2026-03-14 Claude |

## Round 2 — salvage assessment (2026-08-16)

Question asked: which of these branches hold work worth bringing onto `feature/agent-run-spine`?
This round is assessment only — nothing was cherry-picked, merged, or deleted.

### Method

The first pass leaned on `git diff spine...branch` (three-dot). That is the wrong instrument here:
three-dot diffs from the merge base, so for a branch 800+ commits behind it reports everything the
branch added since April as if the spine still lacked it. `origin/managed-personas-dtos` showed
`SyncManagedPersona.cs` as a **new file** that way — it is byte-identical to the spine's copy.

Every verdict below rests on one of two checks instead:

1. `git cherry -v feature/agent-run-spine <branch>` — `-` means the patch already exists upstream.
   A `+` is a filter, not a verdict: work that landed via squash or rebase still shows `+`.
2. `git diff feature/agent-run-spine <branch> -- <paths>` — **two-dot, path-scoped**. Empty output
   is the actual proof of supersession. Unscoped two-dot is useless on these branches (it drags in
   838 commits of spine history the branch is simply behind on).

Verdicts are classified **SUPERSEDED** / **ABSENT — applies** / **ABSENT — stale** / **OWNER CALL**.
"Stale" means the work is genuinely missing from the spine but the surrounding code has drifted far
enough that the patch is a rewrite, not a cherry-pick — the value is the design, not the diff.

**Known limit of these checks.** Testing whether a branch's file path exists on the spine proves the
*path* is absent, never that the *functionality* is. That test misfired twice in this pass and both
misfires are corrected below: the spine implements the login allow-list as `AllowedSyncProviders`,
not `AllowedLoginProviders`; and the spine's consent gate lives inside `DirectTranscriptionService`
rather than in a file named `ConsentGate`. Every "absent" verdict here was therefore backed by a
second name-agnostic grep for the *behaviour*, not just the filename.

### Verdict table

| Branch | Verdict | One-line reason |
|---|---|---|
| `feature/meeting_transscription` | **ABSENT — applies** | 135 files missing on spine; a consent/privacy *hardening* layer above the gate the spine already has |
| `origin/claude/provider-specific-setup-options-0eTN6` | **OWNER CALL** | Spine has no Anthropic provider type at all |
| `feature/community-edition-client` | **OWNER CALL** | No licensing code anywhere on spine |
| `feature/right_click` | **OWNER CALL** | No shell extension, no single-instance IPC on spine |
| `feature/38_brainstorming` | **OWNER CALL** | No brainstorming code on spine; predates tool gating |
| `origin/claude/prompt-logging-transparency-AkHvh` | **OWNER CALL** | Absent, but arguably against the privacy-logging policy |
| `claude/refine-local-plan-uzT5G` | **ABSENT — stale** | Refactor never landed; its 1595-line test file targets an April API |
| `claude/restructure-navigation-pane-a0AuE` | **ABSENT — stale** | Sidebar rewrite is stale; the plan + spec docs are unique |
| `origin/feature/suggestions` | **ABSENT — stale** | Only `AssistantHintsEnabled` is new; patch would delete 948 spine lines |
| `feature/23_multi_window` | **SUPERSEDED** | Spine's window manager went well past it; docs are the only salvage |
| `claude/auto-fill-microsoft-login-name-jVKL0` | **SUPERSEDED** | `git cherry` says `-`; the fix is on the spine |
| `claude/enhance-transcription-service-mPdYr` | **SUPERSEDED** | Strict ancestor of `feature/meeting_transscription` |
| `origin/managed-personas-dtos` | **SUPERSEDED** | Byte-identical to spine |
| `origin/claude/pia-policy-settings-docs-2rlLT` | **SUPERSEDED** | Same allow-list shipped under a different setting name |

### `feature/meeting_transscription` — the one that matters

The single branch worth acting on. 135 files exist on it and not on the spine (checked by listing
`git diff --name-only spine...branch` and testing each path for existence in the working tree).

The spine took a different transcription path — `DirectTranscriptionService` plus the "Save to vault"
flow — so the branch's `LiveMeetingService`, `LiveTranscriptionOverlay` and `LiveTranscriptionViewModel`
are the **superseded** part. That is not where the value is.

The value is a consent / privacy layer that is orthogonal to which capture pipeline runs.

**First, the correction that sets the framing.** The spine is *not* missing consent gating.
`DirectTranscriptionService` is built around it: it injects `IConsentStateManager`,
`INamedConsentClassifier`, `IConsentAuditLog` and `IConsentEvidenceStore`, constructs a
session-scoped `ConsentForwardLoop` its own doc comment calls "THE privacy boundary", resets consent
state per session, and emits `SessionStarted` / `SessionStopped` audit events. So the branch is
**not** what makes meeting recording lawful — the spine already gates. What the branch adds is the
*hardening* layer on top of a gate that already works.

`src/Pia.Wpf/Services/Consent/` holds **14 files on the spine** (all at the top level — no
subdirectories) against **58 across the tree on the branch**. Absent on the spine:

- **Orchestration**: `ConsentGate`, `IConsentOrchestrator` + factory, `ConsentScope` — a policy layer
  above the forward loop, not a replacement for it
- **Classification**: `CascadingConsentClassifier`, `LlmConsentClassifier`, `ConsentPromptTemplates`
  — the spine has only the rule-based `NamedConsentClassifier`
- **Post-STT defence**: `PostSttDefenseFilter`, `BlocklistFilter`, `ConservativeCrossTalkResolver`
- **Tamper-evident audit**: `HashChainedAuditLog`, `AuditChainSigner`, plus a standalone
  `tools/verify-audit-chain` console verifier. The spine's `JsonlConsentAuditLog` is append-only but
  not hash-chained, so it is not tamper-evident.
- **Biometric consent** (9 files): store, cosine matcher, retention worker, and its settings UI
- **Privacy** (6 files, incl. a PII detector), **Cloud** provider registry, **Revocation**
- **Security modes**: `SecurityMode`, `SecurityModeViewModel`, `SecurityModeSection.xaml`
- **36 test files** under `tests/Pia.Wpf.Tests/Consent/` (20 top-level, 7 `Privacy/`, 7 `Biometric/`,
  1 `Snippet/`, 1 `Revocation/`), against 8 consent tests on the spine

Non-consent work also absent: `MeetingToolHandler` (summarize + query registered as a built-in
plugin), `MeetingTranscriptWriter`, `ActionCardChoice` multi-choice action cards, `PathShortener`,
`SessionEncryption`, `SherpaOnnxVadDetector`.

And 13 docs that exist nowhere else, including the five-phase consent-management plan set
(`docs/superpowers/plans/2026-04-27-consent-management-phase1-mvp.md` … `phase5-v4.md`) and the
German `docs/consent-management-spezifikation.md`.

**Recommendation.** Do not merge the branch — the transcription half would fight the spine. Read the
consent spec and the phase plans first, then port the hardening layer as its own piece of work,
grafted onto the `ConsentForwardLoop` the spine already runs. The pieces that most plausibly earn
their keep are the hash-chained audit log (an append-only JSONL that cannot be shown to be
un-tampered is weak evidence in a dispute) and the cross-talk resolver. Biometric consent and the
LLM classifier cascade are larger bets and should be decided separately.

**What I'd need from you**: confirm meeting recording is still a shipping feature, and whether the
biometric-consent half (voiceprint storage with a retention worker) is in scope or was an experiment.

### The four other genuine gaps

**`origin/claude/provider-specific-setup-options-0eTN6` — no Anthropic provider.** The spine's
`AiProviderType` is `PiaCloud, OpenAI, AzureOpenAI, Ollama, OpenRouter, OpenAICompatible, Mistral,
VLlm`. No `Anthropic`. The branch adds it (and lacks `VLlm`, so it is not a superset — do not
cherry-pick the enum). Anthropic models are currently only reachable through `OpenAICompatible` or
OpenRouter. The 896-commit-old patch is worth reading as a spec, not applying.
*What I'd need from you*: is a first-class Anthropic provider wanted, or is OpenAI-compatible enough?

**`feature/community-edition-client` — licensing.** `grep -rln "License|Licence" src/` returns
nothing on the spine. The branch adds `LicenseErrorParser` / `LicenseErrorBus` / `LicenseErrorHandler`,
a 402-response path through `AuthService` and `SyncClientService`, a `LicenseErrorViewModel`, and 261
lines of tests. The new files are self-contained and would apply; the three touched existing files
have drifted and would conflict.
*What I'd need from you*: is there still a community-edition / licensed-edition split?

**`feature/right_click` — Explorer integration.** No `src/Pia.ShellExtension` on the spine, and
`App.xaml.cs` has no mutex, named pipe, or `--open-with` handling. The C++ `IExplorerCommand` project,
the sparse-package manifest and the signing scripts are purely additive. The risk sits in the
`App.xaml.cs` single-instance wiring, `scripts/build-velopack.ps1` and the release workflow, all of
which have moved on.
*What I'd need from you*: is "Open with Pia" in Explorer still wanted? It carries a signing-cert
requirement that the other candidates do not.

**`feature/38_brainstorming`.** No `Brainstorm*` anywhere on the spine. A 493-line
`BrainstormToolHandler`, a question-card control, and a 191-line prompt doc. It predates the entire
plugin/tool-gating architecture (`ToolScope`, approval tiers, built-in plugin GUIDs), so the handler
would need re-homing rather than porting.
*What I'd need from you*: is guided brainstorming still on the roadmap, or did personas absorb it?

**`origin/claude/prompt-logging-transparency-AkHvh`** is absent by file existence, but flagging it
rather than recommending it: `PromptLogService` writes full prompts to disk, and the Privacy-First
Logging rule in `CLAUDE.md` names prompts as sensitive and requires `SensitiveDebug`, which is erased
from release IL. A user-visible opt-in prompt log is a defensible product decision — but it is a
decision, not a gap to close by default.

### Stale-but-real, worth reading rather than porting

- **`claude/refine-local-plan-uzT5G`** — the refactor never landed (spine's `SyncClientService.cs`
  has no `Apply*Async` or `BuildPushRequestAsync`; it is 1755 lines). The asset is the test file:
  1595 lines on the branch against 638 on the spine. But sync has been through Phases 1–4 of the
  transfer optimization since April, so those tests target a dead API surface. Mine them for
  *cases* the current suite misses; do not restore the file.
- **`claude/restructure-navigation-pane-a0AuE`** — the spine's `NavigationSidebarView.xaml` is a
  hand-written per-mode layout with no sub-item `ItemsControl`, so the "sub-items in every mode"
  behaviour is genuinely absent. The code targets an April `MainWindowViewModel`. The 5 unpushed
  local commits carry a 337-line plan and a 71-line spec that exist in no other copy — if this
  branch is deleted, extract those two files first.
- **`origin/feature/suggestions`** — the spine already has `AssistantSuggestionsEnabled` and its
  settings UI. The branch's only new content is `AssistantHintsEnabled` (rotating composer watermark
  hints + a "Did you know?" empty-chat tip). Path-scoped two-dot shows the patch would delete 948
  lines from `AssistantViewModel`/`AssistantView`. Re-implement the idea in an hour; do not port it.

### Supersessions, with the check that established each

- **`claude/auto-fill-microsoft-login-name-jVKL0`** — `git cherry` returns `-`. The spine already has
  `UserName = _authService.UserDisplayName;` at `FirstRunWizardViewModel.cs:427`. The path-scoped
  two-dot shows the branch would *remove* the E2EE setup step, the `IPolicyService` login gating and
  EntraID login.
- **`claude/enhance-transcription-service-mPdYr`** — `git merge-base --is-ancestor` confirms every
  one of its 16 commits is contained in `feature/meeting_transscription`. Zero unique work; it can be
  deleted the moment the meeting branch is dispositioned.
- **`origin/managed-personas-dtos`** — path-scoped two-dot on both touched files is empty. Landed via
  the `feature/managed-personas-client` merge at `57d249d0`.
- **`origin/claude/pia-policy-settings-docs-2rlLT`** — the spine implements the same allow-list at
  `PolicyService.cs:96` (`IsLoginProviderAllowed`), keyed on `AllowedSyncProviders` rather than the
  branch's `AllowedLoginProviders`, and `FirstRunWizardViewModel` already exposes
  `IsLocalLoginVisible` / `IsGoogleLoginVisible` / `IsMicrosoftLoginVisible` / `IsEntraIdLoginVisible`.
  Same feature, different setting name.
- **`feature/23_multi_window`** — the spine's `IWindowManagerService` is a mature per-mode API
  (`ShowWindow(WindowMode)`, `HideWindow(WindowMode)`, `IsVisible`, `IsInForeground`,
  `CanDismissWithHotkey`, `ManagedWindow` open/close events). Path-scoped two-dot against the branch
  is net **−389 lines** — applying it would regress. Salvage only:
  `docs/plans/2026-03-13-multi-window-per-mode.md` (847 lines),
  `docs/plans/2026-03-13-token-usage-tracking.md` (934 lines, unrelated content riding along) and
  `docs/test-plans/multi-window-manual-tests.md` exist nowhere else.

### Suggested order

1. Read the consent spec and phase plans off `feature/meeting_transscription`; decide whether the
   consent layer gets ported to `DirectTranscriptionService`. Everything else is small by comparison.
2. Answer the four owner calls (Anthropic provider, community edition, Explorer integration,
   brainstorming) so the branches holding them can be either scheduled or deleted.
3. Whenever deletion comes up in a later round, extract the doc-only artefacts named in this section
   first — several plans and specs have exactly one copy. Not actioned here.

## Dispositions

### `feature/dynamic-schema-ui` + `merge/dynamic-schema-ui` — deleted 2026-08-16

Both branches carry one body of work: a JSON-Schema-driven dynamic form for editing
`MemoryObject.Data`, with a form/JSON toggle in the Memory view. `merge/dynamic-schema-ui` is a
real two-parent merge (`faa73941` + `3d6c1e65`) whose entire content is `feature/dynamic-schema-ui`
merged into an older mainline — it adds no work of its own.

Superseded by the markdown vault. Memory is now markdown files with frontmatter: the Memory view
consumes `VaultMemoryItem` (`path#heading` addressing over `## section`s of `profile`/`contacts`/
`preferences` documents), and `MemoryView.xaml` binds `VaultComposition`/`PiaVaultOverview`. There is
no JSON editor left for a schema-driven form to sit behind. None of the branch's 20 new files
(`JsonSchemaModel`, `JsonSchemaValidator`, `EditableJsonDocument`, `DynamicFormControl`,
`DynamicFieldControl`, `SchemaFieldTemplateSelector`, `SchemaFieldBindingViewModel` and their tests)
exist on the spine, so nothing was partially adopted.

Deleted at `3d6c1e65` and `3906951f`. This also removed the only copy of the 2499-line plan
`docs/plans/2026-03-28-dynamic-schema-ui.md`, which lived solely on the feature branch; recover it
with `git show 3d6c1e65:docs/plans/2026-03-28-dynamic-schema-ui.md` while the reflog holds.

`MemoryObject` with its JSON `Data` string does still exist on the spine — it is not what the Memory
view renders, but it has not been deleted either, so "the JSON memory model is gone" is not quite
true even though the JSON *editing surface* is.
