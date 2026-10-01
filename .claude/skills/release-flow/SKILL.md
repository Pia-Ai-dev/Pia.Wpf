---
name: release-flow
description: "Branching, pull requests and releases in this repo. Invoke as /release-flow start <name>, pr, cut, ship, or hotfix <name>. Use when starting a feature branch, opening a PR, cutting or shipping a release, making a hotfix, or merging main back into develop. Also use before any command that would write main or develop."
---

# Release flow

```
feature/<name> ──PR──▶ develop ──cut──▶ release/<yyyy-mm-dd> ──PR──▶ main ──CI──▶ vX.Y.N
                          ▲                                                   │
                          └──────── back-merge/<ver> (archive notes) ──PR─────┘
hotfix/<name> (from origin/main) ──PR──▶ main ──CI──▶ vX.Y.N ──▶ back-merge/<ver>
```

- `origin` is GitHub, `Pia-Ai-dev/Pia.Wpf`. `develop` is the default branch. A push to `main` cuts a
  release (`build-and-release.yml`); `develop` and every other branch build only on a manual
  **Build and Release** run, which uploads the installers as a run artifact and publishes nothing.
- `main` and `develop` are never written locally. `.claude/hooks/guard-protected-branches.ps1` blocks
  commit, merge, push and branching off `main` (except `hotfix/*` and `back-merge/*`), so a blocked
  command means the step is wrong. Do not work around the hook.
- Every PR is merged with **Create a merge commit**. Never squash or rebase: `main` must stay in
  `develop`'s history, or the next release conflicts. Both rulesets allow merge only.
- Only the `Pia-Ai-dev` account can approve and merge into `main`; this machine's credential
  (`neo42man`) pushes branches and opens PRs. Merging is always the user's act.
- `docs/release_notes/RELEASE.md` is edited only on `release/*` and `hotfix/*`, and emptied only on
  `back-merge/*`. A feature PR that edits it makes the back-merge conflict.
- `gh` is not installed. Open PRs through the REST API with the git-credential token, never printing it:

```bash
TOKEN=$(printf "protocol=https\nhost=github.com\n\n" | git credential fill | sed -n 's/^password=//p')
python -c "import json,sys; json.dump({'title':sys.argv[1],'head':sys.argv[2],'base':sys.argv[3],'body':open(sys.argv[4],encoding='utf-8').read()}, open('pr.json','w'))" "<title>" "<branch>" "<base>" body.md
curl -s -X POST -H "Authorization: Bearer $TOKEN" https://api.github.com/repos/Pia-Ai-dev/Pia.Wpf/pulls -d @pr.json
```

Write `pr.json` and `body.md` to the session scratchpad, not the repo. Fetch first, every time:
`git fetch origin --prune --tags`.

## start <name>

`git switch -c feature/<name> origin/develop`. If `feature/<name>` already exists, check it out
instead. If the user is on a branch that already has commits, check
`git merge-base --is-ancestor origin/develop HEAD` before stacking new work on it.

## pr

1. Clear the Zero-Warning Policy and the Test Gate from `CLAUDE.md`.
2. `git push -u origin HEAD`.
3. Base: `develop` for `feature/*`, `chore/*` and `back-merge/*`; `main` only for `release/*` and
   `hotfix/*`. Title: the branch's purpose in one line. Body: what changed and why, from
   `git log origin/<base>..HEAD`, ending with the PR attribution line.
4. Hand the user the PR URL and remind them: **Create a merge commit**.

## cut

1. Preflight: `origin/develop` is ahead of `origin/main` (`git log --oneline origin/main..origin/develop`),
   and the user has a green manual **Build and Release** run of `develop` or accepts the risk.
2. `git switch -c release/$(date +%Y-%m-%d) origin/develop`.
3. Run the `help-corpus` skill. Its regenerating script is the user's to run, via `!`.
4. Curate `RELEASE.md` from `git log --no-merges --format=%s origin/main..HEAD`, following
   `docs/release_notes/README.md`. Commit.
5. `git cliff --output CHANGELOG.md` and commit it as `docs: update CHANGELOG.md`. `git-cliff` is
   not preinstalled; ask the user to run `! winget install --id orhun.git-cliff -e` if it is missing.
6. `pr` with base `main`. While the PR is open, fixes go onto the release branch.

## ship

After a `release/*` or `hotfix/*` PR has been merged into `main`:

1. Wait for the **Build and Release** run on `main` to finish green, and confirm the tag exists:
   `git fetch origin --tags && git describe --tags --abbrev=0 origin/main`. Call that `<ver>` without
   its `v`. If the run is red, stop and show the failing step; do not open the back-merge.
2. Remind the user that pia-ai.de is published by hand from the run summary's link.
3. Archive the shipped notes on a back-merge branch:

```bash
git switch -c back-merge/<ver> origin/main
DATE=$(git log -1 --format=%cs v<ver>)
{ printf '# Pia %s\n' "<ver>"; tail -n +2 docs/release_notes/RELEASE.md; } > "docs/release_notes/$DATE-<ver>.md"
: > docs/release_notes/RELEASE.md
git add docs/release_notes && git commit -m "docs: archive <ver> release notes"
```

4. `pr` with base `develop`. It should not conflict. If it does, stop and show the conflict; don't
   resolve it by hand. Delete the `release/*` branch once the back-merge has been merged.

## hotfix <name>

```bash
git switch -c hotfix/<name> origin/main
```

`RELEASE.md` on `main` still holds the last shipped body, and the build refuses to republish it.
Archive it first, exactly as `ship` step 3 does but for the latest tag. The dated file then matches
the one the release's back-merge wrote, so the two merge cleanly. Write the hotfix's notes into the
emptied `RELEASE.md`, fix, then `pr` (base `main`) and `ship`.
