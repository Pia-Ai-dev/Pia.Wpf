# Routines editor refresh: drafting, required fields, localized blueprints, less prose

**Status:** implemented on `feature/routines-editor-refresh`; one live-run check owed (§10.5).
Extended 2026-09-02 with drafted tool grants (§8a) and the slot-to-instruction tint (§8b).
**Owner:** Marco Altmann. **Written:** 2026-09-02.
**Origin:** owner request of 2026-09-02 (five numbered concerns), plus the first round of test-user
feedback on the blueprint catalog shipped by
[`2026-08-24-blueprint-catalog-expansion.md`](2026-08-24-blueprint-catalog-expansion.md).

---

## 1. The five concerns

1. **No AI assist in the editor.** The persona editor drafts a whole persona from one sentence
   (`PersonaEdit_Description` + `PersonaEdit_GenerateDraft`); the routines editor has nothing.
2. **Required fields are invisible until Save is pressed.** `SaveAsync` validates and writes a
   `StatusMessage`; nothing marks a field beforehand and Save is never disabled.
3. **Blueprint prompts are English-only.** Titles, descriptions, slot labels and slot help live in
   `ViewStrings{,.de,.fr}.resx`; `QueryTemplate` and each slot's `Default` are hard-coded English in
   `RoutineBlueprint.cs`, so a German user opens a card and reads an English wall of text.
4. **Test users find the blueprints too complex** and cannot tell what to edit to make one their own.
5. **The editor wastes vertical space** — every dropdown owns a full row at a fixed 240 px, and the
   goal box is `MinHeight="60"` for what is currently an 875-character prompt.

## 2. Owner decisions (2026-09-02)

Concern 4 was the open design question; three calls were made.

- **Shorten only.** No slot-first restructure of the editor, no collapsing the goal box behind an
  expander. The goal box stays the primary surface; the text in it gets much shorter.
- **A length bar, enforced by a test: template body ≤ 320 characters**, excluding the shared guard
  suffix. Precedent: the release-notes length bar.
- **One slot per blueprint**, as today. No second knob.
- Concern 3 is **localize at creation time** — the goal is resolved in the current UI language when
  the card opens or the tool creates the job, and the stored text never changes afterwards. This is
  already how `EditName` behaves, so no live re-render and no migration.

This supersedes §6 of the catalog-expansion plan ("`QueryTemplate` stays English-only"). The
"slot-prompt UI" entry on that plan's checklist stays deferred, and is now deferred *by decision*
rather than by omission.

## 3. What localization does and does not buy

`AssistantPromptComposer.BuildLanguageInstruction()` already appends *"Always respond to the user in
'German' unless the user asks you to switch."*, and both headless paths
(`HeadlessTurnExecutor`, `BackgroundAssistantTurnRunner`) build their prompt through `PrepareTurn`.
**A German user already gets German output from an English template.** Localizing the blueprints
therefore buys readability of the prefilled goal in the editor — which is exactly concern 4 — and not
output language. It is worth doing for that reason alone, but the de/fr prose does not carry the
model-behaviour risk that a mistranslated tool instruction would.

## 4. Concern 4 — the cut

Measured bodies today (guard excluded), highest first: `meeting-followup` 1434, `weekly-review` 1043,
`competitor-watch` 974, `regulation-watch` 791, `client-watch` 780, `market-snapshot` 726,
`stock-watchlist` 724, `habit-checkin` 701, `learn-one-thing` 677, `bills-renewals` 671,
`sports-roundup` 671, `morning-brief` 667, `security-advisories` 673, `meal-ideas` 570,
`release-watch` 551, `word-of-the-day` 531, `industry-pulse` 526, `evening-winddown` 513,
`topic-digest` 402, `news-briefing` 393. Mean 693. All twenty get rewritten.

The house style of the 2026-08-24 plan — *name the tools, state the output shape and its length cap,
forbid what must not happen, handle the empty case in one line, say "Change nothing"* — is what
produced the length. Four of its five clauses are **identical on every card in a family**, so they move
out of the body into a shared suffix and stop counting against the bar:

