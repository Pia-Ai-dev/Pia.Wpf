# Plan — A Blueprint Catalog Behind the Routines Box

**Status:** planned, not started. Self-contained: everything needed to execute it is below.
**Owner:** unassigned. **Written:** 2026-08-22.
**Origin:** §3.1 of [`2026-08-22-hermes-update-review.md`](2026-08-22-hermes-update-review.md).

---

## 1. The problem

The Routines view shipped 2026-08-17. A user who opens it and clicks **New routine** gets a blank
`Name`, a blank `Query`, a recurrence picker, and a tool-grant list. They have to invent, in one
sitting:

- that a recurring automation is a thing Pia can do at all,
- *what* to automate,
- the prompt that makes it work,
- and which tools to grant it.

Four inventions before anything happens. Meanwhile `ScheduledJobKind.Research` already does
scheduled topic digests perfectly well — it is simply undiscoverable, because nothing in the UI
suggests that's a thing you might want.

**A blueprint catalog turns the blank box into a menu.** Same job engine, same editor, same storage —
the only new thing is a set of curated starting points.

There is a second, quieter benefit that shows up in §8: **a blueprint is also where least-privilege
tool grants get delivered.**

---

## 2. What Pia has today

| Piece | Where | State |
|---|---|---|
| Job record | `src/Pia.Wpf/Models/ScheduledJob.cs:39` | `Name`, `Query`, `Kind`, `GrantedTools`, `ProviderId`, structured recurrence, `QuietOnSuccess`, `OwnerDeviceId`, `ConsecutiveFailures` |
| Job kinds | `ScheduledJob.cs:5` | `enum ScheduledJobKind { Research, AgentTask }` |
| Recurrence | `Models/Reminder.cs:3` | `enum RecurrenceType { Once, Daily, Weekly, Monthly, Yearly }` + `TimeOfDay` / `DayOfWeek?` / `DayOfMonth?` / `Month?` / `SpecificDate?` |
| Creation API | `Services/Interfaces/IScheduledJobService.cs` | `CreateAsync(name, query, recurrence, timeOfDay, dayOfWeek, dayOfMonth, month, specificDate, providerId, grantedTools, kind, quietOnSuccess)` |
| Editor | `ViewModels/RoutinesViewModel.cs` | Already exposes `JobKinds`, `Recurrences`, `DayOfWeekChoices`, `DayOfMonthChoices` (1–31), `MonthChoices`, `ProviderChoices`, and the `EditorWantsSpecificDate` / `WantsDayOfWeek` / `WantsDayOfMonth` / `WantsMonth` visibility flags |
| Recurrence maths | `Services/Scheduling/RecurrenceCalculator.cs` | Complete |
| Catalog | — | **Absent. This plan.** |

**The editor is already a slot form.** It has the field types, the option lists and the conditional
visibility. What it lacks is anything to prefill it with.

---

## 3. The reference implementation

Hermes's `cron/blueprint_catalog.py` (799 lines). Two records and one function:

```python
BlueprintSlot(name, type, label, default, options, optional, help, strict)
#   type ∈ {time, enum, text, weekdays}
#   strict=False → options are suggestions, not a closed set

AutomationBlueprint(key, title, description, category,
                    schedule_template,   # cron string with {slot} placeholders
                    prompt_template,     # seed instruction, may contain {slot}s
                    slots, deliver_default, skills, tags)

fill_blueprint(bp, values) -> create_job kwargs
```

Its load-bearing constraint, quoted from the source: *"The result is passed straight to `create_job`
— **no second schema**."* One definition, four renderers: a GUI form, a pre-filled CLI command, a
`hermes://` deep-link, and a docs catalog entry.

A worked example:

```python
AutomationBlueprint(
    key="weekly-review", title="Weekly review", category="weekly",
    schedule_template="{minute} {hour} * * {dow}",
    prompt_template="Run the weekly-review-planning skill's procedure… "
                    "Recommendations and drafts only — no mutations without approval.",
    slots=[_TIME("18:00"),
           BlueprintSlot("day", "enum", "Which day?", default="sunday",
                         options=("sunday", "monday", "friday", "saturday")),
           _DELIVER],
    skills=("weekly-review-planning",),
)
```

