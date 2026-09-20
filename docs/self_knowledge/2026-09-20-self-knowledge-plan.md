# Pia answers questions about Pia

**Status:** shipped and live-validated on a real profile; one contrast arm outstanding. **Owner:** Marco Altmann.
**Written:** 2026-09-20.
**Origin:** users asking the in-app assistant *"can I perform agentic tasks?"*, *"can you change the
way you answer?"*, *"how do I change the TTS output language?"* and getting a web search or an
invention.

---

## 1. The gap

The answers already existed and were good. `../Pia/src/Pia.Docs` holds 36 English `wpf/**` pages
covering every one of those questions — `guides/agent-runs.mdx § Switch to Agent Mode`,
`guides/personas.mdx § System Prompt, Guardrails and Output Format`, `guides/speech.mdx § Selecting
a Voice`. They are indexed into the **server** knowledge base.

That knowledge base is PiaCloud-only, and the client only forwards an `X-Pia-Persona` header
(`src/Pia.Wpf/Services/PiaCloudChatClient.cs:35,72`) so the server can union a persona's KBs. A grep
for `KnowledgeBase|knowledge|kb_` across `src/` returns nothing. **On OpenRouter, DeepSeek, a local
model, or offline, the desktop chat had no path to the docs at all.**

Docs alone would not have been enough either. They describe a build, not *this* install — and one of
the three questions is a trap they can only half-answer:

- **There is no TTS language setting.** Language is a property of the chosen voice
  (`Services/Tts/TtsVoiceCatalog.cs:41-50`: Amy/Alba EN, Thorsten/Eva/Ramona DE, Siwis/UPMC FR).
- `AppSettings.TargetLanguage` is **Optimize-mode only**, and `TargetSpeechLanguage` is speech
  *recognition*. A model reasoning from property names picks the wrong one with confidence.
- The settings path a user must follow is rendered in **their** interface language, which an English
  corpus cannot give them.

## 2. What shipped

Two read-only tools in a new built-in pack, plus the routing that makes the model reach for them.

| Tool | Arguments | Returns |
|---|---|---|
| `pia_help` | `query?` | Top 5 guide sections: page title, heading path, snippet, reference, docs URL |
| | `reference?` | That section in full (or the whole page for a bare `guides/speech`) |
| | neither | The table of contents |
| `pia_settings` | `area?` | Current values plus the **localized** path to change each one |

### 2.1 The corpus

`scripts/Update-HelpCorpus.ps1` runs the docs repo's own `build-kb.mjs --no-zip` — which already
flattens Starlight MDX components, strips front matter and rewrites links to absolute
`docs.pia-ai.de` URLs — takes the `product: wpf` documents out of its manifest, and writes
`src/Pia.Wpf/Resources/Help/help-corpus.json.gz`. One `EmbeddedResource`, one `LogicalName`
(`Pia.HelpCorpus`), mirroring `Pia.DefaultFileIgnore`.

**36 pages, 235 KB raw, 77 KB gzipped.** Decompressed lazily on the first `pia_help` call, never at
startup. No `server/**` admin docs; no `de/`/`fr/` mirrors (they are stale, and `build-kb.mjs`
filters them out anyway).

The script's `-Check` mode regenerates to memory and compares the **pages**, not the whole file:
`sourceCommit` moves on every unrelated docs commit, and a check that cries drift over a `server/**`
edit is a check nobody runs. CI cannot run it at all — the docs repo is a separate checkout that is
not on the build agent — so refreshing is a manual step, driven by the
`help-corpus` skill before a push to `main`.

That sibling checkout is shared and moves under you: it advanced twice during the session that built
this. `-Check` names both the snapshot commit and the current one, so a stale snapshot is visible
even when the desktop guide itself has not changed.

### 2.2 Search

`HelpSearchService` splits each page on `##`/`###` into sections and indexes them into an in-memory
SQLite FTS5 table. FTS5 is already used in this app (`Infrastructure/SqliteContext.cs:1221,1257,1294`)
and `Microsoft.Data.Sqlite` is already referenced, so this adds no dependency. Roughly 250 sections;
the connection is held open for the life of the singleton, because closing it drops the database.

That one connection serves every window and every background run, and a `SqliteConnection` does not
support two concurrent commands, so `Search` takes a gate. `HelpSearchConcurrencyTests` is a smoke
check rather than a reproduction — measured, it passes with the gate removed, because a 250-row
in-memory table answers too fast to lose the race reliably. The gate is there on the contract, not
on the evidence of that test.

Three things decide whether it retrieves anything. The first two were measured; the third is on the
contract:

