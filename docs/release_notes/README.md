# Release notes

`RELEASE.md` holds the notes for the **next** release. It is curated on the release branch and
ships as-is.

## How it reaches a reader

Two destinations, one file. `build-and-release.yml` copies `RELEASE.md` over git-cliff's commit dump
when its body has changed since the last release tag, stamps the version header itself, and creates
the GitHub release with it. **Publish
WPF Download** in `Pia-Ai-dev/Pia` then takes the release body verbatim
(`jq -r '.body'`) and serves it as `storage.pia-ai.de/f/wpf/RELEASE-NOTES.md`, which the download
page on pia-ai.de links.

## What that costs the format

Storage serves the file as `text/plain` — the workflow asserts it — so a visitor reads the **raw
Markdown**, while GitHub renders the same bytes. Both have to be legible:

- `##` headings and `- ` bullets, one level deep. No tables, no HTML, no nested lists.
- Hard-wrap at 80 columns; nothing reflows it for the browser.
- `→` and `—` are safe: storage answers `text/plain; charset=utf-8`. Confirm with
  `curl -sSI https://storage.pia-ai.de/f/wpf/RELEASE-NOTES.md` if that ever changes — the
  workflow's own assertion matches the content type loosely and would not catch a dropped charset.
- First line is `# Pia <version>`, but you don't have to keep it current — the build overwrites it
  with the version actually being shipped. What decides curated-vs-fallback is whether the body
  changed since the last release tag, so notes nobody refreshed fall back to git-cliff instead of
  republishing the previous release's text.

## How long, and how precise

Write for someone deciding whether to update. Not commit subjects, never a `Co-Authored-By:`
trailer or an internal batch or spec id.

- **One bullet per change, and the bullet is the change** — not its history.
- **Sentence one says what the reader can now do**, or what changes under them. Every later sentence
  has to earn its place: a caveat, a migration, a number, a thing that still needs doing.
- **Four lines is the ceiling**, six if a migration has to be spelled out. A section is one to five
  bullets. If a bullet needs more, it is two changes or it is explaining itself.
- **Cut**: how it used to work, unless the old behaviour is what misled people · why it was built ·
  the mechanism · class, file and setting names · what it was measured against · anything a reader
  cannot act on.
- Name UI exactly as the UI does — panel titles, button labels, checkbox text. An invented name
  sends the reader hunting for something that is not there.

The bar is that a reader can tell in one line whether this release affects them. Prose that explains
itself belongs in the commit message, which is where the reasoning is preserved anyway.

## Where it is written, and where it is archived

`RELEASE.md` changes on exactly three kinds of branch (see the `release-flow` skill):

- `release/<yyyy-mm-dd>` and `hotfix/<name>` curate it, just before their PR into `main`.
- `back-merge/<ver>`, cut from `main` after the release run is green, archives it on its way back into
  `develop`:

```bash
DATE=$(git log -1 --format=%cs v<ver>)
{ printf '# Pia %s
' "<ver>"; tail -n +2 docs/release_notes/RELEASE.md; } > "docs/release_notes/$DATE-<ver>.md"
: > docs/release_notes/RELEASE.md
```

A feature PR leaves it alone: an edit on `develop` while a release is open makes the back-merge
conflict. Pushes to `develop` never build, so the archive commit needs no skip-ci marker.

CI cannot archive for you. The `Main` ruleset requires a pull request with an approving review plus
verified signatures, so a push by `github-actions[bot]` is refused with `GH013`.

**The guard.** If `RELEASE.md` keeps a shipped body and someone appends the next entry, the
diff-since-last-tag check sees a change, takes the curated path, and republishes the old notes
alongside the new. The **Refuse to republish the last release's notes** step fails a `main` run if
the file still contains the whole body released at the previous tag. It sits immediately after
checkout, so it costs seconds rather than a full signed build. A hotfix branch therefore archives
the last shipped body before writing its own — under the same dated name the release's back-merge
uses, so the two merge cleanly.

Leaving `RELEASE.md` empty between releases is safe, and is the point: an unchanged or empty file
downgrades to git-cliff rather than shipping the wrong notes.

## Old releases are pruned

`prune-releases.yml` runs weekly and deletes releases that are both older than 90 days and outside
the newest five, so the list does not grow without bound at a release per push to `main`. The
`Prune releases` step summary shows the keep/delete decision per tag, and a manual dispatch defaults
to a dry run.

Nothing downstream reads an old release. Velopack resolves the newest non-prerelease release and
fetches only what its `releases.win.json` names — the current full, the current delta, the previous
full — and pia-ai.de serves its own uploaded copies rather than linking a GitHub asset. **Tags are
never deleted.** `CHANGELOG.md` is rebuilt from all of them with git-cliff on every release branch, and
the release-notes diff base comes from `git describe --tags`, so dropping a tag would quietly
rewrite history on the next release.

To change the policy, edit the defaults in the workflow (or pass `-KeepMinimum` / `-MaxAgeDays` to
`scripts/Prune-Releases.ps1`, which is the whole implementation and is safe to run locally — it
deletes nothing without `-Apply`).
