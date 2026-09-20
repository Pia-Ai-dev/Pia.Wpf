# Agent-run `.scratch/` handling — live validation

**Status:** All three changes verified live.
**Owner:** Marco Altmann
**Written:** 2026-09-20
**Origin:** Commit 5778d1b5, "Stop a run's working notes interrupting and outliving it", validated
before merging it to `main`.

Run against the **real production profile** (no `PIA_*` overrides) on a Debug build, provider Pia
Cloud against the local Docker server. The chat working folder was the default,
`%USERPROFILE%\Documents\Pia Assistant\Playground`. Every artifact created here was removed
afterwards; the folder was left with exactly the eleven entries it started with.

## The gate arm is masked on a profile that already grants `write_file`

This profile carries a persisted "always allow" for `write_file` and `edit_file`. The scratch arm
sits below every grant tier by design, so a `.scratch/` **write** never reaches it:

```
Auto-approved write_file (AutoApprovedStandingGrant, plugin 1000…006)
```

That is correct behaviour, not a defect — but it means the new decision is only observable on this
profile through `delete_file`, which has no standing grant. Worth knowing before anyone tries to
reproduce the card on their own machine and concludes it does not work.

## Four turns, two discriminators

| Turn | Path | Decision | Chips |
|---|---|---|---|
| write | `.scratch/notes.md` | `AutoApprovedStandingGrant` | **0** |
| write | `docs/.scratch/x.md` | `AutoApprovedStandingGrant` | **1** (`FileChip_Open_x.md`) |
| delete | `.scratch/notes.md` | **`AutoApprovedScratch`** | — |
| delete | `docs/.scratch/x.md` | prompt (`Unknown`) | — |

The write pair is the chip discriminator and shows suppression is root-level only. The delete pair
is the gate discriminator: the root-level delete auto-ran — the deliberate widening — and its card
carried zero `ToolApproval_*` buttons plus the new status line, rendered from the German resx as
"Automatisch genehmigt · eine Arbeitsnotiz im Scratch-Ordner, die nie veröffentlicht wird". The
nested delete raised all four decision buttons and was declined; the file survived.

One process note: the model would not call `delete_file` on the nested path until the prompt said
the file was the user's own and the deletion was authorised. Its first answer was text only, no tool
call, which looks exactly like a gate that auto-approved. Read the round line — `Round 1: no tool
calls, completing` — before concluding anything about the gate.

## An isolated run raises one chip, for the deliverable

A normal agent run in `Playground` (24 files, well inside the caps) provisioned in `Copy` mode. It
wrote `.scratch/plan.md` at step 0 and `final.md` at step 1, then:

```
Run a80787df… promoted 1 file
```

One chip in the transcript, `FileChip_Open_final.md`. `Playground\.scratch\` stayed empty — the
notes lived and died inside the workspace, and the run directory under `runs\` was gone at teardown.

## The collection arm, reached through the copy cap

Collection only fires for an **un-isolated** run, so the degrade was forced by pushing the source
folder past `MaxProvisionedFiles`:

```
Run ba80651d… workspace provisioning skipped: source exceeds the isolation cap (2001 files…)
Run ba80651d… Cleaned 1 working-note file(s) left by an un-isolated run
```

Before the run, `.scratch/keep.md` was seeded and backdated two hours. Afterwards `keep.md` was
present with its content intact, the run's own `plan2.md` was gone, and the deliverable `widget2.md`
sat in the folder. One chip, for the deliverable. That is the timestamp guard doing the one job that
can cost a user data, so it is the arm worth re-running if this code is ever touched.

Seeding that file moments before launching the run would have deleted it — its mtime would land
after the run start the guard compares against. Backdate it, or the guard working correctly reads
as a data-loss bug.
