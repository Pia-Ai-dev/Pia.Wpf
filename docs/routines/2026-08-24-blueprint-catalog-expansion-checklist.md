# Checklist — blueprint catalog expansion

**Status:** code steps landed on `feature/routines-blueprints`; the two hand checks and the pinned-provider
warning are open.
**Owner:** Marco Altmann. **Written:** 2026-08-24.
**Origin:** [`2026-08-24-blueprint-catalog-expansion.md`](2026-08-24-blueprint-catalog-expansion.md), which is
the plan this tracks.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little
standalone value, unblocks a High.

## Decision gates

| Gate | Question it answers | Blocks |
|---|---|---|
| G1 — the live web run (step 10) | Does a web-dependent routine actually come back with sources and dates, and does it refuse honestly on a provider that cannot search? | Steps 11 and 12. If it refuses cleanly and the refusal is legible in the chat, the editor-side warning is a nicety; if the refusal is silent or confusing, 11 becomes urgent. |

## Steps

- [x] **1. Categories and the web-search flag.** Repurpose `RoutineBlueprint.Category` onto
  `RoutineBlueprintCategories` (`ready` / `your-data`, `InDisplayOrder`, `StemOf`), add
  `RequiresWebSearch`, and define `WebSearchGuard` as a `const` so a test can enforce it.
  *Deps:* — · *Effort:* XS · *Value:* Enabler
- [x] **2. Recategorize the eight existing blueprints.** `topic-digest` and `competitor-watch` to
  `ready` with the guard clause appended; the other six to `your-data`.
  *Deps:* 1 · *Effort:* XS · *Value:* Med
- [x] **3. Twelve new blueprints.** All `Research`, all `GrantedTools: []`, all `ready`, one text slot
  each with a real default, house style per template.
  *Deps:* 1 · *Effort:* M · *Value:* High
- [x] **4. The web-search hint.** `IsWebSearchActive` `private` → `internal`, one
  `GetDefaultProviderForModeAsync(WindowMode.Assistant)` call in `RefreshAsync`, and one hint line
  under the catalog header when that provider cannot search.
  *Deps:* 1 · *Effort:* XS · *Value:* High
- [x] **5. The fourth pane.** `IsCatalogOpen`, `ShowsCatalog` / `ShowsPlaceholder` as full expressions,
  `BrowseBlueprintsCommand` on `Routines_NewJob`, "Start from blank instead", the close affordance, the
  auto-open on an empty routine list, and `Routines_BrowseCatalog` on the placeholder.
  *Deps:* 1 · *Effort:* S · *Value:* High
- [x] **6. Grouping and search.** `BlueprintGroups` rebuilt on every query change, all-terms match over
  title and description, empty groups dropped, a no-matches line, and expansion forced only on the step
  into a search.
  *Deps:* 5 · *Effort:* S · *Value:* High
- [x] **7. `PiaRoutinesSearchBar`.** A copy of `PiaTodoSearchBar` under `Controls/Routines/` with
  `Routines_SearchQuery`, because ids must stay disjoint across views.
  *Deps:* 6 · *Effort:* XS · *Value:* Enabler
- [x] **8. Assistant tool surface.** Print category and needs-web-search per entry in
  `list_routine_blueprints`, so the model can steer a user to something their provider can run.
  *Deps:* 1 · *Effort:* XS · *Value:* Med
- [x] **9. Localization and tests.** ~177 resx entries across en/de/fr, the catalog and view-model
  facts, the `ViewAutomationIdTests` rows, and the playbook ids.
  *Deps:* 3, 6 · *Effort:* M · *Value:* High
- [ ] **10. The two hand checks.** Walk a throwaway profile through the catalog per §8.3 of the plan,
  then fire `market-snapshot` for real per §8.4 — once on a searching provider, once on one that cannot
  search. A passing suite cannot see either.
  *Deps:* 9 · *Effort:* XS · *Value:* High
- [ ] **11. Warn when the *pinned* provider cannot search.** The catalog hint reads the default
  provider; `ScheduledResearchProviderResolver` prefers the job's pin. Carry the provider's search
  capability onto `RoutineProviderChoice`, recompute on `EditProvider` change, and surface the warning
  next to `Routines_Field_Provider` when a web-requiring routine is pinned to a provider that cannot
  search. Needs one new string in three locales.
  *Deps:* 10 (G1) · *Effort:* S · *Value:* Med
- [ ] **12. Revisit the first-run register of the Ready group.** Three reviewer notes, all currently
  owner-approved values: `industry-pulse`'s default overlaps `topic-digest`'s, it shares Monday 08:00
  with `competitor-watch`, and `competitor-watch`'s template opens with a vault lookup although its card
  sits under "Works right away". Decide from what the walkthrough actually looks like, not from the
  table.
  *Deps:* 10 · *Effort:* XS · *Value:* Med

## Not yet planned

- **Slot-prompt UI** — a card would ask for its slot values before opening the editor. Deferred: every
  slot has a default and the rendered goal is editable in the goal box, which for a ticker list beats a
  modal.
- **Monthly blueprints** — needs `DefaultDayOfMonth` on the record; the editor test asserts the weekly
  biconditional today.
- **Localized search keywords** — owner chose title and description only. The cost is that a German
  user typing "Aktien" will not match "Börsenüberblick".
- **A weather brief and a local-events digest** — cut on first-run emptiness and retrieval quality.
  Viable if Pia ever gains a location setting or a weather connector.

## Suggested order

10 first — it is the cheapest step left and it is the only one that can still invalidate the shipped
design; it also answers G1. Then 11 if G1 says the silent-pin case is confusing in practice, and 12
last, since it is a taste call that only the walkthrough can settle.
