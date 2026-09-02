# search_files over extracted text — live UI walkthrough

**Status:** Complete — 9 of 9 planned checks ran, 9 passed; one defect found live, fixed and
re-verified in the same run
**Owner:** Marco Altmann
**Written:** 2026-09-02
**Origin:** Commit `9ca2af00` "Search inside Word, Excel and mail files, and name what could not
be read". Requested as a live-app check of the routing change against real data.

## What was under test

`9ca2af00` moved `search_files` off its own hand-rolled read and onto `ReadFileTextAsync`, the
routing seam `read_file` already used. Six behaviours follow from that:

1. `.docx` searched as extracted text (one line per paragraph).
2. `.xlsx` searched as extracted text (`## Sheet:` header, then tab-separated rows).
3. `.msg`/`.eml` searched as extracted text (headers, `===`, body) — new on the read side too.
4. A hit's line number is the `read_file` line number for the same file.
5. Files that cannot be read at all are counted and named in a `Note:` line instead of vanishing.
6. `.msg`/`.eml` are write-blocked, because a rendered read would let an edit overwrite the
   original with its own rendering.

Plus the question that motivated the change: can the assistant find a recipe by its *content*
when the filename gives nothing away.

## Setup

- Build: Debug at `9ca2af00`. Debug matters — `IsDevMode` puts the logger at `Debug`, which is
  what makes the `SensitiveDebug` tool-args and tool-result lines visible. Gate green before the
  run (6420 total / 0 failed) and again after the smart-case fix below (6423 / 0), 0 warnings in
  Debug and Release rebuilds of both.
- Profile: the real one. Sandbox root `C:\Users\maltm\Documents\Pia Assistant`,
  `assistantDefaultWorkingDirectory = Playground`, so the effective root was
  `…\Pia Assistant\Playground`.
- Provider: DeepSeek (`819d7d72`). Set for the run and restored to Pia Cloud afterwards.
  `useSameProviderForAllModes` is on, so the switch goes through the *Optimize* combo.
- Mode: **Chat**, not Agent. The composer was left in Agent mode from an earlier session and the
  first attempt opened a planned run with an approval gate; that run was cancelled and the mode
  switched before any check counted.
- Driver: WinWright/UIA.

### Verification channel

The reply is not evidence for a tool test. Three Debug-only log lines are:

- `Tool call <name> (callId=…) args: {…}` — was this case actually exercised.
- `Tool <name> handler result (N chars): …` — did it behave.
- `search_files scanned {N} file(s), searched {M}, extracted {E}, skipped {S}, {K} match(es)` —
  and `extracted`/`skipped` are words that exist only in the new code, so their presence is also
  the proof that the running binary is not a stale one.

### Fixture

Deliberately the shape of the question that started this: 21 files, one whose *name* says cookies,
14 whose name says only "rezept", and exactly one of those 14 that is actually a cookie recipe.
Built by `scratchpad/fixgen` (a throwaway console app referencing `DocumentFormat.OpenXml 3.5.1`)
into `Playground/Rezepte`:

- `omas-kekse.docx` — 4 paragraphs, `Geheimzutat: KARDAMOM` is paragraph 3
- `rezept-01-…` … `rezept-14-…docx` — 4 paragraphs each; only `rezept-07-nussmakronen.docx` is a
  cookie recipe (`Kekse aus Eiweiss und Nuessen`, token `HASELNUSSMAKRONE` on paragraph 3)
- `weihnachtsplaetzchen.docx`, `apfelkuchen.docx`
- `zutaten.xlsx` — sheet `Vorrat`, `VANILLESCHOTE` in row 3
- `mail-von-tante.eml` — `Subject: Fwd: Bestes Keksrezept`, token `MELASSE` in the body
- `scan-kochbuch.pdf` — `%PDF-1.7` then NUL bytes; unreadable on purpose
- `notizen.md`

## Results

Line numbers were predicted from the fixture before the run and are quoted as predicted/observed.

| # | Under test | Args as they reached the handler | Observed | |
|---|---|---|---|---|
| E1 | `.docx` content | `{"path":"Rezepte","pattern":"KARDAMOM"}` | `Rezepte/omas-kekse.docx:3:Geheimzutat: KARDAMOM` (predicted 3) | **PASS** |
| E2 | `.xlsx` content | `{"path":"Rezepte","pattern":"VANILLESCHOTE"}` | `Rezepte/zutaten.xlsx:4:VANILLESCHOTE<tab>2 Stueck` (predicted 4) | **PASS** |
| E3 | `.eml` content | `{"path":"Rezepte","pattern":"MELASSE"}` | `Rezepte/mail-von-tante.eml:6:Geheimzutat: MELASSE` (predicted 6) | **PASS** |
| E4 | Unreadable file named | all four calls above | `Note: 1 file(s) could not be searched (binary, image, or over the size limit): Rezepte/scan-kochbuch.pdf.` | **PASS** |
| E5 | Line-number round-trip | `{"path":"Rezepte/omas-kekse.docx","offset":3,"limit":1}` | `total_lines=4` then `3|Geheimzutat: KARDAMOM` — the line E1 reported | **PASS** |
| E6 | `read_file` renders mail | `{"path":"Rezepte/mail-von-tante.eml"}` | `total_lines=6`, `1|Subject: Fwd: Bestes Keksrezept`, `2|From: …`, `3|===` | **PASS** |
| E7 | Mail is write-blocked | `edit_file {"path":"Rezepte/mail-von-tante.eml","old_string":"MELASSE","new_string":"HONIG"}` | `hasPending=False`, `success:false`, `'.eml' files are read-only here …` | **PASS** |
| E8 | Binary provenance | (every call) | `extracted 19, skipped 1` | **PASS** |
| E9 | The original question | `{"pattern":"Keks or Cookie or Plätzchen","path":"Rezepte","mode":"files"}` | 5 files across `.eml`, 3×`.docx`, `.xlsx` — **including `rezept-07-nussmakronen.docx`** | **PASS** |

