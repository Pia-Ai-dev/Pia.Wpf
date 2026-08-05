# Memory Vault Format — Specification v1

- **Status:** Normative. This is the cross-repo contract.
- **`schemaVersion`:** `1`
- **Applies to:** Pia.Wpf (C#), Pia.Mac (Swift/PiaKit), Pia sync server (format-agnostic).

This document is the single authoritative description of the on-disk vault format. Both clients
implement it identically; the server never parses it. **A developer with zero prior context must be
able to produce a byte-identical file from this spec alone.** Where the algorithm matters for
byte-identity (slugs, section boundaries, splice edits), it is given exactly and deterministically.

The shared artifact is **this spec, not code**. `Pia.Shared` (.NET) and `PiaKit` (Swift) each
implement it; `schemaVersion` in frontmatter keeps them honest.

---

## 1. Vault layout

The vault is a directory tree. Its root is configurable; defaults:

- Windows: `%LOCALAPPDATA%\Pia\Vault`
- macOS: `~/Library/Application Support/Pia/Vault`

```
Vault/                          # configurable root
  sources/                      # RAW layer (immutable). Pia READS, never edits or deletes.
  memory/                       # Pia-owned. Pia WRITES ONLY here.
    index.md                    # catalog: every page + 1-line summary, grouped by type (§8)
    log.md                      # append-only journal (§9)
    AGENTS.md                   # the Schema: conventions, human-editable, co-evolved
    profile.md                  # structured — personal_profile (sections = facts)
    contacts.md                 # structured — contact_list (sections = ## <person>)
    preferences.md              # structured — preference (sections = prefs)
    notes/<slug>.md             # freeform notes (one file each)
    projects/<slug>.md          # freeform projects (one file each)
    topics/<slug>.md            # compiled wiki entity/concept pages (one file each), [[linked]]
    .archive/                   # originals kept on merge/migration for rollback
  <user's own .md files>        # the user's vault content. Read-only to Pia, indexed for recall.
```

Ownership rules:

- **`sources/`** is immutable to Pia: read-only, never edited or deleted.
- **`memory/`** is the only tree Pia writes to. Every file Pia writes carries the `pia: managed`
  frontmatter marker (§2).
- **User files** anywhere in the vault (including outside `memory/`) are read-only to Pia but are
  **indexed for recall** (§ index & recall is covered in the implementation plan, not this format spec).
- The **`.archive/`** folder holds pre-merge / pre-migration originals for rollback. Pia writes here.

Structured types map to **one document, records as `##` headings** (`profile.md`, `contacts.md`,
`preferences.md`). Freeform types (`note`, `project`) and compiled wiki entities (`topic`) map to
**one file each** under `notes/`, `projects/`, `topics/`.

---

## 2. Frontmatter schema (contract C1)

Every Pia-managed file **begins** with a YAML frontmatter block, delimited exactly as:

- The very first line of the file is the **delimiter line** `---` (three hyphens).
- The block ends at the next **delimiter line** `---`.
- Everything between is a YAML mapping of scalar keys to values.

**Delimiter-line comparison (CRLF-safe, normative).** For the purpose of the "is this line `---`"
test only, a *line* is its content with the line terminator removed, where the terminator is `\n`,
`\r\n`, or EOF — i.e. a single trailing `\r` immediately before `\n` is **not** part of the content.
A delimiter line is therefore one whose content (after removing exactly one optional trailing `\r`
and the `\n`) equals the three bytes `---`. This comparison is performed on a *logical line view*;
the parser **never mutates `RawText`** (§3.1), so CRLF files round-trip byte-for-byte. Two conforming
readers parse the same bytes identically whether the file uses LF or CRLF.

```yaml
---
pia: managed                        # ownership marker — REQUIRED, literal value "managed"
id: 6f9c0b3e-7c1a-4f2e-9a8b-000000000001   # REQUIRED. Stable sync identity (server row key).
type: contact_list                  # REQUIRED. One of the canonical type set (§7).
title: Contacts                     # REQUIRED. Human display title.
created: 2026-06-07T09:00:00Z       # REQUIRED. ISO-8601 UTC, second precision, trailing 'Z'.
updated: 2026-06-07T09:30:00Z       # REQUIRED. ISO-8601 UTC. Newer wins in 3-way merge (§10).
schemaVersion: 1                    # REQUIRED. Integer. This spec is version 1.
sources:                            # OPTIONAL. Topic/wiki pages only — provenance (list).
  - sources/q2-report.pdf
---
```

### 2.1 Required keys and types

| Key | Type | Rule |
|---|---|---|
| `pia` | string | Literal `managed`. Pia only edits files carrying this. |
| `id` | string (UUID) | RFC-4122 UUID. **On write**, clients MUST emit the lowercase canonical 8-4-4-4-12 form (`Guid.ToString("D").ToLowerInvariant()` / equivalent) so on-disk bytes are identical across WPF/Mac. **On read**, parsers accept any RFC-4122 form (case-insensitive, braces, no-hyphen) via `Guid.Parse`. **Stable**: never changes on rename/move. The server row is keyed on it (§11). Invisible to the user; the human identity is the path. |
| `type` | string | One of the canonical type set (§7). |
| `title` | string | Human display title. |
| `created` | string | ISO-8601 UTC timestamp, format `yyyy-MM-ddTHH:mm:ssZ`. |
| `updated` | string | ISO-8601 UTC timestamp, format `yyyy-MM-ddTHH:mm:ssZ`. |
| `schemaVersion` | integer | `1` for this spec. |

### 2.2 Optional keys

| Key | Type | Rule |
|---|---|---|
| `sources` | list of strings | **Topic/wiki pages only.** Vault-relative paths into `sources/` that back this page (provenance). |

### 2.3 Unknown keys

**Unknown frontmatter keys are preserved verbatim.** Users (and Obsidian plugins) add their own keys;
Pia must round-trip them byte-for-byte. Pia never reorders, rewrites, or strips keys it does not own
when splicing a section body (§ C2 / §10). When Pia must rewrite the whole frontmatter block (only on
file create or an explicit frontmatter-field update), it preserves all unknown keys.

### 2.4 Identity reconciliation (design decision #3 ⇄ C1)

There are **two** identities, and they do not conflict:

- **Record identity = heading slug** (§3, §6). Records *inside* a structured doc carry no GUID; they
  are addressed as `file#Heading` / by slug. This is what keeps files clean and makes
  `[[contacts#John Smith]]` work natively.
- **File sync identity = the frontmatter `id` GUID** (C1). One GUID per *file*, used only as the
  server row key for sync. It never appears in section bodies and never changes on rename/move.

"GUIDs dropped" in the design refers to *per-record* GUIDs, not the single per-file sync `id`.

### 2.5 Timestamp format

All timestamps are **ISO-8601, UTC, second precision, literal trailing `Z`**: `yyyy-MM-ddTHH:mm:ssZ`
(e.g. `2026-06-07T09:30:00Z`). No fractional seconds, no offset other than `Z`.

---

## 3. Section convention

Within a document, the body (everything after frontmatter) is divided into **sections**.

**The section-boundary predicate (normative, used everywhere in this spec):** a line is a section
boundary **iff** it matches the regex `^## (.+)$` against its *logical-line content* (line terminator
removed per §2's delimiter rule) — exactly two `#`, one space, then **at least one** character of
heading text. This single predicate is used identically in §3.1, §3.2, §8, Appendix A, and the merge
in §10.

- Levels other than `##` (`#`, `###`, …) are **not** boundaries; they are body content of the
  enclosing section (or of the preamble, if before the first boundary).
- A bare `## ` line with **no** heading text after the space (e.g. `## ` or `##` followed only by
  trailing whitespace/`\r`) does **not** match `^## (.+)$` and is therefore **body**, not a boundary.
- The **heading** is the captured group `(.+)` with leading/trailing ASCII whitespace trimmed, where
  **ASCII whitespace** is exactly the byte set `{0x09 TAB, 0x0A LF, 0x0B VT, 0x0C FF, 0x0D CR, 0x20
  SPACE}`. (So a CRLF heading line `## John Smith\r\n` yields heading `John Smith` — the trailing
  `\r` is trimmed and never appears in the heading, wikilinks (§5), or index summaries (§8).)
- The **slug** is derived from the heading per §6 and is the section's identity within the file.
- The **body** is every byte after the end of the heading line (i.e. after its terminating newline,
  or after the file's last byte if the heading line ends at EOF) up to — but not including — the
  first byte of the next boundary line, or end-of-file if none follows.
- The **preamble** is everything between the end of the frontmatter block and the first boundary line
  (or EOF if there is no section). It may be empty.

### 3.1 Byte offsets (for splice edits — contract C2)

The parser records, for each section, the **byte offsets** `BodyStart` and `BodyEnd` into the file's
exact original bytes (`RawText`), such that `RawText[BodyStart..BodyEnd]` is the section body **as
defined in §3.2** (including any blank lines up to the next boundary). A section edit is a
**byte-range splice**:

```
newFile = RawText[0..BodyStart] + newBody + RawText[BodyEnd..]
```

Everything outside `[BodyStart, BodyEnd)` — frontmatter, unknown keys, sibling sections, the heading
line itself, user whitespace — is preserved **byte-for-byte**. Pia never re-serializes a whole file
to change one section.

### 3.2 Whitespace and newlines

- The parser preserves `RawText` exactly; it never normalizes line endings.
- `BodyEnd` is the byte index immediately before the next boundary line's first `#` (so the blank
  line(s) separating sections belong to the *preceding* section's body, matching how a human reads
  the file).
- For the **final** section (no boundary follows), `BodyEnd = RawText.Length` (the total file byte
  length), whether or not the file ends with a newline.
- A heading line is terminated by `\n` (or EOF). `BodyStart` is the byte index immediately after that
  terminator (or `RawText.Length` if the heading line is the file's last line with no trailing `\n`,
  giving an empty body).

---

## 4. Structured-record body format

Inside a structured document (`profile.md`, `contacts.md`, `preferences.md`) and inside compiled
`topic` pages, a record body uses **`- key: value` bullet lines** for deterministic field-level merge:

```markdown
## John Smith
- email: john@example.com
- phone: 555-0100
- company: Acme

Met at the Q2 offsite. Prefers email over calls.
```

Rules:

- A **field bullet** is a line matching `^- ([^:]+): (.*)$`. The key is the text between `- ` and the
  first `: `; the value is the rest of the line. Keys are compared case-sensitively after trimming.
- Field bullets form an **ordered map**: first occurrence wins position; a later upsert of an existing
  key **replaces the value in place**; a new key is **appended** after the last existing bullet.
- **Free prose** is any non-bullet text and may appear below the bullets. Prose is preserved
  untouched by field-level merge. A prose-only section in the `Edit` band is rewritten by handing
  *only that section* to the model (bounded scope — never the whole document).

---

## 5. Wikilinks

- `[[file]]` links to another vault file; `[[file#Heading]]` links to a section.
- The target is a **vault-root-relative path without the `.md` extension** (e.g. `[[topics/acme]]`,
  `[[contacts#John Smith]]`). The heading portion after `#` is the section **heading text**
  (not the slug), so it renders natively in Obsidian.
- Pia inserts wikilinks during ingest/cross-linking; it never rewrites a user's existing links.

---

## 6. Slug rules (deterministic — must match across clients)

The slug is computed from a heading string by this exact, ordered algorithm:

1. **Unicode-normalize** the string to NFD (canonical decomposition).
2. **Strip combining marks** (Unicode category `Mn`). This folds diacritics: `é → e`, `ü → u`,
   `ñ → n`.
3. **Lowercase** using invariant (culture-independent) lowercasing.
4. **Replace** every maximal run of characters that are **not** ASCII `[a-z0-9]` with a single
   hyphen `-`. (Spaces, punctuation, symbols, and any remaining non-ASCII characters are separators.)
5. **Trim** leading and trailing hyphens.
6. If the result is **empty** (heading had no ASCII-alphanumeric content), use the fallback
   `section`.
7. **Collision suffix (global uniqueness):** Slugs are assigned **strictly in document order**,
   maintaining the set of slugs already assigned to earlier sections in the same document (these
   are post-step-6 values, after fallback substitution — including any that fell back to `section`
   and any real heading that legitimately slugs to `section`). For each section, let `base` be its
   post-step-6 value. If `base` is **not** already in the assigned set, the section's slug is
   `base`. Otherwise the slug is `{base}-{N}` for the **smallest integer N ≥ 2** such that
   `{base}-{N}` is **not** already in the assigned set. The chosen slug is then added to the
   assigned set. This guarantees every section in a document has a **unique** slug. (So
   `## Section` followed by `## !!!` yields `section` then `section-2`; two `## Café (work)!` yield
   `cafe-work` then `cafe-work-2`. The smallest-free-suffix rule also resolves the case where a
   collision suffix would otherwise clash with a different heading's natural slug — see §6.1.)

### 6.1 Worked examples (normative — these are also Phase 1 test fixtures)

| Heading | Slug |
|---|---|
| `John Smith` | `john-smith` |
| `Alice Jones` | `alice-jones` |
| `Café (work)!` | `cafe-work` |
| `Café (work)!` (second occurrence in same doc) | `cafe-work-2` |

Derivation of `Café (work)!`: NFD+strip-marks → `Cafe (work)!` → lowercase → `cafe (work)!` →
non-`[a-z0-9]` runs → `-`: `cafe` + `-` (from `" ("`) + `work` + `-` (from `")!"`) → `cafe-work-` →
trim trailing `-` → **`cafe-work`**.

Multi-section collision (normative). The document headings `Cafe Work`, `Cafe Work`, `Cafe Work 2`
— in that order — yield slugs `cafe-work`, `cafe-work-2`, `cafe-work-2-2`. The second section's
collision suffix (`cafe-work-2`) is assigned before the third section is processed, so the third
section (whose natural slug is also `cafe-work-2`) takes the smallest free suffix `cafe-work-2-2`.
Every slug stays unique.

---

## 7. Canonical type set (contract C6)

The canonical `type` values, fixed for `schemaVersion: 1`:

| `type` | Storage shape | Document |
|---|---|---|
| `personal_profile` | structured (sections = facts) | `memory/profile.md` |
| `contact_list` | structured (sections = `## <person>`) | `memory/contacts.md` |
| `preference` | structured (sections = prefs) | `memory/preferences.md` |
| `note` | freeform (one file per item) | `memory/notes/<slug>.md` |
| `project` | freeform (one file per item) | `memory/projects/<slug>.md` |
| `topic` | compiled wiki (one file per entity) | `memory/topics/<slug>.md` |

> **Normative override.** The canonical set is **exactly these six** values. Some inline
> `# personal_profile | preference | note | project | topic` example comments in the plan/design omit
> `contact_list` (listing only five) — those comments are illustrative and **stale**; this table and
> implementation-plan contract C6 are authoritative.

### 7.1 Legacy type migration

The pre-migration WPF set had 7 types; Mac had 4. Reconciliation for v1:

- `skill` → **`note`**
- `context` → **`note`**

All other legacy types map to their same-named canonical value. Both clients implement exactly the
six canonical types above; `skill` and `context` are accepted only as *migration inputs* and are
rewritten to `note`.

---

## 8. `index.md` format

`memory/index.md` is a catalog of every Pia-managed page, **grouped by type**, one line per page.
It is regenerated deterministically (sorted), so it is stable under re-runs.

```markdown
---
pia: managed
id: <uuid>
type: note
title: Index
created: 2026-06-07T09:00:00Z
updated: 2026-06-07T09:30:00Z
schemaVersion: 1
---
# Index

## Contacts
- [[contacts]] — People Pia knows about.

## Notes
- [[notes/q2-retro]] — Retrospective notes from the Q2 offsite.

## Topics
- [[topics/acme]] — Acme Corp: customer since 2024, enterprise tier.
- [[topics/john-smith]] — Primary contact at Acme.
```

Rules:

- One `##` group per type that has pages, in canonical type order (§7): `personal_profile`,
  `contact_list`, `preference`, `note`, `project`, `topic`. Group headings use the display name.
- Within a group, entries are **sorted by link target** (ascending, ordinal). Each entry is exactly:
  `- [[<vault-relative-path-no-ext>]] — <one-line summary>` (em dash `—` surrounded by single spaces).
- A one-line summary contains no newlines.

---

## 9. `log.md` format

`memory/log.md` is an **append-only** journal. Every entry is a single grep-parseable line:

```markdown
## [2026-06-07] ingest | q2-report.pdf -> topics/acme, topics/john-smith
## [2026-06-07] remember | contacts#John Smith updated
## [2026-06-07] lint | merged duplicate topics/acme-corp -> topics/acme
```

Rules:

- Each entry is exactly: `## [YYYY-MM-DD] <op> | <description>` — literal `## [`, ISO date, `] `,
  the operation token (lowercase, no spaces, e.g. `ingest`, `remember`, `forget`, `migrate`, `lint`,
  `merge`), ` | `, then a free-text description with no embedded newline.
- Entries are **only appended** (atomic append; never rewritten or reordered).
- The date is the UTC calendar date.

---

## 10. 3-way merge semantics (contracts C2 + Phase 5)

Two devices editing the same per-type document offline reconcile via a **section-aware 3-way merge**
against the last-synced **base** (the client retains the last-synced copy as base; standard 3-way
state). The merge is **per section**, keyed by slug. Slugs are processed in the **ordered union**:
`base` order first, then slugs new in `local` (local order), then slugs new in `remote` (remote
order).

### 10.1 Per-section decision procedure (normative oracle)

For one slug, let `L`, `R`, `B` be its body on local / remote / base, each either a body string or
**absent** (the slug is missing on that side). Define `changed(side) := (side ≠ B)` where presence
differs (present vs absent) **or** content differs. Apply the **first** matching rule:

1. **`L == R`** (both absent, or both present with equal bytes) → if present, emit that body
   (identical edit / both-add-same); if both absent, **drop** the section.
2. Else **`¬changed(local)`** → take `remote`: if `R` present emit `R`; if `R` absent **drop**
   (remote deleted a section local left untouched).
3. Else **`¬changed(remote)`** → take `local`: if `L` present emit `L`; if `L` absent **drop**
   (local deleted a section remote left untouched).
4. Else **both changed**:
   - **a. exactly one side absent** (edit-vs-delete): emit the **present (edited)** side's body and
     **flag a conflict**.
   - **b. both present, `L ≠ R`** (concurrent edits): emit the **conflict marker** (§10.3) and
     **flag a conflict**.

This procedure is the test oracle for Phase 5. It makes the common case (A edits *John*, B edits
*Alice*) auto-merge, and — crucially — distinguishes **delete-of-unchanged** (rule 2/3 → silently
drop) from **edit-vs-delete** (rule 4a → keep the edit + flag). Equivalent decision table:

| B | L | R | changed(L) | changed(R) | Result |
|---|---|---|---|---|---|
| any | absent | absent | — | — | drop (deleted on both) |
| any | `X` | `X` | — | — | `X` (identical / both-add-same) |
| `B` | `B` | `R` | no | yes | `R` (only remote changed) |
| `B` | `L` | `B` | yes | no | `L` (only local changed) |
| `B` | `B` | absent | no | yes | **drop** (remote deleted unchanged) |
| `B` | absent | `B` | yes | no | **drop** (local deleted unchanged) |
| `B` | edited | absent | yes | yes | keep local edit; **flag** (edit/delete) |
| `B` | absent | edited | yes | yes | keep remote edit; **flag** (edit/delete) |
| `B` | `L` | `R` (L≠R) | yes | yes | **conflict marker**; **flag** |

### 10.2 Reassembly (frontmatter / preamble selection)

The reassembled file's **frontmatter and preamble** come from the side with the **newer `updated`
timestamp** (§2.5). **Tie-break:** if the timestamps are equal, **local wins**. (The reference
implementation MUST select by comparing `updated`, not hardcode local.) The merged section bodies
from §10.1 are reassembled in ordered-union order.

### 10.3 Conflict-marker byte layout (exact)

A conflict section body is exactly the concatenation (git-style markers, each on its own line):

```
"<<<<<<< local\n" + nl(L) + "=======\n" + nl(R) + ">>>>>>> remote\n"
```

where `nl(s)` = `s` if `s` is empty or already ends with `\n`, else `s + "\n"` (guarantees the
following marker starts its own line; a body's existing trailing blank lines are otherwise preserved
verbatim). `recall` flags a section as conflicted by detecting the literal line `<<<<<<< local`. The
server itself stays **last-writer-wins and zero-knowledge** — all merge logic is client-side.

---

## 11. Sync envelope (contract C5)

The sync unit is **the file**. Each Pia-managed file maps to one server record. The server is
**format-agnostic** — it stores opaque encrypted bytes and never parses markdown.

When **E2EE is active**, the record's `EncryptedPayload` (AES-GCM) wraps this inner object, and
`WrappedDek` carries the wrapped DEK:

```json
{ "path": "memory/contacts.md", "content": "<full file bytes as UTF-8 text>" }
```

- `path` is the **vault-relative path** of the file.
- `content` is the **entire file content** (frontmatter + body), byte-for-byte.
- **The server row is keyed by the file's frontmatter `id` GUID (C1), NOT by path.** The client sets
  `ServerMemory.Id := <frontmatter id>` for every Pia-managed file, and migration carries each
  per-file `id` forward, so the server key and the on-disk `id` are always identical. Keying by `id`
  (not path) is what lets a file be **renamed/moved without orphaning its server row**.
- When E2EE is active the server **never** sees a plaintext path — `path` lives only inside
  `EncryptedPayload`, and any plaintext `Path` column is left null (C5).

> **Normative override.** Where the design doc's Sync section says "key by relative file path instead
> of GUID," it is **superseded by this spec and implementation-plan Task 5.1**: the row stays keyed by
> the frontmatter `id` GUID and the path travels inside the payload. This spec is authoritative.

When E2EE is **not** active (plaintext sync), the same `{ path, content }` may travel in the
plaintext DTO fields; a nullable plaintext `Path` column on the server then round-trips the path.

The post-quantum E2EE migration changes only *how the DEK is wrapped*, not *what is wrapped* — it
composes with this envelope unchanged.

---

## Appendix A — Parsing algorithm (reference, for byte-identity)

A conforming parser, given the exact file bytes:

1. If the file's first line is a **delimiter line** `---` (per §2's CRLF-safe delimiter comparison),
   read forward to the next delimiter line `---`; the lines between are the frontmatter YAML. Parse
   them into a scalar map (lists kept as raw text). The byte just after the closing delimiter line's
   terminator is the start of the content region. If the first line is not a delimiter line,
   frontmatter is empty and the content region is the whole file.
2. **Preamble** = content-region bytes from its start up to (not including) the first
   **section-boundary line** (§3 predicate `^## (.+)$` on the logical-line content), or to EOF if none.
3. Walk the content region. Each section-boundary line (§3) opens a new section: heading = captured
   group `(.+)` trimmed of ASCII whitespace (§3); slug per §6 (deduped over post-fallback values in
   document order). `BodyStart` = byte index just after the heading line's terminator (or
   `RawText.Length` if the heading is the last line with no trailing `\n`). `BodyEnd` = byte index
   just before the next boundary line's first `#`, or `RawText.Length` for the final section.
4. `RawText` is retained unmodified for splice edits (§3.1); line endings are never normalized.

## Appendix B — Contracts cross-reference

| Contract | Where pinned |
|---|---|
| C1 — frontmatter is sync identity | §2, §2.4 |
| C2 — edits are byte-range splices | §3.1 |
| C3 — DB is disposable | (index/recall — implementation plan, not this format spec) |
| C4 — embeddings never in `.md` | (no embedding key exists in this format; enforced by §2 schema) |
| C5 — server stays zero-knowledge | §11 |
| C6 — type taxonomy reconciliation | §7 |