- **`WebSearchGuard`** (already exists, 14 cards) shortens from 249 characters to one sentence.
- **`ReadOnlyGuard`** (new, the 6 `your-data` cards) carries what those six repeat today: read only,
  create/edit/complete nothing, and name a read that came back empty rather than filling the gap.
  `meeting-followup` is the one card that *does* write, so it takes a **`WriteGuard`** instead: name
  what you could not read before you act on it, and check the existing list before creating.

What is left in a body is the routine's own instruction: what to look at, what shape the answer takes,
and its length cap. Target 2–3 sentences.

**What this deliberately drops**, and why it is acceptable:

- The per-card no-advice clauses on `market-snapshot`, `stock-watchlist` and `release-watch`, and the
  no-legal-advice clause on `regulation-watch`. These were reviewer-driven and are the strongest
  candidates to keep; each is compressed to a trailing four-word clause in the body rather than a
  sentence ("no forecast and no recommendation"), which fits inside 320.
- The placeholder-detection branches on `sports-roundup`, `client-watch` and `competitor-watch` ("if
  that list still names Bayern Munich and Real Madrid, say it is a placeholder"). These exist because
  a shipped default can be left unedited. They go; the catalog test
  `ATemplateThatQuotesItsOwnDefault_QuotesItVerbatim` becomes vacuous and is deleted with them.
  `competitor-watch` additionally loses its vault-lookup branch, which is what §7 of the 2026-08-24
  plan already flagged as odd for a card filed under "Works right away". **Its slot default therefore
  changed from `"(none given)"` to a real list** (`Microsoft, Google, OpenAI and Anthropic`): the sentinel
  only made sense as the trigger for the branch that is gone, and a blank default would have failed the
  renders-from-its-own-defaults test. Its slot help no longer promises to read the vault. Routines already
  created are unaffected — their goal was frozen at creation.
- **`meeting-followup` loses its evidence-quality step** — state whether the transcript is complete
  and whether the speaker labels are real names before extracting anything. This is the one cut with
  a real quality cost, on the one blueprint that holds a write grant. Half of it survives in
  `WriteGuard`. Flagged on the checklist as worth revisiting after a live run.
- `weekly-review` loses its two "do not guess" paragraphs (no movement date, so nothing is "stalled";
  no note date, so nothing belongs to "this week"). Compressed to one clause.

## 5. Concern 3 — mechanics

`QueryTemplate` and `RoutineSlot.Default` are user-visible text, so they move to resx and the record
carries keys instead:

- `RoutineBlueprint.QueryTemplate` → **`QueryKey`**, stem-derived: `Routines_Blueprint_<Stem>_Query`.
- `RoutineSlot.Default` → **`DefaultKey`**, nullable: `Routines_Blueprint_<Stem>_Slot_<Name>_Default`.
  Null still means *required*, which is what makes an unfilled reference an error rather than a hole.
- The guards are **not** `Routines_Blueprint_*` — `EveryResxStemIsItsKeyInPascalCase` owns that
  namespace and would reject a key that names no blueprint. They go to
  `Routines_Catalog_WebSearchGuard` / `_ReadOnlyGuard` / `_WriteGuard`, and the record gains a
  `GuardKey` (nullable) so the choice is data rather than a `switch`.
- **`{slot}` names stay English identifiers in every locale.** They are the fill contract, not prose:
  `RoutineBlueprintFill` matches on `slot.Name`, and `ScheduledJobToolHandler` prints them for the
  model.

A new record resolves a blueprint's text for one locale, so `RoutineBlueprintFill` keeps taking a
value rather than a service:

```csharp
public sealed record RoutineBlueprintText(string Template, IReadOnlyDictionary<string, string?> SlotDefaults)
{
    public static RoutineBlueprintText Resolve(RoutineBlueprint blueprint, Func<string, string> lookup);
}
```

`ToCreateArgs(blueprint, values)` becomes `ToCreateArgs(blueprint, text, values)`. Production passes
`key => _localization[key]` in both callers — `RoutinesViewModel` (three call sites:
`StartFromBlueprint`, `RenderGoalFromSlots`, `ResetEditSlots`) and `ScheduledJobToolHandler` (the
create-from-blueprint leg and the `list_routine_blueprints` slot listing). The tool path uses the UI
locale too: what it creates is stored and then read by the user.

**Register.** The German half of this view is informal throughout — 21 of its `Routines_*` values use
`du`/`dein` and, before this change, none used `Sie`. The three formal strings this change first shipped
(`Routines_Draft_DescribeHint`, `Routines_Draft_Failed`, and the CompetitorWatch slot help, which
contradicted its own sibling `ClientWatch_Slot_Accounts_Help`) were rewritten to match. French is `vous`
throughout and stays that way. The templates use the informal imperative in both locales regardless of the
chrome, because the goal box holds a prompt: a prompt in the polite register reads as a letter.

**The de and fr templates are written, not translated** (owner, 2026-09-02). An instruction that
reads as an English sentence with German words in it is exactly the wall of text concern 4 is about, so
each locale gets the phrasing a native speaker would have typed — its own verb mood and its own
register, not a clause-for-clause mapping of the English. Slot defaults are localized the same way:
`"Bayern Munich and Real Madrid"` is a sensible German default and a poor French one.

`RoutineFillError.Message` and the rest of `list_routine_blueprints` stay English — they are read by
the model, not by the user.

**Tests move to per-locale.** Every catalog invariant that reads the template (braces are all
placeholders, every placeholder names a declared slot, every declared slot is referenced, renders
cleanly from its own defaults, the length bar) runs for `en`, `de` and `fr` via
`ViewStrings.ResourceManager.GetString(key, culture)` — the same mechanism
`LocalizationTests.Every*KeyResolvesInAllThreeLocales` already uses.
`ATemplateThatSearchesTheWeb_AdvertisesThatItNeedsWebSearch` matches an English phrase, so it stays
`en`-only with a comment saying why; the flag is per blueprint, not per locale, so one locale proves
it.

## 6. Concern 1 — generate with AI

Mirrors the persona assist end to end, including its streaming path (Pia Cloud only returns the
expected shape on the streaming leg) and `ExtractJsonObject` for the reply.

- New `RoutineDraft(Name, Goal, Recurrence, DayOfWeek, TimeOfDay, Effort, NeedsWebSearch)` in
  `Models/`, every member nullable, falling back to `Goal` = raw text when the JSON does not parse.
- `ITextOptimizationService.GenerateRoutineDraftAsync(string description, Guid? providerId = null)`.
  Every test fake is an NSubstitute `Substitute.For<ITextOptimizationService>()`, so the new member
  costs nothing there; `RoutinesViewModel` gains a constructor parameter, which reaches three `new
  RoutinesViewModel(` sites in two test files.
- The draft prompt asks for the goal **in the language of the description**, in the shortened house
  shape of §4, and appends the matching guard when `needsWebSearch`. `Kind` is not drafted: it is
  `Research`, because an `AgentTask` with an empty grant list is remapped by the dispatcher to the
  launcher's `write_file` default (§4 of the 2026-08-24 plan). This is set explicitly in the draft apply,
  and it has to be: `StartCreate` opens the editor on `AgentTask`, so a drafted read-only routine saved
  from a blank start would otherwise have run able to write files while its own tool picker read "runs
  only read and report".
  The editor's `AgentTask` default is therefore deliberately **not** protected by the latch: setting a
  property to the value it already holds raises no change notification, so "the user left it on AgentTask"
  and "AgentTask is the default" are the same state, and of the two readings the safe one wins. A kind the
  user actually picked — a meeting routine — does survive, and a test holds that.
- **Prefill rule:** `Name` and `Goal` fill only when blank, as the persona assist does. Kind,
  recurrence, day, time and effort apply only while the editor still holds the values `StartCreate`
  set — the `_pickersUntouched` latch — so a draft never moves a picker the user has already chosen,
  and never moves anything at all on top of a card or an existing routine.
- The persona command has no `catch`, so a provider failure escapes it. This one catches and writes
  `Routines_Draft_Failed` to the existing `StatusMessage`.
- The description and the draft are user content: `SensitiveDebug`, never above it.

## 7. Concern 2 — required fields

`SaveAsync` already defines the set; the editor just never showed it. Required: **Name** always;
**Goal** unless the routine is a meeting; **meeting URL** and **the consent checkbox** when it is;
**a time that parses as `HH:mm`**.

- `CanSave` on the view model, with `[NotifyPropertyChangedFor(nameof(CanSave))]` on `EditName`,
  `EditQuery`, `EditKind`, `EditMeetingUrl`, `EditMeetingConsent` and `EditTimeOfDay`.
- `Routines_RequiredHint` above the fields, `PiaRequiredHintStyle` + the shared
  `Dialog_Edit_RequiredHint` string, shown while `CanSave` is false — the same shape as
  `Personas_RequiredHint`.
- Save binds `IsEnabled="{Binding CanSave}"` rather than gating `CanExecute`, so the reason stays
  visible. `SaveAsync`'s own checks stay: they are the format checks (a URL that is not a Teams link,
  a time that is not `HH:mm`), which a required-field marker cannot express.
