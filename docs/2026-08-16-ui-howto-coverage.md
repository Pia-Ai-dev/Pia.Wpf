# UI how-to questions → docs coverage

Answers `docs/user_questions/2026-08-16-ui-howto-questions.md` against the docs in `Pia/src/Pia.Docs`
(branch `feature/connector-abstraction-phase1`). English only; `de/` and `fr/` mirrors are owed.

No FAQ entries were added, and no question-shaped headings either. Every answer sits in the guide
that owns the surface, as ordinary prose under a declarative topic heading — so the server's
knowledge base indexes it as content rather than as a Q&A pair.

**English-only is safe for the KB.** `src/Pia.Docs/scripts/build-kb.mjs` filters the corpus to the
root locale (`.filter((p) => !/^(de|fr)\//.test(p))`, manifest `locale: 'en'`), so the stale German
and French mirrors never reach the knowledge base and cannot contradict the corrected English pages
inside the index. They are website translation debt, not a retrieval hazard.

**Verified with `npm run build:kb:check`** — the same script that produces the KB preset. It resolves
every internal link and anchor across the corpus: *67 documents, check passed*. `npm run build` also
completes clean (208 pages).

**Legend** — ✅ written this pass · ○ already covered · ⚠ corrected an existing claim

---

## Chat composer (Assistant view)

| Question | Where it's answered |
|---|---|
| Chat vs Agent toggle, what changes | ○ `assistant.mdx` § Give It a Goal Instead of a Message; `agent-runs.mdx` § Switch to Agent Mode |
| Switch a conversation Chat→Agent halfway | ✅ `assistant.mdx` § Switching between Chat and Agent mid-conversation |
| Does the persona dropdown change wording or the model | ○ `personas.mdx` § What a persona changes |
| What the coloured dot on the persona pill means | ✅ `personas.mdx` § Emoji, accent colour, and the coloured dot |
| What the eraser button clears | ✅ `assistant.mdx` § What the eraser button actually clears |
| Record vs Live transcription | ✅ `assistant.mdx` § Record versus the two transcription buttons |
| Join a meeting vs Live transcription with consent | ✅ same section |
| File types and size limit | ○ types / ✅ `attachments.mdx` § How large a file can be |
| Does an attached file reach the AI provider | ✅ `attachments.mdx` § What an attachment sends to your AI provider |
| Multi-line message without submitting | ✅ `assistant.mdx` § Writing more than one line |
| What typing `@` does | ✅ `assistant.mdx` § Referencing your own items with `@`; ○ `memory.mdx` § @ Commands |
| Chat title with auto-titling off; can I rename | ⚠ `assistant.mdx` § How a chat gets its title |
| Running badge; can I keep typing | ✅ `assistant.mdx` § While a chat is still working |
| Stop a reply that's generating | ✅ `assistant.mdx` § Stopping a reply that's already generating |
| Chat chip vs Chat history | ✅ `assistant.mdx` § Chat chip or Chat history — which one to use |

## Assistant view — general

| Question | Where it's answered |
|---|---|
| The notification bell with a number | ✅ `interface.mdx` § The bell on the right edge |
| The panel that slides out from the right | ✅ same; ○ `flow.mdx` |
| Open new window; two personas at once | ✅ `interface.mdx` § What a second window gives you |
| Is "Light" a theme switch; system-follow | ✅ `interface.mdx` § The theme control shows the theme you're on |
| What the encryption banner blocks | ✅ `interface.mdx` § The encryption bar across the top; ○ `cloud-sync.mdx` |
| Can I dismiss the banner permanently | ✅ same section |

## Personas

| Question | Where it's answered |
|---|---|
| Where to add a persona | ○ `personas.mdx` § Creating a persona |
| Why some personas only offer Duplicate | ✅ § Why some personas only offer Duplicate |
| Edit or delete a built-in | ✅ same section |
| Get a built-in back after changing it | ✅ § Getting a built-in persona back |
| Share, export, or move a persona | ✅ § Moving a persona to another machine |
| Reorder the grid; default persona for new chats | ✅ § Ordering the grid, and the persona new chats start with |
| What happens if I leave everything but Name/System Prompt blank | ✅ § What a persona actually requires |
| System Prompt vs Guardrails vs Output Format | ✅ § System Prompt, Guardrails and Output Format — how the three fit together |
| Assembly order; can a Guardrail override | ✅ same section |
| Is "Describe this persona" saved | ✅ § What Draft with AI does |
| Draft with AI: what it fills, overwrite, which provider pays | ✅ same section |
| What Archetype changes | ✅ § Fields that don't change how Pia replies |
| Model type vs Preferred Provider | ✅ same section |
| Persona Tool Access vs Settings → Tool access | ✅ § Tool Access on a persona, and the Tool access settings page |
| Is a persona the intended way to give fewer tools | ✅ same section |
| What Expertise is used for | ✅ § Fields that don't change how Pia replies |
| Reasoning Effort cost; unsupported provider | ✅ § Preferred Provider and Reasoning Effort |
| Preferred Provider vs "Same for all modes" | ✅ same section |
| Emoji / Accent Colour; why mine shows neither | ✅ § Emoji, accent colour, and the coloured dot |
| Is the dot decorative | ✅ same section |
| Validation or preview before save | ✅ § What a persona actually requires |
| Test a persona without making it active | ✅ § Trying a persona out |
| Where a new persona shows up | ○ § Choosing a persona in chat |
| Does switching re-run earlier turns | ○ same section (forward only) |
| Do personas sync; encryption first | ✅ § Persona sync and encryption |

