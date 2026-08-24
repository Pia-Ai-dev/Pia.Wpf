# Report — C5 + C7: the slot engine and the catalog on the assistant's side

**Status:** landed. `dotnet build -t:Rebuild` 0 Warning(s) in both configurations, `dotnet test` no filter
`failed: 0` (4714 total, 4655 succeeded, 1 skipped, 58 not run).
**Owner:** unassigned.
**Written:** 2026-08-24.
**Origin:** [`2026-08-23-c5-c7-batch-brief.md`](2026-08-23-c5-c7-batch-brief.md) §0, executed against
[`2026-08-22-hermes-followup-checklist.md`](2026-08-22-hermes-followup-checklist.md) rows **C5** and **C7**.

---

## 0. The gate, and what it let through

`B4` was ticked before this batch started, and its reading —
[`2026-08-23-compaction-arm-ab-reading.md`](2026-08-23-compaction-arm-ab-reading.md) §1 — refuses the
close-the-item branch on 4 of 4 transcripts (arm B 0.0% against arm A 98.3%, where the rule needed B ≥ 85% of
A). So B6–B10 stay open and nothing in group B was closed by this batch. **Nothing in group B was executed
either**, per §0's hold; the same hold kept A2, A3, A6, A7, P9, all of group D, F1/F3 and the "not yet
planned" table closed.

The working tree at the start held one untracked file — this batch's own brief. That is not §0 rule 1b's
case (someone else's uncommitted work), so the tick proceeded and the brief is committed with the batch.

---

## 1. The three decisions §0 required in writing

**(1) Scope: C5 + C7. C6 is deferred**, adopting §5's recommendation unchanged. C7 needs no new WPF surface,
is dependency-legal without C6, and is what makes C5's load-bearing validation rule matter — at tier 2 the
slot values come from an LLM, which is the case the rule exists for. C6 replaces "edit the prose in the goal
box" with a labelled field for two of eight cards; it is an `M` of `Med` value and it is the only part of
this batch that would have needed a desktop pass. **C5 and C7 are ticked; C6 stays `- [ ]`.**

**(2) Plan §11 Q1 — yes, a job records which blueprint produced it.** `ScheduledJobs.BlueprintKey`, a
nullable TEXT column with both migration halves (`PRAGMA table_info` + `ALTER TABLE`), `QuietOnSuccess` as
the precedent. **Local-only**: absent from `SyncScheduledJob`, from `SyncMapper` and from
`UpsertFromSyncAsync`'s SET list, per E1b — a field the server does not know would come back null and erase
the provenance on the first push-pull cycle. Every pre-existing job carries NULL, and so does every job
created from a blank start.

**(3) Plan §11 Q4 — not answered, because C6 is out of scope.** §5's recommendation stands unexamined for
whoever takes C6: an inline slot block in the existing editor above `Routines_Field_Goal`, visible only for
blueprints with slots, with the one rule that stops slot keystrokes clobbering a hand-edited goal (render on
card click and on slot change; stop re-rendering once the user has edited the goal by hand).

---

## 2. Where the batch deviated from the brief, and why

### 2.1 Trap 4.2 is dissolved rather than defined — `Optional` does not ship

The brief's in-schema fix for `competitor-watch` was "make the slot `Optional` with an empty default", and
§4.2 then asked for the interaction between `Optional` and plan §6 rule 4 (a referenced-but-unfilled slot is
an error, not an empty string) to be defined explicitly.

**Nothing substitutes empty, so the collision never forms.** `competitor-watch`'s `companies` slot has the
default `(none given)`, and the sentence after it branches on exactly that: *"Watch these companies:
{companies}. If no company is named there, start with recall and browse_index…"*. Both renders are
grammatical —

- filled: `Watch these companies: Acme, Globex. If no company is named there, …`
- unfilled: `Watch these companies: (none given). If no company is named there, …`

— where an empty substitution would have left a dangling `: .` mid-sentence. That is the §4.1 "verify this
reads well" check, and it passes with two slots rather than narrowing to one.

Rule 4 therefore keeps its full force: **a referenced slot with neither a supplied value nor a default is an
error.** Resolution is one ladder, `value → Default → error`, and `Optional` would have been a second way to
say what `Default: ""` already says. It is dropped for the same reason rule 3 is not shipped — a flag no
shipped blueprint sets and that duplicates an existing one is inert weight. `Options` and `Strict` go with
it, since rule 3 is what reads them.

### 2.2 Three rules ship, not four

Per §3's decision to ship `RoutineSlotKind.Text` only, plan §6 **rule 3** (enum values checked against
`Options` when `Strict`) has nothing to check and is **not implemented**. It arrives with the first enum
slot, together with `Options` and `Strict`. Rules 1, 2 and 4 all ship and all fire:

