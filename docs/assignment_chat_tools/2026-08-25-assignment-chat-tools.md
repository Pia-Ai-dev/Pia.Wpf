# Managing background assignments from chat

**Status:** proposed, not started
**Owner:** Marco Altmann
**Written:** 2026-08-25
**Origin:** the question "can a user start/query background jobs from chat?", asked after the first live
end-to-end run of the Mesh task plane (see `C:\projects\Pia\docs\pia-mesh\README.md`).

## The answer today: half of it

"Background jobs" is two different features in this app, and only one of them is reachable from chat.

| Surface | What it is | From chat? |
|---|---|---|
| **Routines / scheduled jobs** | recurring, client-side; each firing runs as a headless assistant turn and saves a chat | **Yes** — `create_scheduled_research`, `query_scheduled_research`, `update_scheduled_research`, `delete_scheduled_research`, `list_routine_blueprints`, `create_routine_from_blueprint` (`ScheduledJobToolHandler`) |
| **Assignments (Mesh task plane)** | one-shot, server-side; runs on Temporal, outlives the request, produces an artifact | **No** — UI only: the `Assistant_RunAssignment` composer button and the Assignments view |

So a user can say "every Monday research crypto trends" and it works, but cannot say "kick that off as a
background assignment" or "did my assignment finish?" — those require leaving the conversation.

This plan adds the assignment half.

## The constraint that shapes the whole design

`AssignmentRunOrchestrator.StartAsync` takes an `AssignmentConsentReceipt` as a **required** argument, and:

- `IAssignmentConsentStore.RecordAsync` is the only thing that can produce one;
- `WasRecorded` is deliberately **session-scoped** — "a receipt that outlived the process it was granted in is
  evidence of nothing";
- `SelectionMatches` requires the receipt to be about *this* request — skill name plus the exact item set — so
  a stale receipt for a small selection cannot authorise a larger one.

That machinery exists to make "no background caller can send" a property rather than a promise. **A model
choosing what to send is exactly the case it is defending against**, and this route is the one place content
leaves the end-to-end-encrypted plane. So the tool must not mint a receipt, and must not be able to reach
`StartAsync` on its own.

The consequence is a hard split: **reading is a tool, starting is a proposal.**

## The tools

One new built-in tool pack, `assignments`, new plugin GUID `10000000-0000-0000-0000-00000000000A`
(`…-009` is the highest in use).

### `query_assignments` — read-only

Lists this user's runs, newest first. Wraps `IAssignmentApiClient.ListAsync`.

Returns per run: id, skill, status, step count, token spend, created/completed timestamps. Deliberately **not**
the artifact — the list projection omits it server-side so that polling cannot become a way of downloading
every result the user owns, and the tool must not undo that.

`ListAsync` returns `null` for a transport failure and an empty list for "no runs". **These must produce
different tool results.** Collapsing them makes the model tell the user they have no background jobs when the
truth is the server was unreachable — a confident wrong answer about their own data.

### `get_assignment` — read-only

One run by id, with its event log and artifact text. Wraps `IAssignmentApiClient.GetAsync`.

The artifact is usually already a local chat by the time anyone asks (the drain pass writes it, then collects,
after which the server copy is gone). So this tool's honest job is *progress*, not results: status, the event
log, and a pointer to the chat if one exists. When `plaintextDroppedAt` is set and the run is not in the local
pending store, say the result lives in the user's chat history rather than returning nothing.

### `start_assignment` — proposal only, never executes

Takes a skill name and a prompt. Returns a **`PendingAction`**, exactly as `ScheduledJobToolHandler` does for
`create_scheduled_research`. Confirming the action opens the existing consent dialog
(`AssignmentConsentViewModel`) pre-filled with the model's proposed skill and prompt.

The human then picks the records, ticks the affirmation, and presses Send — which is what calls `RecordAsync`
and mints the receipt. The tool never touches the receipt, and the model never chooses what leaves the
encrypted plane.

The model may propose the *prompt*. It may not propose the *selection*: `declaredInputTypes` scoping and record
choice stay in the dialog. A tool parameter for items would be the model deciding what to decrypt, which is the
whole thing the consent boundary exists to prevent.

## Two traps that will bite an implementation

### A pending action is NOT inert in a headless run

