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

### Measured baseline

Not inferred — run on 2026-09-01 against a throwaway profile seeded with copies of a live vault's
6 `sources/` files, DeepSeek (`deepseek/deepseek-v4-flash`) as the Assistant provider, on the
pre-change build (`32b9edc4`):

| | before (`32b9edc4`) | after (steps 0–3, 5) |
|---|---|---|
| topic pages on disk | **85** | **13** |
| topics discovered | 89 | 13 |
| pages with an unparseable `title:` | **11** | **0** |
| sources ingested | 5 ok, 2 transient failures | 6 ok, 0 failures |

Per source (topics discovered):

| source | before | after |
|---|---|---|
| `business plan.md` (30 KB) | 32 | 7 |
| `meeting-20260825-0930…` (35 KB) | 20 | **0** |
| `meeting-20260825-0900…` (8 KB) | 13 | 5 |
| `dotnet_planning_skill.txt` | 11 | **0** |
| `brainstorming-system-prompts.md` | 7 | 1 |
| `meeting-20260822-1529…` (5 KB) | 6 | **0** |

**Which change did the work — don't read 85 → 13 as one number:**

- **Step 0 removed the 11 poisoned pages outright** (13% of the before count) plus the rest of the
  parse debris on that run — `c.md`, `net.md`, `section.md`, `v4.md`, `psd1.md`, `alf.md`.
- **Step 1's substance bar did nearly all the remaining reduction**: 89 → 13 discovered over the
  same 6 calls.
- **The cap never bound** — zero `keeping the first N` lines. `MaxTopicsPerSource` contributed
  nothing here and is insurance, not the mechanism.
- **Alias collapse never fired** — zero collapses, because the surviving topic set is small and
  non-overlapping. It remains insurance for the six pairs seen on the other machine.

**Open question the numbers raise.** Three of six sources now discover **zero** topics, including
the largest meeting transcript (20 → 0). For the how-to documents that is plausibly correct; for a
35 KB transcript it may mean the substance bar is too strict on conversational sources. Needs a
human read of one of those transcripts before the bar is considered tuned — the lever is the
"merely mentioned" wording in `AiIngestExtractionService`, and `MaxTopicsPerSource` cannot loosen
it.

The 11 are the poisoned-frontmatter bug reproducing on its own — filenames like
`subject-alexander-freund-category-person.md`, i.e. a whole JSON object slugified into a page name.
Also present: `c.md`, `net.md`, `section.md`, `v4.md` from the same class of parse debris, and a
page for essentially every product, person and technology named anywhere in the six documents.

Reproduce with `scripts` in the session scratchpad (`setup-measure.mjs` + `run-measure.ps1`): copy
`providers.json`/`settings.json`, repoint `assistantFilesFolder` at a throwaway workdir, seed
`Vault/sources/`, launch with `PIA_DATA_DIR`/`PIA_LOCAL_DATA_DIR`. Note both env vars name the Pia
directory itself — pointing them at a parent silently boots the app on the real profile.

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

### Step 4 — have Pia DRAFT `memory/charter.md`

*Effort:* M · *Value:* High

**Owner decision, 2026-09-01:** an empty box would not get filled in — the card is only worth
building if Pia drafts the charter and the user edits it. So the shape is: read a sample of
`sources/`, ask the Assistant provider for a short "this knowledge base is about…" statement, show
it in an editable box, and write `memory/charter.md` only when the user saves. The rest of the
guidance below still holds.

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

### Step 6 — on-demand cleanup (done)

`LintService`'s duplicate handling was archive-only: it did not union the loser's `sources:` into
the keeper (so the next re-ingest recreated the page), did not remove the index entry, and did not
retarget links. `RunAsync` also had no caller at all.

- `IngestService.MergeTopicPagesAsync` is the real merge — union `sources:`, archive the loser to
  `memory/.archive/`, drop its index entry, retarget links, re-synthesize the keeper over the
  widened union. It lives in `IngestService` because that is what owns provenance, the `sources:`
  line and re-synthesis.
- Link retargeting is anchored on `[[topics/<slug>` **and** the terminator (`]]`, `|`, `#`). A bare
  substring replace turned `[[topics/dax-40]]` into `[[topics/dax-index-40]]` when merging `dax`
  into `dax-index` — inventing a dangling link out of a healthy one. Both merge directions are now
  covered by tests; only the safe one was, at first.
- `ILintService.RunAsync` gained `applyFixes`. The Vault Overview "Clean up" button dry-runs,
  reports how many merges and links it would make, and applies on confirm.
- **Honest limit:** preview and apply are two independent passes, so the applied set is re-derived
  rather than replayed from the preview. The confirm text says so. Passing the previewed pair list
  into the apply run would close it.

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
