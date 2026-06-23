# clarify — Modification Instructions (bucket: reuse)

> **Scope of this doc.** Planning only. It tells an implementer exactly *what* to change and *why*, naming
> the real Pia files/classes/methods. No C# is written here. The hard part — a **blocking human-input
> round-trip wired into the UI-thread-affine turn loop without deadlocking** — already exists for write
> actions (`ActionCardInfo` + `ChatSession.HandleToolCall`). clarify is an **extension** of that proven path,
> not a new subsystem.

---

## 1. The tool contract (from `docs/coding-tools-spec/clarify.md`)

| Field | Type | Rules |
|-------|------|-------|
| `question` | `string` (required) | The question text **and only the question**. Must NOT embed answer options as prose. |
| `choices` | `string[]` (optional, `maxItems: 4`) | Selectable options. Presence selects **multiple-choice** mode; absence selects **open-ended free-text** mode. |

Behaviors the implementation must honor:

1. **Two modes by `choices` presence.** With `choices` (≤4): render selectable rows; UI **auto-appends a 5th
   "Other (type your answer)" row**. Without `choices`: open-ended free-text input.
2. **Question/choices separation is enforced.** Reject or repair calls that enumerate options inside
   `question` (the single most common misuse).
3. **Blocking round-trip.** Block the agent loop until the user answers or cancels, then **return the
   chosen/typed text as the tool result** (a `FunctionResultContent` string).
4. **Arg normalization.** Flatten dict-shaped choices (`{"label":"..."}`) to the user-facing string; coerce
   and validate before rendering.
5. **Usage discipline** (lives in the tool description, not code): genuine ambiguity / trade-off decisions /
   offering to save skill or memory. **Not** for dangerous-command confirmation (that is the
   `terminal`/`execute_code` approval guard — spec line 54). **Subagents (`delegate_task`) must have clarify
   stripped** — only the top-level agent talks to the user (spec line 55).

---

## 2. What already exists (verified against the codebase)

### 2.1 The blocking round-trip — proven for write actions

`ChatSession.HandleToolCall` (`src/Pia.Wpf/ViewModels/Models/ChatSession.cs`, lines 404–487) is the canonical
pattern clarify rides on:

- Calls `_pluginService.RouteToolCallAsync(toolCall)` → `(object? Result, PluginToolCall? PendingAction)`.
- If `Result` is non-null → returns it immediately as the tool result (lines 431–432).
- If `PendingAction` is non-null (write op) → builds an `ActionCardInfo`, **adds it to
  `message.ActionCards`**, flips `SetState(ChatState.WaitingForTool)`, and `await card.WaitForUserDecisionAsync()`
  (lines 435–457). On Accept it runs `pendingAction.Execute()` and returns its result; on Decline it returns a
  fixed instruction string (lines 459–484).

The whole loop is **UI-thread-affine** — `RunTurnAsync` uses no `Task.Run` / `ConfigureAwait(false)`
(ChatSession.cs lines 22–26, 172–173), so awaiting a `TaskCompletionSource` that a UI button completes does
**not** deadlock.

### 2.2 `ActionCardInfo` — the blocking card model

`src/Pia.Wpf/Models/ActionCardInfo.cs`:

- Holds a `private readonly TaskCompletionSource<bool> _tcs` and exposes `Task<bool> WaitForUserDecisionAsync()`
  (lines 55, 64).
- `Accept` → `TrySetResult(true)`; `Decline` → `TrySetResult(false)`; `Cancel` → `TrySetCanceled()`
  (lines 66–91). **Boolean** result — this is the shape mismatch (see §3).
- `ActionCardCategory` enum = `Memory | Todo | Reminder | Files` (lines 14–20). No clarify category, no choice
  rows.

### 2.3 The cancel sweep — load-bearing for non-deadlock

`ChatSession.Cancel()` (lines 152–157) iterates **every message's `ActionCards`** and calls
`CancelPendingActionCards` (lines 159–167), which fires `card.CancelCommand` on each pending card. This is what
lets a turn-level cancel unblock a `WaitForUserDecisionAsync()` await. **Any clarify card MUST be reachable by
this sweep or cancelling a `WaitingForTool` turn hangs the TCS forever.** (See §5 / §6.)

