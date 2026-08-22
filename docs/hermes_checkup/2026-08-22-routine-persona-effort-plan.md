# Plan — Per-Routine Persona and Reasoning Effort on `ScheduledJob`

**Status:** E1–E6 and E8 landed; **E7 (the Windows verification) is outstanding**, so nothing here has
been built or tested on Windows yet. Tracked in
[`2026-08-22-hermes-followup-checklist.md`](2026-08-22-hermes-followup-checklist.md) as group **E**.
Self-contained: everything needed to execute it is below.
**Owner:** unassigned. **Written:** 2026-08-22.
**Origin:** recommendation #11 of
[`2026-08-22-hermes-update-review.md`](2026-08-22-hermes-update-review.md), promoted out of the
"Not yet planned" table of
[`2026-08-22-hermes-followup-checklist.md`](2026-08-22-hermes-followup-checklist.md) (line 207) into a
new group **E**.

Siblings: [`2026-08-22-routine-blueprints-plan.md`](2026-08-22-routine-blueprints-plan.md) and its
ordering decision [`2026-08-22-c4-before-c5-decision.md`](2026-08-22-c4-before-c5-decision.md), which
this plan composes with in §10 (E8).

---

## 1. What the row delivers

A routine gets two **device-local** pins:

- `ScheduledJob.PersonaId` (`Guid?`) — which persona's system prompt, archetype and preferred provider
  the run uses.
- `ScheduledJob.ReasoningEffort` (`ReasoningEffort?`) — which reasoning effort the run's provider is
  stamped with.

Persisted as two nullable TEXT columns, exposed in the routines editor, and honoured on **both** dispatch
legs (AgentTask and Research). NULL on either means "no pin, inherit" — so every existing profile is
already correct the instant the columns exist.

`Pia.Models.ReasoningEffort = { None, Minimal, Low, Medium, High, XHigh }`. `Persona` already carries
`PreferredProviderId` and `ReasoningEffort?`; `ScheduledJob` already carries `ProviderId`, `GrantedTools`
(synced) and `QuietOnSuccess` (local-only).

---

## 2. Decision — neither field goes on the sync wire

Recorded here in full, because half of "done" for this row is that the decision does not get
re-litigated. Both fields are answered separately.

### 2.1 The decision

> **PersonaId: does NOT sync — device-local. ReasoningEffort: does NOT sync — device-local. No split:
> both stay off `SyncScheduledJob` and out of `UpsertFromSyncAsync`'s SET list.**

### 2.2 Because

**(a) The wire is a two-repo contract this repo does not control.** The server lives in the sibling repo
`/Users/marcoaltmann/Documents/GitHub/Pia`, which consumes `Pia.Shared` as a **git submodule pinned to
Pia.Wpf @ cda4590f (2026-08-12)** — ten days behind this branch. Its push handler
(`Pia/src/Pia.Server/Sync/SyncService.cs:1750-1822`) copies scheduled-job fields **one by one by hand**
into `ServerScheduledJob`. There is no extension-data passthrough, so a new plaintext field is silently
dropped until a submodule bump + entity + EF migration + deploy all land.

**The destructive consequence, verified.** `ScheduledJobService.UpsertFromSyncAsync`
(`ScheduledJobService.cs:668-697`) writes its whole SET list **unconditionally**, and the pull-apply at
`SyncClientService.cs:1532` uses `>=` so a tie favours remote. A synced-but-server-dropped field
therefore returns null and **NULLs the user's own pin on their own machine** — which is exactly the
documented Persona `OutputFormat` bug ([`../sync-e2ee-overview.md`](../sync-e2ee-overview.md), line 48:
"the originating device wiped its own local value after one push→pull cycle"), fixed only by a server
DTO/entity/mapper change plus `AddPersonaOutputFormat`.

**(b) and (c) The persona travels but the gate does not.** User personas do sync (`SyncPersona`, with
`PreferredProviderId` / `ReasoningEffort` in plaintext) and built-ins are identical per client — **but**
`AgentPersonaRoster` (`AppSettings.cs:251`) and `BlockedBuiltInPersonas` are absent from `SyncSettings`.
The gate that decides whether a pin is honoured is per-device even though the persona is not.

**(d) An unresolvable pin fails quietly, unlike a provider.** `UpsertFromSyncAsync` validates nothing and
would store an unresolvable Guid verbatim, and `HeadlessRunLauncher.ResolveRunPersonaAsync`
(`HeadlessRunLauncher.cs:1093-1126`) answers off-roster or unresolvable by logging at Information and
silently substituting the mode persona. That is the opposite of `ProviderId`, whose unresolvable case is
a **visible** `Failed` status plus one re-arm (`ScheduledJobService.cs:467` `IsPreModelFailure`) — which
is what breaks the "ProviderId syncs, so PersonaId must" analogy.

Both fields therefore stay inside `src/Pia.Wpf`, exactly like `QuietOnSuccess`. Local→synced is a
strictly additive change later; a wire slot is provably unreclaimable in this codebase
(`SyncScheduledJob.cs:27` still carries dead `AnswerLength` "for wire-contract stability").

### 2.3 Rejected because

The pro-sync case is strong on principle and its best argument survives: `SyncScheduledJob`'s own summary
draws the boundary at "execution state", a persona pin is run *input* not state, `ModePersonaDefaults`
already syncs a bare persona Guid with a full tombstoning merge (`SyncMapper.cs:953`), and editing a
routine is genuinely not ownership-gated (`RoutinesViewModel.cs:415` plus `CanActOnSelection` at :364) —
so a device-local pin means an edit made on a non-owner device is confirmed by the UI and never runs.

It loses on two claims that failed verification and one asymmetry:

1. Its own implementation plan says to add both columns to `UpsertFromSyncAsync`'s SET list. **That IS the
   data-loss mechanism**, so its reassurance that "both nullable… the apply side never clobbers a local
   value" is false against the actual code.
