# Decision — C4 (the seven blueprints) before C5 (the slot engine)

**Status:** decided, not started. Self-contained: everything needed to execute the decision is below.
**Owner:** unassigned. **Written:** 2026-08-22.
**Origin:** the C4/C5 ordering question raised while briefing the next batch
([`2026-08-22-next-batch-brief.md`](2026-08-22-next-batch-brief.md)), over
[`2026-08-22-routine-blueprints-plan.md`](2026-08-22-routine-blueprints-plan.md) and the checklist rows
in [`2026-08-22-hermes-followup-checklist.md`](2026-08-22-hermes-followup-checklist.md).

---

## 1. The question

Both C4 and C5 are unblocked. C4 (`Deps: C3`) and C5 (`Deps: C1`) each depend only on a ticked row, so
no dependency argument decides the order. Which ships first?

- **C4** — the remaining seven blueprints (`morning-brief`, `evening-winddown`, `weekly-review`,
  `competitor-watch`, `meeting-followup`, `bills-renewals`, `habit-checkin`) plus their en/de/fr
  strings, each declaring its narrowest `GrantedTools` set. `M`, `High`.
- **C5** — `RoutineSlot` + `RoutineBlueprintFill.ToCreateArgs` with the four validation rules
  (rejecting an unknown slot name is the load-bearing one). `M`, `Med`.

---

## 2. The two options as they were framed

**C4-first** — ship the visible half. The Routines page goes from one card to eight; the blank-box fix
that C3 made visible becomes a real menu. The cost: seven prose templates written against today's
manual workaround, each with localized prose in three languages that C6 may later have to retract.

**C5-first** — build the fill engine and the validation rules before writing anything that would use
the workaround, so no localized prose describing a manual step ever ships. The cost: C5's only
consumer today is `RoutinesViewModel.StartFromBlueprint`, so the work is invisible until C6.

---

## 3. Decision

**Ship C4 now** — the seven remaining blueprints, written data-grounded with zero braces — **then C5 at
its corrected, smaller scope of two text slots.**

---

## 4. Why

**C5 alone is invisible.** Its only consumer would be `RoutinesViewModel.StartFromBlueprint`, which
assigns `EditQuery = blueprint.QueryTemplate` into an `AcceptsReturn="True"` TextBox. Render
`topic-digest`'s one slot with its default and the behaviour is byte-identical to today. C5-first spends
an `M` of `Med`-value work to leave the Routines page showing exactly one card next to a fill engine
that this repo's own review lens reads as dead code until C6 lands.

**The premise C5-first rests on does not hold.** It claims four of seven blueprints have a genuine
free-text parameter. Checked against the read surface:

- `ReminderToolHandler.HandleQueryReminders` prints each reminder's Description, Recurrence, Time,
  Status, Next fire and Day of week / Day of month / Month.
- `TodoToolHandler.HandleQueryTodos` prints Title, Priority/Status, Column, Due (with `OVERDUE`),
  Notes, CompletedAt and LinkedReminderId.
- `PersonaPromptShape.BuildIdentityBlock` injects `The current date and time is yyyy-MM-dd HH:mm (dddd)`
  into every turn's system prompt, so "due today", "tomorrow" and "the next fourteen days" are
  answerable without a slot.

So `bills-renewals`, `habit-checkin`, `morning-brief`, `evening-winddown` and `weekly-review` can be
written as prose that scans the user's real data — strictly better than a typed slot that goes stale the
moment they add a subscription. Plan §7 lists `weekly-review`'s slots as "time, day", and both are
already typed record fields bound at `Routines_Field_Time` and `Routines_Field_DayOfWeek`, so C5 adds
nothing there.

That leaves **exactly one genuine literal** among the seven (`competitor-watch`), which means writing C4
first shrinks C5 from five text slots to two — information obtainable only by writing the prose bodies.

**Writing C4 is also what surfaces the traps.** Two of them:

- `search_files` is scoped to the assistant files folder, while meeting transcripts live at the
  vault-relative `sources/meeting-<yyyyMMdd-HHmm>-<slug>.md` path
  (`MeetingVaultMarkdown.BuildReference`). A validator built first would have validated a template that
  silently finds nothing.
- The AgentTask/Research grant asymmetry in §7 below, which inverts C4's own security claim for five of
  eight cards.

---

## 5. What was rejected, and why

C5-first is the stronger-sounding case and lands three real hits.