`AssistantMessage.ActionCards` is `ObservableCollection<ActionCardInfo>`
(`src/Pia.Wpf/Models/AssistantMessage.cs`, line 30); its `OnActionCardsChanged` comment documents that the
collection is **only ever `.Add`'d** (lines 114–129) and wires `HasPendingConfirmation` off each card's
`IsPending`.

### 2.4 Registration & routing infra

- `IPluginToolHandler` (`src/Pia.Wpf/Services/Interfaces/IPluginToolHandler.cs`): contract every handler
  implements. `PluginToolCall` record carries `(ToolName, PluginName, Description, Details, Func<Task<object?>> Execute)`.
- `BuiltInPluginHandler` (`src/Pia.Wpf/Services/Plugins/BuiltInPluginHandler.cs`): adapter wrapping a domain
  handler as `IPluginToolHandler`, with `FromXxxHandler` factories and an optional `isAvailable` gate
  (lines 42–45, 185–202 — the files-plugin sandbox gate).
- `BuiltInPluginDefaults` (`src/Pia.Wpf/Services/Plugins/BuiltInPluginDefaults.cs`): well-known plugin GUIDs +
  `SyncPlugin` defaults (handlerId, defaultEnabled, systemPromptAddition). Files plugin id is
  `10000000-0000-0000-0000-000000000006`.
- `PluginService.InitializeBuiltInPlugins` (`PluginService.cs` lines 73–94): switch on `handlerId` → factory →
  `RegisterHandler` (which indexes each tool name into `_toolNameRoutes`, lines 196–207).
- `PluginService.RouteToolCallAsync` (lines 265–284): tool-name → handler lookup → `HandleToolCallAsync`.

### 2.5 Action-card UI builder

`ActionCardBuilder` (`src/Pia.Wpf/Services/ActionCardBuilder.cs`) maps a `PluginToolCall` to an `ActionCardInfo`,
resolving category, localized titles/warnings, and applying privacy detokenization
(`Detokenize`/`DetokenizeDetails`, lines 118–125). `ActionCardControl`
(`src/Pia.Wpf/Controls/ActionCardControl.xaml.cs`) is the code-behind that renders it (XAML-driven; renders
Accept/Decline/Cancel — **no choice rows, no text-input field today**).

### 2.6 Background-session surfacing (free for clarify)

`BackgroundChatNotificationSurface` (`src/Pia.Wpf/Services/BackgroundChatNotificationSurface.cs`) already
notifies on `WaitingForTool` (lines 56–112, `TryResolveBodyKey` 161–171) via in-app snackbar or OS toast, with
an "open chat" action routing back to the originating chat. Because a clarify card lives inline on the
originating session's message, this path covers a background clarify with **no new code**.

### 2.7 Subagent tool filtering site

`AssistantPromptComposer.PrepareTurn` (`src/Pia.Wpf/Services/AssistantPromptComposer.cs`, lines 26–59) resolves
the per-turn tool set from `_pluginService.GetAllTools()` and, for @-commands, filters via
`GetAllowedToolNames` (lines 188–197). This is the natural enforcement point for stripping clarify from
subagents — **but the `delegate_task` compose path was not seen in this review** (see §6, open question).

### 2.8 `IDialogService` — a candidate widget, deliberately NOT used

`IDialogService.ShowInputDialogAsync(title, prompt)` (`src/Pia.Wpf/Services/Interfaces/IDialogService.cs`,
line 20) is a modal text-input primitive. It is **rejected** as the clarify bridge — see §4.0.

---

## 3. Gap analysis