`_TIME` and `_DELIVER` are shared slot factories reused across all 15 blueprints — the catalog stays
readable because the common slots are declared once.

---

## 4. What we do NOT port

**The scheduling half.** Hermes's central design rule is *"users never type raw cron — a blueprint
carries a fixed recurrence in `schedule_template` and parameterizes only the human-friendly parts."*

Pia has no cron strings anywhere. Recurrence is already a typed enum plus typed fields. So
`schedule_template`, `_resolve_schedule`, `WEEKDAY_PRESETS` and all the placeholder-filling — a
meaningful share of those 799 lines — **has no Pia equivalent to build**. A Pia blueprint carries
default values for fields the editor already binds.

**Three of the four renderers.** `blueprint_slash_command`, `blueprint_deeplink` and
`blueprint_catalog_entry` assume a CLI, a URL scheme and a docs site. Pia needs the form renderer
only.

---

## 5. Tiers

Each tier is independently shippable and independently valuable. **Stop after any of them.**

### Tier 0 — a static catalog, no engine

A record holding prefill values, a static catalog, and cards in `RoutinesView`. Clicking a card opens
the **existing** editor prefilled and focused. No slot types, no validation, no new renderer, no
changes to `ScheduledJobService`.

```csharp
public sealed record RoutineBlueprint(
    string Key,
    string TitleKey,           // .resx key, not literal text — see §9
    string DescriptionKey,
    string Category,
    ScheduledJobKind Kind,
    RecurrenceType Recurrence,
    TimeOnly DefaultTime,
    DayOfWeek? DefaultDayOfWeek,
    string QueryTemplate,
    IReadOnlyList<string> GrantedTools,
    bool QuietOnSuccess = false);

internal static class RoutineBlueprintCatalog
{
    public static IReadOnlyList<RoutineBlueprint> All { get; } = [ /* §7 */ ];
}
```

**This is ~80% of the value.** It removes all four inventions from §1 at once.

### Tier 1 — typed slots and a validated fill

For blueprints with a genuine free-text parameter (topic digest: *which topic?*; competitor watch:
*which companies?*). Adds:

```csharp
public enum RoutineSlotKind { Text }

public sealed record RoutineSlot(
    string Name, RoutineSlotKind Kind, string LabelKey, string HelpKey,
    string? Default = null);
```

plus a `RoutineBlueprintFill.ToCreateArgs(blueprint, values)` that validates and renders
`QueryTemplate`. Validation rules are in §6 — **one of them is not optional.**

**As shipped 2026-08-24, and narrower than the shape above.** `Time` and `Enum` are typed editor fields no
blueprint wants as a slot, so only `Text` ships; `Options` and `Strict` go with the enum rule that reads them
(§6 rule 3, not implemented). `Optional` is gone too: resolution is one ladder, `value → Default → error`, so
`Default: ""` already says what an optional slot would have, and nothing substitutes empty. Reasoning in §2 of
[`2026-08-24-c5-c7-batch-report.md`](2026-08-24-c5-c7-batch-report.md).

### Tier 2 — the assistant can use the catalog

Expose the catalog and its slot schema through `ScheduledJobToolHandler` so the agent creates a
routine *from a blueprint* and asks the user for blank slots, instead of inventing a `Query` string
freehand. Hermes's framing: *"Agent → a seed prompt; it asks for any blank/ambiguous slot."*

### Tier 3 — skip

Deep-links, slash commands, a docs catalog. Revisit only if Pia grows a CLI or a URL scheme.

---

## 6. Validation rules (Tier 1)

Copy these from `fill_blueprint`. The first one is load-bearing.

1. **Reject unknown slot names.** Hermes's own comment: *"a typo'd `tiem=07:15` must not silently
   create a job with the default time."* This matters **more** in Pia than in hermes, because at
   Tier 2 the slot values come from an **LLM**. Silent default-substitution on a hallucinated slot
   name is a failure nobody notices — the routine just quietly does the wrong thing every morning.
2. **A missing required slot names the slot in the error**, so the form can show a field-level error
   *and* the agent knows exactly what to ask for. One error shape serves both surfaces.
