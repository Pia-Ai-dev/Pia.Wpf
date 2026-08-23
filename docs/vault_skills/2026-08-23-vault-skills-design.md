# Skills as Vault Data — Design

- **Status:** Approved, not implemented. No code written.
- **Owner:** Marco Altmann
- **Written:** 2026-08-23
- **Origin:** [`../hermes_checkup/2026-08-22-hermes-update-review.md`](../hermes_checkup/2026-08-22-hermes-update-review.md)
  §3.6 recorded hermes's `SKILL.md` house style as a prompt-authoring standard and explicitly ruled out
  its skill *marketplace*. It never asked whether Pia should have a skill *mechanism*. This design answers
  that question: yes, built on the memory vault.

Every code reference below was read at source on 2026-08-23. No build, no test run, no runtime observation.

---

## 1. Why this is small

Skills in Claude Code and opencode are two halves: a store of procedure documents, and a cheap
always-present index that gets one of them loaded at the right moment. **Pia already has the store.**

- `VaultPaths.IsRecallIndexable` (`src/Pia.Wpf/Infrastructure/Vault/VaultPaths.cs:63`) is a **denylist** —
  everything `.md` is recall-indexed except housekeeping, `sources/` and `.archive/`. A file at
  `memory/skills/foo.md` is embedded and searchable the moment it lands.
- `MemoryService.ReadTopicAsync`'s policy guard (`src/Pia.Wpf/Services/MemoryService.cs:681`) is that same
  denylist, behind the same containment chain. The page is already readable by the assistant.

What is missing is the other half. `AssistantPromptComposer.BuildSystemPrompt` has no skills section, and
the tool-selection tree routes memory tools only at step 3 — *"STORING, RECALLING, or UPDATING personal
information"* — so a request like "write my status report" would never look for a procedure.

This is an index and a trigger on a store that works, plus a way to accumulate skills without the owner
having to sit down and author them.

## 2. What a skill is

One markdown document per skill at `memory/skills/<slug>.md`.

```markdown
---
pia: managed
type: skill
title: Weekly status report
description: How I write a weekly status
created: 2026-08-23T09:00:00Z
updated: 2026-08-23T09:00:00Z
schemaVersion: 1
---
## When to use
- Asked for a status report, a weekly update, or "what did I ship".
- **Don't use for:** a single-project recap, or a retrospective.

## Procedure
1. Pull todos completed since last Monday. *Done when* every completed item is listed.
2. Group by project, newest first. *Done when* no project appears twice.
3. One line each, no adjectives. *Done when* no line exceeds 20 words.

## Pitfalls
- An empty week is a one-line "nothing shipped", not a padded list.
```

`description` is the index line and is load-bearing — see §3. The body shape is hermes's house style cut
down to the three sections that earn their place: triggers with explicit counter-triggers, numbered steps
each ending in a checkable criterion, and pitfalls. `Prerequisites` / `How to Run` / `Quick Reference` /
`Verification` are dropped; Pia has no shell, no scripts directory, and no separate run step.

### 2.1 A seventh canonical type

`type: skill` joins the six-value set. The single edit that matters is one row in
`VaultIndexService.CanonicalGroups` (`src/Pia.Wpf/Services/Wiki/VaultIndexService.cs:31`):

```csharp
("skill", "Skills"),
```

Both `MemoryService.EnumerateBrowseGroups` (`:963`) and `VaultViewModel.EnumerateDisplayGroups`
(`src/Pia.Wpf/ViewModels/VaultViewModel.cs:370`) walk that list and **skip any type not in it**, and
`VaultViewModel.CountDisplayable` (`:352`) leaves unknown types out of the header total. So:

- Without the row, skills are invisible in the Vault view and in `browse_index` — no clutter, no surface.
- With the row, they appear in both at once. The two walks have drifted before (the comment at
  `VaultViewModel.cs:356` records the fix); a test must cover both.

`InferTypeFromPath` (`MemoryService.cs:1571`) gains a `memory/skills/` arm so a hand-written page with no
`type:` still resolves correctly.

### 2.2 Two consequences of the `##` body

