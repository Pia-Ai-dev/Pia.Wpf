# Release notes

`RELEASE.md` holds the notes for the **next** release. It is a living file: rewrite it in place as
work lands, and it ships as-is.

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

## After a release — archive by hand

Two steps, and the build will not do either for you:

```bash
cp docs/release_notes/RELEASE.md "docs/release_notes/$(date +%Y-%m-%d)-<version>.md"
: > docs/release_notes/RELEASE.md
git commit -am "docs: archive <version> release notes [skip ci]"   # [skip ci] or you cut another release
```

`build-and-release.yml` used to do this itself and **never could**. It pushed the emptied file to
`main` as `github-actions[bot]`, and the `Main` ruleset requires a pull request with an approving
review plus verified signatures, with Admin as the only bypass actor. The push was refused with
`GH013` *after* the release had already been published — a red run on a shipped release — and it
took the pia-ai.de step below down with it, so the website silently stayed on the previous version.
The step is gone; a bypass for the bot was considered and declined, to keep the rules meaning what
they say.

**What the automation was protecting against, and what replaces it.** If `RELEASE.md` keeps a
shipped body and someone appends the next entry, the diff-since-last-tag check sees a change, takes
the curated path, and republishes the old notes alongside the new. That is how the 1.4.0 body shipped
at v1.4.0 *and* v1.4.5. The **Refuse to republish the last release's notes** step now fails the run
if the file still contains the whole body released at the previous tag. It sits immediately after
checkout, so it costs seconds rather than a full signed build.

Leaving `RELEASE.md` empty between edits is safe, and is the point: an unchanged or empty file
downgrades to git-cliff rather than shipping the wrong notes.
