# Vault Skills Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available)
> or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Status:** Not started.
- **Owner:** Marco Altmann
- **Written:** 2026-08-23
- **Origin:** [`2026-08-23-vault-skills-design.md`](2026-08-23-vault-skills-design.md) — approved design.
- **Tracking:** [`2026-08-23-vault-skills-checklist.md`](2026-08-23-vault-skills-checklist.md)

**Goal:** Let Pia keep the owner's own procedures as vault documents, put a cheap index of them in every
system prompt, load one on demand, and propose new ones from a weekly review of recent chats.

**Architecture:** A skill is a markdown page at `memory/skills/<slug>.md` with `type: skill`. Storage,
recall and reading already work — `IsRecallIndexable` is a denylist and `ReadTopicAsync` guards on that
same denylist. This plan adds the consumer half (a seventh canonical type, a byte-stable `## Skills`
section in the system prompt, a `load_skill` read tool) and a thin producer half (a `SkillHarvestService`
that composes recent chats into one background turn which proposes pages via a gated `save_skill`).

**Tech Stack:** C# / .NET 10 / WPF, xunit v3 (`Microsoft.Testing.Platform`), `Microsoft.Extensions.AI`
tool definitions, SQLite, CommunityToolkit.Mvvm.

---

## Before you start

Read these three, in order. Everything below assumes them.

1. `docs/vault_skills/2026-08-23-vault-skills-design.md` — the design, including §11.1 (the D1 gate,
   already verified; do not re-litigate it).
2. `CLAUDE.md` — **Zero-Warning Policy**, **Comment Discipline** (no task IDs in code, one short line
   maximum), and the localization rule: new `loc:Str` keys go in the **three** `.resx` files only, never
   in `Designer.cs`.
3. `src/Pia.Wpf/Services/MemoryService.cs:657-710` (`ReadTopicAsync`) — the two-guard pattern
   (containment, then policy) that every new read path must reuse rather than reimplement.

**Test commands.** The gate is `dotnet test` with no filter, and the bar is `failed: 0`. For the inner
loop, run the built exe directly with xunit's **native single-dash** options:

```bash
dotnet build
tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe -class Pia.Wpf.Tests.Vault.SkillCatalogTests
```

**Commit rhythm.** One commit per task, after its tests pass. Branch off `feature/agent-run-spine`.

---

## File structure

| File | Responsibility | Status |
|---|---|---|
| `src/Pia.Wpf/Models/Vault/SkillPage.cs` | The parsed skill: slug, title, description, body. Validation lives here. | Create |
| `src/Pia.Wpf/Services/Interfaces/ISkillCatalog.cs` | Reads `memory/skills/`, caches, renders the index block. | Create |
| `src/Pia.Wpf/Services/SkillCatalog.cs` | Implementation: enumerate, parse, cap, invalidate on watcher. | Create |
| `src/Pia.Wpf/Services/SkillHarvestService.cs` | Composes the chat corpus into one background turn. | Create |
| `src/Pia.Wpf/Services/Wiki/VaultIndexService.cs:31` | `CanonicalGroups` — the one row that makes skills visible in both walks. | Modify |
| `src/Pia.Wpf/Services/MemoryService.cs` | `BuildEntries` collapse (`:606`), `InferTypeFromPath` (`:1571`). | Modify |
| `src/Pia.Wpf/Services/Interfaces/IMemoryService.cs:18` | `RecallHit.Tier` gains `"skill"`. | Modify |
| `src/Pia.Wpf/Services/AssistantPromptComposer.cs` | `## Skills` section, tool-tree step 0. | Modify |
| `src/Pia.Wpf/Services/MemoryToolHandler.cs` | `load_skill` and `save_skill` registration + routing. | Modify |
| `src/Pia.Wpf/Services/Wiki/VaultSchemaService.cs` | Seeds the four starter skills. | Modify |
| `src/Pia.Wpf/Models/ScheduledJob.cs:5` | `ScheduledJobKind.SkillHarvest = 2`. | Modify |
| `src/Pia.Wpf/Services/ScheduledJobBackgroundService.cs:475` | Ternary → switch with an explicit arm. | Modify |
| `src/Pia.Wpf/Services/TokenizingAiClientService.cs:13` | `save_skill` joins `WriteOperations`. | Modify |
| `src/Pia.Wpf/Models/RoutineBlueprint.cs` | Optional `SkillSlug`. | Modify |