2. Its server-contract citation (`docs/server/assistant-chat-history.md` §1, "unknown fields must be
   stored and returned verbatim") is scoped to the chat-history document endpoint, not
   `/api/sync/push`, whose mapper drops unknowns.
3. Its "peers need it" framing is undercut by a fact it concedes: only the owner device ever fires
   (`ScheduledJobService.cs:117`, :131-139), so nothing is *acted on* elsewhere. The only real loss is
   authoring convenience, which is repairable in-client without a coordinated release.

The E2EE-demotion worry raised by the other brief is discounted: `ProviderId` / `GrantedTools` already
ride inside the ciphertext (`SyncMapper.cs:992`), so plaintext leakage would be carelessness, not
inevitability.

### 2.4 Mitigations that this plan therefore carries

1. **Close the honesty gap the pro-sync side is right about, on a surface that already exists.**
   `RoutinesView.xaml:320-329` already shows a `Routines_NotOwnedHere` banner (bound to
   `SelectedJob.OwnedByThisDevice` via `InverseBooleanToVisibilityConverter`) when the selected routine
   belongs to another device. Extend that string, and give the two new editor fields the same
   visibility/disabled treatment, so a non-owner device never silently accepts a pin that will not run.
   This is the whole cost of the losing argument and it is `XS`-sized.
2. **Make the omission from `UpsertFromSyncAsync`'s SET list the one place that earns a comment**, and
   pin it with two tests: clone `ScheduledJobQuietModeTests.ASyncPull_CannotResetQuietMode` for
   persona + effort, and assert in `SyncMapperNewEntitiesTests` that a round-tripped job does **not**
   carry either field — so promoting them to the wire requires deleting a test that states the reason.
3. **Populate the picker from the LOCAL roster surface only** and render a saved pin that is off-list or
   no longer resolvable *honestly* rather than as a blank, because `ResolveRunPersonaAsync`
   (`HeadlessRunLauncher.cs:1096-1126`) substitutes the mode persona with only an Information log line. A
   persona **name** in a log goes through `SensitiveDebug`; ids and counts stay at Information.
4. **Reserve the "clear the pin" encoding now.** `ResolveRunPersonaAsync:1096` already treats
   `Guid.Empty` as absent, so use `Guid.Empty` as the clear sentinel on `IScheduledJobService.UpdateAsync`
   rather than shipping a pin the user cannot remove through the nullable "leave unchanged" convention.
5. **Persist both as nullable TEXT** and `Enum.Parse`/`TryParse` the effort in `MapJob`, matching the
   existing convention. While the fields are local-only, no ordinal crosses a boundary, so
   `ReasoningEffort` stays free to change instead of becoming append-only the way `ScheduledJob.cs:14-22`
   documents for `ScheduledJobStatus`.
6. **State the promotion path as a plain fact** in the property's one-line doc (no plan/task/batch id,
   and do not imitate `QuietOnSuccess`'s `<para>` blocks or its `T2-18` marker): if these ever go on the
   wire, the E2EE branch is free because the server stores `EncryptedPayload` opaquely, but the plaintext
   branch needs `ServerScheduledJob` columns + an EF migration + a submodule bump, and
   `UpsertFromSyncAsync` must become null-guarded first.

### 2.5 What would reverse it

Three things, any one of which flips `PersonaId` — and only then, separately, `ReasoningEffort`.

1. **The server half actually landing:** `Pia/src/Pia.Server/Models/ServerScheduledJob.cs` gaining the two
   columns with an EF migration, both push branches at `SyncService.cs:1750-1822` copying them, and the
   `lib/Pia.Wpf` submodule bumped past this work — **plus** `UpsertFromSyncAsync` converted to a
   null-guarded apply (the pattern `SyncMapper.cs:910` already uses for
   `AssistantDefaultWorkingDirectory`: "the apply side must not clobber the local value on null") so the
   mixed-version window is survivable.
2. **Ownership transfer shipping** (`SyncScheduledJob.cs:15` still calls it "a future flow"), which makes
   a device-local pin evaporate on transfer and forces the pin to travel with it.
3. **A real report of a multi-device user losing the pin** by editing on the non-owner device *after*
   mitigation 1 ships — that would prove the in-client honesty fix is insufficient and the coordinated
   release is worth buying.

### 2.6 What this means in code

**Zero changes** to `src/Pia.Shared/Models/SyncScheduledJob.cs` and `src/Pia.Wpf/Services/SyncMapper.cs`.
`SyncMapper.ToSyncScheduledJob` (`SyncMapper.cs:979`) copies fields one by one in **both** branches — the
anonymous `plainPayload` for E2EE and the explicit `sync.X = job.X` assignments for plaintext — so a new
model property is automatically absent from the wire in both modes, and `FromSyncScheduledJob` constructs
a fresh `ScheduledJob` whose pins are null by default. Combined with `UpsertFromSyncAsync`'s hand-written
SET list, which simply does not mention the two columns, a pull cannot touch a local pin.

Also verified: `AddJobParameters` **is** used by `InsertAsync`, which `UpsertFromSyncAsync` calls on its
import arm — so a job imported from a peer starts unpinned on this device, exactly like `QuietOnSuccess`.

---

## 3. Decision — persona resolution: no roster gate, and a fallback that is never silent

### 3.1 No roster gate for a user pin

`HeadlessRunLauncher.ResolveRunPersonaAsync` (`HeadlessRunLauncher.cs:1093`) requires roster membership
because a **planner** chose that id — the roster is the user's allow-list for *model* choices. A routine's
persona is chosen by the **user** in the editor, and `AppSettings.AgentPersonaRoster` is empty by default
and capped at 6 (`MaxAgentPersonaRoster`). Gating the editor's pin on it would ship a picker whose every
choice is silently ignored — a dead feature.

The pin is resolved against `IPersonaService.GetPersonasAsync()` — **not** `GetPersonaAsync(id)`.
Verified at `PersonaService.cs:64` that only the former filters `BlockedBuiltInPersonas`
(`PersonaService.cs:100` does not), and `ResolveActiveAsync:402` resolves the user's per-mode selection
against exactly that filtered list. A user-chosen pin must obey the same policy gate as a user-chosen mode
default.

*Aside worth knowing but out of scope: the launcher's existing delegated arm uses `GetPersonaAsync` and
is therefore block-list-blind.*

### 3.2 A dangling pin falls back, it does not fail the run — but never silently

- **Run time:** mode persona + one Information line (the id and a reason token, never a name).
- **Author time:** the editor keeps a "no longer available" row for the dangling id, so opening the editor
  cannot silently rewrite the pin to Default; the detail pane shows the pin.

Justification: `ProviderId`'s unresolvable case is a visible `Failed` because *nothing can run at all*. A
missing persona still lets the routine do its job, and retiring a daily routine on five strikes because a
persona was deleted is the worse outcome.

### 3.3 Both legs get both pins

Symmetry, via ONE ladder in a new `internal static class RunPinResolver`, used by the launcher, the
background turn runner and (optionally) `StepPersonaResolver`. No new ctor dependency anywhere, so no DI
or test-harness churn.

Effort-only-on-Research was priced at roughly one step cheaper and **rejected**: `ScheduledJobKind.Research`
is the **default** kind (`ScheduledJob.cs:42`), it is what every routine created before the AgentTask leg
existed still is, and it is what the one shipped blueprint (`topic-digest`) uses. "Persona pins work,
except on the kind most of your routines are" is a feature with a footnote nobody reads.

---

## 4. Decision — the effort precedence ladder

**The job's effort beats the persona's.** The pin is the more specific, more recent, user-authored
statement, and without it a persona with an effort makes the job's own field decorative.

It lives in exactly one expression:

```
RunPinResolver.ApplyEffort(provider, jobPin, personaEffort)   // jobPin ?? personaEffort
```

which **replaces** the existing hand-rolled clone blocks at `HeadlessRunLauncher.cs:1139` and
`BackgroundAssistantTurnRunner.cs:120` (and optionally `StepPersonaResolver.cs:185`). After this step no
other file contains a `provider.ReasoningEffort =` assignment.

**Clone, never mutate.** `AiProvider` instances come out of a shared store, and `StepPersonaResolver:183`
already documents that leak. `ApplyEffort` clones once, and only when one of the two is non-null.

**Scope line, stated so it is a choice and not a gap:** a per-job effort does **not** reach a delegated
step's persona (`StepPersonaResolver`). A plan that assigns a step to a specialist keeps that specialist's
effort. This is the same call `StepPersonaResolver:150-158` already made and justified for the provider
override — a roster persona was chosen *because of* its provider and effort, and an override that won
everywhere would make the roster's own columns decorative.

---

## 5. Schema and migration

Both columns are **nullable TEXT**, matching the house convention (`ProviderId TEXT NULL` for the Guid,
TEXT + parse for the enum). NULL means "no pin, inherit", so there is **no `NOT NULL DEFAULT` and no
backfill** — do not copy `QuietOnSuccess`'s `INTEGER NOT NULL DEFAULT 0` shape.

**Two halves in `src/Pia.Wpf/Infrastructure/SqliteContext.cs`, both mandatory.**

(a) `CREATE TABLE IF NOT EXISTS ScheduledJobs` (line 325, after `QuietOnSuccess` at line 349):

```
PersonaId TEXT NULL,
ReasoningEffort TEXT NULL
```

(b) The additive migration block (the `PRAGMA table_info(ScheduledJobs)` loop at line 695): two more
`hasPersonaId` / `hasReasoningEffort` flags alongside `hasQuietOnSuccess` (line 692/703), then, each
behind its `if (!hasX)` guard, modelled on the pair at lines 727-734:

```
ALTER TABLE ScheduledJobs ADD COLUMN PersonaId TEXT NULL
ALTER TABLE ScheduledJobs ADD COLUMN ReasoningEffort TEXT NULL
```

Without (b), every existing profile throws on the first read. Without (a), every fresh profile does. Every
test and every fresh profile takes the (a) path, so a missing ALTER block passes the entire suite — which
is why `ScheduledJobsPinMigrationTests` is on the list rather than optional.

**Reading.** `Guid.Parse` for the persona (mirroring `MapJob:782`); `Enum.TryParse` → null for the effort,
**not** `Enum.Parse`. The column never crosses a boundary, `MapJob` runs inside every list read, and
`Enum.Parse` also happily accepts a numeric string — so a hand-edited or future-build value would become
an undefined enum that then reaches a provider. "Unknown means unset" is the only safe reading for a knob
that changes spend.

**Positional read: append at the END of the SELECT list.** `ReadAsync` (`ScheduledJobService.cs:732-737`)
selects 21 columns (`Id … QuietOnSuccess`, indexes 0-20), and it is the **only** SELECT of this table in
the codebase (verified). The new columns are indexes **21 and 22**. Inserting them anywhere else silently
shifts every read after the insertion point — `Status` parsed from a date, `GrantedTools` from a Guid.

**Clear sentinels.** `Guid.Empty` clears the persona pin — `ResolveRunPersonaAsync:1096` already reads
Empty as absent, so the sentinel is adopted rather than invented. Effort has no absent member (`None`
means "no reasoning", a real instruction a user may pin), so it needs a companion
`bool clearReasoningEffort = false`.

---

## 6. Every file to touch

| Path | What |
|---|---|
| `src/Pia.Wpf/Models/ScheduledJob.cs` | `public Guid? PersonaId { get; set; }` and `public ReasoningEffort? ReasoningEffort { get; set; }`. One short line each: persona = run persona, falls back to the active persona when it no longer resolves; effort = wins over the persona's. State device-local as a plain fact **without** copying `QuietOnSuccess`'s `<para>` blocks or its `T2-18` marker. |
| `src/Pia.Wpf/Infrastructure/SqliteContext.cs` | Both migration halves, §5. |
| `src/Pia.Wpf/Services/ScheduledJobService.cs` | **Six sites, five of which must change together or a read throws.** `CreateAsync` (66-98): two trailing defaulted params, assigned onto the new object. `UpdateAsync` (145-228): params + the `if (x is not null)` block + the SET list (199-204) + two `AddWithValue`. `InsertAsync` (700-714): column list AND values list. `ReadAsync`'s SELECT (732-737): append, indexes 21/22. `AddJobParameters` (748-773): two `DBNull`-guarded params, mirroring `ProviderId` at 755. `MapJob` (775-798): `Guid.Parse` / `Enum.TryParse`. **`UpsertFromSyncAsync` (655-696) is deliberately unchanged** — that omission is the one line here that earns a comment. |
| `src/Pia.Wpf/Services/Interfaces/IScheduledJobService.cs` | Mirror both signatures (7-14, 56-62). The clear sentinels are the load-bearing doc: `Guid.Empty` clears the persona pin, and **the same rule applies to the existing `providerId` param** (§8, E3). One line saying why effort needs a companion bool instead, so the asymmetry is not read as an oversight. |
| `src/Pia.Wpf/Services/RunPinResolver.cs` | **NEW** — `internal static class RunPinResolver`, two methods, no state, no DI. `ResolvePersonaAsync(IPersonaService, Guid? pinnedId, UserOperatingMode, ILogger)` and `ApplyEffort(AiProvider, ReasoningEffort? jobPin, ReasoningEffort? personaEffort)`. A static class is abstract+sealed in IL, so `NamingConventionTests.ServiceClasses_MustFollowNamingConvention`'s `AreNotAbstract()` filter skips it; the `Resolver` suffix is on its allow-list regardless. |
| `src/Pia.Wpf/Services/Interfaces/IHeadlessRunLauncher.cs` | `HeadlessRunRequest` (26-34) gains two trailing optional positional params `Guid? PersonaId = null, ReasoningEffort? ReasoningEffort = null`. Safe: all producers pass everything after the first two args **by name**. One line on `PersonaId` saying it is **not** roster-gated, unlike `LaunchChildAsync`'s `personaId` (125-131) — that contrast is the whole design and is otherwise invisible. |
| `src/Pia.Wpf/Services/HeadlessRunLauncher.cs` | `ResolveRunPersonaAsync` (1093) takes a second id: the delegated arm stays exactly as it is, and its `return ResolveActiveAsync(...)` tail becomes `RunPinResolver.ResolvePersonaAsync(...)`. `ResolveProviderAsync` (1129) takes the job's effort; its clone block becomes `ApplyEffort`. `LaunchCoreAsync` (293-311) passes `req.PersonaId` / `req.ReasoningEffort`. `RejectPlanAsync` (984) and `ResumeAsync` (563) pass null. |
| `src/Pia.Wpf/Services/Interfaces/IBackgroundAssistantTurnRunner.cs` | `BackgroundTurnRequest` gains `Guid? PersonaId { get; init; }` and `ReasoningEffort? ReasoningEffort { get; init; }` — init-only, defaulted, so every existing construction site keeps compiling. |
| `src/Pia.Wpf/Services/BackgroundAssistantTurnRunner.cs` | ~115-125: `_personaService.ResolveActiveAsync(...)` becomes `RunPinResolver.ResolvePersonaAsync(...)`; the hand-rolled effort clone becomes `ApplyEffort`. **Rewrite the comment at 118-121** — "the persona still contributes the reasoning-effort override" stops being the whole truth the moment a job pin can outrank it. One line, not left to rot. |
| `src/Pia.Wpf/Services/ScheduledJobBackgroundService.cs` | Two dispatch legs, one line each. `ExecuteAgentTaskAsync` (~507): add `PersonaId: job.PersonaId, ReasoningEffort: job.ReasoningEffort` to the `HeadlessRunRequest`. `RunResearchTurnAsync` (~720): the same two onto the `BackgroundTurnRequest`. `RunNowAsync` (277) routes through `ExecuteJobAsync`, so the manual run inherits both with no third edit. |
| `src/Pia.Wpf/ViewModels/RoutinesViewModel.cs` | One new ctor dep, `IPersonaService` (no `ISettingsService` — nothing in the editor needs the operating mode). See §7. |
| `src/Pia.Wpf/Views/RoutinesView.xaml` | Two ComboBoxes after the Provider box (512-518), two hint TextBlocks, one warning Border, two optional read-only detail rows. See §7. 2-space indent. |
| `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` | 12 new keys plus one edited string (`Routines_NotOwnedHere`, line 1129). See §7.4. |
| `src/Pia.Wpf/Resources/Strings/ViewStrings.de.resx` | Same 12 keys + the `Routines_NotOwnedHere` edit (line 1119). |
| `src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx` | Same 12 keys + the `Routines_NotOwnedHere` edit (line 1119). |
| `docs/ui_automation/ui-automation-playbook.md` | Line 35 ("Routines editor") gains `_Persona`, `_Effort`. The playbook is the registry of record and must be updated in the same change. |
| `docs/hermes_checkup/2026-08-22-hermes-followup-checklist.md` | Add group **E** (E1–E8 with `*Deps:* · *Effort:* · *Value:*` on each), delete the row from the "Not yet planned" table (line 207), and add E to the suggested order. |
| `src/Pia.Wpf/Models/RoutineBlueprint.cs` | **OPTIONAL, sequenced after C4** — one trailing `ReasoningEffort? DefaultEffort = null` record param plus one line in `StartFromBlueprint`. See §10. **No default persona.** |
| `src/Pia.Wpf/Services/StepPersonaResolver.cs` | **OPTIONAL, 2 lines** — its own clone block (~185) calls `RunPinResolver.ApplyEffort(provider, null, persona.ReasoningEffort)` so the "one ladder" claim is literal rather than aspirational. Behaviour identical. Leave its existing comment intact; it is the precedent for deferring step-level effort. |

`ViewStrings.Designer.cs` needs **no** edit: it is checked in but stale and is not used for lookup — the
resolution path is `ILocalizationService` → `ResourceManager`, and a missing key renders as `[Key]`
(`LocalizationSource.cs:46`).

---

## 7. The editor design

### 7.1 Where the persona list comes from

`IPersonaService.GetPersonasAsync()`, loaded in `RefreshAsync`'s existing `PostOrRun` block right beside
`ProviderChoices` (261-264), with a leading null "default" row exactly like
`Settings_ScheduledJobs_Provider_Default`.

- **Not the roster** — empty by default, so the picker would be dead (§3.1).
- **Not `GetPersonaAsync`** — block-list-blind (§3.1).
- `GetPersonasAsync` returns built-ins ∪ managed ∪ user in a stable, already-sensible order, so no
  sorting is needed.

### 7.2 The dangling pin — the case that decides whether this feature is honest

If the saved `PersonaId` is not in the list, `StartEdit` appends a synthetic choice labelled
`Routines_Field_Persona_Missing` and selects it. Without this, the ComboBox falls to Default, the user
sees "Default", presses Save, and **the pin is destroyed by an edit they did not make**. The detail pane
shows `PersonaName` for a pinned row for the same reason — `RunPinResolver` substitutes the mode persona
with only a log line, so the UI is the only place a person can find out.

### 7.3 The effort picker

`SelectedValuePath` / `SelectedValue` over localized `(value, label)` pairs — the pattern the Kind and
Recurrence boxes already use (the XAML comment at 435-436 explains why: a ComboBox bound straight to
`Enum.GetValues` renders the C# identifier in every locale and no parity test can see it). Built once in
the ctor like `JobKinds` / `Recurrences` (196-200).

Seven rows: a null "inherit" row plus the six members. **The null row's label must not contain the word
the `None` row uses.** `null` = "take the persona's effort"; `None` = "no reasoning at all". Two different
instructions, and this is the single likeliest place to ship a wrong one.

### 7.4 ViewModel and strings

`[ObservableProperty] EditPersona` / `EditEffort`, set in `StartCreate` (367), `StartFromBlueprint` (389)
and `StartEdit` (416), read in `SaveAsync` (448) — sending **`Guid.Empty`, not null**, when the default row
is chosen, for the persona **and** for the provider. `RoutineRow` gains `PersonaId` / `PersonaName` /
`EffortLabel` plus `HasPersonaPin` / `HasEffortPin` for the detail pane. A persona **name** is user
content: rendered freely, `SensitiveDebug` only in logs.

`EditorPinsEnabled` is false only when `EditingJobId is not null` **and** the row is foreign-owned — **not**
bound to `SelectedJob.OwnedByThisDevice` directly, because `StartCreate` leaves a foreign row selected and
the naive binding would warn about a brand-new routine this device is about to own. Disabled-and-visible
rather than hidden, so an existing pin stays readable.

AutomationIds: `Routines_Field_Persona`, `Routines_Field_Effort` — `<Surface>_<Thing>`, matching the eleven
siblings, plus playbook line 35 in the same change.

12 new resx keys × en/de/fr, plus one edit:

| Key | Purpose |
|---|---|
| `Routines_Field_Persona` | Label. |
| `Routines_Field_Persona_Default` | The leading "inherit the active persona" row. |
| `Routines_Field_Persona_Missing` | The dangling-pin row (§7.2). |
| `Routines_Field_Persona_Hint` | Says the pin stays on this device — the honesty half of the sync decision, always visible. |
| `Routines_Field_Effort` | Label. |
| `Routines_Field_Effort_Default` | The null "inherit" row — must not say the `None` word. |
| `Routines_Field_Effort_Hint` | Says it overrides the persona's. |
| `Routines_Effort_None` … `_Minimal` `_Low` `_Medium` `_High` `_XHigh` | The six member labels. |
| **edit** `Routines_NotOwnedHere` (1129 / 1119 / 1119) | Add the pin sentence — one string for one fact, reused by both banners. |

---

## 8. Steps

Order matters: E1 → E2 → E3 → E4 → E5 → E6. **E1 is decisive on its own** (a pin that persists but is not
read yet changes nothing and breaks nothing); **E5 is the vertical slice** that makes it visible.

### E1 — Model + the two columns + the clear sentinels · *Deps:* none · *Effort:* **S** · *Value:* **Enabler**

`ScheduledJob.PersonaId` and `ScheduledJob.ReasoningEffort`; both migration halves; the five
`ScheduledJobService` sites that must move together; the `Guid.Empty` / `clearReasoningEffort` sentinels.
All of §5.

### E1b — `UpsertFromSyncAsync` stays untouched, pinned by tests · *Deps:* E1 · *Effort:* **XS** · *Value:* **High**

§2.6. Zero changes to `SyncScheduledJob.cs` and `SyncMapper.cs`; one short comment on the SET list saying
the two pins are deliberately absent — the single place in this change where the WHY is invisible from the
code, because an omission cannot be read. No plan/batch id in it.

### E2 — One persona ladder, one effort ladder: `RunPinResolver` · *Deps:* E1 · *Effort:* **S** · *Value:* **Enabler**

Today there are three clone-and-stamp blocks (`HeadlessRunLauncher.cs:1139`,
`BackgroundAssistantTurnRunner.cs:120`, `StepPersonaResolver.cs:185`). Adding a per-job effort to two of
them by hand would make four.

`ResolvePersonaAsync(IPersonaService, Guid? pinnedId, UserOperatingMode, ILogger)`:

1. null or `Guid.Empty` ⇒ `ResolveActiveAsync(WindowMode.Assistant, mode)` — today's behaviour, byte for
   byte, and the overwhelmingly common case.
2. the id matches an entry in `GetPersonasAsync()` ⇒ that persona (§3.1).
3. no match ⇒ `ResolveActiveAsync` + `LogInformation` with the id and a reason token
   (`unresolvable-persona`), mirroring the phrasing at 1104-1114. **Never a persona name.**
4. anything throws ⇒ same fallback, `LogWarning` with `ex.GetType().Name` only — a persona store's message
   can embed a name, which the existing code already says at 1119-1123.

`ApplyEffort(AiProvider, ReasoningEffort? jobPin, ReasoningEffort? personaEffort)`: `jobPin ?? personaEffort`,
clone once and stamp only when that is non-null. §4.

Chosen as a static class over an injectable service specifically because a new ctor parameter on
`HeadlessRunLauncher` and `BackgroundAssistantTurnRunner` would force edits to ~6 test harnesses.

### E3 — The AgentTask leg, and the provider-clear bug in the same seam · *Deps:* E2 · *Effort:* **S** · *Value:* **High**

`HeadlessRunRequest` gains both pins. Verified safe: all three construction sites
(`ScheduledJobBackgroundService.cs:507`, `ChatSessionManager.cs:1412`, `AgentRunOrchestrator.cs:1206`) pass
everything after the first two arguments by name. The child site builds its **own** request with no
`PersonaId`, so a parent's pin cannot leak into a fan-out — the child keeps getting its specialist through
`LaunchChildAsync`'s separate, roster-gated parameter.

`ResolveRunPersonaAsync` becomes two arms and one tail (§3.1). `ResolveProviderAsync` hands the job's
effort to `ApplyEffort`. Note what this buys for free: the provider ladder already reads the persona
(`req.ProviderId` → `persona.PreferredProviderId` → mode default), so a pinned persona also brings its
preferred provider when the job pins no provider of its own — which is what makes the pin mean something
rather than just swapping a system prompt.

**Separate pre-existing bug, same seam, fixed here.** `UpdateAsync`'s `providerId` is `Guid?` with null =
"leave unchanged", and `RoutinesViewModel.SaveAsync:497` passes `providerId: EditProvider?.Id`, where the
leading `ProviderChoices` row carries `Id == null`. **Selecting "Default provider" on a job with a pinned
provider therefore does nothing at all** — the save reports success, the pin survives, and the routine
keeps running on the provider the user just removed. The `Guid.Empty`-clears rule fixes it in one line at
each end. Verified no other caller is affected: `ScheduledJobToolHandler.cs:264` passes a name-resolved
`providerId` and never `Empty`.

### E4 — The Research leg gets BOTH pins, not just effort · *Deps:* E2 · *Effort:* **S** · *Value:* **High**

§3.3. `BackgroundTurnRequest` gains two init-only nullable properties; `RunResearchTurnAsync` (~720)
passes `job.PersonaId` / `job.ReasoningEffort`; the runner's `ResolveActiveAsync` call and its hand-rolled
clone both become `RunPinResolver` calls. `PrepareTurn` then composes the **pinned** persona's system
prompt — which is the actual substance of the feature on this leg, not a side effect. Rewrite the comment
at 118-121.

### E5 — The editor: two controls, twelve strings, two AutomationIds · *Deps:* E1 · *Effort:* **M** · *Value:* **High**

All of §7. This is the vertical slice.

### E6 — Tests · *Deps:* E3, E4, E5 · *Effort:* **M** · *Value:* **High**

§9. The one thing that can break the Windows `dotnet test` gate from here is not a failing assertion, it
is a **non-compiling test project** — see the first risk in §11.

### E7 — Verification handoff · *Deps:* E6 · *Effort:* **XS** · *Value:* **High**

On Windows, in order:

1. `dotnet build -t:Rebuild -v:n`, then again with `-c Release`. `Directory.Build.props` sets
   `TreatWarningsAsErrors`, and WPF re-reports `src/` warnings under the generated `_wpftmp.csproj` markup
   pass — read the count off MSBuild's `N Warning(s)` summary line, not a grep (every warning prints twice
   at `-v:n`). MSBuild is German-localized on the dev machine (`Warnung(en)`); the baseline is 0.
2. `dotnet test` with **no filter**. `failed: 0` is the bar. Live-provider tests report `Not Run` by
   themselves.
3. Eyeball once in the real app: create a routine with a persona + effort; reopen the editor (the pin must
   come back selected, **not** Default); delete that persona; reopen (the "no longer available" row, not a
   silent Default); Run now (the log line shows the id and the fallback reason).
4. **Open the app once against a real pre-change profile**, not just a fresh one — migration half (b) only
   ever runs on a database that predates the columns.

### E8 — Blueprint defaults: effort yes (sequenced), persona no · *Deps:* E1, C4 · *Effort:* **XS** · *Value:* **Med**

§10.

---

## 9. Tests

| File | Cases |
|---|---|
| `tests/Pia.Wpf.Tests/Services/ScheduledJobPersonaPinTests.cs` **(NEW)** | Clone the shape of `ScheduledJobQuietModeTests.cs` (real `SqliteContext` in a temp dir, substituted `ISettingsService`, `RecurrenceCalculator`, `SyncDeleteTrackerService`). A new job has neither pin. `CreateAsync` carries both onto the returned object **and** the row it wrote. Both round-trip through the DB, including `ReasoningEffort.None` as a value **distinct from null**. An unrelated edit (name only) clears neither — the "null = leave unchanged" contract. `Guid.Empty` clears the persona pin while null leaves it. `clearReasoningEffort: true` clears the effort while null leaves it. **`Guid.Empty` clears the PROVIDER pin** — the pre-existing bug; this case fails on today's code. `UpsertFromSyncAsync` cannot reset either pin (direct clone of `ASyncPull_CannotResetQuietMode:88-111`, asserting the peer's `Name` DID land so the pull is proven to have run). |
| `tests/Pia.Wpf.Tests/Services/SyncMapperNewEntitiesTests.cs` **(EXTEND)** | A job with both pins set, run through `ToSyncScheduledJob` → `FromSyncScheduledJob`, comes back with both null — asserted in **plaintext AND E2EE** mode, because the mapper has two independent hand-written field lists and testing only one leaves the other free to drift. Assert on the DTO too (no property carries the value), so promoting either field to the wire has to delete a test that states why it was off it. |
| `tests/Pia.Wpf.Tests/Infrastructure/ScheduledJobsPinMigrationTests.cs` **(NEW)** | Model on `AssistantChatsMigrationTests.cs` (hand-seeded legacy table + `SqlitePool.ClearFor` in `Dispose`). Seed a `ScheduledJobs` table **without** the two columns plus one row, open `SqliteContext`, then assert both columns exist (`PRAGMA table_info`), the pre-existing row survived, and its pins read as NULL. The only test that exercises migration half (b). |
| `tests/Pia.Wpf.Tests/Services/ScheduledJobBackgroundServiceTests.cs` **(EXTEND + COMPILE FIX)** | Extend the `HeadlessRunRequest` capture test (~428-450) to assert the captured request carries the due job's pins; add the research-leg twin using `FakeRunner.LastRequest` (:1421). **Must also update `FakeJobService.CreateAsync` (:1348) and `UpdateAsync` (:1361)** or the whole test project fails to compile — both are `=> throw new NotImplementedException()`, so the fix is mechanical. |
| `tests/Pia.Wpf.Tests/Services/D5PausePremiseTests.cs` **(COMPILE FIX ONLY)** | Same two signatures at :721 and :731, same `NotImplementedException` bodies. No new assertions. |
| `tests/Pia.Wpf.Tests/Services/ScheduledJobToolHandlerTests.cs` **(COMPILE FIX ONLY)** | `FakeJobService` at :338 / :377 has **real** bodies — add the parameters and leave them unused (the handler passes nothing), so the fake keeps recording exactly what the handler sends. |
| `tests/Pia.Wpf.Tests/Services/HeadlessRunLauncherTests.cs` **(EXTEND)** | Add `PlanPersona` / `PlanProvider` to `FakePlanner` (:52) — it receives `(goal, ctx, persona, provider, ct)` and today records only `ctx`, so without this the only observable is the stub chat's `ProviderId`, which can prove *which persona* won but not *which effort*. Three facts: (1) a pinned persona is honoured with an **empty roster** — the decisive difference from `LaunchChildAsync`, asserted the way the existing child test does at :1343 plus `PlanPersona`; (2) a pin that resolves to nothing falls back to the mode persona and the run still **completes** rather than failing; (3) a job effort pin beats the persona's on the provider handed to the planner, and the persona's still applies when the job pins none. All three go through `BuildLauncher`'s existing `rosterPersona` / `rosterProvider` / `appSettings` hooks — no new substitute to leave unstubbed. |
| `tests/Pia.Wpf.Tests/Services/BackgroundAssistantTurnRunnerTests.cs` **(EXTEND)** | Via the existing `Harness`. A pinned `PersonaId` is what `Composer.PrepareTurn` receives (the system prompt is the substance of the pin on this leg, and `ResolveActiveAsync` must **not** be the one consulted); and the effort on the `AiProvider` handed to `Ai.GetChatCompletionWithToolsAsync` is the job pin when set, the persona's when not. `Harness.Personas` must have `GetPersonasAsync` stubbed for the pin path — an unstubbed one would resolve nothing and the test would silently assert the fallback instead of the feature. |
| `tests/Pia.Wpf.Tests/ViewModels/RoutinesViewModelTests.cs` **(EXTEND + COMPILE FIX at BOTH ctor sites, lines 61 and 572)** | Each site needs an `IPersonaService` substitute with `GetPersonasAsync` stubbed. Cases: `StartEdit` fills `EditPersona` / `EditEffort` from the row and `SaveAsync` forwards them (`Received` with the exact args); choosing the default row sends `Guid.Empty` for the persona **and** the provider, not null — the assertion that pins the bug fix at the VM end; a saved pin not in the list renders as the "no longer available" choice and a subsequent Save preserves it; `EditorPinsEnabled` is false when editing a foreign-owned row but **true** for `StartCreate` while that same foreign row is still selected (the trap in the naive binding); every interpolated key resolves in invariant/de/fr — `Routines_Effort_{6}`, both `_Default` rows, `_Persona_Missing` — read straight off `ViewStrings.ResourceManager` exactly as `RoutineBlueprintCatalogTests.EveryBlueprintKeyResolvesInAllThreeLocales` does, because `LocalizationTests`' literal-key regexes cannot see an interpolated key and a missing one renders as `[Key]`. |
| `tests/Pia.Wpf.Tests/Services/RoutineBlueprintCatalogTests.cs` **(EXTEND — only if E8's `DefaultEffort` lands)** | A non-null `DefaultEffort` must satisfy `Enum.IsDefined`, folded into the existing `EveryPrefillIsLegalForTheEditor` loop. |
| `tests/Pia.Wpf.Tests/Architecture/LocalizationTests.cs` | **No edit.** `AllTranslations_MustBeComplete` already enforces base⇄de⇄fr completeness and orphan-freedom, so it covers the 36 new resx entries automatically and fails loudly on a typo in any one file. |

**Not runnable here.** `dotnet test` cannot execute on macOS (`net10.0-windows`), so every case above is
written to be gate-safe rather than verified: no new substitute is left unstubbed on a path a test walks,
the three `FakeJobService` updates are pure signature copies, and the two `RoutinesViewModel` ctor sites
are named explicitly because a missed one is a compile error, not a red test.

---

## 10. E8 — Blueprint defaults

**`DefaultEffort` (`ReasoningEffort?`): yes, but sequenced.** A trailing optional record param plus one
line in `StartFromBlueprint` — roughly four lines. Cheap and real: a daily digest genuinely wants a
different effort from a weekly review. But it is worth **nothing** until there is more than one blueprint
to differentiate. **Land E1–E6 first, then add the parameter as part of C4** so its seven blueprints each
choose a value with their query in front of them. If C4 slips out of the batch, **drop this step** rather
than shipping an always-null parameter.

**`DefaultPersonaId`: no** — and the reason usually given for this needs correcting rather than repeating.
A default persona GUID **is** expressible in a static catalog: `BuiltInPersonas.PiaPersonalId` …
`ExplainItSimplyId` are fixed constants in `Pia.Shared/BuiltInPersonas.cs:14-20`, deliberately
byte-identical on every client. It is still the wrong thing to ship, for two better reasons:

1. Any built-in can be hidden by `BlockedBuiltInPersonas`, and `GetPersonasAsync` filters it — so on a
   policy-managed device a blueprint's prefilled pin would resolve to nothing and fall back. A prefill that
   is a no-op for exactly the users under central management.
2. The value of this feature is pinning the user's **own** persona, and a catalog cannot know those ids.
   Prefilling a built-in also quietly overrides the user's chosen mode persona for every routine they
   create from a card, which is not what clicking a template says it will do.

**Also not in this batch, deliberately:** `ScheduledJobToolHandler` gains no `persona` / `effort` args. Its
arg surface is a compatibility surface, and while the model can already pick a provider by name, a persona
pin swaps the **system prompt** of an unattended run — an authoring choice that belongs to the person, not
the assistant. Strictly additive later.

---

## 11. Risks

1. **COMPILE-BLOCKING, and the top risk by far.** Changing `IScheduledJobService.CreateAsync` /
   `UpdateAsync` breaks three hand-written fakes (`ScheduledJobBackgroundServiceTests:1348/1361`,
   `D5PausePremiseTests:721/731`, `ScheduledJobToolHandlerTests:338/377`), and the new
   `RoutinesViewModel` ctor arg breaks two construction sites (`RoutinesViewModelTests:61`, `:572`). Five
   edits in four files. A missed one surfaces as a red Windows gate with a compile error, not a test
   failure.
2. **`MapJob` is positional.** The two new columns must be **appended** to `ReadAsync`'s SELECT (indexes
   21, 22). Inserting them anywhere else silently shifts every read after the insertion point.