1. **Localization retraction.** `topic-digest`'s manual workaround is already frozen into human-reviewed
   de/fr prose, and every blueprint that repeats it is a string to retract in three languages. Plan §9
   names localization as the main cost, and that is fair.
2. **The brace ban is a prohibition on the feature.** `NoQueryTemplateCarriesAnUnfilledPlaceholder`
   forbids `{` and `}` in any template, which encodes Tier 0 as a ban rather than a not-yet. The inverted
   form ("every brace names a declared slot") is the strictly stronger test the seven should land under.
3. **Nothing links a saved job back to its card.** `ScheduledJobs` has no `BlueprintKey`, and `Query` is
   on the wire, so blueprint attribution is unanswerable.

All three are small and **none is decided by ordering.**

- The retraction cost is avoidable by wording descriptions as **outcomes rather than mechanisms** — a
  writing rule, not a schema — and C4 can pay down the one existing instance rather than multiply it.
- The test inversion is one method either way; if six of seven templates never need a brace, C4-first
  makes the inversion *cheaper*, not dearer.
- The attribution gap is already open post-C3, is not widened by seven more prefills, and C5 does not
  close it. C5's own plan lists it as a separate step.

Where the case actually breaks is its count. Trading the `High`-value user-visible half of the feature
for an `M` of `Med`-value plumbing built to serve slots that three of those blueprints turn out not to
want is the wrong trade — and the C5-first brief concedes the shape of it: an engine with no UI
consuming it until C6 lands.

---

## 6. Mitigations

These are conditions on how C4 is written, not optional advice.

### 6.1 How the seven `QueryTemplate`s are to be written

**Six of seven are data-grounded** — no literal, no brace — so
`NoQueryTemplateCarriesAnUnfilledPlaceholder` stays green **unmodified**. Every template names its tools
explicitly and ends with a graceful-degradation clause.

| Key | Reads | The template says |
|---|---|---|
| `morning-brief` | `query_todos`, `query_reminders` | Report only what is due or fires today plus anything already marked `OVERDUE`, each with its time; order by time, not priority; change nothing. |
| `evening-winddown` | `query_todos`, `query_reminders` | Name what went overdue today and was not completed, then what is due or fires tomorrow. No encouragement, no advice, no writes. |
| `weekly-review` | `query_todos` (`completed`, then `all`), `list_columns`, `browse_index`, `read_topic` | What finished, what is still open and overdue, how open work sits across the columns; then the week's notes worth carrying forward. **Explicitly instruct it not to call anything stalled** (see §8). |
| `bills-renewals` | `query_todos` (`all`), `query_reminders` (`all`) | Scan titles, notes and descriptions for renewal / subscription / invoice / licence / insurance / membership wording; report only what falls due in the next fourteen days, with its date and where it was found; invent no amount; if nothing matches, say exactly that in one line rather than widening the wording until something does. |
| `habit-checkin` | `query_reminders`, `query_todos` (`completed`) | Take the recurring reminders as standing commitments (Recurrence is printed); say which were due today and whether anything related was ticked off; three lines, then one short reflection question on the weakest one. Do not congratulate or moralise. |
| `meeting-followup` | `recall`, `browse_index`, `read_topic`, `read_source`, `query_todos` | Evidence first — see §6.3. |

**`competitor-watch` is the one honest literal.** Web search needs company names and Pia has no field
that holds "companies I track". Order matters: `recall` and `browse_index` **first** for companies the
vault already names, and watch those; only if it names none, fall back to a named placeholder list
(recommend Microsoft, Google, OpenAI, Anthropic) **and say in one line that it is a placeholder**, so the
list gets corrected rather than quietly digested every week. Then search the web for material
developments in the past week — launches, pricing changes, funding, leadership changes, outages,
withdrawals — at most two items per company, one sentence each, with a source link and a date; skip
speculation, opinion and re-reporting; per-company "nothing material" gets its own one-liner. It writes
nothing, so the worst unedited outcome is noise and token spend.

### 6.2 The degradation clause is not defensive padding

`AssistantPromptComposer.PrepareTurn` has a `persona.ToolScope` allowlist branch that can filter
`query_todos` out of the turn entirely, and `PluginService.GetAllTools` drops a disabled plugin's tools.
A template that assumes the read succeeded can produce a confident fabrication on a restricted persona.
So: **if a read comes back empty or unavailable, say so in one line rather than inventing.**

### 6.3 `meeting-followup` is evidence-first

Per review recommendation #15 and the speaker-attribution work in flight. Order is the whole point:

