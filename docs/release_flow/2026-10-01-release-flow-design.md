# Release flow: develop → release branch → main → back-merge

- **Status:** Implemented on `feature/release-flow`; GitHub settings partly applied (see below)
- **Owner:** man
- **Written:** 2026-10-01
- **Origin:** Owner decision to run Pia.Wpf releases like the Pia repo (`../Pia`, `release-flow`
  skill): PRs gather in `develop`, a release is a PR into `main`, and `main` merges back.

## Why

Every merge to `main` cut a release, so each feature PR shipped on its own. That had three costs:
- The release notes had to be archived by a commit to `main` carrying the skip-ci marker.
- When an archive was missed, the next release run was refused. v1.4.290 shipped, #65 appended to
  its body, and the `main` run for #65 failed at **Refuse to republish**.
- The help corpus had to be refreshed before each of those pushes.

## The flow

```
feature/<name> ──PR──▶ develop ──cut──▶ release/<yyyy-mm-dd> ──PR──▶ main ──CI──▶ vX.Y.N
                          ▲                                                   │
                          └──────── back-merge/<ver> (archive notes) ──PR─────┘
hotfix/<name> (from origin/main) ──PR──▶ main ──CI──▶ vX.Y.N ──▶ back-merge/<ver>
```

- `develop` is the default branch and the target of every feature PR.
- `release/<yyyy-mm-dd>` is cut from `develop`. It is named by date because the version is nbgv's
  git height, which is only known once the merge commit exists.
- On the release branch: run the `help-corpus` skill, curate `RELEASE.md`, and regenerate
  `CHANGELOG.md` with git-cliff. Then open the PR into `main`.
- Merging it releases: `build-and-release.yml` tags `vX.Y.N`, creates the GitHub release and links
  the pia-ai.de publish step.
- `back-merge/<ver>` is cut from `main` after the run is green. It archives the notes
  (`YYYY-MM-DD-<ver>.md`, dated by the tag's commit) and empties `RELEASE.md` on its way into
  `develop`. No skip-ci marker is needed because `develop` never builds on push.
- A hotfix branches from `origin/main`, archives the last shipped body under the same dated name,
  writes its own notes, and goes through `ship` like a release.
- Every PR is merged with **Create a merge commit**. A squash would drop `main` out of `develop`'s
  history and make every back-merge conflict.
- `RELEASE.md` is edited only on `release/*` and `hotfix/*`, and emptied only on `back-merge/*`.

The step-by-step procedure lives in `.claude/skills/release-flow/SKILL.md`.

## CI

`build-and-release.yml`:
- **Push to `main`:** releases, unchanged.
- **Manual run on `main`:** re-cuts a release, the recovery path for a skipped run.
- **Manual run on any other branch:** the same signed publish and both MSIs, uploaded as a 14-day
  run artifact. It skips the previous-release download, the republish guard, git-cliff, the tag, the
  GitHub release and the pia-ai.de step. A tag cut from `develop` would take a version `main` needs
  later and enter the Velopack delta chain.
- **Removed:** the dead `branch` dispatch input (checkout always uses the dispatched ref).
- **Concurrency:** queues on `main` and cancels superseded runs elsewhere.

`changelog.yml` is deleted. It never ran: a release created with `GITHUB_TOKEN` triggers no other
workflow, and its bot push to `main` would be refused by the ruleset anyway. `CHANGELOG.md` is
regenerated on the release branch instead.

Unchanged: versioning (`1.4` + git height), `prune-releases.yml`, and the updater. It reads only
non-prerelease GitHub releases, which only `main` creates.

## Local guard

`.claude/hooks/guard-protected-branches.ps1` is registered in `.claude/settings.json` for Bash and
PowerShell. It blocks, for Claude Code sessions:
- commit, merge, cherry-pick, revert, am and rebase while on `main` or `develop`
- pushes to either branch, and `push --all` / `--mirror`
- new branches from `main` other than `hotfix/*` and `back-merge/*`

It is a port of the Pia hook, without the nbgv `prepare-release` check this repo has no use for.

## GitHub settings (done as Pia-Ai-dev)

The `Main` ruleset used to target `~DEFAULT_BRANCH`, so switching the default branch would have
moved protection off `main`. Order:

1. [x] `Main` ruleset → target `refs/heads/main`, allowed merge methods **merge** only.
2. [ ] Create `develop` from `main`.
3. [ ] Create the `Develop` ruleset (JSON below; Rulesets → New → Import).
4. [ ] Default branch → `develop`.
5. [ ] Optional: untick "Allow squash merging" and "Allow rebase merging" in Settings → General.

```json
{
  "name": "Develop",
  "target": "branch",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["refs/heads/develop"], "exclude": [] } },
  "rules": [
    { "type": "deletion" },
    { "type": "non_fast_forward" },
    { "type": "required_signatures" },
    {
      "type": "pull_request",
      "parameters": {
        "required_approving_review_count": 1,
        "dismiss_stale_reviews_on_push": true,
        "require_code_owner_review": false,
        "require_last_push_approval": false,
        "required_review_thread_resolution": true,
        "allowed_merge_methods": ["merge"]
      }
    }
  ],
  "bypass_actors": [{ "actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "always" }]
}
```

`actor_id: 5` is the built-in Admin role, the same bypass `Main` uses.

## Transition

This branch archives the v1.4.290 notes into `docs/release_notes/2026-10-01-1.4.290.md` and
leaves only #65's Desktop bullet in `RELEASE.md`. It targets `develop` once that exists, and the
first `release/*` cut ships it, together with #65, which never released.
