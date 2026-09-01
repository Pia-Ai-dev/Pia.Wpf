# Topic proliferation — checklist

**Status:** 4 of 6 steps landed
**Owner:** Marco Altmann
**Written:** 2026-09-01
**Origin:** [2026-09-01-topic-proliferation-plan.md](2026-09-01-topic-proliferation-plan.md)

*Effort:* `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.
*Value:* `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler`
little standalone value, unblocks a High.

## Steps

- [x] **0 — Frontmatter that cannot be read back.** Encode `title:`/`category:` as YAML scalars,
  degrade on a malformed block instead of throwing, and archive + re-ingest pages already poisoned.
  *Deps:* — · *Effort:* S · *Value:* High · `d2bfdf5c`
- [x] **5 — Gate the line fallback.** Stop every line of non-JSON model output becoming a topic, and
  take the first balanced `[...]` span that actually parses.
  *Deps:* 0 · *Effort:* XS · *Value:* High · `d2bfdf5c`
- [x] **1 — Notability bar and ceiling.** A substance test, language-neutral exclusions, and
  `MaxTopicsPerSource` stated in the prompt and enforced on the result.
  *Deps:* — · *Effort:* XS · *Value:* High · `6f1e7eb3`
- [x] **2 — Deterministic alias collapse.** `TopicIdentity` plus an identity map with a fixed
  precedence rule, so an alias lands on the page its twin already owns.
  *Deps:* — · *Effort:* S · *Value:* High · `1f32713e`
- [x] **3 — Triageable `browse_index`.** A one-line summary per entry, skipping template field
  bullets; plus a cap on the known-slug list inlined into every synthesis prompt.
  *Deps:* — · *Effort:* S · *Value:* High
- [ ] **4 — Surface `memory/charter.md`.** An editable card on the Vault Overview, so the only lever
  over which topics earn a page stops being invisible.
  *Deps:* — · *Effort:* M · *Value:* High
- [ ] **6 — On-demand cleanup.** Make `LintService`'s archive-only duplicate handling a real merge,
  then put it behind a Vault Overview button with a dry-run report.
  *Deps:* 2 · *Effort:* M · *Value:* High

## Decision gates

| Gate | Question it answers | Blocks |
|---|---|---|
| Live re-measure after 1+2 | Did the count actually drop, and by how much? If a source still yields 8 near-identical pages, the clamp belongs after dedup in `IngestService`, not in the extractor | Whether 4 and 6 are still worth their effort |
| `MaxTopicsPerSource` default | Is 8 meaningful for this vault? `(Get-ChildItem "<vault>\sources\*").Count` decides — with 4 sources the cap barely binds and the substance bar is carrying step 1 alone | Tuning only |
| Charter authoring | Will a user actually write one? If not, step 4 is a card nobody fills in and the effort belongs in 6 instead | Step 4 |

## Not yet planned

- Retargeting a miscategorized page's `category:` from the UI (a `person` page stored as `concept`
  resolves the empty `concept` template forever; rebuild alone cannot fix it).
- Detaching a page from ingest entirely — edit freely, never regenerate.
- A per-vault topic ceiling, as opposed to the per-source one.
- Feeding the existing topic vocabulary to the extractor prompt. Deliberately skipped in favour of
  the deterministic C# collapse: a vault-wide name list in a prompt is a scope leak the PII
  tokenizer only partly covers (see the withheld-slug rationale in `AiIngestSynthesisService`).

## Suggested order

0+5 first — a correctness bug, and every later step writes frontmatter through the builders it
fixes. Then 1 → 2 (forward-only, no migration), then 3 (independent; fixes the tool-call half on its
own). Re-measure on a live vault before spending 4 or 6.
