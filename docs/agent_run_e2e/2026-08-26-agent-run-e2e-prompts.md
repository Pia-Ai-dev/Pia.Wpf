# Agent-run e2e prompts (foreground + background)

**Status:** executable · **Owner:** Marco Altmann · **Written:** 2026-08-26
**Origin:** owner request — drive six agent runs through the real UI with WinWright to prove the
foreground and background run flows work end to end against the OpenRouter/DeepSeek provider.

Six goals, each pinned to its own chat and its own working directory, each forcing several
built-in file tools (`list_files`, `read_file`, `search_files`, `write_file`; `delete_file` only
in FG2). Every prompt names its output files literally and demands a count that the seeded
fixture already fixes, so verification is `grep`/line-count, not a judgement call.

Deliberately absent from all six: memory and vault tools (`remember`, `create_source`,
`update_source`, `recall`). `PIA_DATA_DIR` does **not** redirect the memory vault, so a vault
write from a throwaway profile would land in the real one.

## Fixtures

Each folder lives under the throwaway assistant files root and is seeded before launch. The
expected numbers below are properties of the seed, not of the model.

| Folder | Seeded | Expected |
|---|---|---|
| `Inventory` | `inventory.csv` (12 products), `reorder-policy.md` | 5 below reorder point, 1 of them DISCONTINUED → 4 rows |
| `ReleaseNotes` | `VERSION.txt` (2.4.7), `fragments/` (6 `.md`) | 6 merged, 2 without a ticket id, bump to 2.4.8 |
| `Support` | `tickets/` (9 `.txt`) | 3 billing, 4 bug, 2 howto |
| `Finance` | `expenses-q1.csv`, `expenses-q2.csv`, `budget.md` | 6 over-budget categories |
| `Docs` | 5 `.md` with `[text](target)` links | 7 links, 3 broken |
| `Config` | `baseline.env`, `prod.env`, `staging.env` | 4 keys drift, 2 missing in staging |

## Foreground (Agent lever → Send)

### FG1 — `Inventory`

> List every file in this working folder, then read `inventory.csv` and `reorder-policy.md`.
> Apply the policy to decide which products need reordering. Before you finalise, run a
> `search_files` for the word `DISCONTINUED` across the folder and exclude every product that
> appears in a discontinued marker file — those must not be reordered even if they are below
> their reorder point.
> Write exactly two files: `reorder-report.md` — a markdown table of the products to reorder plus
> a two-sentence summary naming the total order quantity — and `reorder-list.csv` with the header
> `sku,name,on_hand,reorder_point,order_qty` and one row per product to reorder, no others.
> Finally read both files back and state in your answer how many data rows `reorder-list.csv`
> has and whether that matches the table in the report.

### FG2 — `ReleaseNotes` (exercises `delete_file`, i.e. an approval card)

> Read `VERSION.txt`, then list and read every fragment under `fragments/`. Merge them into a
> single `CHANGELOG-2.4.8.md`, grouped under `## Added`, `## Fixed` and `## Changed`, each entry
> keeping its `[PIA-nnn]` ticket id where it has one.
> Any fragment with no ticket id must NOT go into the changelog — instead list its filename and
> first line in `fragments-missing-ticket.txt`, one per line.
> Then update `VERSION.txt` to `2.4.8`, and finally delete every fragment file you successfully
> merged. Report how many fragments you merged, how many you skipped and how many you deleted.

### FG3 — `Support`

> List the folder and read every ticket under `tickets/`. Classify each one as `billing`, `bug`
> or `howto` using the `Category:` line where present and the body text where it is missing.
> Write one file per category — `triage-billing.md`, `triage-bug.md`, `triage-howto.md` — each
> starting with a `# <Category> (<n>)` heading and then one `- <ticket-id>: <one-line summary>`
> per ticket.
> Then write `triage-index.md` containing a table of the three categories with their counts and
> a total row. Before you finish, `search_files` for `URGENT` and add an `## Urgent` section to
> `triage-index.md` naming every ticket that contains it. State the three counts in your answer.

## Background (Agent lever → Run in background)

No delete-ish verb appears in any of these: `delete_file` is outside the auto-approve preset, so
an unattended run that reached for it would park `WaitingForInput` forever.

### BG1 — `Finance`

> Read `budget.md`, then read `expenses-q1.csv` and `expenses-q2.csv`. Total the spend per
> category across both quarters and compare it against the budget.
> Write `overspend-report.md` with a table of `category | budget | actual | delta` sorted by the
> largest overspend first, and a closing line naming the total overspend. Write
> `overspend.csv` alongside it with the header `category,budget,actual,delta` and one row per
> category that is over budget, no others.
> Then `search_files` for `PENDING` and append an `## Unsettled` section to `overspend-report.md`
> listing every line that contains it, with its file name.

### BG2 — `Docs`

> List every markdown file in this folder and read each one. Collect every inline link of the
> form `[text](target)` where the target is a relative path, and check whether the target file
> exists in the folder.
> Write `link-audit.md` with two sections: `## Broken` — one `- <source file> → <target>` line
> per link whose target is missing — and `## Resolved` for the rest. Write `broken-links.csv`
> with the header `source,target` and one row per broken link.
> Then, for each file that contains at least one broken link, write a sibling file named
> `<name>.issues.txt` naming the broken targets, one per line. State the total link count and
> the broken count in your answer.

### BG3 — `Config`

> Read `baseline.env`, `prod.env` and `staging.env`. Treat `baseline.env` as the reference set of
> keys and values.
> Write `config-drift.md` with three sections: `## Missing` (keys in the baseline absent from an
> environment file), `## Extra` (keys present in an environment file but not the baseline) and
> `## Changed` (keys present in both with a different value — show `baseline → actual`). Every
> line must name the environment file it came from.
> Write `config-drift.csv` with the header `env,key,kind,baseline,actual`, one row per finding.
> Then `search_files` for `TODO` and write `config-todo.txt` listing each hit as
> `<file>:<line text>`. State the number of findings per kind in your answer.