- The `*` goes in the resx value, as `Dialog_PersonaEdit_Name` does. `Settings_ScheduledJobs_Field_Goal`
  is rendered twice in `RoutinesView` — the read-only detail pane and the editor — so the editor gets
  its own `Routines_Field_Name` / `_Goal` / `_Time` keys and the detail pane keeps the unstarred one.
  That leaves `Settings_ScheduledJobs_Field_Name` and `_Time` with no reader at all, so both were
  **deleted from all three locales**; nothing in the suite catches a dead resx key, so leaving them would
  have meant leaving them forever. `_Goal` stays — the detail pane still renders it.

## 8. Concern 5 — layout

Two `*` columns, fixed 240 px widths dropped, one pair per row:

| Left | Right |
|---|---|
| Kind | Recurrence |
| the recurrence detail — day of week, or month + day of month, or date | Time |
| Provider | Persona |
| Effort | — |

Only `Yearly` shows two recurrence-detail fields (`EditorWantsMonth` implies
`EditorWantsDayOfMonth`), so column 0 stacks them and column 1 holds Time in every case. Name, the
slot fields and Goal stay full width; Goal's `MinHeight` goes 60 → 150, which is what makes a
320-character goal readable without scrolling.

Every control added here — the describe box, the draft button, the required hint — needs a
`Routines_*` `AutomationProperties.AutomationId`, a row in
`tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs` in the same change, and a line in
`docs/ui_automation/ui-automation-playbook.md`.