- **Entry collapse.** `MarkdownVaultParser` splits a `##`-sectioned document into one `VaultMemoryItem`
  per section, so a three-section skill would render as three rows. `MemoryService.BuildEntries` (`:606`)
  already collapses `memory/topics/` to one entry per page for exactly this reason; `memory/skills/` needs
  the same arm.
- **Recall tier.** `RecallHit.Tier` (`src/Pia.Wpf/Services/Interfaces/IMemoryService.cs:18`) is
  topic-or-record, described in its own comment as "binary on purpose". It gains `"skill"` for
  `memory/skills/`, so a procedure hit is distinguishable from a personal fact in the recall payload.

### 2.3 Sync costs nothing

`VaultSyncService` reconciles Pia-managed vault files by section-aware three-way merge over markdown; the
server is zero-knowledge and last-writer-wins. A skill page is just another document. There is **no typed
DTO**, so the failure mode from checklist item E1b — a field the server does not know about coming back
null and erasing the owner's data after one push-pull cycle — cannot occur here.

## 3. The index

A `## Skills` section composed in `AssistantPromptComposer.BuildSystemPrompt` from a new `ISkillCatalog`,
cached and invalidated by the existing vault watcher.

```
## Skills

Load the procedure with load_skill(slug) before starting. Never work from the description alone.

- status-report — How I write a weekly status
- invoice-format — Invoice layout and wording
- support-triage — Triaging an inbound support mail
```

**Budget.** `description` is capped at **80 characters**, validated on write and truncated on read so a
hand-edited page cannot blow the budget. The index is capped at **40 skills / 4 KB**. Hermes caps at 60
characters because its own index truncates at 57; Pia has no such truncation, so the number is a choice —
80 buys a readable trigger while keeping a full 40-skill index near 3.5 KB.

**No silent truncation.** Over the cap, the index lists what fits and ends with one line naming how many
were omitted, and the Vault view shows the same warning. A capped index that reads as complete is worse
than no index.

**Byte stability.** The section is a pure function of the skill set, so it is identical across turns and
the prompt-cache prefix survives. This is why the index goes in the system prompt rather than being
matched per-turn: a section that varies per turn invalidates the cached prefix — the same objection the
review raised against hermes's micro-compaction. A test pins that two composes over an unchanged vault are
byte-identical.

**Tool-selection tree.** A new **step 0** precedes the existing four:

> 0. Does one of the listed skills cover this request? — YES → call `load_skill(slug)` and follow its
>    procedure before anything else. NO → continue to step 1.

Position matters: at step 3 or later, "write my status report" routes to Todo before a skill is ever
considered.

**@-command turns are excluded.** That path deliberately narrows the toolset and drops
`suggest_agent_mode` to stay byte-stable (`AssistantPromptComposer.cs:47`); the skills section and
`load_skill` stay out of it for the same reason.

## 4. `load_skill`

A read tool registered on `MemoryPluginId`
(`src/Pia.Wpf/Services/Plugins/BuiltInPluginDefaults.cs:15`), taking a slug and returning the page body
plus a short standing note that each step carries a completion criterion.

It delegates to the same read path as `read_topic` against `memory/skills/<slug>.md`, so containment,
the recall-visibility policy check and the traversal guards have exactly one implementation. The separate
name exists for two reasons: it makes the index line actionable without the model having to know that a
skill is a vault path, and it gives the UI a distinct "skill loaded" affordance instead of an anonymous
`read_topic` card.

Reads default-allow, so `load_skill` needs no grant plumbing on any surface.

## 5. Reach

| Surface | Index | `load_skill` | Note |
|---|---|---|---|
| Interactive chat | yes | yes | Excluding @-command turns (§3) |
| Agent runs | yes | yes | Planner turn and step turns; index paid per step |
| Routines / headless | yes | yes | Both dispatch legs; reads need no grant |
| Voice mode | **no** | **no** | Known ungated write path; deserves its own look, not a ride-along |

Two things fall out:

- **`RoutineBlueprint` gains an optional `SkillSlug`.** This is the `skills=(…)` field hermes's
  `AutomationBlueprint` carries and the Pia port dropped, because at porting time there was nothing to
  reference. A blueprint that names a skill keeps its `QueryTemplate` short instead of inlining a
  procedure.
