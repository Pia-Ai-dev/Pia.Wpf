# Routine blueprints: a catalog a new user can actually use

**Status:** implemented on `feature/routines-blueprints`; two verification steps still owed by a human (§8).
**Owner:** Marco Altmann. **Written:** 2026-08-24.
**Origin:** [`../hermes_checkup/2026-08-22-routine-blueprints-plan.md`](../hermes_checkup/2026-08-22-routine-blueprints-plan.md)
§7 and §11, plus the owner decisions of 2026-08-24 recorded in §2 below.

---

## 1. What was wrong

The blueprint catalog shipped 2026-08-22 with eight entries, and it had three defects that together
made it useless to the person it was written for.

1. **Nothing paid off on day one.** Six of the eight read the user's *own* data — todos, reminders,
   kanban columns, vault notes, meeting transcripts. On a fresh profile every one of them answers
   "there is nothing for today". Only `topic-digest` and `competitor-watch` are fed by the world.
2. **The catalog was reachable only on an empty profile.** It rendered inside the placeholder pane,
   gated on `!IsEditorOpen && SelectedJob is null`. Create one routine and the menu was gone for good.
3. **No grouping and no search.** `RoutineBlueprint.Category` existed, held cadence values
   (`daily` / `weekly` / `meetings`), and was read by nothing in `src`.

## 2. Owner decisions (2026-08-24)

- **Catalog-first**: the existing `Routines_NewJob` button opens the catalog, not a blank editor.
- **Two groups**: "Works right away" and "Uses your Pia data".
- **About twelve new blueprints** — 20 in the catalog in total.
- **Search matches title and description only.** No keyword resx, so a German user typing "Aktien"
  will not match "Börsenüberblick". Revisit if search proves weak in use.

## 3. The load-bearing risk: silent fabrication

`AssistantPromptComposer.IsWebSearchActive` is `provider.EnableWebSearch || provider.ProviderType ==
AiProviderType.PiaCloud`. `EnableWebSearch` defaults to false and only OpenAI / OpenRouter / Mistral
have a handler for it. There is no web-fetch or HTTP tool in `BuiltInPluginDefaults` — web search is a
provider capability injected into the request body, not something a blueprint can grant. And when it
is off, `BuildSystemPrompt` says *nothing*: the omitted `webSearchSection` is a citation-format
instruction, so the model is never told it cannot search. "Markets at lunch" on an Ollama provider
therefore does not fail — it prints confident, fabricated prices at 12:00 every day.

Two mitigations, both shipped:

- **`RoutineBlueprintCatalog.WebSearchGuard`**, a `const` clause appended to every web-dependent
  template: answer nothing from memory, carry a source link and a date per claim, and if you cannot
  search say exactly that in one line and report nothing else. It also went onto `topic-digest` and
  `competitor-watch`, which had no such clause before.
- **`RoutineBlueprint.RequiresWebSearch`** (default false), rendered as a "Needs web search" chip on
  the card, plus one hint line under the catalog header when the *default assistant provider* cannot
  search. `IsWebSearchActive` went `private static` → `internal static` so the hint applies the same
  rule the prompt does; `Pia.Wpf.csproj` already carries `InternalsVisibleTo Pia.Wpf.Tests`.

The hint reads the provider `ScheduledResearchProviderResolver` falls back to. A routine that pins its
own provider in the editor is **not** covered — see §7.

## 4. The catalog data

`src/Pia.Wpf/Models/RoutineBlueprint.cs`.

`Category` was repurposed onto `RoutineBlueprintCategories` (`ready` / `your-data`, with
`InDisplayOrder` fixing display order and `StemOf` producing the resx stem). It is free to change:
only `Key` is persisted, as `ScheduledJob.BlueprintKey`.

Final split: **Works right away 14** (`topic-digest` and `competitor-watch` recategorized, plus the
twelve below) · **Uses your Pia data 6**.

| Key | Title (en) | Recurrence | Time | Effort | Slot · default | Web |
|---|---|---|---|---|---|:--:|
| `news-briefing` | Morning headlines | Daily | 06:30 | Low | `focus` · "world news and business" | yes |
| `word-of-the-day` | Word of the day | Daily | 07:30 | Minimal | `language` · "Spanish" | no |
| `security-advisories` | Security advisories | Daily | 08:30 | Medium | `products` · "Windows, Microsoft 365 and Google Chrome" | yes |
| `market-snapshot` | Markets at lunch | Daily | 12:00 | Low | `markets` · "the S&P 500, the Nasdaq, the DAX, EUR/USD and gold" | yes |
| `stock-watchlist` | Your watchlist | Daily | 18:00 | Low | `holdings` · "Apple, Microsoft and Nvidia" | yes |
| `sports-roundup` | Your teams this week | Weekly Mon | 07:00 | Low | `teams` · "Bayern Munich and Real Madrid" | yes |
| `client-watch` | Clients and partners | Weekly Mon | 07:30 | Medium | `accounts` · "Microsoft, SAP and Salesforce" | yes |
| `industry-pulse` | This week in your industry | Weekly Mon | 08:00 | Medium | `industry` · "information technology" | yes |
| `regulation-watch` | Rules and compliance watch | Weekly Mon | 09:00 | Medium | `scope` · "data protection and IT security in the European Union" | yes |
| `release-watch` | New releases | Weekly Mon | 10:00 | Low | `projects` · ".NET, Python and Node.js" | yes |
| `meal-ideas` | This week's meals | Weekly Sat | 10:00 | Low | `preferences` · "quick weeknight dinners for two…" | no |
| `learn-one-thing` | One thing explained | Weekly Sun | 09:00 | Medium | `subject` · "economics" | no |