`SkillCatalog` stays separate from `MemoryService` on purpose: `MemoryService` is already 1,912 lines and
adding a cached, watcher-invalidated projection to it would make a large file larger for no gain. The
catalog depends on `IVaultStore` only.

---

## Chunk 1: The skill document type

Goal of this chunk: a hand-written `memory/skills/foo.md` is recognised, visible in the Vault view and
`browse_index`, and distinguishable in recall. No prompt changes yet.

### Task 1: `type: skill` is a canonical type

**Files:**
- Modify: `src/Pia.Wpf/Services/Wiki/VaultIndexService.cs:31-39`
- Modify: `src/Pia.Wpf/Services/MemoryService.cs:1571` (`InferTypeFromPath`)
- Modify: `src/Pia.Wpf/Resources/Strings.resx`, `Strings.de.resx`, `Strings.fr.resx`
- Test: `tests/Pia.Wpf.Tests/Vault/SkillTypeGroupingTests.cs` (create)

- [ ] **Step 1: Write the failing test**

The point of this test is that the **two group walks agree**. They have drifted before — the comment at
`VaultViewModel.cs:356` records the fix — so one test covers both.

```csharp
[Fact]
public async Task SkillPage_AppearsInBothGroupWalks()
{
    using var vault = TestVault.Create();
    await vault.WriteAsync("memory/skills/status-report.md",
        "---\ntype: skill\ntitle: Weekly status\ndescription: How I write a weekly status\n---\n" +
        "## When to use\n- Asked for a status report.\n");

    var browse = await vault.MemoryService.BrowseIndexAsync();
    Assert.Contains(browse.Categories, c => c.Category == "skill");

    var snapshot = await vault.MemoryService.ListMemoriesAsync();
    var displayed = VaultViewModel.EnumerateDisplayGroupsForTest(snapshot.Items);
    Assert.Contains(displayed, g => g.Key == "skill");
}
```

`EnumerateDisplayGroups` is currently `private static`. Make it `internal static` and add
`[assembly: InternalsVisibleTo]` only if the test project does not already have it — check
`src/Pia.Wpf/Pia.Wpf.csproj` first; several tests already reach internals, so it very likely does. Do not
add a public test-only wrapper.

- [ ] **Step 2: Run it and confirm it fails**

```bash
tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe -class Pia.Wpf.Tests.Vault.SkillTypeGroupingTests
```

Expected: FAIL — both assertions, because `CanonicalGroups` has no `skill` row so both walks skip the item.

- [ ] **Step 3: Add the row**

`VaultIndexService.cs`, appended after `("topic", "Topics")` so skills sort last in every grouped list:

```csharp
("skill", "Skills"),
```

Then in `MemoryService.InferTypeFromPath`, so a hand-written page with no `type:` still resolves:

```csharp
var p when p.StartsWith("memory/skills/", StringComparison.OrdinalIgnoreCase) => "skill",
```

- [ ] **Step 4: Localize the display name**

`CanonicalGroups` display strings are consumed by the Vault view. Check whether the view localizes them
before hard-coding "Skills" — if the other five are localized, add `Vault_Group_Skills` to all three
`.resx` files (en `Skills`, de `Fähigkeiten`, fr `Compétences`) and follow that pattern. If they are
hard-coded English, match the neighbours and add nothing.

- [ ] **Step 5: Run tests, confirm green, commit**

```bash
git add src/Pia.Wpf/Services/Wiki/VaultIndexService.cs src/Pia.Wpf/Services/MemoryService.cs \
        tests/Pia.Wpf.Tests/Vault/SkillTypeGroupingTests.cs
git commit -m "Recognise type: skill as a canonical vault type"
```

### Task 2: A multi-section skill is one entry, not three