## Optimize templates

| Question | Where it's answered |
|---|---|
| Where to create a rewrite template | ✅ `templates.mdx` § Where optimize templates live |
| What Optimize is vs asking Pia in a chat | ✅ `optimize.mdx` § How Optimize differs from asking Pia in a chat |
| Style Description vs Generated Prompt — which is sent | ✅ `templates.mdx` § Which box is actually sent to the model |
| What Generate Prompt does; provider; token cost | ✅ § What Generate Prompt costs |
| Must I press Generate Prompt | ✅ § Writing the prompt yourself instead |
| Which fields are genuinely required | ✅ § Which fields are genuinely required |
| Why my template card has no description line | ✅ § Why your own template card has no description line |
| What Set Default does and where it's used | ✅ § Setting the default optimize template |
| Which template the hotkey uses with no default | ✅ same section; ○ `optimize.mdx` § Templates Shape the Result |
| What View Prompt shows; copy a built-in's prompt | ✅ § Reading a template's prompt |
| What Output Action controls | ✅ `optimize.mdx` § What Accept does, and where the result can go |
| Use optimize templates inside an Assistant chat | ✅ `templates.mdx` intro |
| Do templates sync | ✅ § Optimize template sync between devices |
| Reorder or group templates | ✅ § Ordering and grouping optimize templates |

## Memory / vault

| Question | Where it's answered |
|---|---|
| "memory" vs "vault" vs Assistant files folder | ✅ `memory.mdx` § Memory, vault, objects — the words Pia uses |
| What an "object" is; is 48 a lot; prune | ✅ § Vault size, and when to prune |
| Add or edit an entry from this view | ✅ § Adding a memory yourself; ○ § Editing a memory |
| Delete one thing without wiping everything | ○ § Editing a memory |
| Where the categories come from | ✅ § Where the categories come from |
| PROFILE / PREFERENCE / NOTE tags | ✅ § The PROFILE, PREFERENCE and NOTE tags on entries |
| Is the composition bar telling me to act | ✅ § Reading the composition bar |
| What Source documents are; how a file gets in | ○ § Vault at a glance; `auto-ingest.mdx` § Adding documents |
| What "Compiled into N topic page(s)" means | ✅ § What "Compiled into 31 topic page(s)" means |
| Edit a source file in Explorer — does Pia notice | ✅ `auto-ingest.mdx` § Editing a source file outside Pia |
| Regenerate Embeddings — what, how long, when | ✅ `memory.mdx` § Regenerate Embeddings |
| What the embedding model is for; changing it | ✅ § The embedding model; § If the embedding model changes |
| The header toolbar buttons | ✅ § Browsing your memories (toolbar table) |
| Does anything reach the provider; banner effect | ✅ § What leaves your device; § Storage and sync |

## Tool approval

| Question | Where it's answered |
|---|---|
| Allow once vs this session vs Always; how long each lasts | ○ `tool-permissions.mdx` § Answering an Action Card |
| What Always allow covers | ✅ § What "Always allow" actually covers |
| What the chevron expands to; seeing exact arguments | ✅ § Seeing exactly what Pia is about to do |
| If I Decline, does the turn stop | ✅ § What happens when you Decline |
| Leaving an approval unanswered | ✅ § Don't leave an approval waiting |
| Getting back to a waiting chat from anywhere | ✅ § Finding a chat that's waiting for you |
| Do I get notified | ✅ same section |
| Why some tools ran without asking | ✅ § Why some tools ran without asking |
| Where I see what I've approved; revoking | ○ § The Settings Page |
| Persona Tool Access vs Settings → Tool access | ✅ `personas.mdx` § Tool Access on a persona…; ○ `tool-permissions.mdx` § Where Else Permissions Come From |

## Agent runs, Meeting, other shell surfaces

| Question | Where it's answered |
|---|---|
| Agent run vs the composer's Agent toggle | ○ `agent-runs.mdx` intro + § Switch to Agent Mode |
| Where to watch a run; closing the window mid-run | ○ § The Run Panel / ✅ § Closing the window while a run is going |
| What Beta means for my data | ✅ `meetings.mdx` § What "Beta" means for your data here |
| Do meeting participants get told | ✅ `meetings.mdx` § What the other participants see |
| Chat history / Reminders / Todo — create or read-only | ✅ `interface.mdx` § Which screens let you create things |
| What Account is for if I'm not signed in | ✅ `cloud-sync.mdx` § What the Account screen is for before you sign in |
| What Plugins are; can I add one not in the list | ○ `plugins.mdx` intro |