Constraints every entry has to satisfy — `tests/Pia.Wpf.Tests/Services/RoutineBlueprintCatalogTests.cs`
is the enforcing spec:

- **Daily, or Weekly with a `DefaultDayOfWeek`** (the biconditional is asserted). The record has no
  `DefaultDayOfMonth`, so there are no monthly blueprints without widening it.
- `DefaultTime` round-trips `"HH:mm"` — whole minutes only.
- **`GrantedTools: []`** on all twelve; only `meeting-followup` grants a write tool, and that is
  pinned by a test.
- **`Kind: ScheduledJobKind.Research`.** An `AgentTask` with an empty grant list is remapped by the
  dispatcher to the launcher's `write_file` default, i.e. a card advertising no grants that can write
  files.
- **`DefaultEffort` set explicitly**, never `ReasoningEffort.None`.
- **Resx stem = key in PascalCase**: `Routines_Blueprint_MarketSnapshot_Title`, `_Description`,
  `_Slot_Markets_Label`, `_Slot_Markets_Help`.
- **Every slot carries a non-null default and the template renders from its own defaults with no brace
  left.** A default of `null` makes the slot required and fails the render test.
- **Every default is a real value that produces a real answer on the first run.**
  `competitor-watch`'s `"(none given)"` is not a precedent — its template branches into a vault lookup
  and then a named fallback list, so it always produces something.

House style per template: name the tools, state the output shape and its length cap, forbid what must
not happen, handle the empty case in one line, say "Change nothing", and end with `WebSearchGuard`
**iff** `RequiresWebSearch`. Business briefings use the researched item shape — *what happened, why it
matters, what to watch next*. Money templates carry an explicit no-advice clause; `regulation-watch`
carries a no-legal-advice clause.

**A run has no memory of the previous run** — each firing is a fresh chat. `word-of-the-day` and
`learn-one-thing` therefore key their variety to the date in the system prompt
(`PersonaPromptShape.BuildIdentityBlock` emits "The current date and time is …"), not to "pick a
different one each time", which has nothing to diff against.

## 5. The catalog surface

`RoutinesView` right pane has four mutually exclusive states, exclusive **by construction**, because
the four `ScrollViewer`s are plain siblings in one `Grid` with no `ZIndex` — a second true state does
not merely look wrong, it still hit-tests:

```csharp
ShowsCatalog     => IsCatalogOpen && !IsEditorOpen && SelectedJob is null;
ShowsPlaceholder => !IsCatalogOpen && !IsEditorOpen && SelectedJob is null;
ShowsDetail      => !IsEditorOpen && SelectedJob is not null;
// the editor pane binds IsEditorOpen directly
```

- `Routines_NewJob` rebinds from `StartCreateCommand` to `BrowseBlueprintsCommand`, which cancels any
  edit, clears `SelectedJob` and opens the catalog **unfiltered**.
- The catalog carries "Start from blank instead" (`Routines_StartBlank`, still bound to the untouched
  `StartCreateCommand`) and a close affordance (`Routines_CatalogClose`).
- **First run:** at the end of `RefreshAsync`, `Jobs.Count == 0` clears the selection and opens the
  catalog. That is the whole point of the feature.
- The placeholder pane keeps its icon and text, lost the inline blueprint list, and gained
  `Routines_BrowseCatalog`.
- A save closes both the editor **and** the catalog before the reload, so the pane the save lands on is
  decided synchronously rather than by whether the reload succeeds.

Grouping and search:

- `BlueprintGroups` is rebuilt whenever `SearchQuery` changes; `Blueprints` stays the unfiltered
  source. (`ICollectionViewService` does not fit — it filters in place and cannot regroup.)
- **Match**: trim, split on whitespace, require **every** term case-insensitively in
  `Title + " " + Description`.
- Empty groups are dropped; when every group is empty a "no matches" line shows.
- Default expansion is `Ready` open, `YourData` collapsed. **Entering** a search forces both open —
  only the step into one, so a group collapsed mid-search stays collapsed for the next keystroke.
