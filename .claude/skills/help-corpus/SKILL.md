---
name: help-corpus
description: "Use before every push to main, and whenever the desktop guide in Pia.Docs changes. Checks that Pia.Docs covers what is about to ship, refreshes the bundled help corpus that the assistant's pia_help tool searches, and commits it. Not for editing doc pages — that happens in the Pia repo."
---

# Help corpus refresh

`src/Pia.Wpf/Resources/Help/help-corpus.json.gz` is a checked-in build artifact: 36 English `wpf/**`
pages, snapshotted out of the sibling Pia.Docs checkout. **CI cannot regenerate it** — the docs repo
is not on the build agent — and a push to `main` cuts a release, so whatever is committed is what
ships and what `pia_help` can answer from for that version.

| Thing | Where |
|---|---|
| Docs repo | `../Pia/src/Pia.Docs` — branch `master`, a **shared** checkout |
| English desktop pages | `../Pia/src/Pia.Docs/src/content/docs/wpf/**` |
| Script | `scripts/Update-HelpCorpus.ps1` (`-Check` compares, bare regenerates) |
| Snapshot | `src/Pia.Wpf/Resources/Help/help-corpus.json.gz` (`EmbeddedResource`, `Pia.HelpCorpus`) |

Background, if something here does not add up:
[`docs/self_knowledge/2026-09-20-self-knowledge-plan.md`](../../../docs/self_knowledge/2026-09-20-self-knowledge-plan.md).

## Step 1 — Preflight the docs checkout

```bash
git -C ../Pia fetch
git -C ../Pia status -sb
git -C ../Pia status --porcelain -- src/Pia.Docs/src/content/docs
```

| State | Do |
|---|---|
| Behind `origin/master` | **Stop.** Ask the user to pull. A snapshot taken here silently omits pages that already exist. |
| Ahead of `origin/master` | **Warn.** Every hit carries a `docs.pia-ai.de` URL, so a page that is only local answers in-app and 404s in the browser until those commits are pushed. |
| Dirty under `src/content/docs` | **Stop.** The script stamps `sourceDirty: true` and bakes the uncommitted text into a shipped resource, traceable to no commit. |
| Clean and current | Continue. |

**Never write in `../Pia`.** No `pull`, no `checkout`, no `stash`, no commit. It is shared, it
carries unpushed work, and it moves under you. Read, `fetch`, report.

## Step 2 — Does Pia.Docs cover what is about to ship?

`docs/release_notes/RELEASE.md` becomes the release body, so its bullets **are** the list of
user-visible changes this push ships. Walk them:

```bash
grep -ril "<keyword from the bullet>" ../Pia/src/Pia.Docs/src/content/docs/wpf
```

Report one table — bullet → the page and heading that answers it, or **GAP**. A bullet is covered
when a user could act on it from that page, not when the page merely mentions the feature. Only
English `wpf/**` counts: `build-kb.mjs` filters `de/`, `fr/` and `server/**` out of the corpus.

Gaps hand off — **and cannot be closed from this session.** Skills load per project, so the docs
repo's own `writing-docs` pipeline is not reachable from a Pia.Wpf session. Give the user the gap
list and stop:

> Open a session in `C:\projects\Pia`, run `/writing-docs` with these gaps, commit there, then
> re-run this skill.

Order is load-bearing: pages land in Pia.Docs **first**, snapshot second. Snapshot first and the
release ships a corpus that cannot answer the release it shipped with.

## Step 3 — Check for drift

```bash
pwsh -NoProfile -File scripts/Update-HelpCorpus.ps1 -Check
```

**Exit 1 means drift, not failure** — a tool wrapper will render it as an error; read the text, which
names the snapshot commit and the current docs commit. It compares the pages, not the whole file, so
an unrelated `server/**` commit is correctly silent. It writes nothing in this repo (it does rebuild
`dist/kb-preset` in the docs repo, a build output).

Needs `node` on PATH; run `npm install` in `../Pia/src/Pia.Docs` if the builder complains about
dependencies. Up to date **and** no gaps in step 2 → say so and stop, there is nothing to commit.

## Step 4 — Refresh, build, gate

```bash
pwsh -NoProfile -File scripts/Update-HelpCorpus.ps1
dotnet build
```

Then run the test gate exactly as **Test Gate** in `CLAUDE.md` defines it.

Do not skip it. `HelpSearchTests` pins eight real user questions to the page that must answer them,
and section references are derived from headings by `HelpSectionParser` — a heading rename or a
restructure upstream turns those red although the script succeeded. A red test there is the finding:
work out which page now answers the question, and whether a term the user types needs an alias or
stop-word entry in `HelpSearchService`. Re-pinning a test to whatever came back defeats the only
check that the corpus still answers anything.

## Step 5 — Commit

The `.gz` alone, naming the docs commit it came from, so a stale snapshot is diagnosable later:

```
Refresh the help corpus from Pia.Docs <short-sha>
```

The skill ends here. Pushing is the user's act — and that push cuts the release.

## Hard rules

- Never hand-edit `help-corpus.json.gz` or the JSON inside it. The script is its only writer.
- Never snapshot from a docs checkout that is behind `origin/master` or dirty under `src/content/docs`.
- Never widen this into refreshing `de/`/`fr/` or `server/**`. They are deliberately out of the
  corpus.
