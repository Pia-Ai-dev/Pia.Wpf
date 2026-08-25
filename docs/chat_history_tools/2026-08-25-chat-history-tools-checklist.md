# Chat-history tools — checklist

**Status:** Steps 1–10 landed 2026-08-25 with the gate green; step 11 (human smoke test) is open.
No open decisions — all four were settled 2026-08-25, so this is executable cold.
**Owner:** Marco Altmann
**Written:** 2026-08-25
**Origin:** [2026-08-25-chat-history-tools-design.md](2026-08-25-chat-history-tools-design.md)

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new
surface · `L` a week or more, a new subsystem.

**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline ·
`Enabler` little standalone value, unblocks a High.

## Decisions (settled — do not re-litigate)

Each of these could have cancelled steps below it. All four are answered; the steps assume these
answers. Full reasoning in the design doc §10.

| # | Question | Answer | Consequence for the steps |
|---|---|---|---|
| **G1** | Settings toggle, and its default? | **Toggle, default on** (`AssistantChatHistoryToolsEnabled = true`) | Step 9 is in scope and ships in the same branch as step 8. |
| **G2** | Are headless agent-run chats searchable? | **Yes, unlabelled** | Step 4 takes no `IAgentRunService` dependency. Labelling is in *not yet planned*. |
| **G3** | Ranked search with snippets, or metadata only? | **Ranked** | Steps 2 and 3 exist; `SearchAsync` stays untouched for the no-query path. |
| **G4** | Scope reads to the current chat's provider? | **No** | Step 2 still carries a `providerId` parameter so reversing this is a call-site change. |

## Steps

- [x] **1. `TaskContext.ChatId`** — add the optional member and set it at all four ambient sites, so "which chat am I in" is answerable on the unattended surfaces where `TaskId` is the run id. *Deps:* — · *Effort:* XS · *Value:* Enabler
- [x] **2. `SearchRankedAsync` on the chat store** — one FTS5 method with `snippet()` and `ORDER BY rank`, plus the `AssistantChatSearchHit` record, the shared stub-hiding `EXISTS` constant, and the end-of-day `toDate` expansion. *Deps:* 1 · *Effort:* S · *Value:* Enabler
- [x] **3. Store tests** — snippet non-empty on a body match (the content-carrying-FTS assertion), relevance ordering, `excludeChatId`, stubs hidden, and both search paths agreeing on the same date bounds. *Deps:* 2 · *Effort:* XS · *Value:* Med
- [x] **4. `ChatHistoryToolHandler` + interface** — the two tools, their schemas and descriptions, current-chat exclusion in both (search hides it, `read_chat` refuses it), caps, `ThinkingContent` stripped, `has_more`/`next_offset`, `IsAvailable` off the G1 setting. *Deps:* 1, 2 · *Effort:* M · *Value:* High
- [x] **5. Handler tests** — exclusion on both tools, null-`ChatId` fail-open, cap clamping in both directions, paging, unknown id returns a readable error. *Deps:* 4 · *Effort:* S · *Value:* High
- [x] **6. Plugin registration** — `BuiltInPluginDefaults` entry (design §11.1 verbatim), `FromChatHistoryHandler`, the `PluginService` ctor param and switch arm, the one test construction site, the `Bootstrapper` singleton. *Deps:* 4 · *Effort:* S · *Value:* Enabler
- [x] **7. Registration test** — mirror `GitPluginRegistrationTests`; also re-run `ToolClassifierTests` unchanged as proof no `ToolClass` was added. *Deps:* 6 · *Effort:* XS · *Value:* Med
- [x] **8. Prompt + status strings** — the decision-tree step 5 and the step 3 counter-example in `AssistantPromptComposer`, the two `ActionCardBuilder` arms, and both keys in all three `MessageStrings*.resx` (design §11.5–11.7). *Deps:* 6 · *Effort:* XS · *Value:* Med
- [x] **9. Settings toggle** — `AssistantChatHistoryToolsEnabled` through `AppSettings`, `AssistantSettingsViewModel`, `AssistantView.xaml` with its `AutomationId`, and three `ViewStrings*.resx` (design §11.8). No `ViewAutomationIdTests` edit — that number is a floor, not a count. *Deps:* 6 · *Effort:* S · *Value:* High
- [x] **10. Zero-warning + gate** — `dotnet build -t:Rebuild` in Debug **and** Release at `0 Warning(s)`, `dotnet test` at `failed: 0`. *Deps:* 1–9 · *Effort:* XS · *Value:* High
- [ ] **11. Human smoke test** — in a real profile: ask for something from a past chat, confirm the hit list is useful, the drill-in reads, the current chat is neither listed nor readable by id, and the toggle actually removes the tools. *Deps:* 10 · *Effort:* XS · *Value:* High

## Suggested order

Cheapest decisive work first: **1** (the `ChatId` trap — small, and load-bearing for everything
after it), then **2 + 3** (the store method, proven by test before anything depends on it). That
pair is the whole technical risk of the feature. If `snippet()` or the FTS join misbehaves it
surfaces there, for a day's work, rather than after the handler is built on top of it.

Then the vertical slice: **4 + 5**, **6 + 7**, **8**. At the end of step 8 the tools are live with
no way to turn them off, so **9** lands in the same branch — not a follow-up.

**10** and **11** close it. Do not treat the branch as done on a green `dotnet test` alone: the
design's central claim — that these reads are ungated on every surface, background runs included
— is about behaviour no unit test observes. Step 11 is where a human actually looks at it.

## Not yet planned

- `@chat` at-command domain (`AtCommandDomain`, `GetAtCommandToolMapping`, the picker).
- Labelling a hit as "produced by an agent run" via `IAgentRunService.GetByChatAsync` (G2) — an
  extra handler dependency and one query per hit, for a distinction nobody has asked for yet.
- A paged `GetMessagesRangeAsync` on the store, if `read_chat`'s full-transcript read proves
  costly on a real imported profile.
- Surfacing a chat hit as a clickable card in the timeline rather than plain tool output.
- Whether an agent run's own chat should be readable by that same run (self-reference loop).