**Files:**
- Modify: `src/Pia.Wpf/Services/MemoryService.cs:604-624` (`BuildEntries`)
- Test: `tests/Pia.Wpf.Tests/Vault/SkillTypeGroupingTests.cs` (extend)

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task MultiSectionSkill_CollapsesToOneBrowseEntry()
{
    using var vault = TestVault.Create();
    await vault.WriteAsync("memory/skills/status-report.md",
        "---\ntype: skill\ntitle: Weekly status\ndescription: How I write a weekly status\n---\n" +
        "## When to use\n- Asked for a status report.\n\n## Procedure\n1. Pull todos.\n\n## Pitfalls\n- None.\n");

    var browse = await vault.MemoryService.BrowseIndexAsync();
    var skills = browse.Categories.Single(c => c.Category == "skill");

    Assert.Single(skills.Entries);
    Assert.Equal("Weekly status", skills.Entries[0].Title);
    Assert.Equal("memory/skills/status-report.md", skills.Entries[0].Ref);
}
```

Expected without the fix: three entries, one per `##`, because `MarkdownVaultParser` splits a sectioned
document into one `VaultMemoryItem` per section.

- [ ] **Step 2: Run it and confirm it fails with `Assert.Single` seeing 3**

- [ ] **Step 3: Generalise the collapse**

`BuildEntries` currently special-cases `memory/topics/`. Replace the literal prefix test with a helper so
the two page-shaped trees share one rule:

```csharp
private static bool IsPageCollapsed(string filePath) =>
    filePath.StartsWith("memory/topics/", StringComparison.OrdinalIgnoreCase)
    || filePath.StartsWith("memory/skills/", StringComparison.OrdinalIgnoreCase);
```

and use it at `:606` in place of the inline `StartsWith`. Keep the existing title-recovery branch: a
sectioned skill carries no page-level title on its section items, so it falls to `PrettifySlug` exactly as
a sectioned topic does — **but** a skill always has frontmatter `title`, so prefer that when present.
Read the surrounding block before editing; the `pageItemCount` lookup drives which branch runs.

- [ ] **Step 4: Run tests, confirm green**

- [ ] **Step 5: Commit**

```bash
git commit -am "Collapse a sectioned skill page to one browse entry"
```

### Task 3: A recall hit names its tier

**Files:**
- Modify: `src/Pia.Wpf/Services/Interfaces/IMemoryService.cs:8-20`
- Test: `tests/Pia.Wpf.Tests/Vault/RecallHitTierTests.cs` (create)

- [ ] **Step 1: Write the failing test**

```csharp
[Theory]
[InlineData("memory/skills/status-report.md", "skill")]
[InlineData("memory/topics/acme.md", "topic")]
[InlineData("memory/notes/holiday.md", "record")]
public void Tier_IsDerivedFromPath(string path, string expected)
    => Assert.Equal(expected, new RecallHit(path, "h", "s", 1f).Tier);
```

- [ ] **Step 2: Run it — the skill row fails, returning `record`**

- [ ] **Step 3: Add the arm**

```csharp
public string Tier =>
    FilePath.StartsWith("memory/skills/", StringComparison.OrdinalIgnoreCase) ? "skill"
    : FilePath.StartsWith("memory/topics/", StringComparison.OrdinalIgnoreCase) ? "topic"
    : "record";
```

Update the `<summary>` above it: it currently says "Binary on purpose" and names two values. Say three,
and keep it to the same one-or-two lines — Comment Discipline applies to XML-doc.

- [ ] **Step 4: Run tests, confirm green. Also re-run `MemoryToolIntegrationTests` — the recall payload
  shape is asserted there.**

- [ ] **Step 5: Commit**

### Task 4: A skill page parses and validates

**Files:**
- Create: `src/Pia.Wpf/Models/Vault/SkillPage.cs`
- Test: `tests/Pia.Wpf.Tests/Vault/SkillPageTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Four behaviours, and the third is the load-bearing one — the index budget is only safe if a hand-edited
file cannot blow it.

```csharp
[Fact]
public void Parse_ReadsSlugTitleAndDescription() { /* slug from filename, title+description from frontmatter */ }