| Rule | What it does | Where it is pinned |
|---|---|---|
| 1 | A supplied name that is not a declared slot is refused, not defaulted | `AnUnknownSlotNameIsRefusedAndNamed`, and through the tool in `CreateFromBlueprint_RefusesAnUnknownSlotName` |
| 2 | A slot with no default and no value is refused, and the error names it | `ARequiredSlotWithNoValueIsRefusedAndNamed` |
| 4 | A template reference the blueprint does not declare is an error, not a literal `{topic}` | `AnUndeclaredPlaceholderIsRefused` |

Rule 1 is the load-bearing one and it is deliberately about the **name**, not the value:
`AnUnknownSlotNameIsRefusedEvenWithABlankValue` stops a typo passing whenever the model sends an empty
string. Slot names are ordinal and case-sensitive (`SlotNamesAreCaseSensitive`).

**Neither shipped slot can fire rule 2** — both carry a default, which is what lets the card path render.
Rule 2 fires for any blueprint that declares a default-less slot, and the test above proves it does; that is
a different status from rule 3, which cannot fire at all while one kind exists.

### 2.3 `RoutineSlotKind` ships with one member and no reader

Carried because §3 decided it, and recorded plainly: the field is inert until a second member exists. It is
the seam `Time` and `Enum` land on, and adding them is code-only — `RoutineSlot` is not persisted.

---

## 3. What shipped

### C5 — the slot engine

- `RoutineSlot(Name, Kind, LabelKey, HelpKey, Default)` and `RoutineSlotKind`, beside `RoutineBlueprint`;
  `RoutineBlueprint.Slots` defaults to empty, so six of the eight blueprints are untouched.
- `RoutineBlueprintFill.ToCreateArgs(blueprint, values)` in `Pia.Services`, returning `RoutineFillResult`
  rather than throwing — one error shape (`RoutineFillError`) serves both consumers, with `SlotName` for the
  field an editor would mark and an English `Message` for the tool result the model reads. The result types
  live in `Pia.Models`, not beside the engine: `NamingConventionTests` bans records from the `Pia.Services`
  root namespace, and it caught this rather than a reviewer.
- Two slots: `topic-digest`'s `{topic}` (default *artificial intelligence*, so the rendered prompt is
  byte-for-byte what shipped before) and `competitor-watch`'s `{companies}` (§2.1).
- **Trap 4.3 is closed in the same change.** `StartFromBlueprint` renders through the fill engine, so the
  goal box never shows a literal `{topic}`. The signal the routing landed is the two assertions at
  `RoutinesViewModelTests` that used to read `EditQuery == blueprint.QueryTemplate` and now read
  rendered-with-defaults.
- **The brace test is inverted**, per §3: `NoQueryTemplateCarriesAnUnfilledPlaceholder` becomes
  `EveryBraceInATemplateNamesADeclaredSlotOfThatBlueprint`, which also refuses an unbalanced brace
  (`BracesAreAllPlaceholders`, five theory rows). Two catalog tests join it —
  `EveryDeclaredSlotIsReferencedByItsOwnTemplate` (a slot the template never mentions is a question with no
  effect on the prompt) and `EveryBlueprintRendersCleanlyFromItsOwnDefaults`.