- Cards are two-up via a `WrapPanel` with a fixed 320 width, not `UniformGrid Columns="2"`: a uniform
  cell takes the tallest card's height, and the descriptions span 61–104 characters. Keep new
  descriptions inside that 60–105 band.
- The card's meta row is recurrence and time only. Kind is absent on purpose: all 20 are `Research`,
  so the chip would repeat 20 times and leak an enum name. The detail pane still shows it, where a
  saved routine can be either kind.
- The group header `Expander` is inside an `ItemsControl.ItemTemplate`, so its AutomationId is the
  **bound** form (`Routines_Category_<key>`), as is each card's (`Routines_Blueprint_<key>`).

`src/Pia.Wpf/Controls/Routines/PiaRoutinesSearchBar.xaml` follows the three existing search bars
(`PiaTodoSearchBar`, `PiaVaultSearchBar`, `PiaHistorySearchBar`): a `UserControl` with a `Query`
dependency property, an icon, a watermark and a borderless `TextBox`. It is a copy rather than a reuse
of `PiaTodoSearchBar` because that control's id is the literal `Todo_SearchQuery`, and CLAUDE.md
requires prefixes disjoint across views.

## 6. Assistant tool surface and localization

`ScheduledJobToolHandler.list_routine_blueprints` renders from `RoutineBlueprintCatalog.All`, so the
twelve appear to the model automatically. Each entry now also prints its **category** and its **needs
web search** state, so the model can steer a user to something that will work on their provider.

Localization: two keys per blueprint plus two per slot, plus the catalog chrome, in
`ViewStrings{,.de,.fr}.resx`. `QueryTemplate` stays English-only — **superseded 2026-09-02**, see
[`2026-09-02-routines-editor-refresh.md`](2026-09-02-routines-editor-refresh.md) §5: the template and
each slot default moved to resx and resolve in the UI locale at creation time. All three files are `i/lf w/crlf` —
edit in the working tree as CRLF. Chrome keys are `Routines_Catalog_*` / `Routines_Search_*` /
`Routines_Category_*`; `Routines_Blueprint_*` is reserved for the per-blueprint namespace that
`EveryResxStemIsItsKeyInPascalCase` defines, so no chrome string may be parked there.

## 7. Known limits

- **The web-search hint covers the default provider only.** `ScheduledResearchProviderResolver`
  prefers a job's pinned `providerId` and only falls back to
  `GetDefaultProviderForModeAsync(WindowMode.Assistant)`, which is what the hint reads. Pin a
  non-searching provider on a web-dependent routine in the editor and nothing warns; the run posts
  "I cannot search the web" and the UI never explains why. The hint's wording is scoped to the default
  so it is not a false alarm, but the editor-side warning is a tracked open step on the checklist.
- **Slot-prompt UI is deferred.** Every slot has a default, the rendered goal lands in the editor's
  goal box, and the user edits it there before saving.
- **No monthly blueprints** — the record has no `DefaultDayOfMonth` and the editor test asserts the
  weekly biconditional.
- **A weather brief and a local-events digest were cut**, on first-run emptiness and retrieval
  quality: both need a city the user has not supplied, and forecasts and event listings are the two
  least reliable things to pull from a general web search. Both become viable if Pia gains a location
  setting or a weather connector.
- **Reviewer notes left as owner calls, not defects:** `industry-pulse`'s "information technology"
  default overlaps `topic-digest`'s "artificial intelligence", and it shares Monday 08:00 with
  `competitor-watch`; `competitor-watch` sits in "Works right away" although its template opens with a
  vault lookup. All three values come from the approved plan.

## 8. Verification

1. `dotnet build -t:Rebuild -v:n` and again `-c Release` — 0 Warning(s), 0 Error(s) in both.
2. `dotnet test` with no filter — the gate is `failed: 0`.
3. **Owed:** run the app against a throwaway profile (`PIA_DATA_DIR` / `PIA_LOCAL_DATA_DIR` plus
   `defaultWindowMode`) so the catalog opens on an empty routine list, and check by hand that the
   catalog opens first with 14 cards two-up under "Works right away" and 6 collapsed under "Uses your
   Pia data"; that `mark` narrows to Markets at lunch / Your watchlist and expands both groups while
   clearing restores; that a card opens the editor prefilled with no literal `{` and Save creates a
   working routine; that "Start from blank instead" opens an empty editor; and that on a non-PiaCloud
   provider with `EnableWebSearch` off the hint line and the eleven chips appear.
4. **Owed:** run one web-dependent routine for real. Set `market-snapshot` a couple of minutes out,
   let it fire, open the chat from the run history, and confirm source links and dates. Then switch
   the default provider to one without web search, fire it again, and confirm it says it cannot search
   **instead of** printing prices. A passing test suite cannot see this.
5. `git ls-files --eol` on the three resx files after any bulk edit — they must stay `i/lf`.
