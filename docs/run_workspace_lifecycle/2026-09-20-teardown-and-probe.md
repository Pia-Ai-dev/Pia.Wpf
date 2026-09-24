# Run workspaces: an undeletable teardown and a silent 30 s probe stall

**Status:** Planned, not started
**Owner:** Marco Altmann
**Written:** 2026-09-20
**Origin:** Review of `%LOCALAPPDATA%\Pia\Logs\pia-2026-09-20.log` after the handouts run
`d1092e76`, which was itself clean.

Neither finding comes from commit 5778d1b5 ("Stop a run's working notes interrupting and outliving
it"). Its only change to `RunWorkspaceService.cs` is a single hunk at line 661 adding the scratch
cleanup; `ProvisionAsync`, `TryProvisionWorktreeAsync` and `TryDeleteDirectory` are untouched
(`git show 5778d1b5 -- src/Pia.Wpf/Services/RunWorkspaceService.cs`).

## Finding 1 — a workspace the model ran `git init` in can never be deleted

`%LOCALAPPDATA%\Pia\runs\` holds three directories from 2026-08-05/06. Every launch the sweep tries
and fails:

```
Failed to delete a run workspace directory
  System.UnauthorizedAccessException: Access to the path '9b0364c5…' is denied.
Run 0c6146f2… workspace directory survived teardown in None mode; the next startup sweep removes it
```

The blocked paths are all `.git/objects/xx/<hash>`. Git writes loose objects with the read-only
attribute; the three surviving workspaces are the ones a run used the `git_*` tool pack in.
Confirmed directly:

```powershell
(Get-Item "$env:LOCALAPPDATA\Pia\runs\0c6146f2-…\.git\objects\10\9b0364c5…").Attributes
# ReadOnly, Archive
```

`Directory.Delete(dir, recursive: true)` in `TryDeleteDirectory` (`RunWorkspaceService.cs`) refuses
read-only entries, so the failure is permanent, not transient. The warning's "the next startup sweep
removes it" is therefore a false promise: it has failed at every launch for six weeks and costs six
warnings per launch. `RunWorkspaceService.cs` already anticipates a model-created `.git` on the
*promotion* side; teardown does not.

### Fix 1

- Clear the read-only attribute across the whole tree — **directories as well as files** — before
  deleting. `RemoveDirectory` refuses a directory carrying `FILE_ATTRIBUTE_READONLY` too, so a
  files-only pass can fail on the same exception.
- Retry the delete once after the attribute pass; keep the existing warning for a genuine second
  failure.
- Reword the "next startup sweep removes it" line so it states what is true (the directory is kept
  and the sweep will retry), not a removal it cannot promise.
- **Test seam:** create a workspace containing a file *and* a subdirectory with
  `FileAttributes.ReadOnly`, tear it down, assert the directory is gone. Reproduces without git.

*Effort:* XS · *Value:* Med — a visible, growing leak in the user profile and a lying log line.

### Noticed, not in scope

Five further `runs\<id>` directories (oldest 2026-08-26) still carry a `.workspace.json` and are not
swept at all. Those are failed/cancelled runs kept for the panel's publish offer, which is by design
— but nothing bounds that class by age, so it is a slower leak of the same kind. Decide separately
whether the 7-day `TornDownStubMaxAge` should also bound a kept workspace.

## Finding 2 — 30 s of dead time before planning, logged nowhere

Two runs on 2026-09-20 sat **30.09 s** and **30.25 s** between `Created run` and
`workspace provisioned`, with nothing logged in between:

```
10:27:06.699  Created run a80787df…
10:27:36.790  Run a80787df… workspace copied in 24 file(s), 432614 bytes
```

The same source folder cost 70–150 ms on 09-18 and 09-19, and 4.0–4.4 s on 09-12.

### What consumes the 30 s

`ProvisionAsync` calls `TryProvisionWorktreeAsync`, whose first act is
`git rev-parse --show-toplevel` in the source root through `GitProcessRunner`, whose `Timeout` is
`TimeSpan.FromSeconds(30)` — the only 30 s constant on that path, matching both observations to
within 250 ms. `RunProcessAsync` reads stdout/stderr with `ReadToEndAsync` rather than
`BeginOutputReadLine`, so `WaitForExitAsync` waits on the process handle alone: the 30 s is the git
process not exiting, not a pipe-drain deadlock.

`rev-parse` has no work to do there — the folder is not a repo, and `GIT_CEILING_DIRECTORIES` is set
to the parent of the working directory, so the upward walk cannot leave it.

**The trigger is not recoverable from the log, and should not be guessed at.** The timeout branch
logs nothing: `TryProvisionWorktreeAsync` returns `null` on `!toplevel.Succeeded` without
distinguishing "not a repo" from "timed out", and the `Degraded` metadata flag is true for both. What
is known: both stalls were in the same process session, 9 minutes apart; the 11:43 session on the
same build and the same folder did not stall; `git` answers in ~30 ms there interactively. Candidates
that were checked and do *not* explain it: the working folder is plain NTFS with no reparse point, so
OneDrive placeholder hydration is out; and the build is unchanged between the stalling and
non-stalling runs, so 5778d1b5 is out. One unverified candidate worth recording rather than
believing: `git` resolves to `C:\Program Files\Git\cmd\git.exe`, the Git-for-Windows wrapper that
re-executes `mingw64\bin\git.exe`, so each probe is two process launches with Defender real-time
protection on — but that does not by itself explain two stalls 9 minutes apart in one session.

### Fix 2

Remove the exposure rather than shorten it:

1. **Skip the subprocess when it cannot succeed.** Worktree mode needs a repository whose toplevel
   is inside the assistant files folder, and the ceiling already restricts discovery to the source
   root itself — so the probe can only succeed if `<sourceRoot>\.git` exists. Test for it with
   `Directory.Exists` / `File.Exists` (a submodule or linked worktree makes `.git` a file) and
   return `null` before launching git. On a non-repo working folder — the normal case — this drops
   the git process launch entirely, and with it the whole window.
   **Test seam:** with a substituted `IGitProcessRunner`, assert it is never invoked when
   `<sourceRoot>\.git` is absent, and still invoked when it is present.
2. **Log the timeout in `GitProcessRunner.RunAsync`, not at the call site.** The `git_*` tool
   pack shares this runner and this 30 s budget, so anything that can hang provisioning can hang
   `git_status`. One warning in the runner — subcommand name, `TimedOut`, exit code, elapsed ms; no
   paths, no stderr text — covers every caller and makes the next occurrence attributable instead of
   a gap between two timestamps.
3. **Give a probe its own budget.** 30 s is right for `worktree add` and `commit`, not for a
   `rev-parse` that answers in 30 ms. A per-request timeout on `GitProcessRequest` gives the two
   read-only probes 10 s — clear of the ~4 s a cold git start has cost — and leaves the mutating
   commands at 30 s.

*Effort:* S · *Value:* High — a 30 s freeze with the run panel already on screen and no explanation.

### Follow-up for the validation record

`docs/agent_run_scratch/2026-09-20-live-validation.md` describes both stalling runs. Its conclusions
stand — copy mode is correct for a non-repo folder — but the stall it was reached through is not
recorded there. Add a line once Fix 2 lands.

## Suggested order

Fix 1 first (XS, self-contained, testable without git), then Fix 2 step 1, then steps 2 and 3
together.

## What shipped

Both, on 2026-09-20.

Fix 1 is `ClearReadOnlyAttributes` + one retry inside `TryDeleteDirectory`, and the reworded
warning. It reaches the three stranded directories: the launcher's sweep enumerates
`runs\<id>` directories (not metadata documents), and a run row the database no longer has is
removed immediately — `HeadlessRunLauncher.TearDownWorkspaceAsync` → `TearDownAsync` →
`TryDeleteDirectory`, which is exactly the pair of warnings the log shows for them.

Fix 2 is all three steps: the `.git` existence check before the probe, the `TimedOut` warning in
`GitProcessRunner.RunAsync`, and the 10 s per-request budget on the two read-only probes.

One thing that fell out of Fix 2 worth keeping: twelve tests across two files armed a fake git
runner and then asserted copy-mode fallback. With the pre-probe check in place they all still
passed — the check declined before the runner was ever asked, so they were measuring the default
instead of the mechanism. Both fixtures now seed a `.git` marker in the source root; the nine that
turned red are the proof the arming matters.
