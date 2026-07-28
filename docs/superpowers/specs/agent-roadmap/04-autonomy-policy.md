# Batch 04 — Autonomy policy (`PolicyJson`)

**Phase 2 · Size M–L · Work on `feature/agent-run-spine`** (there is no `feature/agent-mcp-gate` branch — the
MCP-gate work is the commit range `ed030f2` → `c62bc97`; see the chronicle in [`00-OVERVIEW.md`](00-OVERVIEW.md))

`AgentRun.PolicyJson` was reserved "for the Phase 2 autonomy policy" (plan §2 line 87, DDL line 438). The MCP/tool
approval **gate** already exists (commit range `ed030f2` → `c62bc97`: interactive gate + unattended grant gate +
destructive guard). What's missing is a **per-run policy** that decides, up front, how that gate behaves for a
given run.

⚠ **The column is no longer empty.** The hardening batch writes the run's resolved **write-grant envelope** into
`PolicyJson` at launch (a private, `v`-versioned camelCase object carrying `grantedWrites` + the origin trigger)
and `HeadlessRunLauncher.ResumeAsync` reads it back so a resume can never widen what the launch granted; a
missing/unreadable/unknown-version envelope resumes with the `{write_file}` floor. So this batch must **extend
that document** (add policy members alongside `grantedWrites`, bump `v` with a fallback that stays restrictive) —
not claim the column, and not break the resume reader.

## Goal

A persisted, per-run autonomy policy that sets the approval posture — e.g. which tool classes auto-approve, which
always prompt, and the hard never-auto set (destructive MCP stays blocked from auto per M3) — so an unattended run
can be granted a bounded autonomy envelope instead of ad-hoc grants.

## Key seams

- `AgentRun.PolicyJson` (`Models/AgentRun.cs`) — the persisted policy blob (append-only, already present).
- The approval gate from the MCP-gate batch — reads the policy to decide auto vs prompt vs deny.
- `AgentRunCreateRequest` / the launch paths (`ChatSessionManager`, `HeadlessRunLauncher`) — where a run's policy
  is chosen (default from settings; overridable per launch).
- Settings (`Assistant → Agent runs`) — the default policy + user-visible autonomy controls.

## Decisions to resolve

- **Policy shape:** a small typed record serialized to `PolicyJson` (e.g. `{ autoApprove: [toolClasses], alwaysPrompt: [...], neverAuto: [...] }`).
  Keep it minimal and forward-compatible.
- **Interactive vs headless defaults:** interactive defaults to prompt; headless/scheduled uses the unattended
  grant envelope. The destructive-MCP guard (M3) is a hard floor the policy cannot loosen.
- **Where the policy is authored:** a sane default in settings this batch; a full per-run editor is later.

## Guardrails

- The M3 invariant holds: **no policy can auto-approve a destructive MCP call.**
- Privacy: policy is config, not user content; fine to log the policy *shape*, never tool payloads.
- Backward-compatible: a `PolicyJson` that carries only the grant envelope (every run written before this
  batch) behaves exactly as today — the resume grant restore must keep working, and an unknown policy value
  must fail *restrictive*, never bypass.

## Tests

- A run whose policy auto-approves class X does not prompt for X but still prompts/denies for Y.
- Destructive MCP is never auto-approved regardless of policy.
- Null policy == current behavior.

## Acceptance

Runs carry a bounded, persisted autonomy policy the gate honors; destructive floor intact; build green.

> Scope check: confirm at design time exactly what the MCP-gate batch already implemented — this batch is the
> **policy layer on top of the existing gate**, not a re-implementation of the gate.
