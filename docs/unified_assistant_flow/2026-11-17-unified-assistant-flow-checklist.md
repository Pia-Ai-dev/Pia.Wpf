# One assistant flow — checklist

**Status:** in progress — groups A–D and F landed, E and G in part; gates G2 and G3 open ·
**Owner:** Marco Altmann · **Written:** 2026-11-17
**Origin:** [2026-11-17-unified-assistant-flow-design.md](2026-11-17-unified-assistant-flow-design.md)

Tick a box in the commit that lands the step.

*Effort:* `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.

*Value:* `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little
standalone value, unblocks a High.

## Decision gates

Do not tick a step that depends on an open gate without revisiting it.

| Gate | Question it answers | Decided |
|---|---|---|
| G1 | Owner decisions O1–O5 (design section 12) | CLOSED 2026-11-17 — all five as proposed |
| G2 | Does the conversation model propose plans as good as the planner's, and leave small requests alone? Test, pass bar and fallback are in design section 10. | open |
| G3 | Why does the commit to the run branch fail in a git folder, and is it fixed? Gates group G only. | open |
| G4 | Is issue #95 merged? | CLOSED 2026-11-17 — merged into `develop` as PR #98 |

## P — prerequisites

- [ ] **Find and fix the failing run-branch commit.** Reproduce "run-branch commit failed (1)" in a
      debug build, where the git error text is logged, and fix the cause.
      *Deps:* — · *Effort:* S · *Value:* High
- [ ] **Spike: the conversation model as planner.** A throwaway `propose_plan` tool behind a debug
      switch; replay the four prompts of the latency validation plus four multi-step goals; compare
      plan quality and misroutes with today's triage and planner. Answers G2.
      *Deps:* — · *Effort:* S · *Value:* Enabler

## A — storage that two writers can share

- [x] **Message-level upsert and explicit delete on the chat store.** Insert or update by id with the
      header untouched, delete by ids, order by timestamp; Regenerate and the vision rejection call
      the delete.
      *Deps:* — · *Effort:* M · *Value:* Enabler
- [x] **Move the live session, the job and the one-message appends onto it.** The job stops rewriting
      messages it did not author and stops writing the header; the live window repeats a missed pull
      when its turn ends.
      *Deps:* upsert · *Effort:* M · *Value:* Enabler
- [x] **Concurrency tests.** A live turn and a job saving one chat in every interleaving lose nothing,
      duplicate nothing and keep the order.
      *Deps:* upsert · *Effort:* S · *Value:* High

## B — the plan block

- [x] **Show the run panel as a block in the transcript.** Anchored to a message through the run's
      `FirstMessageId`, not an entry of the message list, with the slim line above the composer.
      *Deps:* — · *Effort:* M · *Value:* High
- [x] **Restore finished blocks when a chat is opened.** Look up the chat's runs including finished
      ones.
      *Deps:* block · *Effort:* S · *Value:* High
- [x] **Record which messages a job wrote, and show them inside the block.** A local record of run,
      step and message id; replies in their step's row, replies of re-planned attempts under "Earlier
      attempts".
      *Deps:* block · *Effort:* M · *Value:* Med
- [x] **Stop a job from the block.** A cancel command for a headless run; Pause moves next to it.
      *Deps:* block · *Effort:* S · *Value:* High
- [x] **Regenerate rules per job state.** Discard a waiting plan first, block on a paused or parked
      job, allow on a finished one.
      *Deps:* block · *Effort:* S · *Value:* Med
- [x] **Make the check a row.** "Check the result" as the last row, with its outcome and the publish
      result.
      *Deps:* block · *Effort:* XS · *Value:* Med

## C — seeing into a job

- [x] **Forward live text and reasoning.** A text callback on the background runner, a carrier keyed
      by run id, the running step's row as the sink.
      *Deps:* block · *Effort:* M · *Value:* High
- [x] **Tool rows per step.** Name, short argument line, duration and outcome from the timeline.
      *Deps:* block · *Effort:* S · *Value:* High
- [x] **Keep call and result rows after the job.** Narrow the settle purge and the prune to parked and
      withheld rows; update the comments and tests that promise the old rule.
      *Deps:* G1 (O1) · *Effort:* S · *Value:* Enabler
- [x] **Open a call: input and result.** A read by step, the "not kept" states, and the readable copy
      with real names if O1 is yes.
      *Deps:* kept rows, tool rows · *Effort:* M · *Value:* High