| # | Spec requirement | Current behavior in Pia | Needed change |
|---|------------------|-------------------------|---------------|
| G1 | Tool returns an **arbitrary string** (chosen/typed answer) as the tool result. | `ActionCardInfo` resolves a **`bool`** via `TaskCompletionSource<bool>`. `PluginToolCall` is built around an `Execute` write-lambda; there is **no write to gate**. | Add a **text-returning** completion path: a `TaskCompletionSource<string?>` on the card and a `Task<string?> WaitForAnswerAsync()`. clarify never sets `PluginToolCall.Execute`. |
| G2 | Multiple-choice UI: ≤4 selectable rows + auto "Other (type your answer)". | `ActionCardControl` renders Accept/Decline/Cancel only. No choice rows, no free-text field. | Extend `ActionCardInfo` with a clarify shape (`Question`, `Choices`, mode flag) and extend the control's XAML to render choice rows (multiple-choice) or a text box (open-ended), plus the auto-appended Other row. |
| G3 | Two modes by `choices` presence. | No notion of modes; cards are write-confirmation only. | Card model carries a mode discriminator derived from whether `choices` was supplied/non-empty. |
| G4 | Question/choices separation **enforced** (reject/repair embedded options). | No validation exists. | Add arg validation in a new clarify handler **before** building the card (policy in §4.4). |
| G5 | Normalize dict-shaped choices to strings. | No normalization. | Normalize in the handler before card-build (policy in §4.4). |
| G6 | ≤4 choices enforced. | No enforcement. | Enforce in handler (truncate vs reject — policy in §4.4). |
| G7 | Tool is registered and routable. | No clarify tool, no handler, no route. | New `IClarifyToolHandler` + `BuiltInPluginHandler.FromClarifyHandler` + `BuiltInPluginDefaults` entry + `PluginService` switch arm. |
| G8 | Subagents have clarify **stripped**. | No such rule; `delegate_task` compose path not located. | Add an exclusion rule at the tool-set composition site for subagents (§4.6); confirm the site first. |
| G9 | Privacy: question + typed answer are user payloads. | `HandleToolCall` already models `SensitiveDebug` previews / `#if DEBUG` arg dumps (ChatSession.cs 407–429). | clarify must log question/choices/answer via `SensitiveDebug` only — never plain `LogInformation` (§4.7). |
| G10 | Background clarify reaches the user. | `BackgroundChatNotificationSurface` already notifies on `WaitingForTool`. | **None** — works for free once the card sets `WaitingForTool` (§2.6). |
| G11 | Cancel must unblock the await. | `Cancel()` sweep handles `bool` cards via `CancelCommand`. | clarify card must be in the **same** `message.ActionCards` collection AND its cancel must complete the **string** TCS (with the dismissal sentinel, §4.5) — not just the bool one (§4.3, §5). |

---

## 4. Ordered modification instructions

> Decision recorded up front: **extend the existing card model + collection; do not build a sibling
> `ClarifyCardInfo` in a separate collection.** Rationale (the decisive discriminator): the turn-level
> `Cancel()` sweep, the `ActionCardControl` DataTemplate, the `HasPendingConfirmation` accent, and the
> background-notify path **all key off `message.ActionCards` containing `ActionCardInfo`**. A sibling type in a
> separate collection re-wires all of them, and missing the cancel sweep **deadlocks a cancelled
> `WaitingForTool` turn**. Extending the existing path inherits all four behaviors.

### 4.0 — Forced fork decisions (close these in the doc; do not leave open)

- **Native inline card, NOT `IDialogService.ShowInputDialogAsync`.** A modal dialog (a) cannot return its result
  into the turn loop as a `FunctionResultContent`, and (b) breaks for **background** sessions, where the whole
  `WaitingForTool` → notification-surface → "open chat" flow assumes the question lives **inline on the
  session's message**, not in a modal owned by some window. `IDialogService` stays a widget reference, not the
  bridge.
- **Native, NOT MCP** (cross-cutting Q3): an external MCP server cannot render a WPF card or block the UI
  thread. clarify is intrinsically a host-UI capability. Build native.

### 4.1 — Extend `ActionCardInfo` (`src/Pia.Wpf/Models/ActionCardInfo.cs`)

- Add a clarify **category/kind** so the control can branch. Either add `Clarify` to `ActionCardCategory`
  (enum line 14) or add a dedicated `ActionCardKind` discriminator. Prefer a small dedicated discriminator if
  reusing the `Files`/`Memory` category styling would mislead the title/warning logic in `ActionCardBuilder`.
