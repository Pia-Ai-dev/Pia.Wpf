# Release notes

`RELEASE.md` holds the notes for the **next** release. It is a living file: rewrite it in place as
work lands, and it ships as-is.

## How it reaches a reader

Two destinations, one file. `build-and-release.yml` copies `RELEASE.md` over git-cliff's commit dump
when its first line names the version being built, and creates the GitHub release with it. **Publish
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
- First line is `# Pia <version>`. The build matches the version against it, so notes nobody
  refreshed fall back to git-cliff instead of republishing the previous release's text.

Write for someone deciding whether to update — what they can now do, and what changes under them.
Not commit subjects, never a `Co-Authored-By:` trailer or an internal batch or spec id.

## After a release

Copy `RELEASE.md` to `YYYY-MM-DD-<version>.md` in this folder as the archive, then empty it out for
the next cycle. Leaving it stale is safe — the version check downgrades it to git-cliff rather than
shipping the wrong notes.