One thing the first render changed: **Time went to column 0 and the recurrence detail to column 2**, the
reverse of the table above. Time is the only one of the pair that always shows, so on a Daily routine the
planned order left it alone on the right with a hole beside it. The detail now also sits directly under the
Repeats picker it belongs to. Effort sits in a two-column grid's left cell rather than at a fixed 240, so it
lines up with Provider above it.

## 8a. Drafted tool grants (owner request, 2026-09-02, after the first live draft)

The draft now picks the write tools the goal needs. `RoutineDraft` gained `Tools`, and
`GenerateRoutineDraftAsync` gained an `availableTools` parameter — the model is handed the names this
device actually offers rather than left to remember what a tool is called.

Two filters, because a grant is the one drafted field that can act on its own:

- **The offer** excludes `IsPresumedExternalDeleteLike` names and anything `ServerDeclaredDestructive`.
  This is the same create-time rule the model already faces on the tool path, where
  `ScheduledJobToolHandler` rejects those names out of a `grantedTools` CSV — so the two model-driven
  grant paths agree rather than each having their own idea. `delete_file` is *ours*, so it stays on offer:
  the filter is about destructive names we do not ship.
- **The reply** is intersected with the offer. A name the catalog does not have is dropped, not kept as an
  orphan row: the user never asked for it, and a stored grant travels to every other device on the next
  sync.