- Add clarify-only data: `Question` (string), `Choices` (`IReadOnlyList<string>`, already ≤4 and normalized by
  the handler), and an `IsOpenEnded` flag (true when no choices).
- Add a **string completion channel**: `private readonly TaskCompletionSource<string?> _answerTcs` and
  `public Task<string?> WaitForAnswerAsync() => _answerTcs.Task`. Keep the existing bool `_tcs` untouched for
  write actions.
- Add commands the choice UI binds to: e.g. `SelectChoice(string)` → `_answerTcs.TrySetResult(choice)`;
  `SubmitOther(string text)` / `SubmitOpenEnded(string text)` → `_answerTcs.TrySetResult(text)`. Each sets
  `State = Accepted`, `IsExpanded = false`, mirroring the existing `Accept` guard (`if (State != Pending) return;`).
- **Make the existing `Cancel` command complete the string channel too.** `Cancel` currently does
  `_tcs.TrySetCanceled()` (lines 84–91). For a clarify card it must **also** resolve `_answerTcs` — and per the
  spec the model still needs a result, so resolve it with the **dismissal sentinel string** (§4.5), not a
  cancellation. Recommended: branch on kind — clarify cards `_answerTcs.TrySetResult(<dismissal sentinel>)`;
  write cards keep `_tcs.TrySetCanceled()`. (This is what makes the §2.3 cancel sweep work unchanged.)

> **No `offset` / `limit` / `line-numbers` params.** Those belong to the file tools, not clarify. clarify's
> only schema surface is `question` + `choices` (guard against template boilerplate creep).

### 4.2 — New clarify handler + schema

- Add `IClarifyToolHandler` (interface) and `ClarifyToolHandler` (implementation) under `src/Pia.Wpf/Services/`,
  mirroring the shape of the other tool handlers but **without** an `ExecutePendingActionAsync` write path.
- `GetTools()` returns a single `AITool` built via `AIFunctionFactory` with the **exact** schema from
  clarify.md §"JSON Schema (exact)" — same `name`, `description` (including the embedded usage discipline and
  the "options go in `choices`, never in `question`" rule), and `parameters`.
- The handler does **not** itself block. Because the blocking await is session-scoped (it must happen inside
  `RunTurnAsync` on the UI thread), the handler's job is **validation + normalization**, then handing a
  clarify request to `ChatSession`. Two viable wirings — pick one in implementation:
  - **(a) Extend the route tuple.** Add a third slot to the `RouteToolCallAsync` /
    `IPluginToolHandler.HandleToolCallAsync` return so a handler can say "this is an interactive question",
    carrying `(question, choices, isOpenEnded)`. `ChatSession.HandleToolCall` detects it, builds the clarify
    `ActionCardInfo`, awaits `WaitForAnswerAsync()`, returns the string. *(Cleanest contract, but touches the
    `IPluginToolHandler` signature — see G7 regression note.)*
  - **(b) Carry the request inside the existing `PluginToolCall`.** Reuse the `PendingAction` slot but mark it
    as a clarify request (e.g. a sentinel `PluginName == "clarify"` and the question/choices packed into
    `Details`); `ChatSession.HandleToolCall` branches on that marker before the write-confirmation branch.
    *(No interface change, but overloads the write-action record — document the overload clearly.)*
- **Recommendation:** prefer (a) if a clean third return shape is acceptable, since it makes "interactive
  input request" a first-class concept rather than a piggybacked write action. Record the choice as an open
  question for the reviewer (§6).

### 4.3 — Wire the blocking await in `ChatSession.HandleToolCall`

In `ChatSession.HandleToolCall` (lines 404–487), add a clarify branch **before** the existing write-action
branch:

1. Build the clarify `ActionCardInfo` (via `ActionCardBuilder`, §4.4) from the normalized
   `(question, choices, isOpenEnded)`.
2. `message.ActionCards.Add(card)` (same collection as write cards — required for the cancel sweep and the
   `HasPendingConfirmation` accent).