3. **Forgetting migration half (b).** Every test and every fresh profile takes the `CREATE TABLE` path, so
   a missing ALTER block passes the entire suite and throws on first read for every existing user.
4. **The null row versus `ReasoningEffort.None`.** Two different instructions in adjacent ComboBox rows
   ("inherit" vs "no reasoning"). If the null row's label says "None" in any of the three locales, users
   will pin no-reasoning by accident on unattended runs. Worth a second read of all six de/fr strings.
5. **`TreatWarningsAsErrors` is on.** A parameter added to a fake and then unused, a nullable-flow warning
   in `MapJob`'s `TryParse`, or an unused `using` in the new file each fail **both** configurations.
   Verify with `-t:Rebuild` (incremental builds skip projects and do not re-emit) in Debug and Release.
6. **Naming a property the same as its type.** `ReasoningEffort? ReasoningEffort` shadows the type inside
   the declaring class, so the enum's members cannot be referenced unqualified there. `Persona` already
   does exactly this and compiles, and nothing in `ScheduledJob` / `HeadlessRunRequest` /
   `BackgroundTurnRequest` needs to name a member — but a later "harmless" default initializer would not
   compile.
7. **Both pins are honoured at DISPATCH, not on RESUME.** Dispatch reaches the step turns and not just
   the plan, but only because `LaunchCoreAsync` seeds the executor through `Initialize(personaOverride:)`.
   Without that, `HeadlessTurnExecutor` re-resolves the mode persona itself and composes every step prompt
   from it — the plan would name the pinned persona while the work ran as the default assistant, which is
   how this shipped until review caught it. RESUME is the remaining gap: `ResumeAsync` calls
   `ResolveActiveAsync` and `ResolveProviderAsync(null, persona)` directly
   (`HeadlessRunLauncher.cs:560-563`), so a scheduled AgentTask that parks at its budget and is later
   continued runs on the current mode persona at the mode default effort. Both pins drop together, and the
   job's `ProviderId` already behaves this way, so the three are consistent rather than newly broken. The
   proper fix is persisting the resolved persona and effort on the run row — tracked as E9, deliberately
   not done here, and stated so nobody reports it as a bug in this feature.