1. `recall` for this week's meetings; `browse_index` only if recall misses. Forced by the code:
   `RecallHit.Tier` documents that the recall pool excludes `sources/`, so recall finds the ingested
   topic page and the source is reached through that topic's cited refs — recall/`browse_index` →
   `read_topic` → cited source ref → `read_source`.
2. **Not `search_files`** — `FilesToolHandler` registers it as searching the assistant files folder, so
   it would silently find nothing.
3. For each meeting source dated today, state **first**: title and date, who the front matter lists as
   attendees, whether the transcript looks complete or cut off, and whether speaker labels are real
   names, generic (`Speaker 1`), or absent because labelling was switched off. All three shapes are real
   — `SpeakerToDisplayNameConverter.Resolve` falls a blank label back to the localized "me"/"them", and
   the `MeetingSuppressSpeakerLabels` policy landed on this branch. Name any span you are unsure of.
4. **Only then** extract action items, attributing an owner only where the transcript supports it —
   "owner unclear" rather than a guess.
5. `query_todos` **before** creating anything; skip a follow-up already on the list; then `create_todo`
   once per genuinely new item with the meeting title and date in the notes, so a re-run is not a
   duplicate factory.
6. If no meeting source is dated today, or a read is unavailable: one line, create nothing.

This template is deliberately the longest of the eight. The evidence gate is the row's value, and prompt
text is not subject to comment discipline.

### 6.4 Blunt the localization-retraction cost by never naming a mechanism

Every new `Description` states the **outcome** ("a short heads-up on what renews soon"), never
"change X in the goal box before saving". In the same C4 commit, rewrite `topic-digest`'s three existing
descriptions to outcome wording:

| File | Line | Today ends with | Becomes |
|---|---|---|---|
| `ViewStrings.resx` | 1143 | "Change the topic in the goal box before saving." | dropped |
| `ViewStrings.de.resx` | 1133 | "Ändere das Thema im Feld „Ziel“, bevor du speicherst." | dropped |
| `ViewStrings.fr.resx` | 1133 | "Changez le sujet dans le champ « Objectif » avant d'enregistrer." | dropped |

The "you can change anything before saving" promise already lives in `Routines_Blueprints_Hint`
(`ViewStrings.resx:1141`), which is where it belongs and where it does not multiply by eight. **After C4
there is exactly zero localized prose to retract when C6 lands.**

### 6.5 Other conditions

- **Correct plan §7's slot table in the same change** — drop the text slots from `weekly-review`,
  `bills-renewals` and `habit-checkin`, recording that the read surface covers them. C5 then lands scoped
  to two text slots (`topic-digest`/topic, `competitor-watch`/companies) and drops from `M` toward `S`,
  and its inverted test validates two templates, not seven.
- ~~**Answer plan §11 Q1 (does a created job record its blueprint key?) before C5, not before C4.**~~ It is
  orthogonal — an additive `ScheduledJobs` column with the `PRAGMA table_info` / `ALTER TABLE` pair
  (`SqliteContext.cs` ~325 and ~687, `QuietOnSuccess` as the precedent) plus a deliberate sync call — and
  every job created without it simply carries NULL.
  **Answered yes and closed 2026-08-24, with C5.** `ScheduledJobs.BlueprintKey`, both halves, and off the
  sync wire per E1b. It is appended to the END of the positional SELECT because `MapJob` reads by ordinal.
  §1(2) of [`2026-08-24-c5-c7-batch-report.md`](2026-08-24-c5-c7-batch-report.md).
- **Author C4 as ONE agent**, despite the batch brief's "C4 fans out one agent per blueprint". Seven
  agents would all write the same `RoutineBlueprint.cs` and the same three `.resx` files, which breaks the
  batch's own disjoint-file-ownership rule.
- **Descriptions are drafted, not translated.** The de/fr strings need real prose in the existing register
  (de informal *du*, fr formal *vous*) — not English with an umlaut. A native read is the gate.

---

## 7. The `GrantedTools` table — the security half of the feature

Plan §8 says every new blueprint declares the narrowest grant set that makes it work, and that this must
be stated in the PR. It belongs here too, because of the finding in §8 below.

Reminder of the tool split: `GrantedTools` holds **write** tool names only. Reads are always allowed and
never need a grant. Web search is a **provider capability**, not a grantable tool — which is why
`topic-digest` ships `GrantedTools: []`.