[Fact]
public void Parse_MissingDescription_YieldsNull() { /* an unusable page is skipped, not thrown on */ }

[Fact]
public void Parse_OverlongDescription_IsTruncatedOnRead()
{
    var page = SkillPage.Parse("memory/skills/x.md", Doc(description: new string('a', 200)));
    Assert.Equal(80, page!.Description.Length);
    Assert.EndsWith("…", page.Description);
}

[Fact]
public void Validate_OverlongDescription_IsRejectedOnWrite()
    => Assert.False(SkillPage.IsWritable(new string('a', 81), out _));
```

Read on truncate, write on reject — deliberately asymmetric. A file the owner edited by hand outside Pia
must still work; a page Pia is about to write must be correct.

- [ ] **Step 2: Run and confirm they fail to compile (`SkillPage` does not exist)**

- [ ] **Step 3: Implement `SkillPage`**

A record plus a static `Parse` over a `VaultDocument`, returning `null` when the page is unusable
(no `description`). `MaxDescription = 80`. Reuse `VaultSlug.Slugify` for slug normalization — do **not**
write a second slug algorithm; `VaultSlug`'s doc comment says there is exactly one implementation.

- [ ] **Step 4: Run tests, confirm green**

- [ ] **Step 5: Commit**

---

## Chunk 2: The index and `load_skill`

Goal: a skill fires. This is the chunk that proves the feature.

### Task 5: `ISkillCatalog`

**Files:**
- Create: `src/Pia.Wpf/Services/Interfaces/ISkillCatalog.cs`, `src/Pia.Wpf/Services/SkillCatalog.cs`
- Modify: `src/Pia.Wpf/Bootstrapper.cs` (DI registration — follow the neighbouring singleton pattern)
- Test: `tests/Pia.Wpf.Tests/Vault/SkillCatalogTests.cs` (create)

Interface:

```csharp
public interface ISkillCatalog
{
    Task<IReadOnlyList<SkillPage>> GetAsync();

    /// <summary>The `## Skills` block, or empty when there are none. Byte-stable for a given skill set.</summary>
    Task<string> RenderIndexAsync();

