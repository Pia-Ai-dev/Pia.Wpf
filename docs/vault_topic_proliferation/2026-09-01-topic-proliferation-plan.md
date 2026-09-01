# Curbing vault topic proliferation

**Status:** Steps 0–3 and 5 implemented; steps 4 and 6 open
**Owner:** Marco Altmann
**Written:** 2026-09-01
**Origin:** A user report that ingest creates far more topic pages than expected on live systems —
cluttering the Vault view and forcing many `browse_index` → `read_topic` calls — plus a
frontmatter-corruption bug found in a separate session. Supersedes the "Not in scope" entry in
[../vault_page_templates/2026-08-31-page-templates-plan.md](../vault_page_templates/2026-08-31-page-templates-plan.md).

## The evidence

A real vault's `memory/topics/` held 45 pages, and their names name the causes:

| Cause | Evidence | Weight |
|---|---|---|
| No notability gate | A finance source produced a page per named entity — `apple`, `google`, `microsoft`, `meta`, `nvidia`, `tesla`, `bmw`, `broadcom`, `sap`, `tiktok`, `vmware` — plus 8 market indices and 12 people, including the user's own name | Dominant |
| Alias splitting | `azure-openai`/`azure-openai-service`, `dax`/`dax-40`, `dow-jones`/`dow-jones-industrial-average`, `meta`/`meta-platforms`, `nasdaq`/`nasdaq-100`, `s-4hana`/`sap-s-4hana` | ~12% of pages |
| English-only exclusions | `kurs-gewinn-verhaltnis`, `marktkapitalisierung` are exactly the generic-term class the prompt banned — with only English examples | Real, cheap |
| Charter absent | `VaultCharterService` reads only `memory/charter.md`; nothing seeds or surfaces it, so `charterBlock` was empty and notability ran ungrounded | Enabler |
| No garbage collection | `LintService` implements duplicate-merge and orphan detection and has **no caller** (registered `Bootstrapper.cs`, invoked nowhere) | Cleanup only |
| Poisoned frontmatter | `title: {"subject": "Ilka Brenner", "category": "person"},` — invalid YAML, 10 pages from one `create_source` | Correctness bug |

Retrieval was a separate cost: `BrowseEntry` was `(Title, Ref)` with no summary, so the model had
to `read_topic` its way through the map.

## What shipped

### Step 0 + 5 — the frontmatter bug (`d2bfdf5c`)

Worse than "the pages stay unindexed": `MarkdownVaultParser.ParseFrontmatter` let the
`YamlException` out and `VaultStore.ReadAsync` did not catch it, so it reached every caller. Ingest
reads a page before rewriting it, so the write that would have repaired the page never ran and the
whole source failed to re-ingest at every startup.

- `VaultYaml.EncodeScalar` now encodes `title:`/`category:` in both `VaultFrontmatter` builders,
  `VaultSchemaService`, and `VaultIndexService`'s unknown-key passthrough (which re-emitted parsed,
  unquoted values verbatim). Hand-rolled: YamlDotNet's serializer emits *documents* and prefixes
  `--- ''` on values it cannot write plainly, which would end the frontmatter block early — the
  round-trip test caught that.
- `ExtractJsonArray` spanned first `[` to last `]`, so an echoed `[Person_1]` swallowed the array
  and dropped the run into the line fallback. It now walks balanced spans and takes the first that
  parses.
- The line fallback turned every non-empty line into a topic. Now gated to short, unpunctuated,
  non-structural lines and capped.
- `ParseFrontmatter` degrades to an empty map; `VaultRepairService` archives still-unparseable topic
  pages to `memory/.archive/` (never deletes — they may hold a manual preamble) and clears their
  ingest state so reconcile re-synthesizes them.

### Step 1 — notability (`6f1e7eb3`)

A substance bar ("merely mentioned, listed, quoted or named in passing does not earn a page"), a
language-neutral exclusion class, and `AppSettings.MaxTopicsPerSource` (default 8, JSON-only,
clamped 1–50 on read) both stated in the prompt and enforced on the returned list.

Two limits worth knowing:

- The ceiling is enforced **in the extractor**, so it bounds what one model call may yield rather
  than the distinct-page count. Step 2's collapse runs after and reduces below the ceiling rather
  than refilling it. Deliberate — erring low is the right direction for an over-creation complaint —
  but if a model routinely over-returns, move the clamp to `prepared` in `IngestService` (which
  already holds `ISettingsService`) so it counts distinct topics.
- The source is still truncated at `MaxSourceChars`, so "most central first" is a ranking over a
  prefix on a long document.