8. **Extending `Routines_NotOwnedHere` changes the detail pane's banner too**, not only the editor's.
   Coherent in both places (both are about a routine that runs elsewhere) and it keeps one string for one
   fact — but if the detail-pane wording is considered load-bearing, the alternative is a second key used
   only in the editor, at the cost of two strings saying almost the same thing in three languages.
9. **The persona picker has no cap**, unlike the roster's `MaxAgentPersonaRoster` of 6. A user with many
   personas gets a long ComboBox. Acceptable — this is a picker, not a prompt payload, so none of the
   roster clamp's reasoning applies — but stated so nobody "fixes" it by reaching for the roster later.
10. **One more await per navigation** in `RefreshAsync` (`GetPersonasAsync` alongside `GetProvidersAsync`).
    Already off the ctor and inside the `IsBusy` guard, so it is a millisecond on a view that already does
    N+1 firing-history reads.

---

## 12. Open questions for the owner

1. **Fix the pre-existing provider-clear bug in this batch?** Selecting "Default provider" on a job with a
   pinned provider is a silent no-op today. It is the same `Guid.Empty` sentinel the persona pin needs and
   no existing caller ever passes `Empty`, so the risk is near zero — but it is a behaviour change outside
   the row's stated scope and it deserves its own line in the commit message rather than arriving as a
   surprise.