3. **Enum values are checked against `Options`** when `Strict` is true. `Strict = false` means the
   options are suggestions and any value passes (hermes uses this where the valid set depends on the
   user's configuration and is validated further downstream).
   **Not shipped.** With `Text` the only kind there is nothing for it to check, and a rule that cannot fire
   is worse than an absent one. It arrives with the first enum slot, together with `Options` and `Strict`.
4. **A `QueryTemplate` referencing a slot that wasn't filled is an error, not an empty string.** In
   hermes a missing `.format()` key raises `BlueprintFillError`. The C# equivalent must not silently
   leave a literal `{topic}` in the prompt.
   **As shipped this also covers the undeclared reference**: a `{name}` the blueprint does not declare is an
   error of its own kind, and a slot that is declared but has neither a value nor a `Default` is rule 2's.

**Three of the four ship, 2026-08-24.** Rules 1, 2 and 4 are implemented and each has a test that fires it;
rule 3 is the one above. Rule 1 is deliberately about the slot NAME rather than its value, so a typo cannot
pass by carrying an empty string.

---

## 7. The catalog to ship

Only automations Pia can actually execute. Pia has Todos, Reminders, Kanban, Vault, Memory, web
search with citations, and meeting transcripts. It has **no email and no calendar connector**, so
hermes's inbox monitor and its calendar-dependent briefings do not apply as written; the price-watch
blueprint needs stateful fetch-and-compare that Pia has no home for.

| Key | Kind | Drives | Text slots (Tier 1) |
|---|---|---|---|
| `morning-brief` | Research | Todos due today + active reminders | — |
| `evening-winddown` | Research | Tomorrow's todos + reminders | — |
| `weekly-review` | Research | Completed vs open todos, where open work sits across the Kanban columns, the week's vault notes | — |
| `topic-digest` | Research | *Already fully supported — just undiscoverable* | **topic** |
| `competitor-watch` | Research | Named companies, material news only | **companies** |
| `meeting-followup` | Research | New vault transcripts → action items | — |
| `bills-renewals` | Research | Recurring-payment heads-up | — |
| `habit-checkin` | Research | Recurring nudge + reflection | — |

**Kind is `Research` for all eight, including the seven that only read** — every row but
`meeting-followup`, whose sole grant is `create_todo`. The `AgentTask` dispatch leg maps
a job's empty `GrantedTools` list to `null`, and `HeadlessRunLauncher` turns that `null` into
`HeadlessRunRequest.DefaultGrantedWrites = ["write_file"]` — so an `AgentTask` card advertising no grants
would in fact run able to write files, the exact opposite of what §8 promises. The `Research` leg passes
`GrantedTools` through verbatim, and empty means empty. `Research` is not web-search-specific: it is one
tool-enabled background turn on the same plugin pipeline, with enough tool rounds for the two to six reads
these blueprints make. The visible cost is that the Kind chip on those cards reads "Research" rather than
"Agent run". Full reasoning and the escalation if the owner keeps `AgentTask`:
[`2026-08-22-c4-before-c5-decision.md`](2026-08-22-c4-before-c5-decision.md) §8. Pinned by
`TheGrantsABlueprintAdvertisesAreTheGrantsItsRunGets`, which computes the effective write set the way the
dispatcher does and fails the moment a Kind flips to `AgentTask` with an empty grant list.

**No time or day slot anywhere.** `DefaultTime` and `DefaultDayOfWeek` are typed record fields the editor
already binds at `Routines_Field_Time` and `Routines_Field_DayOfWeek`, so Tier 1 adds nothing for them.

**The text slots on `weekly-review`, `bills-renewals` and `habit-checkin` are dropped — the read surface
covers them.** `query_todos` prints Title, Priority, Status, Column, Due (flagged `OVERDUE`), Notes,
CompletedAt and LinkedReminderId; `query_reminders` prints Description, Recurrence, Time, Status, Next
fire, Day of week, Day of month and Month; and `PersonaPromptShape.BuildIdentityBlock` puts the current
date and time into every turn's system prompt. So "what renews in the next fourteen days" and "which
recurring commitments were due today" are answerable as prose over the user's own data — strictly better
than a typed literal that goes stale the moment they add a subscription. That leaves Tier 1 scoped to two
text slots, `topic-digest`/topic and `competitor-watch`/companies, and its inverted brace test validating
two templates rather than seven.

**`weekly-review` cannot report a stalled Kanban card.** `TodoToolHandler.HandleQueryTodos` prints no
`CreatedAt` and no `UpdatedAt`, and `list_columns` returns only a column's name, its markers and its todo
count. Nothing in the tool output records when a card last moved, so the template reports *where* open
work is sitting and is explicitly instructed not to call anything stalled, stuck or neglected. Closing
this would mean widening the tool output, which is its own row.

`topic-digest` is the one to build first: it needs no new capability at all, and it demonstrates that
the catalog surfaces things Pia could *always* do.

`meeting-followup` is worth calling out — it depends on the speaker-attribution work currently in
flight, and its prompt should follow that work's evidence-first framing (state transcript
completeness and low-confidence spans before extracting anything).