## D — asking in place

- [x] **Mark chat jobs.** The policy envelope records that a run was started from a plan in the
      user's chat; the lever path sets it now, `propose_plan` later.
      *Deps:* — · *Effort:* XS · *Value:* Enabler
- [x] **A chat job honours session grants and asks on the block.** The four card choices with their
      card meaning; the step waits in the same round while the chat is on screen.
      *Deps:* G1 (O3), marker, block · *Effort:* L · *Value:* High
- [x] **Make the wait visible, and park when nobody is there.** The clock stops, the line above the
      composer says so, a Flow item is raised; off screen or after ten minutes the job parks with the
      question open, MCP calls included, and a late answer has the same four choices.
      *Deps:* asking on the block · *Effort:* M · *Value:* High
- [x] **Questions from the job on the block.** `request_user_input` with an answer field.
      *Deps:* block · *Effort:* S · *Value:* Med

## E — the front door

- [x] **A hidden setting for the new flow, off by default.** With it on the lever is hidden; groups E
      and F are reachable only through it until group G.
      *Deps:* — · *Effort:* XS · *Value:* Enabler
- [ ] **`propose_plan` and job creation.** The tool, shared plan validation, the run parked for
      approval with its anchor, one job per chat, the weak-provider warning on the block.
      *Deps:* G2, setting, block · *Effort:* M · *Value:* High
      *Note:* Built behind the setting, G2 open.
- [ ] **Approval for every plan, editable there.** Plan edits allowed at the approval park; Start and
      Discard on the block; the send lock at that park removed.
      *Deps:* G1 (O2), `propose_plan` · *Effort:* S · *Value:* High
      *Note:* Built behind the setting, G2 open.
- [x] **Start an approved chat job at once.** It does not wait for a background slot.
      *Deps:* G1 (O4) · *Effort:* S · *Value:* Med

## F — talking to a running job

- [x] **Unlock the composer during a job.** Remove the Send lock; keep Regenerate blocked.
      *Deps:* group A · *Effort:* S · *Value:* High
- [x] **The job block and the read tool.** State, steps and the running step appended to the turn;
      `job_step_details`.
      *Deps:* unlocked composer · *Effort:* S · *Value:* High
- [x] **Changing the plan while it runs.** `job_update_plan` with queued changes applied at the step
      boundary and overlaid on the block.
      *Deps:* job block · *Effort:* M · *Value:* High
- [x] **Steering.** `job_note` kept for the rest of the job, `job_pause` with the new "after this
      step" flag, `job_restart_step`, `job_continue`, `job_answer`, `job_stop`.
      *Deps:* job block, G4 · *Effort:* M · *Value:* High

## G — the end and the removals

- [x] **Take the title call out of the path to Completed.**
      *Deps:* — · *Effort:* XS · *Value:* Med
- [x] **Move "Run in background" into a menu on the Send button.**
      *Deps:* — · *Effort:* XS · *Value:* Med
- [ ] **Turn the new flow on for everyone and remove the old path.** Lever, its setting, triage on
      this path, the context banner and digest for chat jobs, `suggest_agent_mode` and its chip, the
      note box, the pinned panel, the hidden setting.
      *Deps:* groups E and F, G3 · *Effort:* M · *Value:* High
- [ ] **Follow-ups outside the code.** Playbook and UI scripts that drive the lever, the desktop guide
      in Pia.Docs, the stale `fast` line in the latency checklist.
      *Deps:* removal · *Effort:* S · *Value:* Med

## Not yet planned

- A step that reaches the 24-round ceiling: carry on in a further turn instead of a wrap-up call or a
  repeat from the start.
- End a step at `emit_step_result`, saving the 3–13 s round after it.
- Parallel sub-agents for independent steps without a persona roster, with a real brief and no plan
  and verify of their own.
- The uncached first request of a job, and cache breaks from a per-step persona.
- The placeholder mismatch on resume (design section 14).

## Suggested order

1. **P**, both steps. The spike decides the shape of group E, and the commit fix helps users today. The
   fix does not hold back A to F; it has to be closed before G.
2. **A**. Nothing user-visible, and every later group that unlocks the composer rests on it.
3. **B**, then **C**. From here a job is no longer a black box. Both ship under the lever.
4. **D**. The largest single cause of waiting in the logged runs. Ships under the lever.
5. **E**, then **F**, behind the hidden setting.
6. **G**. The lever goes last, once E and F have been used for real.
