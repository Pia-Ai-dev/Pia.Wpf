# Ingest Topic Synthesis (Karpathy LLM-Wiki) — Design

**Date:** 2026-07-08
**Branch:** `feature/meeting_attendee`
**Status:** Design approved (brainstorming). Plan pending.

---

## 1. Problem

The current ingest pipeline (`IngestService` + `AiIngestExtractionService`) is a **deterministic
extractor**, not a wiki builder. For each RAW file under `sources/` it makes one "extract salient
entities" LLM call, turns *every* returned entity into its own `memory/topics/<slug>.md`, and writes
a machine-managed `## Source: <file>` section of `- key: value` bullets on that page (replaced in
place on re-ingest). The result — 72 pages from 4 sources — reads as useless, for measurable reasons
when held against the Karpathy LLM-wiki model
(https://gist.github.com/karpathy/442a6bf555914893e9891c11519de94f):

1. **Junk-page granularity.** No notability filter, so the EULA alone spawned `use`, `software`,
   `documentation`, `agreement`, `authorized-scope`, `license-key`, `license-term`, … — contract
   boilerplate, not knowledge. The ~10 pages that matter are buried.
2. **It is an index, not a wiki — no synthesis.** Each page silos knowledge by source (`## Source:`
   blocks). A topic touched by two sources (`net-10.md`) shows two disconnected bullet blocks. The
   whole value of the wiki model — *merging* sources into one authored narrative, reconciled, with
   contradictions noted — is structurally impossible in this design.
3. **Fragments, not prose.** `- key: value` scraps read like scraped metadata.
4. **Accidental linking.** `Related:` links are substring matches ("does one entity's name appear
   inside another's facts"), yielding a sparse, arbitrary graph.
5. **Flat index.** 72 alphabetical entries, no categories, so no visible structure.

**Root cause.** The design optimized for *idempotent re-ingest* (a source's `## Source:` section is
replaced exactly on re-run). That property is fundamentally at odds with synthesis, which requires
merging sources into a single narrative that cannot be a per-source replaceable block. The Karpathy
model is the inverse: **the LLM owns and rewrites each page.**

## 2. Goal

Turn ingest into a topic-driven synthesis pipeline that produces a genuine LLM wiki:

- Only **notable** entities (grounded in a vault charter) become topic pages; boilerplate is skipped.
- Each topic page is a single **synthesized narrative** merged from *all* contributing sources,
  written as prose with dense `[[topics/<slug>]]` wikilinks — not per-source bullet silos.
- On any change to a page's source set, the page is **re-synthesized from all still-present
  sources**, so it always reflects current sources.
- The index groups topics by category, not one flat alphabetical block.

### User decisions (from brainstorming Q&A)

| Question | Decision |
|----------|----------|
| Scope | Full synthesis rewrite (not just a notability filter) |
| Source change/deletion reconcile | **Re-synthesize from all still-present sources** |
| Notability | **Charter-grounded relevance** (vault purpose note feeds extraction) |
| Synthesis mechanics | **A: stateless — re-read raw sources per topic** (no cached-facts layer) |
| Manual edits | **Preserve a manual preamble** above the synthesized body |

### Non-goals

- Binary source handling (PDF/images) — stays deferred; non-text sources remain skipped.
- A progress UI / background-job handle — ingest keeps running inline via `IIngestScheduler`.
- A cached per-source extracted-facts layer (Approach B) — rejected; raw sources are small
  (12k-char cap), so re-reading is negligible and B's extra machinery/storage/sync cost isn't worth
  it.
- A per-feature ingest provider override — keeps using the default provider.
- Settings UI for the charter — the charter is a plain managed note, edited like any memory.

## 3. Components

### 3.1 Charter — `VaultCharterService` (new, small)

Reads `memory/charter.md` if present; else falls back to `memory/profile.md`; else empty string.
Its text is injected into extraction so the model knows what the vault is about and keeps only
relevant topics. Absent/empty charter degrades to prompt heuristics — never throws.

### 3.2 Extraction becomes topic discovery — `AiIngestExtractionService`

`ExtractEntitiesAsync` is charter-grounded and returns notable **topic name + coarse `type`**
(person / organization / product / concept / regulation / technology / …). It no longer extracts
`facts` (synthesis re-reads raw sources), so that code and the `ExtractedEntity.Facts` field go away
(or `Facts` is dropped from the record). Prompt is hardened to skip definitional/legal/generic
boilerplate. Defensive JSON-then-lines parsing is retained.

### 3.3 New `IIngestSynthesizer` — `AiIngestSynthesisService`

- **Input:** topic name, `type`, charter text, and the truncated text of *all* contributing raw
  sources for that topic.
- **Output:** a synthesized markdown body (short lead sentence → prose/bullets as natural, with
  dense `[[topics/<slug>]]` wikilinks) plus a one-line summary for the index.
- Behind an interface so tests inject a deterministic fake (real LLM output is not byte-stable).
- Degrades gracefully with no provider configured (empty body → topic skipped, mirroring current
  extractor behaviour).

### 3.4 `IngestService` rewrite (topic-driven)

For source X:

1. Guards unchanged: vault containment, text-only, non-empty.
2. Discover notable topics in X (charter-grounded extraction).
3. For each topic T:
   - Contributing sources = existing page frontmatter `sources:` ∪ `{X}`.
   - Read each contributing raw source (same containment/text guards; skip missing).
   - Synthesize T's managed body from all of them.
   - Write page = **preserved manual preamble** + regenerated managed body, split by a new
     mandatory `<!-- pia:managed -->` sentinel line (a fresh in-file convention introduced here —
     note `VaultSlug.PreambleSlug` is unrelated: it is a recall/index *chunk* slug, not an in-file
     boundary). The preamble is taken from the RAW page text before the sentinel, never from the
     parser's `doc.Preamble` (which folds a heading-less body into the preamble). Frontmatter is
     rebuilt identity-preserving (`id`/`created` reused) and carries `type: topic`, `category`, and
     the merged `sources:` list.
4. Upsert the categorized index entry (synthesizer's one-line summary).
5. Append one `ingest` log line naming the source and touched pages.

The entire `## Source:`/`SpliceSectionAsync`/section-upsert machinery is **deleted** — provenance
now lives only in frontmatter `sources:`.

### 3.5 Removal / reconcile — `RemoveContributionsAsync`

For each page listing the removed source: drop it from `sources:`; if no sources remain → delete the
page + its index entry; else **re-synthesize** the body from the remaining sources. Integrates
unchanged with `AutoIngestService` (watcher + startup reconcile) and `IngestStateStore` hash gating.

### 3.6 Categorized index — `VaultIndexService`

The Topics list is grouped by `type` frontmatter (People / Organizations / Products / Concepts /
Regulations / Technology / Other) instead of one flat alphabetical block.

## 4. Data flow

```
source X changed
  └─ extract notable topics (charter-grounded)  ── AiIngestExtractionService
       └─ for each topic T:
            sources(T) = page.frontmatter.sources ∪ {X}
            read raw text of each source(T)
            body = synthesize(T, type, charter, [raw...])  ── AiIngestSynthesizer
            write page = preamble + body ; frontmatter{ type, sources }
            index.upsert(T, summary, type)
       └─ log "ingest | X -> T1, T2, …"
```

## 5. Migration (one-time)

No in-place migration of the 72 junk pages. Delete `memory/topics/*`, reset the index Topics
section, clear `IngestStateStore` hashes → `AutoIngestService` reconcile rebuilds all sources fresh
under the new pipeline. Executed once on the new schema version.

## 6. Error handling

- No provider / empty synthesis / empty extraction → ingest no-ops for that topic (never throws),
  matching current graceful degradation.
- Missing or non-text contributing source during a topic's union read → skipped, synthesis proceeds
  with the rest.
- All parsing best-effort; malformed model output yields an empty list, not an exception.

## 7. Testing

- Fake `IIngestSynthesizer` + fake extractor for deterministic assertions (no raw-string equality
  against real LLM output).
- Unit coverage: source-union computation, re-synthesis on source change, removal→re-synthesize,
  page+index deletion at zero sources, preamble preservation across re-synthesis, categorized index
  grouping, charter fallback chain (charter → profile → empty).
- Respect the branch baseline gate: MTP runner, exclude
  `Pia.Wpf.Tests.Integration.Providers`, no new failures beyond the known 2 pre-existing.

## 8. Known limitation (v1)

Cross-source synthesis only unions sources that **each independently** surface a topic as notable.
If source Y genuinely discusses topic T but Y's extraction didn't flag T, T's page won't pick up Y
until a future lint/reconcile re-scan. Acceptable for v1; a lint pass can close it later.