    void Invalidate();
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task RenderIndex_EmptyVault_ReturnsEmptyString() { }
[Fact] public async Task RenderIndex_SortsBySlug_SoOutputIsStable() { }
[Fact] public async Task RenderIndex_OverCap_NamesTheOmittedCount() { }
[Fact] public async Task GetAsync_SkipsAPageWithNoDescription() { }
[Fact] public async Task Invalidate_CausesARereadOnNextGet() { }
```

The overflow test asserts the exact tail line, because the whole point is that a capped index does not
read as complete:

```csharp
Assert.EndsWith("\n- (41 more skills are not listed; the index is capped at 40.)\n", block);
```

- [ ] **Step 2: Run and confirm failure**

- [ ] **Step 3: Implement `SkillCatalog`**

- Enumerate `memory/skills/*.md` through `IVaultStore`. Remember `EnumerateAsync` is **not a real glob**
  (`VaultPaths.cs:9`) — it walks the subtree; filter the results yourself.
- Sort by slug, ordinal. Stability is a correctness property here, not tidiness: an unsorted index would
  vary between reads and break the prompt cache.
- Cap `MaxSkills = 40` and `MaxChars = 4096`, whichever binds first, and append the omitted-count line.
- Cache the rendered string; `Invalidate()` clears it. Wire `Invalidate` to the vault watcher next to the
  existing recall-index invalidation — find it by grepping for the watcher's change handler.

- [ ] **Step 4: Run tests, confirm green**

- [ ] **Step 5: Commit**

### Task 6: The `## Skills` section, byte-stable

**Files:**
- Modify: `src/Pia.Wpf/Services/AssistantPromptComposer.cs:23` (ctor), `:123-172` (`BuildSystemPrompt`)
- Test: `tests/Pia.Wpf.Tests/Services/AssistantPromptComposerSkillsTests.cs` (create)

`AssistantPromptComposer` takes `ILocalizationService` and `IPluginService` today; add `ISkillCatalog`.
Note `BuildSystemPrompt` is synchronous — resolve the rendered block in `PrepareTurn` (which is also
synchronous) or make the catalog expose a cached synchronous read. **Prefer the cached synchronous read**:
turning `PrepareTurn` async ripples into every call site including the step path.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void SystemPrompt_WithNoSkills_HasNoSkillsSection()
    => Assert.DoesNotContain("## Skills", Compose(skills: []));

[Fact]
public void SystemPrompt_ListsEachSkillOnce()
{
    var prompt = Compose(skills: [Skill("status-report", "How I write a weekly status")]);
    Assert.Contains("- status-report — How I write a weekly status", prompt);
}

[Fact]
public void SystemPrompt_IsByteStableAcrossComposes()
{
    var skills = new[] { Skill("b-skill", "B"), Skill("a-skill", "A") };
    Assert.Equal(Compose(skills), Compose(skills));
}
```

The third is the one that protects the prompt cache, which is the entire reason the index lives in the
system prompt rather than being matched per turn. Do not weaken it to a `Contains` check.

- [ ] **Step 2: Run and confirm failure**

- [ ] **Step 3: Add the section**

Placed after `pluginSection` and before `toolSelectionSection`, so the tool tree's new step 0 reads with
the list already in view:

```csharp
var skillSection = string.IsNullOrEmpty(skillIndex)
    ? string.Empty
    : $"## Skills\n\nLoad the procedure with load_skill(slug) before starting. Never work from the description alone.\n\n{skillIndex}\n\n";
```

- [ ] **Step 4: Run tests, confirm green. Also run `AssistantPromptComposerMemoryToolsTests` and
  `AssistantPromptComposerAgentSuggestTests` — both assert prompt content and will see the new section.**

- [ ] **Step 5: Commit**

### Task 7: Tool-selection tree step 0

**Files:**
- Modify: `src/Pia.Wpf/Services/AssistantPromptComposer.cs:139-171`
- Test: `tests/Pia.Wpf.Tests/Services/AssistantPromptComposerSkillsTests.cs` (extend)

- [ ] **Step 1: Write the failing test** — assert step 0 is present and that it precedes the reminder
  branch in the string (`IndexOf("0. Does one of the listed skills") < IndexOf("1. Does the request mention a specific TIME")`).

Position is the behaviour under test. At step 3 or later, "write my status report" routes to Todo before a
skill is ever considered.

- [ ] **Step 2: Run and confirm failure**

- [ ] **Step 3: Insert the step**, renumbering nothing — the existing steps keep their numbers, the new one
  is 0:

```
0. Does one of the listed skills cover this request?
   - YES → call load_skill(slug) and follow its procedure before anything else.
   - NO → Continue to step 1.
```

Emit it **only when the skills section is non-empty**, or a vault with no skills gets a step pointing at
an empty list.

- [ ] **Step 4: Run tests, confirm green**
- [ ] **Step 5: Commit**

### Task 8: `load_skill`

**Files:**
- Modify: `src/Pia.Wpf/Services/MemoryToolHandler.cs:38-113`
- Modify: `src/Pia.Wpf/Services/AssistantPromptComposer.cs:180` (the `@Memory` tool-name list)
- Test: `tests/Pia.Wpf.Tests/Vault/LoadSkillToolTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task LoadSkill_ReturnsTheBody() { }
[Fact] public async Task LoadSkill_UnknownSlug_ReturnsAReadableMiss() { }
[Fact] public async Task LoadSkill_TraversalSlug_IsRejected()
{
    var result = await handler.HandleAsync("load_skill", Args(slug: "../../sources/secret"), default);
    Assert.Contains("outside", result.ToString(), StringComparison.OrdinalIgnoreCase);
}
[Fact] public async Task LoadSkill_NonSkillPath_IsRejected()
    => /* slug "../topics/acme" must not resolve, even though that page is recall-visible */;
```

The last two matter most. `read_topic` accepts any recall-visible page; `load_skill` must additionally
pin the result under `memory/skills/`, or the slug becomes a general read primitive with a friendlier name.

- [ ] **Step 2: Run and confirm failure**

- [ ] **Step 3: Implement**

Register alongside the other memory tools:

```csharp
AIFunctionFactory.Create(LoadSkillSchema, "load_skill",
    "Load a skill's full procedure by slug, as listed in the Skills section of your instructions. " +
    "Follow its numbered steps; each carries a completion criterion. Call this before starting a task a skill covers."),
```

Route it in the `switch` at `:105`. The handler builds `memory/skills/{slug}.md` and delegates to
`_memoryService.ReadTopicAsync` — **reuse, do not reimplement**: that method already runs containment then
policy, and duplicating the guard chain is how the two drift. Reject the slug up front if it contains
`/`, `\`, or `..`, so a traversal never reaches the resolver at all.

Add `"load_skill"` to the `@Memory` tool-name list at `AssistantPromptComposer.cs:180`.

- [ ] **Step 4: Run tests, confirm green**
- [ ] **Step 5: Commit**

**End of chunk 2 — stop and try it in the real app.** Write a skill page by hand, ask Pia something it
covers, and confirm it calls `load_skill` and follows the procedure. If it does not, the fault is the
description or step 0's position, not the plumbing, and it is much cheaper to find that now than after
chunk 4.

---

## Chunk 3: Reach and starters

### Task 9: Four starter skills

**Files:**
- Modify: `src/Pia.Wpf/Services/Wiki/VaultSchemaService.cs`
- Test: `tests/Pia.Wpf.Tests/Wiki/VaultSchemaServiceSkillTests.cs` (create)

- [ ] **Step 1: Write the failing tests** — a starter is written when absent; an existing file with the
  same path is **never** overwritten (assert byte-equality against the owner's edited content after a
  second `EnsureScaffoldingAsync`); the run is idempotent.

- [ ] **Step 2: Run and confirm failure**

- [ ] **Step 3: Implement**, following `BuildDefaultAgents()` exactly — same frontmatter shape
  (`pia: managed`, fresh lowercase id, `schemaVersion: 1`), same write-only-when-absent rule. Four pages:
  `meeting-action-items`, `document-obligations`, `weekly-review`, `grounded-citations`. Bodies come from
  the source procedures summarised in the review's §3.6(b); English, unlocalized (design D2).

  Each body must be a worked example of the format — `## When to use` with a `**Don't use for:**` bullet,
  `## Procedure` with a *Done when* on every step. They are the only examples most owners will ever see.

- [ ] **Step 4: Run tests, confirm green**
- [ ] **Step 5: Commit**

### Task 10: Reach — agent runs and both routine legs

**Files:**
- Modify: wherever the agent planner and step turns compose their system prompt (grep `AgentPlanner` for
  its prompt construction; `:782` and `:827` are the planner and replan prompts named in checklist A4)
- Test: `tests/Pia.Wpf.Tests/Services/SkillReachTests.cs` (create)

- [ ] **Step 1: Write the failing tests** — the composed prompt for an agent planner turn, an agent step
  turn, and a `BackgroundTurnRequest` turn each contain `## Skills`; a voice-mode turn does **not**.

The negative case is as much the point as the positives. Voice mode is a known ungated write path and is
deliberately out of scope.

- [ ] **Step 2: Run and confirm failure**

- [ ] **Step 3: Implement.** Both routine legs already compose through the same
  `IAssistantPromptComposer`, so they may pass with no change once Task 6 lands — **run the tests before
  writing any code** and only touch what actually fails. `load_skill` is a read, and reads default-allow
  in `BackgroundAssistantTurnRunner` (`:138`), so there is no grant plumbing on this task.

- [ ] **Step 4: Run tests, confirm green**
- [ ] **Step 5: Commit**

### Task 11: `RoutineBlueprint.SkillSlug`

**Files:**
- Modify: `src/Pia.Wpf/Models/RoutineBlueprint.cs:5-16`
- Test: `tests/Pia.Wpf.Tests/Models/RoutineBlueprintTests.cs` (extend the existing file)

- [ ] **Step 1: Write the failing test** — a blueprint carrying `SkillSlug` produces a `QueryTemplate`
  that names the skill, and every slug named by a blueprint resolves to a starter skill that actually
  ships. That second assertion is what stops a dangling reference.

- [ ] **Step 2: Run and confirm failure**

- [ ] **Step 3: Add the optional trailing parameter** — `string? SkillSlug = null`, trailing and defaulted
  so all eight existing construction sites stay source-compatible. Point `meeting-followup` at
  `meeting-action-items`.

- [ ] **Step 4: Run tests, confirm green**
- [ ] **Step 5: Commit**

---

## Chunk 4: The harvest

Read design §6 and §11.1 before starting. D1 is resolved: `SkillHarvest` is a new kind, and it is safe
because the due-jobs query is owner-pinned.

### Task 12: `save_skill`

**Files:**
- Modify: `src/Pia.Wpf/Services/MemoryToolHandler.cs`
- Modify: `src/Pia.Wpf/Services/TokenizingAiClientService.cs:13` (`WriteOperations`)
- Modify: `src/Pia.Wpf/Services/ActionCardBuilder.cs:158`, `:203` (status text and the diff-card arm)
- Test: `tests/Pia.Wpf.Tests/Vault/SaveSkillToolTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task SaveSkill_ReturnsAPendingCallWithADiff() { }
[Fact] public async Task SaveSkill_OverlongDescription_IsRejectedBeforeAnyWrite() { }
[Fact] public async Task SaveSkill_ExistingSlug_DiffsAgainstTheCurrentBody() { }
[Fact] public async Task SaveSkill_WritesNothingUntilApproved() { }
```

The last one is the safety property: the design's whole defence against the proposal feedback loop is that
no skill appears without the owner seeing a diff.

- [ ] **Step 2: Run and confirm failure**

- [ ] **Step 3: Implement**

Return a `MemoryToolCall` with a populated `DiffPreview` and `TargetPath`, exactly as `remember` does at
`MemoryToolHandler.cs:332` — read that block first and mirror it. There is **no central write-tool
registry**: "write-ness" is emergent from returning a pending action, which `ResolveToolGate` then checks
against the granted set (`BackgroundAssistantTurnRunner.cs:511`, `IsNamedGrant`).

Two registrations are still needed and are easy to miss:
- `TokenizingAiClientService.WriteOperations` — so the tool's arguments are privacy-tokenized like every
  other write.
- `ActionCardBuilder` — a status string and the `"remember" or "update_source" or …` arm at `:203` that
  decides which calls render as a diff card.

- [ ] **Step 4: Run tests, confirm green**
- [ ] **Step 5: Commit**

### Task 13: `ScheduledJobKind.SkillHarvest`

**Files:**
- Modify: `src/Pia.Wpf/Models/ScheduledJob.cs:5`
- Modify: `src/Pia.Wpf/Services/ScheduledJobBackgroundService.cs:474-475`
- Test: `tests/Pia.Wpf.Tests/Services/SkillHarvestDispatchTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void SkillHarvest_IsOrdinalTwo() => Assert.Equal(2, (int)ScheduledJobKind.SkillHarvest);

[Fact] public async Task SkillHarvestJob_DoesNotDispatchAsResearch() { }

[Fact] public async Task DueJobs_ExcludeAJobOwnedByAnotherDevice() { }
```

The third test does not look like it belongs to this feature, and it is the most important one here. It
pins the property that makes the new enum value safe on a peer running an older build (design §11.1). If a
later change to the due-jobs query drops the owner-device predicate, this feature silently becomes unsafe,
and this is the test that says so.

- [ ] **Step 2: Run and confirm failure**

- [ ] **Step 3: Implement**

Append to the enum — **never reorder, never remove**; it crosses the sync wire as an int:

```csharp
public enum ScheduledJobKind { Research, AgentTask, SkillHarvest }
```

Then replace the ternary at `:475` with a switch carrying an explicit arm per kind. Give the `default` arm
a warning log and no dispatch, so a future kind is inert here even though it cannot be inert on an older
peer.

- [ ] **Step 4: Run tests, confirm green**
- [ ] **Step 5: Commit**

### Task 14: `SkillHarvestService`

**Files:**
- Create: `src/Pia.Wpf/Services/SkillHarvestService.cs`
- Modify: `src/Pia.Wpf/Bootstrapper.cs` (registration)
- Test: `tests/Pia.Wpf.Tests/Services/SkillHarvestServiceTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Compose_IncludesChatsSinceTheLastHarvest() { }
[Fact] public async Task Compose_ExcludesChatsBeforeTheLastHarvest() { }
[Fact] public async Task Run_GrantsOnlySaveSkill() { }
[Fact] public async Task Run_NoChats_DoesNotStartATurn() { }
[Fact] public async Task Run_AdvancesTheWatermarkOnlyOnSuccess() { }
```

`Run_GrantsOnlySaveSkill` asserts `GrantedWriteTools` is exactly `["save_skill"]`. A harvest turn that can
also `remember` or `write_file` is a different, much larger permission than the one the design justified.

- [ ] **Step 2: Run and confirm failure**

- [ ] **Step 3: Implement**

The service reads assistant chats since a persisted watermark, composes them into the prompt, and calls
`IBackgroundAssistantTurnRunner.RunAsync` with:

```csharp
new BackgroundTurnRequest
{
    Prompt = prompt,
    Provider = provider,
    GrantedWriteTools = ["save_skill"],
    Trigger = AgentRunTrigger.Scheduled,
    TriggerRef = job.Id,
    OwnerDeviceId = job.OwnerDeviceId,
}
```

Chat content reaches this one turn and nothing else — that is the reason there is no `search_chats` tool.
The corpus is user content, so any log line about it uses `SensitiveDebug`; the counts may be logged
normally.

The instruction should ask for conventions **stated or corrected more than once**, one `save_skill` call
each, and an explicit one-line "nothing recurred" instead of a padded list — copy the phrasing discipline
from `topic-digest`'s `QueryTemplate` in `RoutineBlueprint.cs:41-48`.

- [ ] **Step 4: Run tests, confirm green**
- [ ] **Step 5: Commit**

### Task 15: The weekly job

**Files:**
- Modify: `src/Pia.Wpf/Services/ScheduledJobBackgroundService.cs` (the new dispatch arm calls the service)
- Modify: `src/Pia.Wpf/Models/RoutineBlueprint.cs` (a ninth card)
- Modify: the three `.resx` files (title + description)
- Test: `tests/Pia.Wpf.Tests/Services/SkillHarvestDispatchTests.cs` (extend)

- [ ] **Step 1: Write the failing test** — a `SkillHarvest` job coming due invokes `SkillHarvestService`
  and not the research leg.

- [ ] **Step 2: Run and confirm failure**

- [ ] **Step 3: Implement.** The blueprint card gives the harvest a visible, editable, switch-off-able
  home in Routines, which is what makes the proposal loop inspectable. `Kind: SkillHarvest`,
  `Recurrence: Weekly`, `GrantedTools: ["save_skill"]`, `DefaultEffort: Low`. Add the `.resx` strings to
  **all three** locales; `LocalizationParityTests` enforces it.

- [ ] **Step 4: Run tests, confirm green**
- [ ] **Step 5: Commit**

---

## Done criteria

- [ ] `dotnet test` with no filter: **`failed: 0`**.
- [ ] `dotnet build -t:Rebuild -v:n` reports **`0 Warning(s)`** in **both** Debug and Release. WPF
      re-reports `src/` warnings under a generated `_wpftmp.csproj`; fixing the source clears both.
- [ ] A hand-written skill page loads and visibly changes an answer in the real app.
- [ ] A harvest run against a profile with real chat history proposes at least one page, and the diff
      appears for approval rather than being written.
- [ ] Open a **pre-change profile**: skills absent, no crash, Routines renders. There is no schema
      migration in this plan, but the vault-shape assumptions are new.

## Not in this plan

Design §6.3 phase 2 — the counted-repetition signal store and the rejected-output signal. Both are
precision upgrades to be tuned against what the naive harvest actually proposes. Also out: staleness
handling for skills whose convention has changed, and voice-mode reach.
