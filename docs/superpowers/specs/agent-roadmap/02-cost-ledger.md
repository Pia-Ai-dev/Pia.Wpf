# Batch 02 — Remove `CostUsd` (pricing withdrawn)

**Phase 2 · Size XS · Work on `feature/agent-run-spine`** (the only ref this system was built on — see the
chronicle in [`00-OVERVIEW.md`](00-OVERVIEW.md))

**This batch used to be "price table populates `CostUsd`". That is withdrawn by decision, 2026-07-30.** Pia
will not show a money figure for a run. The batch is now the opposite of what it was: it *removes* the
half-built pricing seam so nothing later mistakes it for a plan.

## Why pricing was withdrawn

A price figure Pia can compute is a figure Pia cannot stand behind:

- **Pia Cloud** — the client cannot know what a turn costs; only the server can. The default provider would
  therefore be the one with a permanently blank cost column.
- **Custom / OpenAI-compatible / vLLM / Azure** — unknowable by construction, or per-agreement. Azure's label
  is a *deployment* name, not a model id.
- **Nothing arrives from the wire.** `AiClientService.BuildFinishedItem` builds `UsageDetails` from
  input/output counts it aggregated itself; no provider-reported cost or credit field is parsed anywhere, and
  OpenRouter's spend value is inside the raw representation the adapter drops.
- So the only possible source was a **bundled static table**, which goes stale silently and reads to a user
  like a bill rather than a guess. Publishing an estimate that drifts is worse than publishing nothing.

The ledger's substance was never the money. Tokens and **active** wall-clock are exact for every provider,
and they are what the strip already shows.

## Goal

`CostUsd` no longer exists — not in the DTOs, not in the persisted ledger, not in the UI, not as a TODO.

## The seams to remove

Anchored **by name, not line** — batches 03/04 are editing `RunProgressViewModel.cs` while this is written, so
every number below would be stale by the time the batch runs. Find them with `grep -ri costusd src tests`.

| File | What is there |
|---|---|
| `Services/AgentRunService.cs` | `CostUsd` on the private `Ledger` DTO — the persisted field |
| `ViewModels/RunProgressViewModel.cs` | `private double? _costUsd;` — the `[ObservableProperty]` backing field |
| `ViewModels/RunProgressViewModel.cs` | `CostUsd = ledger.CostUsd; // TODO Phase 2: price table populates cost` in `Project` |
| `ViewModels/RunProgressViewModel.cs` | the `$"${cost:0.##}"` segment in `FormatLedger` |
| `ViewModels/RunProgressViewModel.cs` | `CostUsd` on the VM's private mirror of the ledger DTO |
| `Models/AgentRun.cs` | the `costUsd?` term in the `LedgerJson` shape comment |

That is the whole surface, verified `30956c5`: no price table, no rate constant, no settings entry, no resx
key, no XAML binding (`RunProgressPanel.xaml` binds only `LedgerSummary`), and no test that asserts a cost.
Also strike the pricing clauses in the parent plan if they somehow survive — they were amended out on
2026-07-30 (`../2026-07-18-agent-system-phase1-plan.md` §5 amendment, §7, §9, §16.1 shape line).

## Persisted-data compatibility

`JsonOptions` sets only `PropertyNamingPolicy` — no `IgnoreNullValues` — so **every ledger written so far
carries a literal `"costUsd": null`**. After the removal:

- writes stop emitting the key;
- reads ignore it, because `System.Text.Json` skips unknown members by default.

No migration, no schema bump, no reader shim. Confirm this with a round-trip test over a legacy JSON string
that still contains `"costUsd": 0.42` — it must parse, and the tokens/time must survive.

## Guardrails

- Removal only. Do not add per-phase attribution, cache-token classes, or any other ledger field here —
  those are separate future batches, and one of them is what makes a ledger *more* honest, not richer.
- `LedgerSummary` keeps its `Tokens · seconds` shape; only the `$…` segment disappears.
- No currency symbol, no `Usd`, no "est." anywhere in the run panel.

## Tests

- `FormatLedger` renders `Tokens · Ns` and never a `$` segment.
- Legacy `LedgerJson` containing `costUsd` deserializes cleanly; input/output tokens and `wallClockMs` are
  unchanged.
- Existing `RunProgressViewModelTests` stay green (none of them referenced cost).

## Acceptance

`grep -rniE "costusd|\\\$\{?cost" src tests` returns nothing; the ledger strip shows tokens and active time only; build green
in Debug **and** Release at `0 Warning(s)`.

## If a real cost figure is ever wanted

It does not come back as a client-side table. The prerequisite is the **provider reporting spend on the
wire** — Pia Cloud returning cost or credits in its usage payload, and `AiClientService` recovering
OpenRouter's from the raw representation. Accrue what a provider states; show nothing otherwise. That is a
server work item first, and out of scope for this roadmap.
