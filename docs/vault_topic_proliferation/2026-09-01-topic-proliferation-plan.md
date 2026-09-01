# Curbing vault topic proliferation

**Status:** All six steps implemented; measured 85 → 13 topic pages (19 with a charter) on real sources
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

| | before (`32b9edc4`) | after, no charter | after, with a charter |
|---|---|---|---|
| topic pages on disk | **85** | **13** | **19** |
| topics discovered | 89 | 13 | 20 |
| pages with an unparseable `title:` | **11** | **0** | **0** |
| sources ingested | 5 ok, 2 transient failures | 6 ok | 6 ok |

The 11 are the poisoned-frontmatter bug reproducing on its own — filenames like
`subject-alexander-freund-category-person.md`, i.e. a whole JSON object slugified into a page name.
Also present: `c.md`, `net.md`, `section.md`, `v4.md` from the same class of parse debris, and a
page for essentially every product, person and technology named anywhere in the six documents.

Per source (topics discovered):

| source | before | no charter | with a charter |
|---|---|---|---|
| `business plan.md` (30 KB) | 32 | 7 | 8 |
| `meeting-20260825-0930…` (35 KB) | 20 | **0** | **6** |
| `meeting-20260825-0900…` (8 KB) | 13 | 5 | **0** |
| `dotnet_planning_skill.txt` | 11 | **0** | **3** |
| `brainstorming-system-prompts.md` | 7 | 1 | 3 |
| `meeting-20260822-1529…` (5 KB) | 6 | **0** | **0** |

**Which change did the work — don't read 85 → 13 as one number:**

- **Step 0 removed the 11 poisoned pages outright** (13% of the before count) plus the rest of the
  parse debris on that run — `c.md`, `net.md`, `section.md`, `v4.md`, `psd1.md`, `alf.md`.
- **Step 1's substance bar did nearly all the remaining reduction**: 89 → 13 discovered over the
  same 6 calls.
- **The cap never bound** — zero `keeping the first N` lines. `MaxTopicsPerSource` contributed
  nothing here and is insurance, not the mechanism.
- **Alias collapse never fired** — zero collapses, because the surviving topic set is small and
  non-overlapping. It remains insurance for the six pairs seen on the other machine.

**Does the charter earn its keep? Yes — measured.** The third column re-runs the same six sources
with a hand-written charter naming the people, customer organizations, products, regulations and
engineering practices that matter to this vault. It lifts discovery 13 → 20 and, importantly,
brings back the two sources that had gone silent: the 35 KB transcript 0 → 6 and the planning-skill
document 0 → 3. That is the case for step 4 in one number.

**Read the per-source column with care — these are single runs.** The 8 KB meeting went the other
way (5 → 0) with a *better*-grounded prompt, which can only be model variance. Per-source deltas of
a few topics are inside the noise; only the aggregate 85 → 13/20 is a large enough effect to lean
on. Repeating each condition three times would be the way to make the per-source numbers mean
something, and has not been done.

**Still open.** The two smaller meeting transcripts discover zero in *both* after-runs. That is the
consistent signal, and a charter does not rescue them. It may be right (short status meetings that
genuinely name nothing notable) or it may be the substance bar being strict on conversational text.
The lever is the "merely mentioned" wording in `AiIngestExtractionService`; `MaxTopicsPerSource`
cannot loosen it. A human read of one of those transcripts settles it.

**The charter is not a noise filter either** — the charter run still produced `4-9.md` and
`setup-cfg.md`, which are a version number and a filename rather than topics.

Reproduce with `scripts/Measure-TopicYield.ps1`, which builds the isolated profile, seeds the
sources (and optionally a charter), runs the app and prints the counts above:

```powershell
./scripts/Measure-TopicYield.ps1 -Label after `
  -SourcesPath "$HOMEDocumentsPia AssistantVaultsources" `
  -Exe .srcPia.WpfinDebug
et10.0-windows10.0.17763.0Pia.Wpf.exe `
  -ProviderId <a provider guid from providers.json>
```

Point `-Exe` at a worktree build to measure an older commit. One trap the script encodes: `PIA_DATA_DIR`
and `PIA_LOCAL_DATA_DIR` name the Pia directory ITSELF, not a parent containing one — point them at a
parent and the app silently boots on the real profile and ingests into the real vault.

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

### Step 4 — Pia drafts `memory/charter.md` (done)

**Owner decision, 2026-09-01:** an empty box would not get filled in, so the card only pays off if
Pia proposes the text. `ICharterDrafter` / `AiCharterDraftService` reads up to 12 text sources
(2 000 chars each — breadth beats depth for a charter), asks the Assistant provider for a 3–6
sentence statement in the documents own language, and returns it. Nothing is written:
`VaultCharterService.SaveCharterAsync` persists only what the user saves, and clearing the box
deletes the page, because an empty charter has to mean "no grounding" rather than a page whose body
is whitespace.

The Vault Overview card shows the charter, an empty state explaining what it is for, a "Draft one
for me" button, and an editor with save/cancel.

- **No auto-seeded content.** `GetCharterAsync` returns the body verbatim into the extraction
  prompt, so filler text would become filler grounding.
- **No "Rebuild all pages" on save.** `RebuildPageAsync` re-synthesizes from a page recorded
  `sources:` and never calls `DiscoverTopicsAsync`, so rebuild-all would spend one LLM call per
  page and change the topic count by zero. The card says instead that the charter applies to
  documents ingested from now on. A "re-ingest all sources" action is the follow-up if the wait
  proves annoying — it must clear `IngestStateStore` for those refs first, or the hash gate no-ops
  every one.
- Drafted text is model output and can easily open with `Scope:` or a quote. Covered by a
  round-trip test, and safe because of step 0's scalar encoder.
- The drafter publishes its own `TokenMapAmbient` around the call and re-identifies loosely,
  exactly as `AiIngestSynthesisService` does. It runs off no chat turn, its excerpts are meeting
  transcripts, and the model rewrites them — without a published map a mangled placeholder would
  be persisted to a synced page.
- `memory/charter.md` is registered in `VaultPaths.Housekeeping` and `LintService.Housekeeping`, so
  the charter does not itself surface as a memory record, get recall-indexed, or be reported as an
  orphan by the cleanup pass — the treatment `templates.md` needed when this branch added it.

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