- **"Done when" feeds `ExpectedArtifact`.** On an agent run, a skill's step criteria are exactly the
  checkable predictions checklist item A4 asks the planner to emit. The two arrive at the same discipline
  from opposite directions.

## 6. The harvest

The owner's requirement is that skills accumulate **without being authored**: Pia notices a repeated
convention and offers to save it. The first release ships the naive version of that.

### 6.1 Why it is not a `RoutineBlueprint`

There is no assistant-facing tool that reads chat history. `AssistantChatsFts` exists as an FTS5 table
(`src/Pia.Wpf/Infrastructure/SqliteContext.cs:1110`) with nothing exposed on top of it. A harvest turn
therefore cannot see the chats it is meant to mine, and a blueprint's `QueryTemplate` is a static string
that cannot carry them.

Adding a general `search_chats` tool is the wrong fix: it hands every ordinary turn a window into all
history. Instead a **`SkillHarvestService`** composes the corpus into the prompt itself and calls
`IBackgroundAssistantTurnRunner.RunAsync` directly. Chat content reaches exactly one turn, by construction,
and no new capability is exposed to the general tool set.

### 6.2 Shape

- Scheduled weekly by a `ScheduledJob`. The service builds a `BackgroundTurnRequest` whose `Prompt` is the
  harvest instruction plus the assistant chats since the last harvest.
- The instruction asks for conventions the owner **stated or corrected more than once**, one proposed
  skill page each, and — following `topic-digest`'s no-padding rule — an explicit one-line "nothing
  recurred" rather than a manufactured list.
- The write is `save_skill(slug, title, description, body)`. It is a **write** tool, so it belongs in
  `BackgroundTurnRequest.GrantedWriteTools`, passes the existing deny-by-default gate, and returns a
  `MemoryToolCall` carrying a `DiffPreview` — the same approval path as `remember` and `update_source`. In
  a headless run it parks as a pending write the owner approves from the run panel, which already has that
  surface.

### 6.3 Deferred to phase 2

The owner also asked for a counted-repetition signal and a rejected-output signal. Both are precision
upgrades on the naive pass and are deliberately not in the first release — they should be tuned against
what the naive harvest actually proposes, not guessed at beforehand.

- **Counted repetition.** A signal store keyed on near-duplicate instructions across chats, offering at the
  third occurrence. Needs a store and an embedding pass that do not exist.
- **Rejected or edited output.** A rejected approval diff is already recorded and is a high-precision
  signal that a convention is missing. Cheapest of the two; likely the first phase-2 step.

## 7. Starter skills

Four pages seeded on first run by the same rule `AGENTS.md` follows in `VaultSchemaService` — written only
when absent, never overwritten, `pia: managed`. They solve the cold-start empty state and double as the
format's worked examples. The set comes from the review's §3.6(b): meeting → action items, document →
obligations, weekly review, grounded citations.

Bodies are seeded **unlocalized (English)**, following the `AGENTS.md` precedent rather than the
`RoutineBlueprint` one. A blueprint's title and description are UI chrome and live in the three `.resx`
files; a skill body is prompt text. Owner decision **D2** below can reverse this.

## 8. What this is not

- **Not a marketplace.** The review already ruled that out; nothing here distributes or installs skills.
- **Not personas.** A persona is whole-conversation identity, always on. A skill is task-scoped and
  loaded on demand. They compose; neither replaces the other.
- **Not plugins.** `BuildSystemPrompt` already composes a `## Plugins` section from
  `IPluginService.GetCombinedSystemPromptAdditions()`, so prompt injection would have come free — but
  plugins are GUID-registered code. They do not sync, are not hand-editable, and cannot be written by a
  background turn. Skills must be vault documents.
- **Not `AGENTS.md`.** Pia writes that file and never reads it. Making it an always-loaded conventions
  block is a defensible separate feature — standing rules versus on-demand procedures — and is out of
  scope here.

## 9. Risks

**The loop is the main one.** Pia proposes a skill → the owner approves → the page shapes every later turn
→ which shapes what Pia proposes next. Nothing in the mechanism damps that on its own. Four brakes, all of
them already built:

