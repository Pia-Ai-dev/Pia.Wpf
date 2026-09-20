# Self-knowledge checklist

Tracks [`2026-09-20-self-knowledge-plan.md`](2026-09-20-self-knowledge-plan.md). Tick a box in the
commit that lands it.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler`
little standalone value, unblocks a High.

## Decision gates

| Gate | Question it answers | Decided |
|---|---|---|
| `Q1` | Bundled snapshot, runtime fetch, or the server KB? | **Bundled snapshot** — the only option that works offline, on every provider, and without a Pia Cloud account |
| `Q2` | Desktop guides only, or also `server/**` admin docs? | **Desktop only** — 36 pages; admin docs double the corpus and answer questions most users cannot act on |
| `Q3` | Docs only, or docs plus a live settings read? | **Both** — the docs describe a build, and the TTS question is only fully answerable from the install |

## Steps

- [x] **S1 — Corpus snapshot.** `Update-HelpCorpus.ps1` drives the docs repo's own `build-kb.mjs`
      and writes one gzipped `EmbeddedResource`. *Deps:* — · *Effort:* `S` · *Value:* `Enabler`
- [x] **S2 — Section parser.** Fence-aware `##`/`###` split with unique per-page references.
      *Deps:* S1 · *Effort:* `XS` · *Value:* `Enabler`
- [x] **S3 — Search.** In-memory FTS5 with bm25, porter stemming, a shared query sanitizer extracted
      from `AssistantChatService`, OR-joined terms, stop words and an alias table.
      *Deps:* S2 · *Effort:* `M` · *Value:* `High`
- [x] **S4 — `pia_help`.** Search / read / table-of-contents on one tool, with a miss note that says
      what was actually searched. *Deps:* S3 · *Effort:* `S` · *Value:* `High`
- [x] **S5 — Plugin wiring.** GUID `…00C`, `FromHelpHandler`, `PluginService` arm, DI, read-only
      names. *Deps:* S4 · *Effort:* `S` · *Value:* `Enabler`
- [x] **S6 — Routing.** Plugin prompt addition plus a lead-in above the tool tree, conditional on the
      pack being on. *Deps:* S5 · *Effort:* `XS` · *Value:* `High`
- [x] **S7 — `pia_settings`.** Nine areas, localized paths, secrets excluded.
      *Deps:* S5 · *Effort:* `M` · *Value:* `High`
- [x] **S8 — Tests.** Corpus, search, settings, registration, routing — 52 tests; full gate green at
      7736 / Failed: 0. *Deps:* S7 · *Effort:* `S` · *Value:* `High`
- [x] **S9 — Cost measurement.** 1505 chars ≈ 376 tokens per turn, with a test that caps both prompt
      fragments. *Deps:* S6 · *Effort:* `XS` · *Value:* `Med`
- [ ] **S10 — Live validation.** All three driving questions plus a German phrasing, in Chat mode,
      against a real provider, with the pack-disabled arm for contrast. The three questions are done
      — see [`2026-09-20-live-validation.md`](2026-09-20-live-validation.md); only the disabled arm
      is left. *Deps:* S8 · *Effort:* `XS` · *Value:* `High`

## Not yet planned

- A Pia.Docs page describing this feature, so the corpus can answer questions about itself.
- A pre-release step (or a release-playbook line) that runs `Update-HelpCorpus.ps1 -Check`.
- Anything for `ToolScope.None` personas and providers without tool calling — see the plan's §4.

## Suggested order

S1 → S2 → S3 → S4 → S6 is the vertical slice: it answers all three driving questions on its own and
can be live-validated before `pia_settings` exists. S7 is the accuracy layer on top. S10 is the only
step left and needs a desktop session with a real provider key.
