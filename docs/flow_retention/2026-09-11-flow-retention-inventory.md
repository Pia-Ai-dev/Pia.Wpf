# Flow item retention — publisher inventory and per-source ages

**Status:** Implemented 2026-09-11 — `FlowRetention.MaxAgeFor`, swept by `FlowService`
**Owner:** Marco Altmann
**Written:** 2026-09-11
**Origin:** The question "is the planned automatic retention implemented?" — answer: the four
design-§6 mechanisms (transient expiry, cap-50 eviction, dedup, auto-retract) are all
implemented, but only 3 of the 18 rows below are ever transient, so in practice the rail only
shrinks when the user clicks. The age policy **amends** design §6's "persistent items stay
until dismissed or the entity resolves" contract; that doc was deleted in `76d408e2`, so this
file is now the Flow lifecycle spec.

## 1. Every publish site

Fourteen `Publish` call sites, split below into 18 rows where one site emits more than one
severity. `CreatedAt` is the age basis; a dedup re-publish bumps it, so "age" means *time since
the last state change*, not time since first sight.

| # | Publisher | Source | Severity | Lifetime | DedupKey | Durable | Auto-retract today |
|---|---|---|---|---|---|---|---|
| 1 | `FlowSnackbarService.Show` | Snackbar | Info/Success | Transient (timeout) | null | No | expires |
| 2 | `FlowSnackbarService.Show` | Snackbar | Warning/Error | Persistent | null | No | **none** |
| 3 | `FlowSnackbarService.PublishAction` | Snackbar | ActionRequired | Persistent | null | No | **none** |
| 4 | `FlowNotificationService.ShowToast` | InAppToast | Info | Transient 3 s | null | No | expires |
| 5 | `FlowNotificationService.ShowSuccess` | InAppToast | Success | Transient 3 s | null | No | expires |
| 6 | `FlowNotificationService.ShowError` | InAppToast | Error | Persistent | null | No | **none** |
| 7 | `ScreenCaptureIndicator.NotifyCapture` | InAppToast | Info | Persistent | `screen-capture:{k}` | No | **none** |
| 8 | `BackgroundChatNotificationSurface` | BackgroundChat | **ActionRequired** (WaitingForTool) | Persistent | chatId | Yes | on chat open/read |
| 9 | `BackgroundChatNotificationSurface` | BackgroundChat | Success / Error | Persistent | chatId | Yes | on chat open/read |
| 10 | `ReminderBackgroundService` | Reminder | ActionRequired | Persistent | reminderId | Yes | **none** (Snooze/Done only) |
| 11 | `ScheduledJobNotificationSurface.NotifySuccess` | ScheduledJob | Success | Persistent | jobId | Yes | **none** |
| 12 | `…NotifyMeetingSaved` | ScheduledJob | Success | Persistent | jobId | Yes | **none** |
| 13 | `…NotifyFailure` | ScheduledJob | Error | Persistent | jobId | Yes | **none** |
| 14 | `TodoDeadlineBackgroundService` | TodoDeadline | Warning / Error | Persistent | todoId | Yes | reconcile, when no longer due |
| 15 | `AgentRunNotificationSurface` (parked) | AgentRun | ActionRequired | Persistent | runId | Yes | on Running/Cancelled/terminal |
| 16 | `AgentRunNotificationSurface` (terminal) | AgentRun | Success / Error | Persistent | runId | Yes | on run open, on chat delete |
| 17 | `AssignmentNotificationSurface` | Assignment | Success / Error | Persistent | assignmentId | Yes | **none** |
| 18 | `PolicyNotificationSurface` | Policy | Info | Persistent | **null** | No | **none**, and no dedup → they stack |

Rows 11–13 share `jobId`, so a recurring job only ever holds one card and each fire bumps its
`CreatedAt`. The age therefore only starts running once the job stops firing — which is the
intended reading of a 7-day number for a daily job.

## 2. Maximum age per source