---

## 8. The security angle

`GrantedTools` already exists on `ScheduledJob`, and today the user picks it by hand in the editor.
Hand-picking tool grants is the standard route to over-granting: faced with a checklist and no
guidance, people tick more than they need.

**A blueprint carries a least-privilege default per automation type** — a topic digest gets web
search and not `delete_file`. That makes the safe choice the *default* choice rather than an informed
one, and it does so without adding a policy layer.

This is a second, independent reason to build Tier 0, and it should be stated in the review of any PR
that adds a blueprint: **every new blueprint declares the narrowest grant set that makes it work.**

---

## 9. Costs and constraints

- **Localization is the main real cost.** Every title, description, slot label and help string needs
  `.resx` entries in `en` / `de` / `fr` (`src/Pia.Wpf/Resources/Strings/`). Hence `TitleKey` rather
  than `Title` in §5 — resolving through `ILocalizationService` from the start avoids a rename later.
- **`QueryTemplate` is user-facing content once rendered.** If it is ever logged, it goes through
  `SensitiveDebug` — a rendered query can contain whatever the user typed into a text slot.
- **No second job engine.** Every path ends at the existing `IScheduledJobService.CreateAsync`. If a
  blueprint needs a field that method doesn't take, add the parameter there — do not add a parallel
  create path. This is hermes's constraint and it is the one that keeps the feature small.
- **`ScheduledJobKind` is append-only** (`ScheduledJob.cs:7-12` — it crosses the sync wire as an int
  with no `Enum.IsDefined` validation). Blueprints must use the existing two kinds.
- **Blueprint keys are persisted-adjacent.** If a created job records which blueprint produced it
  (useful, and cheap to add via `ExtraJson`), the key becomes a compatibility surface: never rename a
  shipped key, only add.

---

## 10. Work breakdown

| Step | Tier | Notes |
|---|---|---|
| 1 | 0 | `RoutineBlueprint` record + `RoutineBlueprintCatalog` with `topic-digest` only |
| 2 | 0 | `.resx` entries (en/de/fr) for that one blueprint — proves the localization shape before ×8 |
| 3 | 0 | Card list in `RoutinesView`; click → existing editor, prefilled, focused. AutomationIds per `docs/ui_automation/ui-automation-playbook.md` |
| 4 | 0 | Remaining seven blueprints + their strings |
| 5 | 1 | `RoutineSlot` + `RoutineBlueprintFill.ToCreateArgs` with three of the four §6 rules — **done 2026-08-24** |
| 6 | 1 | A slot-prompt step before the editor opens, for blueprints with text slots — **deferred** |
| 7 | 2 | Catalog + slot schema exposed via `ScheduledJobToolHandler` — **done 2026-08-24** |

Steps 1–3 are the vertical slice: one blueprint, end to end, localized and automatable. Everything
after is repetition or extension.

---

## 11. Open questions for the owner

1. **Does a created job record its blueprint key?** Cheap via `ExtraJson`, and it makes "how many
   people use the weekly review" answerable. But see the compatibility note in §9.
2. **Cards or a list?** Eight blueprints is small enough for either. Cards read as a menu, which is
   the point; a list is less work and less visual noise next to the existing job list.
3. **Where does the catalog sit relative to "New routine"?** Catalog-first with a blank-start escape
   hatch is the recommendation — it is what makes the feature do its job — but it demotes the current
   primary action, so it is a UI call worth making deliberately.
4. **Should Tier 1 slot-prompting reuse the existing clarification UI** (`RunClarifications`,
   `UserInputRequestStore`) rather than a bespoke dialog? Probably yes at Tier 2; possibly overkill
   at Tier 1.