1. Every write goes through the approval diff; no skill appears without the owner seeing it.
2. Skills are visible and deletable in the Vault view (§2.1) — the loop is inspectable.
3. The index cap bounds how much a runaway harvest can inject.
4. Headless never auto-approves; a proposed write parks.

Secondary:

- **Per-turn cost.** The index is paid on every turn on every surface in §5, including every agent step.
  The cap is the control; the byte-stability test is what keeps it from also costing the prompt cache.
- **Stale skills.** A convention that changed leaves a page that still fires. Frontmatter `updated` already
  exists and the Vault view can sort on it; no staleness mechanism in v1.
- **A new `ScheduledJobKind` crosses the sync wire** — see **D1**.

## 10. Testing

- The composed system prompt is byte-identical across two composes over an unchanged vault.
- Index cap: overflow lists what fits and names the omitted count; nothing is dropped silently.
- `description` over 80 chars is rejected on write and truncated on read.
- `type: skill` groups correctly in **both** `EnumerateBrowseGroups` and `EnumerateDisplayGroups` — the
  drift at `VaultViewModel.cs:356` is the reason this is one test over two walks.
- A multi-section skill collapses to a single `BrowseEntry`.
- `load_skill` rejects a traversal ref and a ref outside `memory/skills/`.
- A skill page round-trips `SectionMergeEngine` unchanged.
- `RecallHit.Tier` returns `"skill"` for a `memory/skills/` path and is unchanged for the other two.
- `ViewAutomationIdTests` gains its row if the Vault view grows a control.

## 11. Owner decisions

| # | Question | Recommendation |
|---|---|---|
| **D1** | `ScheduledJobKind.SkillHarvest = 2`, or a placeholder token in a blueprint `QueryTemplate` expanded pre-dispatch? | **Resolved 2026-08-23: the new kind.** See §11.1 — verified at source, not assumed. |
| **D2** | Are starter skill bodies localized? | No. Follow `AGENTS.md`, not `RoutineBlueprint`. Revisit if a non-English owner reports the starters reading wrong. |
| **D3** | Does a loaded skill need its own UI affordance? | Start with the ordinary tool-call card. A dedicated affordance is cheap to add once there is evidence the owner cannot tell whether a skill fired. |

### 11.1 D1, verified

The concern was that a new `ScheduledJobKind` reaches a peer running an older build, which cannot know it.
Traced through the four places the value passes:

| Step | Site | Behaviour with an unknown kind |
|---|---|---|
| Sync in | `SyncMapper.cs:1044`, `:1065` | `(ScheduledJobKind)(sync.Kind ?? 0)` — unchecked cast, no `Enum.IsDefined`. Stored verbatim. |
| Persist | `ScheduledJobService.cs:773` | `job.Kind.ToString()` on an undefined value writes the numeric string `"2"`. |
| Read back | `ScheduledJobService.cs:804` | `Enum.Parse<ScheduledJobKind>("2")` **succeeds** — .NET parses numeric strings for enums. No throw; the row round-trips. |
| Dispatch | `ScheduledJobBackgroundService.cs:475` | A **ternary**, not a switch: anything that is not `AgentTask` runs `ExecuteResearchAsync`. An unknown kind is therefore **not inert**. |

The last row refutes the assumption this gate was written to check. The risk collapses one layer down: the
due-jobs query is owner-pinned —

```sql
WHERE NextFireAt <= @Now AND Status = 'Active'
  AND (OwnerDeviceId IS NULL OR OwnerDeviceId = @LocalDevice)   -- ScheduledJobService.cs:122
```

— and a job is stamped with its creating device at `:93`. A peer stores the row and **never fires it**, so
the dispatcher there never sees the unknown kind. The residual defect is cosmetic: an older build's
Routines list renders the job as "Research" (`ScheduledJobToolHandler.cs:187`).

Two obligations follow for the new build: replace the ternary at `:475` with a switch carrying an explicit
`SkillHarvest` arm, and pin the owner-device behaviour with a test so a later change to the due-jobs query
cannot silently re-open this.

## 12. Effort

`M` or larger, and it spans a consumer half and a producer half that can ship separately. The
implementation plan therefore carries its own `YYYY-MM-DD-vault-skills-checklist.md` in this folder, per
`CLAUDE.md`.