Applied only when **nothing is ticked**. A grant has a real blank state, unlike a picker, so this needs no
latch: a tick the user made stands, and a card that grants nothing meant it.

## 8b. Tying the slot field to the instruction (owner request, 2026-09-02)

Test users read the editor and did not see that the slot field feeds the prose below it. The owner asked
for the substituted value to be coloured inside the instruction.

**A WPF `TextBox` cannot colour part of its text.** Measured, not assumed: the span was wired through and
tinted via `IsInactiveSelectionHighlightEnabled` + `SelectionBrush`, and nothing rendered. Setting the same
selection externally through UIA rendered nothing either, and a plain `TextBox` behaved identically — so it
is neither the WPF-UI theme nor the timing. Do not spend another round on that route.

What ships instead, chosen by the owner over a `RichTextBox`: **the goal renders as read-only coloured text
until the user clicks it, and as the plain box afterwards.** Only ever one of the two is visible, which is
what removes the alignment problem an overlay would have had.

- The view model already tracked the two states. `ShowsGoalPreview` is
  `GoalHighlightLength > 0 && !IsGoalEditing && !EditorIsMeeting`; `EditGoalCommand` sets `IsGoalEditing`,
  and each editor entry point clears it so the second card is not opened on a plain box.
- `GoalPrefix` / `GoalHighlightText` / `GoalSuffix` are bound as three `Run`s. A test asserts they
  reassemble into exactly `EditQuery` — the user must not be reading something other than what will run.
- The span is `template.IndexOf("{slot}")` plus the substituted value's length. Sound because **every
  template carries exactly one placeholder in all three locales**, which the catalog tests assert; with one
  slot per blueprint there is nothing else it could point at.
- The preview is a `Button`, not a `Border`, so a keyboard user reaches it and it carries a
  `Routines_Field_Goal_Preview` id. The code-behind hands focus and the caret to the real box on the
  switch, or the click would leave the user typing into nothing.
- A hand edit clears the span, so the tint cannot outlive the rendered goal it describes.

## 9. Order

**4 → 3 → 1**, with 2 and 5 anywhere. Shortening first cuts the translation surface by about two
thirds (20 × 3 × ~693 chars becomes 20 × 3 × ~300), and the draft prompt in concern 1 has to encode
the shortened house shape, so it cannot be written before that shape exists. Items 4 and 3 land as one
edit to the resx files: writing the long English templates into resx and then shortening them would
touch all sixty strings twice.

## 10. Verification

1. `dotnet build -t:Rebuild -v:n` and again `-c Release` — `0 Warning(s)`, `0 Error(s)` in both.
2. `dotnet test` with no filter — the gate is `failed: 0`.
3. `git ls-files --eol` on the three resx files: they must stay `i/lf w/crlf`.
4. **Done 2026-09-02**, on a throwaway profile driven through UIA at a maximized window: a card opens the
   editor with a fully localized prefill in `en`, `de` and `fr` (`Nachrichten am Morgen` /
   `Weltgeschehen und Wirtschaft`; `Apprends-moi un mot … en espagnol`, which is the article trap the
   French defaults were written around); clearing Name disables Save and shows `Routines_RequiredHint`;
   the draft command reports `Routines_Draft_Failed` instead of throwing when no provider is set up.
   **Still owed:** one real draft against a configured provider, and the paired dropdowns at the
   *narrowest* pane width.
5. **Done 2026-09-02:** the owner ran the AI draft against a configured provider and it worked as
   designed. The drafted-tools and preview rounds were then verified on a throwaway profile: the slot
   value renders in the accent colour inside the instruction, and invoking the preview hands a focused,
   caret-at-end goal box over with the text unchanged.

6. **Owed by a human:** fire one shortened web routine and one shortened `your-data` routine for real
   and confirm the shorter prompt still produces sourced, dated output and still refuses honestly on a
   provider that cannot search. Step 10 of the 2026-08-24 checklist was never ticked, so the guard has
   never been verified at *any* length; this closes both.