2. **Should a job's effort pin also override a DELEGATED step persona's effort?** This plan says no,
   following `StepPersonaResolver`'s existing, well-argued refusal to let the run-level provider override
   win at step level. The counter-argument is real: a user who pins Minimal on a routine to control spend
   may be surprised that a fan-out's specialists run at their own, possibly higher, effort. Reversing it
   means threading the pin into `StepPersonaResolver.ResolveAsync` and touching both executor call sites.
3. **Does C4 land in this same batch?** `RoutineBlueprint.DefaultEffort` is worth ~4 lines across seven
   differentiated blueprints and nothing with one. If C4 slips, drop E8's first half.
4. **Should `create_scheduled_research` / `update_scheduled_research` expose the two pins?** This plan says
   no for now (§10). Strictly additive later.
5. **Is this doc in the right folder?** It sits in `docs/hermes_checkup/`, next to the review that spawned
   it and its sibling [`2026-08-22-routine-blueprints-plan.md`](2026-08-22-routine-blueprints-plan.md).
   `docs/routines/` also exists (holding `2026-08-17-routines-view-review.md`) and is the more topical
   home; the documentation rule points at "next to the review or spec that spawned it", which is
   `hermes_checkup`. Say if `docs/routines/` is preferred — moving it means fixing every inbound reference
   in the same commit.