3. `SetState(ChatState.WaitingForTool)`.
4. `var answer = await card.WaitForAnswerAsync();` inside try/finally that restores
   `SetState(ChatState.Running)` (mirror lines 442–457).
5. Return `answer` (the chosen/typed string, or the dismissal sentinel on cancel) as the tool result.
   **No `Execute()` call** — clarify has no write.

Keep `HandleToolCallWithStatus` (lines 396–402) working: `ResolveStatusText("clarify")` should fall through to
the default "processing" string (it already does — `ActionCardBuilder.ResolveStatusText` returns
`Msg_Assistant_StatusProcessing` for unknown names, lines 66–84).

### 4.4 — Validation, normalization, and card-build policy (in the handler / builder)

Performed in `ClarifyToolHandler` **before** the card is built:

- **Dict-shaped choices (G5):** if a choice arrives as `{"label": "..."}` (or similar), flatten to the
  user-facing string. Drop empty/whitespace results.
- **>4 choices (G6):** **truncate to the first 4** (recommended over hard-reject — keeps the turn moving; the
  schema's `maxItems:4` already signals intent). Log the truncation via `SensitiveDebug`.
- **Empty `choices` array vs absent:** treat an empty array the same as absent → open-ended mode.
- **Embedded options in `question` (G4 — "the single most common misuse"):** **policy = soft-reject, do not
  attempt prose repair.** Heuristically detect enumerations in `question` (e.g. trailing `1) … 2) …`, ` - `
  bulleting, or `a) / b)` patterns) when `choices` is empty; if detected, **return a soft error string** to
  the model (not a card) instructing it to resubmit with options in `choices`. Rationale: reliable extraction
  of options from free prose is error-prone; a one-line correction loop is cheaper and safer than guessing.
  Mark detection-heuristic tuning as an open question (§6).
- **"Other (type your answer)" row (G2):** **render-only — do NOT inject "Other" into the `choices` data.**
  The control appends the Other affordance at render time. This keeps the data clean and the count ≤4, and
  keeps the model's returned string equal to the user's typed text (not the literal "Other").
- **`ActionCardBuilder` extension:** add a `Build` overload (or branch) that maps a clarify request to the
  clarify-kind `ActionCardInfo`, setting `Question`/`Choices`/`IsOpenEnded` and a localized title (e.g. a new
  `ActionCard_Category_Clarify` resource). Apply the existing `Detokenize` to the question and to each choice
  if tokenization is enabled (reuse `Detokenize`, lines 118–119) so privacy tokens render as real values.

### 4.5 — Dismissal/cancel result string

When the user cancels/dismisses without answering, the model still needs a deterministic tool result. Return a
fixed instruction string analogous to the declined-action string (ChatSession.cs line 483), e.g.
*"The user dismissed the question without answering. Do not re-ask the same question; proceed with a
reasonable default or ask what they would like to do."* Define it as a localized resource. This is the
**dismissal sentinel** referenced in §4.1 and §4.3.

### 4.6 — Subagent stripping (G8)

clarify must be absent from the tool set composed for `delegate_task` subagents (spec line 55). Enforcement
belongs wherever a subagent's tool list is built. `AssistantPromptComposer.PrepareTurn` /
`GetAllowedToolNames` (AssistantPromptComposer.cs 26–59, 188–197) is the analogous filter for @-commands and is
the likely site — **but the `delegate_task` compose path was not located in this review.** Action: locate the
subagent tool-composition site first, then add a rule that excludes the `clarify` tool name for subagent turns.
Do not guess the mechanism (§6).

### 4.7 — Privacy-logging compliance (G9)

Per CLAUDE.md, the `question` text, the `choices`, and the user's typed/selected answer are **user
content/payloads** → must use `_logger.SensitiveDebug(...)` (or `#if DEBUG`), never plain `LogInformation`. The
existing `HandleToolCall` already models this exactly (`SensitiveDebug` result preview at lines 425–429,
`#if DEBUG` arg dump at 408–410). New log lines:

- Tool-call received: `LogInformation` may state only `"Handling clarify (mode={Mode}, choiceCount={N})"`
  (mode + count are non-sensitive); the question/choices text goes to `SensitiveDebug`.
- Answer: `SensitiveDebug` only.
- Truncation/soft-reject events: `SensitiveDebug` (they reference the offending text).

### 4.8 — Registration (G7)

- Add `ClarifyPluginId` GUID to `BuiltInPluginDefaults` (next in the `10000000-…-0000000000NN` series), add it
  to `PreloadedPluginIds`, and add a `Defaults` entry with `handlerId: "clarify"`, `defaultEnabled: true`, and
  a `systemPromptAddition` reinforcing the usage discipline (mirrors the files entry, lines 84–95).
- Add `BuiltInPluginHandler.FromClarifyHandler(...)` factory (mirror `FromFilesHandler`, lines 185–202; clarify
  needs **no** `isAvailable` gate — it is always available to top-level agents).
- Add the `"clarify"` arm to the `InitializeBuiltInPlugins` switch (`PluginService.cs` lines 79–88).
- Register `IClarifyToolHandler` → `ClarifyToolHandler` in the DI container (Bootstrapper) and add it to the
  `PluginService` constructor injection list (lines 42–63), matching the other built-in handlers. *(Bootstrapper
  edit is implementation, not part of this planning doc; it is listed here so it is not forgotten.)*

### 4.9 — UI rendering (`ActionCardControl` XAML + code-behind)

Extend the `ActionCardControl` view to branch on the clarify kind:

- **Multiple-choice:** render `Choices` as selectable rows (e.g. an `ItemsControl` of buttons bound to
  `SelectChoiceCommand`), plus a final **"Other (type your answer)"** row that reveals a text box bound to
  `SubmitOtherCommand`.
- **Open-ended:** render a single multiline text box + submit button bound to `SubmitOpenEndedCommand`.
- Keep the existing write-confirmation template untouched (Accept/Decline/Cancel) — branch by kind so
  current cards are unaffected.

### Implementation checklist (maps to spec §"Implementation checklist")

- [ ] Mode by `choices` presence; ≤4 choices (truncate); auto "Other" row rendered (not data-injected).
- [ ] Soft-reject options embedded in `question`; return correction string.
- [ ] Blocking UI round-trip via extended `ActionCardInfo` (`TaskCompletionSource<string?>`); return answer
      string from `ChatSession.HandleToolCall`.
- [ ] Normalize dict-shaped choices → strings in the handler before card-build.
- [ ] Cancel sweep resolves the string TCS with the dismissal sentinel (no deadlock).
- [ ] Subagent strip rule added at the (to-be-confirmed) `delegate_task` compose site.
- [ ] Privacy: question/choices/answer logged via `SensitiveDebug` only.
- [ ] Handler + factory + defaults + PluginService arm + DI registration.

---

## 5. Regression risks

| Risk | Why | Mitigation |
|------|-----|------------|
| **Cancel deadlock** | If the clarify card is NOT in `message.ActionCards`, or its `Cancel` does not complete `_answerTcs`, then `ChatSession.Cancel()`'s sweep (lines 159–167) never unblocks `WaitForAnswerAsync()` and a cancelled `WaitingForTool` turn **hangs**. | Use the same `ActionCards` collection; make `Cancel` resolve the string TCS with the dismissal sentinel. Add a test: start a clarify turn → `Cancel()` → turn settles to Idle (not stuck). |
| **`IPluginToolHandler` signature change** (if wiring 4.2(a)) | Adding a third return slot touches every handler (memory/todo/reminder/files/research/MCP) and the `TokenizingAiClientService` decorator. | If choosing (a), make the new slot optional/nullable so existing handlers compile unchanged. Or choose (b) (no interface change). |
| **`ActionCardBuilder` write-only assumptions** | `Build` maps category from `PluginName` and derives `IsDestructive` from `ToolName.Contains("delete")` (lines 26–35). A clarify card forced through the existing path would mis-title. | Branch/overload in the builder; do not route clarify through the write-action `Build`. |
| **`OnActionCardsChanged` "Add-only" invariant** | `AssistantMessage.OnActionCardsChanged` (lines 114–129) explicitly assumes the collection is only `.Add`'d and would leak subscriptions on `Clear()`. | clarify follows the same Add-only usage; do not introduce `Clear()`. |
| **Persistence of clarify cards** | `HasPendingConfirmation` is computed/never persisted (AssistantMessage.cs 50–53). A resolved clarify answer must survive as message content/result, not as card state. | Ensure the answer is reflected in the tool-result message; do not rely on persisting the card. Confirm `AssistantMessageMapper` does not choke on the new card kind. |
| **Sandbox/files UX untouched** | clarify shares the card model but must not alter the files sandbox gate (`FromFilesHandler` `isAvailable`, lines 185–202). | clarify has its own handler/plugin id; it does not touch `IFilesToolHandler` or `SafeFolderPath`. |
| **Tool-selection prompt tree** | The decision-tree system prompt (AssistantPromptComposer.cs 130–147) routes Reminder/Todo/Memory; clarify is orthogonal. | clarify guidance lives in its own `systemPromptAddition`; do not wedge it into the decision tree. |

---

## 6. Open questions

1. **Wiring (a) vs (b)** for surfacing the interactive request from the handler to `ChatSession`: extend the
   `RouteToolCallAsync` / `IPluginToolHandler` return tuple with a clean third "interactive input" slot
   (touches all handlers but is the honest contract), or piggyback on `PluginToolCall` with a sentinel
   `PluginName == "clarify"` (no interface change, overloads the write record)? Recommend (a); needs reviewer
   sign-off because of the cross-handler ripple.
2. **`delegate_task` tool-composition site** was not located in this review. Where does a subagent's tool list
   get built, and is `AssistantPromptComposer` even on that path? The strip rule (§4.6) cannot be specified
   precisely until this is confirmed. (Cross-cutting Q4: per-session answer routing is **already implicit** —
   the card lives on the originating session's message and that session's `RunTurnAsync` awaits it, so no
   central `task_id` is needed for routing; the only task_id-adjacent work is this subagent strip.)
3. **Embedded-options heuristic (§4.4):** how aggressive should detection be? False positives would reject
   legitimately open-ended questions that happen to contain a list. Recommend conservative detection
   (only when `choices` is empty AND a clear enumeration pattern is present) and revisit after dogfooding.
4. **Open-ended UX:** single-line vs multiline input; submit on Enter vs explicit button; does empty submission
   count as a dismissal or a valid empty answer? Recommend multiline, explicit submit, empty = dismissal.
5. **Localization keys:** new resources needed — clarify card title/category, the "Other (type your answer)"
   label, the open-ended placeholder, the submit label, and the dismissal sentinel string. Enumerate and add
   across all locale files.
6. **Does `delegate_task` reuse `ChatSession`/`RunTurnAsync`?** If a subagent runs through the same turn loop,
   stripping clarify from its tool set (§4.6) is sufficient; if it has a separate loop, confirm clarify can't
   leak in by another path.

---

## 7. Cross-cutting questions — dispositions (scoped, not diluted)

| Q | Applies to clarify? | Disposition |
|---|---------------------|-------------|
| Q1 Code-exec security model | **No** (boundary only). | clarify is explicitly **not** the dangerous-command confirmation path (spec line 54); that is the `terminal`/`execute_code` approval guard. clarify runs no code. |
| Q2 Filesystem scope / workspace root | **No.** | clarify touches no filesystem; the `SafeFolderPath` sandbox is irrelevant here. |
| Q3 Native vs MCP delegation | **Yes — forced native.** | An MCP server cannot render a WPF card or block the UI thread; clarify is intrinsically a host-UI capability (§4.0). |
| Q4 task_id threading | **Mostly already solved.** | Answer routing is implicit via the session-local card + `RunTurnAsync` await; no central task_id needed. Only task_id-adjacent work is subagent stripping (§4.6, open Q2). |
| Q5 Extend vs rebuild `FilesToolHandler` | **No.** | clarify shares no tool names with the file toolset and does not touch `FilesToolHandler`. |
| Q6 Python runtime for `execute_code` | **No.** | clarify executes nothing; no runtime concern. |