`BackgroundAssistantTurnRunner` routes tools through the same `IPluginService.RouteToolCallAsync`, and when it
gets a non-null `pending` it does **not** drop it — it resolves the unattended gate, whose own doc comment says
"A tool named in this run's grant list executes here — including a destructive one." So a routine granted
`start_assignment` would call `ExecutePendingActionAsync` with no human anywhere, and the orchestrator would
either have to forge a receipt or fail.

**`start_assignment` must be excluded from headless grant sets**, not merely expected not to be granted. There
is precedent for exactly this exclusion — MCP tools "are disabled for headless/scheduled runs this milestone"
because they bypass the unattended write-gate — but the mechanism is MCP-specific (`IsMcpTool`), so this needs
either a small generalisation of it or a pre-route interception like `emit_step_result` / `suggest_agent_mode`,
both of which have no plugin, no GUID and no route entry and are intercepted in both handlers.

The read-only two are fine headless and should stay grantable — a routine that reports on assignment progress
is useful and sends nothing.

### The surface hides itself, so the tools must too

`AssignmentSurface.Hidden` is returned for no server, no token, `401`/`403`/`404`, or an empty skill list, and
the client removes the entry points entirely rather than disabling them. A tool pack that advertises
`start_assignment` on a local-only install invites the model to offer a feature that cannot exist and then
explain a failure.

Register the pack's tools against the same probe the nav uses. The pack row itself can exist unconditionally
(it is how the user enables/disables it); it is `GetTools()` that should be empty when the surface is hidden.
`PluginService.GetToolCatalog` already skips disabled plugins, so an empty `GetTools()` also correctly removes
them from the grant offers.

## Decision gates

| # | Question | What it cancels if answered "no" |
|---|---|---|
| D1 | Does `start_assignment` ship at all, or do we ship the two read-only tools and leave starting to the composer button? | Cancels the pending-action work and the headless exclusion entirely — the read-only pair is then a clean `S`. |
| D2 | If it ships: does the consent dialog accept a pre-filled prompt from a tool, or does it open blank? | Pre-filling is the whole value; opening blank makes the tool no better than the button. `InitializeAsync` already takes a `prefillPrompt`, so this is likely free. |

D1 is genuinely open. The button already exists two clicks away, and the honest case for the tool is
conversational continuity ("research that in the background") rather than saved clicks.

## Steps

*Effort:* `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface
*Value:* `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` unblocks a High

- [ ] **A1 — `IAssignmentToolHandler` + the two read-only tools.** New handler beside
      `ScheduledJobToolHandler`, wrapping `IAssignmentApiClient.ListAsync`/`GetAsync`, with the
      null-vs-empty distinction spelled out in the tool result.
      *Deps:* — · *Effort:* `S` · *Value:* `High`
- [ ] **A2 — Pack registration.** `AssignmentsPluginId` in `BuiltInPluginDefaults`, a
      `BuiltInPluginHandler.FromAssignmentHandler` adapter, `Bootstrapper` wiring, and `GetTools()` returning
      empty while `AssignmentSurface` is hidden.
      *Deps:* A1 · *Effort:* `XS` · *Value:* `Enabler`
- [ ] **A3 — Localization + tool-catalog test.** Three resx files in parity (en/de/fr), and the pack's tools
      asserted present in the catalog when the surface is available and absent when it is not.
      *Deps:* A2 · *Effort:* `S` · *Value:* `Med`
- [ ] **B1 — `start_assignment` as a pending action.** Gated on D1. Returns a `PendingAction`; confirming it
      opens the consent dialog with the proposed skill and prompt pre-filled. No item parameter.
      *Deps:* A2, D1 · *Effort:* `S` · *Value:* `High`
- [ ] **B2 — Headless exclusion for `start_assignment`.** Either generalise the MCP headless exclusion or
      intercept pre-route. Needs a test proving a background turn that names it in its grant set does **not**
      execute it.
      *Deps:* B1 · *Effort:* `S` · *Value:* `High`

Total: `M` if B ships, `S` if D1 says no.

**Suggested order:** A1 → A2 → A3 ships the useful, safe half and can go in on its own. Answer D1 before
starting B; if it ships, B1 and B2 land together and B2 is not optional — B1 without it is a hole in the
consent boundary, not an incomplete feature.

## What this deliberately does not add

- **No `cancel_assignment`.** Cancelling is one click in a view the user is already looking at, and a
  model-initiated cancel of work the user paid tokens for is a bad trade.
- **No `collect`.** It is irreversible and the drain pass owns it. Nothing about it belongs in a conversation.
- **No item selection from the model.** See the consent constraint above.
