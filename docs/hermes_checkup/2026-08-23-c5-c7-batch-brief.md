# Brief — C5–C7: the slot engine, the slot prompt, and the catalog on the assistant's side

**Status:** ready to execute, **gated on B4**. Self-contained: paste §0 into a fresh session and it has
everything.
**Owner:** unassigned. No Windows-desktop measurement is required — the build and test gates are the
whole verification, plus one eyeball pass if C6 lands.
**Written:** 2026-08-23.
**Origin:** the readiness question put over
[`2026-08-22-hermes-followup-checklist.md`](2026-08-22-hermes-followup-checklist.md) rows **C5, C6, C7**,
answered against [`2026-08-22-routine-blueprints-plan.md`](2026-08-22-routine-blueprints-plan.md) (tiers 1
and 2, and its §6 validation rules) and
[`2026-08-22-c4-before-c5-decision.md`](2026-08-22-c4-before-c5-decision.md) (which shrank C5 to two text
slots and made plan §11 Q1 an explicit pre-C5 gate).

---

## 0. The loop prompt

> **Each tick, read `docs/hermes_checkup/2026-08-22-hermes-followup-checklist.md` first and branch on
> what it says. Do not carry state between ticks in your head — the checklist is the state.**
>
> **Two separate signals, do not conflate them.** `B4`'s box governs whether the batch may *start* at
> all; the working tree governs whether *this tick* may touch anything.
>
> 1. **If row `B4` is still `- [ ]`:** this tick does nothing. Another session owns this branch for the
>    B-track compaction sweep, and two sessions implementing at once on `feature/agent-run-spine` is how
>    a rebase eats someone's work. Report "B4 still open" and wait.
> 1b. **If `B4` is ticked but `git status` shows modified or untracked files you do not own:** wait
>    **this tick only** and name the files you are waiting on. Do not read that as the gate being shut —
>    it is very likely unrelated work (F1's test-hygiene changes live in the same test project), and it
>    will clear on its own. Never commit over someone else's uncommitted work.
> 2. **If `B4` is ticked, the tree is yours, and any of `C5` / `C6` / `C7` is still `- [ ]`:** read
>    `docs/hermes_checkup/2026-08-23-c5-c7-batch-brief.md` and implement the batch it scopes — the
>    `RoutineSlot` type and the validated fill (**C5**), the slot prompt in the routines editor (**C6**),
>    and the catalog exposed to the assistant through `ScheduledJobToolHandler` (**C7**). One batch:
>    plan → implement → build gate → test gate → simplify → review → fix → finalize. Review is five
>    dimensions (correctness · CLAUDE.md conformance · tests · integration and architecture · scope and
>    dead code), and every finding is killed or confirmed before anyone acts on it.
> 3. **Stop the loop once the batch has finalized** — that is, once the rows it *took* are ticked and any
>    row it deliberately deferred is recorded as deferred in its report. Do not wait for `C6`'s box: §5
>    recommends deferring it, so a stop condition of "all three ticked" would poll forever after the work
>    is done.
>
> **Three decisions the planning phase must answer in writing rather than settle silently in code.** The
> brief carries a recommendation for each; adopt it or overrule it, but say which and why.
> **(1) Batch scope** — all three rows, or C5+C7 with C6 deferred. C7 is dependency-legal without C6 and
> is what makes C5's load-bearing validation rule matter at all, since at tier 2 the slot values come
> from an LLM.
> **(2) Plan §11 Q1** — does a created job record which blueprint produced it? The C4 decision made this
> a pre-C5 gate. There is no `ExtraJson` on `ScheduledJob`, so it is an additive column with the
> `PRAGMA table_info` / `ALTER TABLE` pair, and per E1b it must stay off the sync wire or a server that
> does not know the field nulls it back out.
> **(3) Plan §11 Q4** — the slot prompt's shape, if C6 is in scope: an inline block in the existing
> editor, a step before the editor opens, or the run-clarification pipeline.
>
> **The hold: every other checklist row stays closed.** Nothing from group A (A2, A3, A6, A7, P9),
> nothing from group B, nothing from group D, nothing from F1/F2, nothing from "not yet planned". Do not
> touch the `AgentTask` empty-grant mapping at the dispatch seam — the C4 decision §8 records why that
> fix is a separate row.
>
> Everything else this brief says.

---

## 1. Where the repo is

Branch `feature/agent-run-spine`, at or after `750385cd`. **Another session commits to this branch**, and
at the time of writing the tree was dirty with four files that belong to that session
(`SqliteContext.cs`, `ScheduledJobToolIntegrationTests.cs`, `FilesToolHandlerWriteTests.cs`,
`WpfStaHost.cs`). Check `git log` and `git status` before trusting any line number below, and never
commit over someone else's uncommitted work.

`dotnet test` with no filter was last reported `failed: 0` when E7 closed group E on 2026-08-23. Treat any
failure as a real regression until proven otherwise.

**Known and not this batch's job:** the gate mutates the machine's real Pia profile (`history.db`,
`%LOCALAPPDATA%\Pia\runs`) — that is row **F1**, deliberately still open.

---

## 2. What already shipped, and where it is

| Piece | Where |
|---|---|
| `RoutineBlueprint` record + `RoutineBlueprintCatalog.All` (8 blueprints, `Find`) | `src/Pia.Wpf/Models/RoutineBlueprint.cs` |
| Card list, `StartFromBlueprint`, `RoutineBlueprintCard` | `src/Pia.Wpf/ViewModels/RoutinesViewModel.cs:240`, `:440`, `:780` |
| Cards + the inline editor panel | `src/Pia.Wpf/Views/RoutinesView.xaml:236`, `:430`+ |
| Strings, en/de/fr | `src/Pia.Wpf/Resources/Strings/ViewStrings*.resx:1153-1170` |
| Catalog tests (the brace ban and the grant equality among them) | `tests/Pia.Wpf.Tests/Services/RoutineBlueprintCatalogTests.cs` |
| The prefill assertions | `tests/Pia.Wpf.Tests/ViewModels/RoutinesViewModelTests.cs:651`, `:694` |
| The create path every route must end at | `IScheduledJobService.CreateAsync` — takes `personaId` and `reasoningEffort` since E1 |
| The assistant's four routine tools | `src/Pia.Wpf/Services/ScheduledJobToolHandler.cs:32` (declarations), `:318` (schemas) |

Two facts that shape C5 specifically:

- **`DefaultEffort` already exists** on `RoutineBlueprint` (E8) and `StartFromBlueprint` carries it into
  the editor through `ApplyPinChoices`.
- **The two slots are real but neither is a bare substitution.** `topic-digest` hard-codes *artificial
  intelligence* in its `QueryTemplate`; `competitor-watch` hard-codes a four-company fallback list inside
  a recall-first branch. See §4.

---

## 3. Decided, not to be re-litigated

- **The brace test inverts.** `NoQueryTemplateCarriesAnUnfilledPlaceholder` currently forbids `{` and `}`
  outright. It becomes *every brace names a declared slot of that blueprint* — the C4 decision §5 already
  called the inverted form the stronger test.
- **C5 is `S`, not the checklist's `M`.** The C4 decision §6.5 re-rated it downward the moment the slot
  count fell from five to two. Update the row when you tick it.
- **Ship `RoutineSlotKind.Text` only.** Time and day are typed record fields the editor already binds, and
  no shipping blueprint wants an enum. Consequence to state plainly rather than paper over: plan §6 rule 3
  (options checked when `Strict`) then has nothing to check, so either implement it unexercised or land 3
  of the 4 rules and say rule 3 arrives with the first enum slot. Do not ship "four rules" with one that
  cannot fire.
- **The rendered query stays English.** `QueryTemplate` is an English literal in code today; slot *labels*
  and *help* are localized, the prompt body is not.
- **Fill returns a result, not an exception.** One error shape serves the form's field-level error and the
  tool's error string — plan §6 rule 2.
- **Slot string keys follow the existing stem shape**: `Routines_Blueprint_<Stem>_Slot_<SlotName>_Label`
  and `_Help`, en/de/fr, real prose in register (de informal *du*, fr formal *vous*) — not English with an
  umlaut. `EveryResxStemIsItsKeyInPascalCase` and `EveryBlueprintKeyResolvesInAllThreeLocales` will both
  need extending.

---

## 4. Four traps, with what to do about each

**4.1 `competitor-watch` does not substitute cleanly.** The first third of its template is *recall /
`browse_index` for tracked companies, else fall back to a placeholder list and say so*. A user-supplied
`{companies}` makes that whole branch dead prose. The in-schema fix needs no new schema: make the slot
`Optional` with an empty default and lead the template with it — "Watch these companies: {companies}. If
that list is empty, start with recall and browse_index…". **Verify this reads well before committing to
two slots**; if it cannot carry one cleanly, C5 narrows to a single slot and says so.

**4.2 `Optional` collides with plan §6 rules 2 and 4.** Rule 4 makes a referenced-but-unfilled slot an
error rather than an empty string — but an *optional* unfilled slot must substitute empty, which is
exactly the competitor-watch default path. Define the interaction explicitly, or it gets built to throw on
first click.

**4.3 C5 must route `StartFromBlueprint` through the fill engine in the same change.** The moment a
template carries braces, the card path shows a literal `{topic}` in the goal box in front of the user.
This is a sequencing constraint, not a preference. `RoutinesViewModelTests:651` and `:694` currently assert
`EditQuery == blueprint.QueryTemplate` verbatim; they become rendered-with-defaults, and that change is the
signal the routing landed.

**4.4 C7 inherits two asymmetries from the card path.**

- **Grants.** The blueprint's `GrantedTools` must be authoritative on the tool path and the model must not
  be able to widen them, or plan §8's security argument is void exactly where the values come from an LLM.
  `TheGrantsABlueprintAdvertisesAreTheGrantsItsRunGets` covers the dispatcher, not this route.
- **The effort pin.** `create_scheduled_research`'s schema takes no persona and no effort, while
  `CreateAsync` has taken both since E1 and the card path passes `blueprint.DefaultEffort`. A
  create-from-blueprint that goes through today's schema silently drops the pin the card honours — the same
  shape of defect as the `AgentTask`/`Research` grant asymmetry the C4 decision §8 caught.

Two smaller C7 notes: prefer a separate `list_routine_blueprints` read tool over baking eight titles,
descriptions and slot schemas into a description that ships on every turn (the catalog is only relevant
when the user asks for a routine); and the pending-action confirmation card (`ActionCardBuilder`, pinned by
`ActionCardBuilderScheduledCategoryTests`) must show the **rendered** query, not the template.

---

## 5. Recommendations for the three decisions

1. **Scope: C5 + C7, C6 deferred.** C7 needs no new WPF surface, is dependency-legal without C6, and gives
   C5 a consumer so it does not read as dead code. C6 is an `M` of `Med` value that replaces "edit the
   prose in the goal box" with a labelled field for two of eight cards. Taking all three is defensible;
   taking C5 alone is not.
2. **Blueprint key: yes, an additive local-only column.** `ScheduledJobs.BlueprintKey`, `QuietOnSuccess`
   as the precedent for both migration halves and for staying absent from `SyncScheduledJob`. Every
   pre-existing job carries NULL. It makes "which cards do people actually use" answerable; putting it on
   the wire needs the server to learn the field first, which is separate work.
3. **C6, if in scope: an inline slot block in the editor**, above `Routines_Field_Goal`, visible only for
   blueprints with slots. The editor is already an inline panel rather than a modal, so this adds no new
   surface. It needs one rule, or slot keystrokes clobber the user's own edits: render the goal on card
   click and on slot change, and **stop re-rendering it once the user has edited the goal by hand**.

---

## 6. Definition of done

- `dotnet build -t:Rebuild` in **both** configurations, `0 Warning(s)` read off MSBuild's summary line.
- `dotnet test`, no filter, `failed: 0`.
- New AutomationIds registered in `ViewAutomationIdTests` with the `[InlineData]` count bumped in the same
  change, if C6 lands a control.
- The checklist rows ticked **in the commit that lands them**, each carrying what it actually shipped —
  including the corrected C5 effort rating and, where the batch deviated, what changed and why.
- Plan §5 and §10 corrected in the same commit if the slot set, the kinds or the rule count moved.