**Not silent-safe.** On re-ingest, `AutoIngestService` diffs the touched set and
`RemoveContributionsAsync` deletes a page whose `sources:` empties. So the first time a source is
touched under the cap, its surplus pages disappear without passing through step 6's confirm dialog.
That is the cheapest cleanup available, and it is also a deletion path the user did not explicitly
opt into — mention it in the release note.

### Step 2 — alias collapse (`1f32713e`)

`TopicIdentity.Canonicalize` reduces a subject to a matching key (parentheticals and a leading
article dropped, slugified, trailing form-words and a trailing bare number stripped). Matching only —
`VaultSlug` stays the sole source of filenames.

`IngestService` builds an identity map over the pages on disk and resolves each subject through it.
**Precedence is fixed in the map, not at the call site**: `EnumerateAsync` promises no ordering, so
an unspecified rule would send the same subject to different pages on different runs. Oldest
`created` wins, then the smallest slug. Collapse only on an exact canonical match, never fuzzy — a
wrong merge is worse than a duplicate page.

Also closes a latent same-path race: `prepared` was not deduplicated, so two subjects slugifying
identically produced two concurrent `SynthesizePageAsync` calls writing one file.

### Step 3 — a triageable `browse_index`

`BrowseEntry` gained a `Summary`, sourced from `VaultMemoryItem.Gist` — the first prose line,
skipping frontmatter fences, HTML comments, headings and `- key: value` field bullets. The bullet
skip is load-bearing: a templated `person` page opens with its field list, so the naive first line
was `- personnel number: 4711`, which is neither a summary nor something to put in a map the model
reads wholesale.

Not sourced from `index.md` — `BrowseIndexResult`'s contract is "built programmatically", so the map
must not depend on index freshness.

**Known limit:** a page split into `##` sections has no item carrying its preamble, so its summary
comes from the first *section*. Topic templates steer to bullets over headings, so this is the
exception; pinned by a test so it stays a known cost.

Also capped the known-slug list in `AiIngestSynthesisService.BuildLinkInstruction`, which inlined
every slug in the vault into every synthesis prompt.

## Still open

### Step 4 — surface `memory/charter.md` in the Vault view

*Effort:* M · *Value:* High

An editable "What is this knowledge base about?" card on the Vault Overview, writing
`memory/charter.md` through `IVaultStore`, with an empty-state prompt explaining that it decides
which topics earn a page.

- **Do not auto-seed charter content.** `GetCharterAsync` returns the body verbatim into the prompt,
  so filler text becomes filler grounding.
- **Do not offer "Rebuild all pages" on save.** `RebuildPageAsync` re-synthesizes from a page's
  recorded `sources:` and never calls `DiscoverTopicsAsync`, so rebuild-all would spend one LLM call
  per page and change the topic count by zero. Either say plainly that the charter applies to
  sources ingested from now on (recommended), or add a distinct "re-ingest all sources" action —
  which must clear `IngestStateStore` for those refs first, or the hash gate no-ops every one.
- New `AutomationProperties.AutomationId`s, with the `ViewAutomationIdTests` `[InlineData]` row in
  the same change.

### Step 6 — on-demand cleanup

*Effort:* M · *Value:* High · *Deps:* step 2's matcher

`LintService` already does duplicate detection (body-embedding cosine ≥ `DuplicateThreshold`),
orphan, stale-source and missing-xref checks — and `RunAsync` has no caller.

Its "merge" is **archive-only** and is not safe to expose as-is: it does not union the loser's
`sources:` into the keeper (so the next re-ingest recreates the page), does not call
`VaultIndexService.RemoveEntryAsync`, does not retarget `[[topics/<loser>]]` links, and does not
update `IngestStateStore`'s touched set. Make it a real merge first — union `sources:` →
`RebuildPageAsync` the keeper → remove the loser's index entry → run `WikiLinkReconciler` — then
surface it as a Vault Overview button that shows a dry-run report and applies only on confirm.

## Verifying on a live vault

```powershell
(Get-ChildItem "<vault>\memory\topics\*.md").Count
(Get-ChildItem "<vault>\sources\*" -Recurse -File).Count   # sizes MaxTopicsPerSource
Select-String -Path "<vault>\memory\topics\*.md" -Pattern '^category:' |
  Group-Object { $_.Line } | Sort-Object Count -Descending
Test-Path "<vault>\memory\charter.md"
Select-String -Path "<vault>\memory\topics\*.md" -Pattern '^title:\s*[\{\[]'   # poisoned pages
```

Then, against a throwaway `PIA_DATA_DIR` profile, re-drop the source that produced the finance
pages and compare the count and titles; and in a chat, ask a question answerable from one topic page
and count tool calls before vs. after step 3.