- Slot strings follow §3's stem shape in all three locales, and both existing key tests were extended to
  count them: `EveryResxStemIsItsKeyInPascalCase` and `EveryBlueprintKeyResolvesInAllThreeLocales`.

### C7 — the catalog on the assistant's side

- **`list_routine_blueprints`** — a read, no approval card. A separate tool rather than eight titles baked
  into a description that ships on every turn, per §4.4. Prints each key, what it does, its schedule, its
  write grants, and every slot with its localized label, its help and what it falls back to.
- **`create_routine_from_blueprint`** — takes `blueprintKey`, an optional `slots` JSON object, and
  name/time/day overrides. **It takes no `query`, no `kind` and no `grantedTools`**, which is the mechanism
  for §4.4's first asymmetry: the model has nowhere to widen the grants.
  `CreateFromBlueprint_TakesNoGrantsNoQueryAndNoKind` pins the absence against the shipped JSON schema.
- §4.4's second asymmetry is closed: the blueprint's `DefaultEffort` reaches `CreateAsync`, so the tool path
  no longer drops the pin the card path honours. Kind, recurrence, time, quiet-on-success and the blueprint
  key travel with it.
- `slots` is a JSON object, not a comma-separated list, because a slot value routinely contains commas —
  *which companies* is exactly that case. A non-object or unparseable value is refused with a worked example.
- Every refusal (unknown key, unknown slot, bad JSON) comes back as a **tool result, not an approval card**,
  so the user is never shown a card offering to create the wrong routine.
- The card shows the **rendered** query (`CreateFromBlueprint_ShowsTheRenderedQueryOnTheCard`), plus which
  blueprint it came from and the effort it will run at — two new `Tool_ScheduledResearch_Detail_*` keys in
  three locales. `ActionCardBuilder` gained the create verb for the new name.

### Two things the brief did not name

- **`create_routine_from_blueprint` counts as authority-authoring.** `ToolPermissionService`'s list is
  documented as "tools whose ARGUMENTS ARE A GRANT LIST", which this one is not — but approving it once lets
  Pia stand up a routine that writes unattended, which is what the caution says. Added, with the remark
  amended to say why it is the exception. This is plan §8's argument, so it belongs to C7 rather than to a
  later row.
- **The `@`-command mapping.** `AssistantPromptComposer` loads *only* the tools a tagged domain lists, so a
  user who tags `@research` would have seen the freehand create and not the catalog. Both names added to the
  `Research` row.

---

## 4. What this batch deliberately did not do

- **C6**, per decision (1). The two slots are still edited as prose in the goal box.
- **Rule 3**, per §2.2.
- **The `AgentTask` empty-grant mapping at the dispatch seam** — the C4 decision §8 records why that is a
  separate row, and §0 held it.
- **`BlueprintKey` is written but never read.** Nothing surfaces "which cards do people actually use" yet;
  the column is the prerequisite, and the question is answerable from the database today. Deliberate: a UI
  for it was not in C5, C6 or C7.
- **No desktop pass.** §0's definition of done is the build and test gates, and C6 — the only part that
  would have added a control — is deferred, so no new AutomationId exists and `ViewAutomationIdTests` needed
  no count bump.

---

## 5. Gates

```
dotnet build -t:Rebuild -v:n            0 Warning(s), 0 Error(s)
dotnet build -t:Rebuild -c Release      0 Warning(s), 0 Error(s)
dotnet test   (no filter)               total 4714 · failed 0 · succeeded 4655 · skipped 1 · not run 58
```

The one `Skipped` is the pre-existing speaker-embedding row; the 58 `Not Run` are the `[LiveApiFact]`
entries the runner excludes by default.