| Key | resx stem | Category | Kind | Recurrence | DefaultTime | DefaultDayOfWeek | GrantedTools | Justification |
|---|---|---|---|---|---|---|---|---|
| `topic-digest` | `TopicDigest` | `daily` | `Research` | `Daily` | 08:00 | — | `[]` | Ships today. Web search is a provider capability, so a digest needs no write grant at all. |
| `morning-brief` | `MorningBrief` | `daily` | `Research` | `Daily` | 07:00 | — | `[]` | It reports. Reads (`query_todos`, `query_reminders`) need no grant, and a brief that edits your list is not a brief. |
| `evening-winddown` | `EveningWinddown` | `daily` | `Research` | `Daily` | 20:00 | — | `[]` | Same two reads, same reasoning. Nothing in "what slipped today" implies a write. |
| `weekly-review` | `WeeklyReview` | `weekly` | `Research` | `Weekly` | 17:00 | **Friday** | `[]` | A retrospective. `DefaultDayOfWeek` **must** be non-null or `EveryPrefillIsLegalForTheEditor` fails its `Weekly ⇔ non-null` biconditional. |
| `competitor-watch` | `CompetitorWatch` | `weekly` | `Research` | `Weekly` | 08:00 | Monday | `[]` | Web search is ungrantable; `recall` / `browse_index` for the fallback are reads. Worst unedited outcome is noise, not damage. |
| `meeting-followup` | `MeetingFollowup` | `meetings` | `Research` | `Daily` | 18:00 | — | `["create_todo"]` | **The one write grant.** Turning notes into follow-ups is the row's value. `create_todo` only — no update, no delete — and the template queries first so a re-run is not a duplicate factory. |
| `bills-renewals` | `BillsRenewals` | `weekly` | `Research` | `Weekly` | 09:00 | Monday | `[]` | Deliberately **not** `create_reminder`: a recurring keyword scan granted `create_reminder` is a duplicate factory, and a heads-up is a report. |
| `habit-checkin` | `HabitCheckin` | `daily` | `Research` | `Daily` | 21:00 | — | `[]` | Reads recurrence straight off `query_reminders`. A nudge that silently edits your habits is worse than a nudge. |

Nothing delete-like anywhere. `NoBlueprintGrantsADeleteLikeTool` already iterates `All` and stays green.

Note the three casings of a key, all of which must line up per blueprint:

- the key string — `meeting-followup`
- the resx stem — `Routines_Blueprint_MeetingFollowup_Title` (PascalCase, hyphens dropped)
- the card's AutomationId — `Routines_Blueprint_meeting-followup` (raw key, hyphens kept), the shape
  `RoutinesViewModelTests` pins for `topic-digest`.

---

## 8. The finding that changes the table: the two legs disagree about an empty grant

**Verified in the code, and it inverts C4's security claim for five of eight cards.**

- **AgentTask leg.** `ScheduledJobBackgroundService.ExecuteAgentTaskAsync` (~line 516) maps
  `GrantedWrites: job.GrantedTools.Count > 0 ? job.GrantedTools : null`, and
  `HeadlessRunLauncher` (line 336) turns that `null` into
  `HeadlessRunRequest.DefaultGrantedWrites = ["write_file"]`
  (`IHeadlessRunLauncher.cs:44`). So an **AgentTask blueprint shipping `GrantedTools: []` actually runs
  with `write_file`** — the exact opposite of what the card advertises, and a contradiction of
  `ScheduledJob.GrantedTools`' own XML-doc ("writes are denied unless listed here").
- **Research leg.** `ScheduledJobBackgroundService` (line 724) passes
  `GrantedWriteTools = job.GrantedTools` straight through, and `BackgroundAssistantTurnRunner`
  (line 142) builds the gate set from it verbatim. Empty means empty.