- **Sanitizing.** A model-supplied string goes straight into `MATCH`, where a colon, a quote, a bare
  `*` or an uppercase `OR` is a syntax error. `AssistantChatService`'s existing token sanitizer was
  extracted to `Services/Search/FtsQueryBuilder.cs` and is now shared. Chat keeps its whitespace
  split and implicit AND (right for typeahead); help splits on **every** non-alphanumeric, so
  `text-to-speech:` becomes three searchable words.
- **OR, not AND.** A four-word question ANDed against a 250-section corpus returns nothing. Help
  ORs its terms and ranks with `bm25`, weighting title 12 and heading 6 against body 1.
- **Stemming plus an alias table.** `tokenize='porter unicode61'` is set for *answers*→*answer*; that
  one was reasoned, not A/B'd against a no-stemming index. The alias table was measured. Prefix
  matching does not close the gap in the other direction — `agentic*` never reaches *agent* — so
  ~20 terms users actually type are mapped by hand, and English question scaffolding (*can, how,
  what, where, you, my, happens*) is dropped as stop words. Before the stop-word list, *"can you
  change the way you answer"* returned three tool-permissions sections; after it, the personas page
  is rank 1.

**No embeddings.** `EmbeddingService` caps at 128 tokens and its model is *downloaded on demand*
(`IsModelAvailable` may be false), so a retrieval path depending on it would fail for exactly the
new user most likely to ask these questions.

The corpus is English, and the tool description says so: the model passes English keywords, and the
`## Language` section pushes the answer back into German or French.

### 2.3 Settings

`HelpSettingsResolver` reports nine areas — `language`, `speech`, `assistant`, `agent`, `providers`,
`personas`, `meetings`, `sync`, `tools` — each row carrying a value **and** a path rooted at
`Nav_Settings` and resolved through `ILocalizationService`, so it reads as the user's own labels.

Row **labels** are English on purpose: the model reads them and answers in the user's language, so
localizing them would have bought nothing and cost ~20 resx keys in three files. Only the path
needs translating, and every key it uses already existed.

Never returned, because these values go to the AI provider: API keys, auth/refresh tokens, account
email, device id, E2EE key material and recovery state, private keywords. The server **host** is
returned (it answers "am I on my company server?"); the path and port are not. The sandbox folder
path already rides `## Environment` on every turn, so it is not new exposure.

### 2.4 Routing

Two carriers, deliberately:

1. **The plugin's `systemPromptAddition`**, which survives an @-command turn where `## Tool
   Selection` is skipped entirely.
2. **An unnumbered lead-in above the tool tree.** It has to land first: the tree's terminal branch
   is *"NO → Respond conversationally without tools"*, which actively steers away from asking.
   Inserting a renumbered step 1 instead would have broken step 3's "= step 5" cross-reference,
   `AssistantPromptComposerWebSearchTests.cs:100` (which asserts the literal `"- NO → Continue to
   step 6."`) and `ToolPipelineTestBase.cs:55`'s own copy of the tree.

The lead-in is conditional on `pia_help` actually being in the turn's tool list, so a user who
disables the pack is never told to call a tool that is not there. `PrepareTurn` now resolves the
tool list before composing the prompt to make that check possible.

### 2.5 Cost, measured

| Added | chars |
|---|---|
| `pia_help` schema + description | 521 |
| `pia_settings` schema + description | 443 |
| Plugin `systemPromptAddition` | 366 |
| Tool-tree lead-in | 175 |
| **Total, every turn** | **1505 ≈ 376 tokens** |

Against a system prompt of roughly 14 k characters (~3.5 k tokens) — of which the `## Plugins`
section alone is ~10.5 k — plus some 52 tool schemas. Everything else (the corpus, the index, the
settings table) is paid only when a tool is actually called.
`HelpPluginRegistrationTests.TheRoutingTextStaysSmallEnoughToCarryEveryTurn` caps both prompt
fragments so this cannot grow back into the capability dump it replaces.

## 3. Gating: nothing to declare

Both tools return `(result, null)`, which is the entire read/write distinction at runtime:
`ChatSession.cs:1235-1236` and `BackgroundAssistantTurnRunner.cs:497-498` return **before**
`ToolAutonomy.Resolve` is ever reached. So there is no `ToolClass` member, no `ToolClassifier` arm,
no autonomy-policy entry, no action card and no gate change. `ToolClassifierTests` passing unchanged
is the proof. Both names were added to `ToolPermissionService.ReadOnlyBuiltInTools` so the grant UI
does not offer a permission that authorizes nothing.

The names are safe against `IsDeleteLike`'s substring match over
`delete|remove|purge|drop|wipe|erase|destroy|truncate`, and the `pia_` prefix keeps them unique
against MCP tools, whose routing is last-wins with no collision detection
(`PluginService.cs:907`).

## 4. Where this deliberately does not reach

