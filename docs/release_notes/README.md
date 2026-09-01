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

Write for someone deciding whether to update — what they can now do, and what changes under them.
Not commit subjects, never a `Co-Authored-By:` trailer or an internal batch or spec id.

## After a release

`build-and-release.yml` does this for you: it copies `RELEASE.md` to `YYYY-MM-DD-<version>.md` in
this folder as the archive, empties it out for the next cycle, and pushes that as a `[skip ci]`
commit — only when it actually shipped curated notes, so a fallback build never wipes real content.
Leaving `RELEASE.md` empty between edits is safe: the diff-since-last-tag check downgrades an
unchanged file to git-cliff rather than shipping the wrong notes.