---

## Corrections made to existing docs

1. **`templates.mdx` documented a feature that does not exist.** It described "conversation
   templates" with *name / description / prompt*, "applied automatically whenever you start a new
   conversation". The real feature is **Optimize** templates (`OptimizationTemplate`,
   `TemplateService`), with *Template Name / Style Description / Generate Prompt / Generated Prompt*,
   used only by Optimize mode. A vector search for "how do I create a template" would have returned a
   confidently wrong nav path and workflow. Rewritten end to end.

2. **`assistant.mdx` claimed chats can be renamed.** There is no rename command — not on the chat
   chip (`ChatTitleChipViewModel` exposes Resume / Delete / New / Show all / quick-switcher) and not
   in Chat history. `RenameChatAsync` in `ChatSessionManager` is the internal auto-title path.
   Replaced with what actually happens: a title derived locally from the first message, optionally
   upgraded by auto-titling, and not hand-editable.

3. **`personas.mdx` "Next steps" pointed at Templates** as the way to "give every new conversation a
   head start" — a leftover from the same mix-up. Repointed at Tool Permissions, Agent Runs, and
   Templates described correctly as the Optimize-side equivalent.

4. **`memory.mdx` said only conversations and todos sync.** Memory syncs too (`SyncMapper`
   `ToSyncMemory` / `ToVaultSyncMemory`). Corrected, with the encryption-banner dependency stated.

## Facts worth keeping (verified in code, not observed in the UI)

- **`TemplateEditModel.CanSave => Name && GeneratedPrompt`.** Style Description is labelled `*` but
  is *not* enforced — you can save without it if you typed the prompt yourself. This requirement is
  the same in both the shipped and the unreleased build; only the way it is *enforced* differs (see
  the version note below), so the guide states the requirement and describes both symptoms.
- **`PersonaEditModel.CanSave => Name && SystemPrompt`.** Everything else is optional.
- **Identity block order** is `SystemPrompt` → `Guardrails` → date line (`PersonaPromptShape`), with
  Output Format in a separate later section.
- **Archetype has no runtime consumer.** Persisted, synced, guessed by Draft-with-AI, never sent to
  the model.
- **Expertise has exactly one consumer**: `AgentPlanner.Describe`, as the roster descriptor when a
  persona has no Tagline.
- **Model type** is relayed as `metadata.pia_persona_type` to Pia Cloud only; ignored by every other
  provider.
- **Draft with AI fills blanks only** — it never overwrites typed input — and uses the dialog's own
  Preferred Provider, falling back to the Assistant-mode default.
- **Provider timeout defaults to 300 s**, which is the wall a pending approval dies against.
- **Attachment limit is ~1 MB of extracted text** (`DroppedFileReader.MaxTextBytes`).
- **The theme control cycles System → Dark → Light**; the label is the current theme.
- **The active persona is stored per `WindowMode`**, so two Assistant windows share one persona.

## Open items

- **The CanSave gating is not in a shipped build.** `86c43cb` / `86530d67` (Save gated on `CanSave`,
  the inline *"Fill in the fields marked \* to enable Save"* hint, and the corrected `*` markers) are
  on `feature/ui-automation-a11y` only — not on `main`, and carried by no tag. The latest tag is
  `v1.3.389`, so users today still get the old stacking `MessageBox`. The template and persona guides
  are therefore written to state the *requirement* rather than the mechanism, and name both symptoms.
  Once the branch ships, those two paragraphs can be simplified to just the greyed-out Save.

- **`de/` and `fr/` mirrors are not updated.** Every page touched here has a translation that now
  lags. This was a deliberate scope call for this session, and it does not affect the knowledge base
  (see the English-only note at the top) — but the published site now shows a German and French
  `templates.mdx` describing a feature that does not exist. That is the highest-value translation to
  port first, followed by the chat-rename correction in `assistant.mdx`.
- **Two code bugs found while verifying** — reported separately, not documented as behaviour:
  - `Memory_Meta_ModelDefault` is the hardcoded literal `text-embedding-3-small` in all three resx
    files, but the embedding model is the local ONNX
    `paraphrase-multilingual-MiniLM-L12-v2` (384-dim). The footer names the wrong model.
  - `Dialog_TemplateEdit_StyleDescription` carries a `*` while `CanSave` does not require it, so the
    Optimize template dialog marks a field required that isn't.
- **The `@` picker was never confirmed under a focused window** (questions doc note 2). It is
  documented because it is wired up (`AtCommandAutocompleteBehavior`) and `@Memory` / `@Todo` /
  `@Reminder` were already documented; the walkthrough failure matched the unfocused-window
  automation artifact. A human check is still worth doing.