A persona with `ToolScope.None`, or a provider flagged "no tool calling", takes
`BuildSystemPromptNoTools` (`AssistantPromptComposer.cs:409`), which emits no `## Plugins` section
and no tools at all. Those users keep the old behaviour. Closing that would mean stuffing capability
prose into the prompt for everyone, which is the thing the token budget rules out.

`PersonaToolScope.ReadOnly` does **not** filter — it is "reserved — treated as `Full` in v1"
(`Models/PersonaToolScope.cs:11`) — so only `None` suppresses the pack.

## 5. One thing that had to change design mid-flight

`HelpSettingsResolver` originally took `IPluginService` to list the user's local MCP servers by
name. That closes a DI cycle — `PluginService` → `IHelpToolHandler` → `HelpSettingsResolver` →
`IPluginService` — and `BootstrapperGraphValidationTests` caught it by building the real graph with
`ValidateOnBuild`. `PluginService` owns that state and already depends on the help handler, so the
dependency was dropped and the row now names only the path, which is the part a user needs anyway.

## 6. Files

**New**

| Path | Role |
|---|---|
| `scripts/Update-HelpCorpus.ps1` | Refresh the snapshot from the docs repo |
| `src/Pia.Wpf/Resources/Help/help-corpus.json.gz` | The corpus (generated) |
| `src/Pia.Wpf/Services/Help/HelpSearchService.cs` | Load, index, search, read |
| `src/Pia.Wpf/Services/Help/HelpSectionParser.cs` | Heading split, fence-aware, unique refs |
| `src/Pia.Wpf/Services/Help/HelpSettingsResolver.cs` | The nine areas and their localized paths |
| `src/Pia.Wpf/Services/Help/HelpCorpusModels.cs` | `HelpPage` / `HelpSection` / `HelpHit` / `HelpSettingRow` |
| `src/Pia.Wpf/Services/HelpToolHandler.cs` | The two tools and their result formatting |
| `src/Pia.Wpf/Services/Interfaces/IHelpToolHandler.cs` | |
| `src/Pia.Wpf/Services/Search/FtsQueryBuilder.cs` | Shared FTS5 query construction |

**Modified:** `Pia.Wpf.csproj` (the resource), `Bootstrapper.cs` (three registrations),
`BuiltInPluginDefaults.cs` (GUID `…00C`, preloaded set, defaults entry, class summary),
`BuiltInPluginHandler.cs` (`FromHelpHandler`), `PluginService.cs` (ctor + `handlerId` arm),
`ToolPermissionService.cs` (read-only names), `AssistantPromptComposer.cs` (lead-in + tool-list
ordering), `AssistantChatService.cs` (uses the extracted query builder).

**Test ripple:** the `PluginService` ctor parameter broke five construction sites under
`tests/Pia.Wpf.Tests/Services/`.

## 7. Verification

- **Gate:** the built exe, no filter — **7736 tests, Failed: 0**.
- **Zero warnings:** `dotnet build -t:Rebuild` clean in Debug and Release.
- **Retrieval:** `HelpSearchTests` pins eight real user questions to the page that answers them,
  plus punctuation cases that would be an FTS5 syntax error unsanitized.
- **Cost:** measured, not estimated — §2.5.
- **Live:** all three questions answered from the tools on a real, server-synced profile that
  predates the pack, with no `web_search` in any turn — see
  [`2026-09-20-live-validation.md`](2026-09-20-live-validation.md). The German answer quoted
  *Einstellungen > Allgemein > Sprache > Sprachausgabe > Stimmauswahl*, which matches
  `ViewStrings.de.resx` key for key, so the path came from the resx rather than from the model
  translating an English one — that is §2.3 working end to end.
- **Still outstanding:** the pack-disabled contrast arm. It was deliberately not run on production
  data, because toggling the pack writes a synced plugin preference. It is safe on a throwaway
  profile: point `PIA_DATA_DIR` / `PIA_LOCAL_DATA_DIR` at scratch directories with a dummy provider,
  where a preference push goes nowhere.

## 8. Follow-ups

- **Corpus staleness.** The `help-corpus` skill refreshes it before a push to `main`. Every hit
  carries its `docs.pia-ai.de` URL so a user can check the live page, and the result footer tells
  the model to say so when the user's screen disagrees.
- **A Pia.Docs page describing this feature**, which would then answer questions about itself.
- **PII tokenization cosmetics.** With tokenization on, a `yyyy-MM-dd` in any tool result becomes
  `[Phone_N]`, and the corpus contains example paths like `sources/meeting-notes-2026-08-11.txt`. A
  display wart in an example filename, not a correctness problem.
- The parked guided-tour track ([`../guided_tour/2026-08-24-d-track-parked.md`](../guided_tour/2026-08-24-d-track-parked.md))
  is the natural later consumer: its §6.4 names narration quality as the part it does not own, and
  this is that part. Leave it parked.
