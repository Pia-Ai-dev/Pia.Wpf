# Vault page templates + rebuild — checklist

**Status:** Code complete, runtime unverified
**Owner:** man
**Written:** 2026-08-31
**Origin:** [2026-08-31-page-templates-plan.md](2026-08-31-page-templates-plan.md)

*Effort:* `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.
*Value:* `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler`
little standalone value, unblocks a High.

## Steps

- [x] **A1 — Scaffold `memory/templates.md`.** One `## <category>` section per extractor category,
  seeded only when absent and checked independently of `AGENTS.md` so existing vaults get one.
  *Deps:* — · *Effort:* XS · *Value:* Enabler
- [x] **A2 — Exclude it from records and recall.** `memory/templates.md` joins
  `VaultPaths.Housekeeping`.
  *Deps:* A1 · *Effort:* XS · *Value:* Enabler
- [x] **A3 — `VaultTemplateService`.** Slug-matched per-category lookup, comment-stripped, never
  throws, `""` for every "no contract" path.
  *Deps:* A1 · *Effort:* XS · *Value:* Enabler
- [x] **A4 — Thread the template through synthesis.** `template` parameter on
  `IIngestSynthesizer.SynthesizeAsync`, resolved per page category in `SynthesizePageAsync`.
  *Deps:* A3 · *Effort:* S · *Value:* High
- [x] **A5 — Strict shape block in the prompt.** Replaces the loose sentence when a template exists;
  byte-identical prompt when it does not.
  *Deps:* A4 · *Effort:* XS · *Value:* High
- [x] **A6 — Point AGENTS.md at the two real levers.** New "Steering ingest" section naming
  `charter.md` and `templates.md`; fresh vaults only.
  *Deps:* A1 · *Effort:* XS · *Value:* Med

- [x] **B1 — `IIngestService.RebuildPageAsync`.** Re-synthesize one page from its recorded sources,
  grounded in `BuildKnownTopicSlugsAsync`, preamble and identity preserved.
  *Deps:* — · *Effort:* S · *Value:* High
- [x] **B2 — `ListTopicPagesAsync`.** Scoped enumeration of `memory/topics/*.md` for the bulk action.
  *Deps:* — · *Effort:* XS · *Value:* Enabler
- [x] **B3 — Scheduler pass-through.** Both new calls on the serial ingest queue.
  *Deps:* B1, B2 · *Effort:* XS · *Value:* Enabler
- [x] **B4 — Per-page Rebuild button.** `PiaInspectorHeader`, gated on
  `VaultMemoryItem.IsRebuildable`, confirm-first.
  *Deps:* B3 · *Effort:* S · *Value:* High
- [x] **B5 — Bulk rebuild with progress and cancel.** `PiaVaultOverview`.
  *Deps:* B3 · *Effort:* S · *Value:* High
- [x] **B6 — Localization.** All new strings in en/de/fr.
  *Deps:* B4, B5 · *Effort:* XS · *Value:* Enabler
- [x] **B7 — AutomationId coverage.** `MemoryNote_Rebuild`, `MemoryOverview_RebuildAll`,
  `MemoryOverview_RebuildCancel`; `ViewAutomationIdTests` rows added, `PiaVaultOverview` newly
  covered.
  *Deps:* B4, B5 · *Effort:* XS · *Value:* Med

- [x] **T1 — Unit tests.** `VaultTemplateServiceTests`; template-threading, rebuild and enumeration
  cases in `IngestServiceTests`; prompt cases in `AiIngestSynthesisServiceTests`; scaffolding cases
  in `VaultSchemaServiceTests`; `VaultPathsTests` and `VaultMemoryItemTests` rows.
  *Deps:* A4, B1 · *Effort:* S · *Value:* High
- [ ] **V1 — End-to-end walkthrough on a throwaway profile.** Deferred to another machine; see
  Verification below. Nothing here is runtime-verified until this is ticked.
  *Deps:* all · *Effort:* S · *Value:* High
- [ ] **V2 — Category audit on the affected vault.** Confirm the person pages carry
  `category: person` and not `concept`; see the plan's *Known limitation*. Decides whether the
  retroactive fix actually lands.
  *Deps:* — · *Effort:* XS · *Value:* High

## Verification (V1)

Against a throwaway profile (`PIA_DATA_DIR`):

1. Drop two sources naming different people, ingest, confirm the pages drift (baseline).
2. Edit `## person` in `memory/templates.md`, **rebuild both pages**, confirm identical field lists
   in identical order, `unknown` where the source is silent, and manual preambles intact.
3. Re-ingest a source; confirm the template still holds and the preamble is still there.
4. Repeat with `Privacy.TokenizationEnabled = true` and confirm real values (not `[Person_1]`) land
   on disk.
5. Replay `tests/ui-scripts/` for the new buttons.

## Suggested order

V2 first — it is a one-line query and it decides whether Part B pays off at all on the existing
vault. Then V1.

## Not yet planned

- Detaching a page from ingest entirely (edit freely, never regenerate).
- Surfacing `memory/charter.md` in the UI; it is still hand-created and still the only lever over
  which topics get a page.
- Correcting a miscategorized page's `category:` from the UI.
