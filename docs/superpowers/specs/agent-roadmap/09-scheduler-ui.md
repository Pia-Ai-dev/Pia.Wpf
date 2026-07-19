# Batch 09 — Scheduler UI (create/edit/list agent jobs)

**Phase 4 · Size M · Branch from the latest branch**

Milestone B wired scheduler **emission** (a scheduled job can create an agent task, plan §17.1) and the plan
notes the full create/edit/list **scheduler UI** for agent jobs stays deferred to Phase 4 (§17 line 1072,
§17 line 1172) — "a minimal/programmatic trigger suffices" until then.

## Goal

A user-facing UI to create, edit, and list scheduled agent jobs (cron/interval + goal + budget + policy), on top
of the existing scheduler emission + `ScheduledJob` model.

## Key seams

- The `ScheduledJob` model + `ScheduledJobBackgroundService` (already emit agent runs; parked-run handling was
  added in the budget-pause batch).
- `ScheduledJobNotificationSurface` — the existing scheduled surface to mirror.
- Settings / a new management view — CRUD over jobs (mirror existing list/edit patterns in the app).
- The autonomy policy (Batch 04) — a scheduled job should carry a policy + budget.

## Decisions to resolve

- **Scope:** full CRUD vs list+create-only first.
- **Job payload:** goal + schedule + budget + autonomy policy (depends on Batch 04) + owner device.
- **Validation:** schedule expression, budget clamping (reuse `RunProfile.FromBudget` bounds).

## Guardrails

- Owner-only advances a job (mirrors `OwnerDeviceId` semantics); no cross-device double-fire.
- Privacy: job goal is user content → `SensitiveDebug`; the UI shows it, logs don't.
- Backward-compatible with existing programmatically-created jobs.

## Acceptance

A user can create/edit/list scheduled agent jobs from the UI; jobs carry budget + policy; existing emission +
owner semantics intact; build green.