`ScheduledJobKind.Research` is **not** web-search-specific: it is one tool-enabled background turn on
the same plugin pipeline (`AssistantPromptComposer.PrepareTurn` with `atCommands: []` hands it every
enabled plugin's tools), with `AppSettings.MaxToolRoundsPerStep = 24` rounds — comfortably enough for
2–6 reads. A named grant still auto-executes there (`ResolveToolGate`'s
`IsNamedGrant: grantedWrites.Contains(...)`).

**Therefore:** the six read-only blueprints ship as **`Research`**, not `AgentTask` as plan §7's table
says, and `meeting-followup` ships an explicit `["create_todo"]` which *replaces* the default and so
withholds `write_file` too. Net: all eight cards get exactly the grants they advertise.

**Do NOT change the mapping at the seam in C4.** Every existing user AgentTask job with no grants relies
on today's `write_file` default; flipping it silently would break running routines. The tri-state fix
("user chose nothing" vs "blueprint chose nothing") is a separate row, and it lands cheapest inside
[#11](2026-08-22-routine-persona-effort-plan.md), which is already opening
`ScheduledJobBackgroundService` and the `ScheduledJobs` columns.

**Escalation, if the owner keeps plan §7's `AgentTask` column:** five of eight cards then ship with an
effective `write_file` grant they do not advertise, and the PR statement required by plan §8 is false for
those five and must say so explicitly.

The visible cost of choosing `Research`: the Kind chip on those cards reads "Research" / "Recherche"
rather than "Agent run". The label is generic enough to read as "look things up and report", but it is a
deviation from both plan §7 and the batch brief's table, which is why it is recorded here.

This is pinned by a test rather than a comment —
`TheGrantsABlueprintAdvertisesAreTheGrantsItsRunGets` computes the effective set the way the dispatcher
does and asserts it equals `bp.GrantedTools`, so it fails the moment someone flips a Kind to `AgentTask`
with an empty grant list, or changes `DefaultGrantedWrites`.

---

## 9. Corrections to [`2026-08-22-routine-blueprints-plan.md`](2026-08-22-routine-blueprints-plan.md) §7

To land in the same commit as C4:

1. **Kind column** — the six read-only rows change from `AgentTask` to `Research`, with one line saying
   why (§8).
2. **Slots column** — drop the text slots from `weekly-review`, `bills-renewals` and `habit-checkin`;
   the read surface covers them. C5's remaining scope is two text slots.
3. **`weekly-review`'s "stalled Kanban cards" is not answerable.** `TodoToolHandler.HandleQueryTodos`
   prints Title / Priority / Status / Column / Due (+`OVERDUE`) / Notes / CompletedAt /
   LinkedReminderId and never `CreatedAt` or `UpdatedAt`; `list_columns` returns only name + count.
   Nothing records when a card last moved. The template must decline to call anything stalled rather
   than guess, and this limitation is recorded in the plan.

---

## 10. What would reverse this

Two things.

1. **If C6 (the slot-prompt step) is pulled into this same batch** rather than a later one, C5 stops
   being invisible plumbing, the retraction cost becomes immediate rather than hypothetical, and
   slots-then-blueprints becomes the cheaper order. **Decide C6's batch before starting C4.**
2. **If the person writing the seven prose bodies finds that three or more cannot be written without a
   user-supplied literal** — most likely failure: `bills-renewals`, if scanning todo Notes and reminder
   Descriptions for renewal wording proves too noisy to be useful in practice — then the "one honest
   literal" premise is wrong, this verdict's arithmetic collapses, and the right move is to stop, land
   C5, and bring the seven back through it.

---

## 11. Choices made while recording this, flagged as such

None of these came from the review or the plan; they are the integrator's calls and are cheap to change.

- **`Research` for six blueprints** (§8) — a deviation from plan §7 and the batch brief. Recommended, not
  mandated; the decision gate is the first step of C4.
- **The default times** — 07:00 / 20:00 / Friday 17:00 / Monday 08:00 / 18:00 / Monday 09:00 / 21:00.
  Each is a one-line change and the editor opens prefilled anyway, but they are the first thing a user
  sees.
- **`Category` as cadence** (`daily` / `weekly` / `meetings`) rather than a topical taxonomy. It has no
  consumer today beyond an is-not-blank assert and is rendered nowhere, so it is safe to retax later —
  unlike `Key`, it is not persisted-adjacent.
- **`competitor-watch`'s named placeholder list.** Naming no company is safer and less opinionated;
  naming some makes the card demonstrably work on first click. Recommended as named-plus-disclaimer.
- **`meeting-followup` at Daily 18:00.** Daily catches the day's meetings while they are fresh, but on a
  meeting-free day it fires and reports nothing. `QuietOnSuccess` cannot help — it suppresses the success
  toast unconditionally, including on the runs that did create follow-ups — so all seven leave it
  defaulted.
- **Eight cards in a 420px column.** The blueprint block is an `ItemsControl` inside an `Auto`
  ScrollViewer whose StackPanel is `VerticalAlignment="Center"`, so content taller than the viewport
  scrolls from the top rather than clipping — but the placeholder becomes a scrolling menu. Cards-vs-list
  is plan §11 Q2 and explicitly out of C4's scope; eyeball it on Windows.