| Source | Severity | Max age | Why |
|---|---|---|---|
| Snackbar, InAppToast | Info, Success | (transient) | unchanged |
| Snackbar, InAppToast | Warning, Error | 1 day | an operational failure is dead once seen |
| InAppToast (screen capture) | Info | 7 days | privacy notice; must survive a long absence |
| Policy | Info | 3 days | read once and done |
| ScheduledJob | Success | 7 days | the result lives in the chat / vault |
| ScheduledJob | Error | 30 days | a monitor that broke must stay loud |
| BackgroundChat | Success, Error | 14 days | the answer stays in the chat; the card is a pointer |
| Assignment | Success, Error | 14 days | same shape as BackgroundChat |
| AgentRun | Success, Error | 14 days | retracts on open; the run stays reachable via its chat |
| Reminder | ActionRequired | 30 days | the reminder row survives its dismissal — see §3 |
| any other | ActionRequired | never | see §3 |
| TodoDeadline | Warning, Error | never | the reconcile loop owns it; a surviving card means still overdue |

## 3. Why ActionRequired barely ages out

`ActionRequired` is already exempt from capacity eviction (design §6). Extending that to
retention keeps one rule instead of two, and each of the four producers has a reason:

- **AgentRun parked** — nothing re-creates this card. `AgentRunNotificationSurface` is purely
  event-driven off `RunChanged`; there is no startup re-publish, and its own comment
  (`AgentRunNotificationSurface.cs:82`) notes that a park it misses is "invisible forever". The
  run itself stays reachable — `ChatSessionManager.RestoreActiveRunAsync` re-attaches a parked
  run when its chat is activated — but only if the user remembers which chat. Its retract path
  (Running / Cancelled / terminal) means the state machine already owns the card's removal.
- **BackgroundChat WaitingForTool** — a pending tool decision, retracted on chat open.
- **Snackbar `PublishAction`** — never durable, so "never" here means "until the app closes",
  exactly as before. Its `InvokeAction` callback cannot survive a restart anyway.
- **Reminder** — the carve-out, at 30 days. `ReminderService.DismissAsync` *updates* the row
  (Status → Completed, or a recomputed `NextFireAt` for a recurring one) and never deletes it,
  so the reminder stays listed in the Reminders view after its card goes. Nothing is lost.

The cost is that an unanswered parked run sits in the rail until the user clicks ✕. That is
accepted: losing a pending decision silently is worse than one stale card.

## 4. Decisions taken

1. **ActionRequired never ages out, except Reminder at 30 days.** Reminder is the one carve-out
   because `ReminderService.DismissAsync` updates the reminder row rather than deleting it, so the
   reminder stays listed in the Reminders view after its Flow card goes. AgentRun parked and
   BackgroundChat WaitingForTool keep the `never` rule — nothing re-creates those cards.
2. **Not configurable.** The table is hard-coded in `FlowRetention`. A single `FlowRetentionDays`
   setting cannot express per-source ages, and a per-source settings UI is out of proportion.

Not a decision: **read vs unread.** `MarkRead` has exactly two callers, both explicit action clicks
in `FlowItemViewModel`; nothing marks a card read by viewing the rail. A read/unread axis is only
meaningful if a mark-read-on-view behaviour is added first, and that is not proposed here.

## 5. Where it runs

One policy function — `FlowRetention.MaxAgeFor(source, severity)`, returning null for "never" —
applied in two places:

- `FlowService.Sweep(now)`, the existing 1 s timer, extended past transient-only.
- Inside `FlowService.LoadAsync`, *before* the first `RaiseChanged()`, so a month-old rail
  never flashes up at launch. Delete-through removes the durable rows.

Keeping the policy in C# rather than a SQL `WHERE` avoids a per-source cutoff clause and leaves
one source of truth.

Folded into the same change: `LoadAsync` added loaded rows without calling `EvictIfNeeded`, so a
DB that had grown past 50 through the protected-items branch reloaded over capacity. It now evicts.