E9 is the one that answers the question this work came from. Asked plainly — *"Ich suche ein
Keksrezept. Schau bitte in meinem Ordner Rezepte nach"* — DeepSeek reached for `search_files`
unprompted, with `mode:"files"` and an alternation over the three German/English words, and found
the cookie recipe whose filename says only `rezept-07`. All five hits are `.docx`, `.xlsx` or
`.eml`; before this change every one of them would have been skipped as binary and the answer
would have been "no matches found".

## Findings

### 1. Case-sensitive search silently returned nothing — FIXED IN THIS RUN

`search_files` compiled its regex with `RegexOptions.None`, and neither the tool description nor
the parameter description said so. Measured live, same folder, same `mode:"files"`:

- `kekse` → `No matches found.`
- `Kekse` → 3 files

A model that lowercases its search term gets a confident "not there" for a folder that plainly
has it. That is the failure this whole change exists to prevent, arriving through a different
door.

The first fix was **smart case**, ripgrep's rule: an all-lowercase pattern matches
case-insensitively, any capital makes the match exact. Verified live — `kekse` 3 files (was 0),
`Kekse` 3, `KEKSE` 0.

**That rule was then withdrawn, because this run's own evidence contradicts it.** Smart case
infers intent from the pattern's capitals, which is sound for a developer typing `TODO` at a
terminal and wrong for a model writing prose. E9 is the proof: DeepSeek searched

```
{"pattern":"Keks|Cookie|Plätzchen", …}
```

It capitalised `Cookie` as orthography, not as a demand for an exact match — and under smart case
that pattern silently stops matching `cookies` written lower-case. The inverse of the defect,
reached by the most natural thing the caller does.

Final rule: **capitalisation is always ignored**, with an explicit `case_sensitive: true`
argument for the rare search that wants exactness. No inference, so no silent miss in either
direction. The cost is a false positive — `TODO` also matching the word "todo" in prose — which
is cheap next to a confident "not found". Re-verified live on a fixture carrying both cases
(`rezept-15-lebkuchen.docx` has lower-case "cookies" and an upper-case `TODO:`; `notizen.md` has
a lower-case `todo:`):

| Call | Matches | |
|---|---|---|
| `cookie` | `mail-von-tante.eml`, `rezept-15-lebkuchen.docx`, `weihnachtsplaetzchen.docx` | finds `Cookies` and `cookies` |
| `Cookie` | the same 3 | the inverse trap is gone; smart case would have dropped `rezept-15` |
| `TODO` | `notizen.md`, `rezept-15-lebkuchen.docx` | lower-case `todo:` included |
| `TODO` + `case_sensitive:true` | `rezept-15-lebkuchen.docx` | the opt-out narrows, and the model set it from plain German |

### 2. "Do not read everything" made the model stop looking entirely — observation, not a defect

Asked *"Welche davon sind Keksrezepte? Lies nicht alle Dateien komplett"*, the model called
`find_files` once and answered from filenames alone — no `search_files`, no `read_file`. It could
not have known about `rezept-07-nussmakronen.docx`. The same question without the economy hint
(E9) produced the right `search_files` call immediately. So the steering in the plugin prompt
works; a hint to be frugal is what displaced it. Worth remembering when reading any future report
that "the assistant didn't look in the files".

### 3. A small folder is read, not searched — expected

The first attempt ran against a 7-file version of the fixture, and the model chose `find_files`
plus `read_file` on all seven. That is reasonable at that size and is why the fixture was rebuilt
at 21 files. No action.

## Not covered, and why

- **The 200-file extraction budget.** Reaching it needs 201 OpenXml packages in one folder;
  cost/benefit says no. The tested behaviour is the `continue`, not the count.
- **`.msg`.** Only `.eml` was exercised; both take the same `FileKind.Email` branch, and
  `MsgReader` has its own tests. Building an OLE compound file by hand for one live check was not
  worth it.
- **`.pdf` content.** Out of scope by design — it needs a text extractor (PdfPig is the
  candidate: Apache-2.0, pure managed, no native payload). The `Note:` line is what stands in for
  it, and E4 is the evidence that a PDF now announces itself instead of vanishing.

## Housekeeping

- Fixture `Playground/Rezepte` deleted; `Playground` is back to `Demo`, `Exports`, `Pia_Docs`,
  `get-windowsuser.ps1`. No `.piaignore` was created, and none was left behind.
- Provider restored to Pia Cloud, confirmed by reading the combo back.
- The seven test chats were **left in place** in the real chat history (titles begin with the
  prompt text). They are the primary record of the run; delete them if you want them gone.
