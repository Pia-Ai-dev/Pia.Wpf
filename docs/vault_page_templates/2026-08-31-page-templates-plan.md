# Per-category page templates + rebuild for ingested topic pages

**Status:** Implemented (runtime UNVERIFIED — end-to-end walkthrough still owed)
**Owner:** man
**Written:** 2026-08-31
**Origin:** A user report that ingest produced many `person` topic pages carrying the same kind of
information (personnel number, name, birthdate) with different layouts, different field names, and
fields missing on some pages — and no apparent way to rework the generated output.

## Problem

Ingest synthesizes `memory/topics/<slug>.md` pages from raw sources. Everything below the
`<!-- pia:managed -->` sentinel is machine-owned and regenerated on every re-ingest; only the manual
preamble above it survives. That is by design, but it left no way to make the *generated* half
consistent.

Root causes, as found in the code before this change:

- **The shape instruction was hardcoded and deliberately loose** — *"Start with a one-sentence
  definition, then short prose or bullets."* `category` was interpolated as a bare word. No
  per-category contract existed anywhere.
- **The one grounding lever was invisible and unset.** `memory/charter.md` is prepended to both
  ingest prompts, but no UI creates or edits it, and it is absent from a default vault — so ingest
  ran with an empty charter and both grounding blocks were dropped.
- **`memory/AGENTS.md` was inert.** It documents the `- key: value` record convention and calls
  itself "human-editable and co-evolved", but nothing in the ingest path ever read it.
- **Existing pages could not be regenerated.** `IIngestScheduler.RunAsync` was reachable only from
  the drag-drop staging path and the transcript overlay, and the watcher is hash-gated — so any
  grounding fix would have been future-only.

## What was built

### Part A — per-category page templates

- **`memory/templates.md`**, a new scaffolded vault document with one `## <category>` section per
  category the extractor emits (`person`, `organization`, `product`, `concept`, `regulation`,
  `technology`, `other`). Seeded only when absent, never overwritten — the same policy as
  `AGENTS.md`, but checked independently so vaults that predate it still get one. Added to
  `VaultPaths.Housekeeping`, so it is neither a memory record nor recall-indexed.
- Only `## person` ships with a contract; every other section is empty, and **empty means
  free-form**. That is the regression guard: a vault whose templates were never edited produces a
  byte-identical prompt to the one before this change.
- **`VaultTemplateService`** — modelled on `VaultCharterService` (concrete singleton, no interface,
  never throws). `GetTemplateAsync(category)` returns the matching section body, matched through
  `VaultSlug.Slugify` so `Person`/`person`/`PERSON` resolve alike. HTML-comment guidance lines are
  stripped so they never reach the prompt.
- **The template is threaded into synthesis.** `IIngestSynthesizer.SynthesizeAsync` gained a
  `template` parameter; `IngestService.SynthesizePageAsync` resolves it per page category.
  `AiIngestSynthesisService` replaces the loose shape sentence with an absolute one when a template
  is present, and quotes the template as a `--- PAGE TEMPLATE ---` block ahead of the sources.

The strict wording targets the three reported symptoms one-for-one:

| Symptom | Rule |
|---|---|
| Different layout / sections | reproduce every template line, in that order |
| Different field names | keep each field key verbatim — never translate, rename or reorder |
| Fields missing on some pages | emit every field; write `unknown` rather than omitting the line |

The placeholder-preservation clause is untouched and matters more here, not less: with
`Privacy.TokenizationEnabled` on, names, personnel numbers and birthdates reach the model as
`[Person_1]`-style tokens and are restored afterwards. A fixed field slot makes that round-trip more
reliable than free prose does.

### Part B — rebuild

- **`IIngestService.RebuildPageAsync(pagePath, ct)`** re-synthesizes one existing page from the
  sources it already records. Built directly on `SynthesizePageAsync` — *not* on
  `RemoveContributionsAsync`, which prunes frontmatter and passes a deliberately reduced source
  list. It resolves `knownSlugs` via `BuildKnownTopicSlugsAsync`; without that,
  `BuildLinkInstruction` forbids wikilinks outright and a rebuild would silently strip every link
  off the page.
- Preserved semantics: the manual preamble survives (via `ExtractManualPreamble`), `id`/`created`/
  `sources` survive, no recorded sources is a no-op, and an empty synthesis leaves the page
  byte-identical. A `rebuild` line is journalled alongside the existing `ingest`/`removed` lines.
- **`IIngestService.ListTopicPagesAsync`** enumerates `memory/topics/*.md` — scoped on purpose,
  since `EnumerateAsync` is not a real glob and `memory/*.md` would return the scaffolding.
- **`IIngestScheduler.RebuildPageAsync` / `ListTopicPagesAsync`** route through the same serial
  gate as ingest, so a rebuild can never interleave with a compile touching the same page.
- **UI:** a per-page Rebuild button in `PiaInspectorHeader` (visible only for topic pages, via
  `VaultMemoryItem.IsRebuildable`), and a "Rebuild all pages" action with progress and cancellation
  in `PiaVaultOverview`. Both confirm first — each rebuild is an LLM request that discards the
  current managed body.

### Documentation

The seeded `AGENTS.md` gained a **Steering ingest** section naming `charter.md` and `templates.md`
and stating plainly that AGENTS.md itself is *not* read by ingest. Fresh vaults only — an existing
AGENTS.md is never overwritten by policy.

## Known limitation — check this on the affected vault

A page's category is frontmatter-fixed at creation, and both the extractor and the removal path
default a missing category to `concept`. A person page created without a category is stored as
`concept`, resolves the (empty) `## concept` template, and stays free-form no matter how good the
`person` template is. **Rebuild alone cannot retarget it.**

```powershell
Select-String -Path "<vault>\memory\topics\*.md" -Pattern '^category:' |
  Group-Object { $_.Line } | Sort-Object Count -Descending
```

If the affected person pages are largely `concept`, either correct `category:` by hand (it is plain
frontmatter, and rebuild then picks up the right template) or extend the resolver to key off `type`
and title as well.

## Not in scope

- Detaching a page from ingest entirely (edit freely, never regenerate) — considered, not chosen.
- Surfacing `memory/charter.md` in the UI. It remains hand-created, and is still the only lever over
  *which* topics get a page at all.
