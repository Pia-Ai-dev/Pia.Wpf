# Applying a server-delivered policy sooner — analysis brief

**Status:** Analysis not started. This brief is grounding only; it deliberately does not pick an option.
**Date:** 2026-08-20
**Repo:** `C:\projects\Pia.Wpf`, branch `feature/agent-run-spine`.
**Predecessor:** commit `0e0054a9` "Deliver enterprise policy per group from the server", which shipped the
contract this brief wants to change the timing of. Its handoff spec is
`docs/2026-08-20-group-client-policy-wpf-handoff.md`.

---

## 1. The ask

A server-delivered policy currently takes effect **at the next application start**. The owner wants it to
apply **as soon as possible**, and has said that **restarting the application to enforce it is acceptable**
if that is what it takes.

So the deliverable is not "make the merge live" specifically — it is "shorten the gap between an admin
publishing and the policy being in force", with restart explicitly on the table as a mechanism rather than
a failure.

## 2. What ships today, precisely

`SyncClientService.PullPageAsync` calls `IPolicyService.ReplaceServerPolicyAsync(document)`, which writes
`policy-cache.json` and returns. It deliberately does **not** touch `PolicyService._cached`,
`_defaultedProperties` or `_enforcedProperties`. The merged policy is computed once per process, in
`GetPolicyAsync`, and `Bootstrapper.cs:84` forces that load during startup.

Consequences to hold in mind:

- Nothing observable happens in the session that receives a policy. No control locks, no value moves, no
  notice.
- From a **fresh install it takes two launches**: one to sign in and receive, one to apply.
- `SettingsService` calls `ApplyPolicy` on every settings load **and every save**, so the merged policy is
  re-applied constantly — just always the *old* one.

## 3. The constraint any option must respect

This is the crux, and it is why restart-only was chosen rather than merely tolerated.

`PolicyLock` (`src/Pia.Wpf/ViewModels/Models/PolicyLock.cs`) states it outright: *"Policy is loaded once per
process, so this deliberately raises no change notification."* The ~11 `Is…Enforced` getters across
`GeneralSettingsViewModel`, `OptimizeSettingsViewModel`, `ProvidersSettingsViewModel`,
`AccountSettingsViewModel` and `FirstRunWizardViewModel` are plain computed properties. Nothing re-raises
`OnPropertyChanged` for any of them — verified by grep.

So there are two separate things that must move together:

| | mechanism | who reads it |
|---|---|---|
| the **value** | `ApplyPolicy` writes into `AppSettings`, persisted by `SaveSettingsAsync` | every feature |
| the **lock** | `IsEnforced` / `PolicyLock[...]` → `IsEnabled` bindings | 8 view models, several XAML files |

**If the value moves and the lock does not, the user watches settings change under them with every control
still enabled and no explanation.** That is the specific failure the current design avoids, and it is not
hypothetical: `SaveSettingsAsync` fires on a draft save and a window move, so a live `_cached` swap would
be observed within seconds.

Any option that makes application earlier has to move both, or move neither and restart instead.

## 4. Timing facts that bound "as soon as possible"

From `SyncClientService`:

- `InitialSyncDelay` = 10 seconds after `StartBackgroundSync`.
- `SyncInterval` = 5 minutes base, backing off by `BackoffGrowthFactor` per consecutive idle cycle to
  `MaxSyncInterval` = 15 minutes.
- So **detection latency is 10 s to 15 min** and is not currently reducible without either a push channel
  or a cadence change. Neither exists for this.

Unverified and load-bearing: **does the server bump `catalogVersion` when an admin writes a group policy?**
Once `ClientPolicyInitialized` is latched the pull is conditional, so an unchanged `catalogVersion` means
the catalog block is fast-skipped and the new document never arrives. Confirm against
`Pia-Ai-dev/Pia` branch `feature/group-client-policy` before designing anything on top. If it does not,
that is the real bug and the rest of this brief is premature.

## 5. The in-repo restart precedent

`GeneralSettingsViewModel.ResetAppDataAsync` (~line 455) already restarts Pia:

```csharp
var exePath = Environment.ProcessPath;
if (exePath is not null)
{
    System.Diagnostics.Process.Start(exePath);
    Environment.Exit(0);
}
```

Note what this does **not** do: no graceful shutdown, no `Application.Current.Shutdown()`, no wait for the
old process to exit. `TrayIconService.cs:335` is the only other exit path and it is a plain
`Application.Current.Shutdown()`.

Two things to check before reusing it:

- No single-instance mutex exists in `src/Pia.Wpf` today (grep found none). But
  `docs/2026-08-16-unmerged-branch-inventory.md:181` records `App.xaml.cs` single-instance wiring on an
  **unmerged branch**. If that lands, `Process.Start` before `Environment.Exit(0)` races the mutex and the
  relaunch dies silently.
- `Environment.Exit(0)` skips finalizers and any graceful-shutdown work. Acceptable for a reset that has
  just deleted the data directory; not obviously acceptable mid-session.

## 6. When a restart is NOT safe

Enumerating this is a required output of the analysis, not an afterthought. Known candidates:

- **A meeting is being recorded or transcribed.** Direct transcription and the Teams attendee capture both
  hold audio state; killing the process loses it.
- **An agent run / background assignment is in flight.**
- **An assistant turn is streaming**, or a tool call is awaiting approval.
- **Unsaved user text** — the chat draft, an open editor.
- **A sync push is mid-flight**, which is doubly awkward because the restart would be triggered from the
  pull half of the same cycle.

## 7. The option space (sketch — the analysis should widen and cost these, not accept them)

1. **Live re-merge + full change notification.** `ReplaceServerPolicyAsync` re-runs the merge and raises a
   `PolicyChanged` event; `PolicyLock` gains `INotifyPropertyChanged` and re-raises `Item[]`; all 8 VMs
   re-raise their `Is…Enforced` getters. Highest fidelity, largest blast radius, and it has to answer what
   happens to a settings page the user has open at that moment.
2. **Prompted restart.** Detect a changed document on pull, show a non-blocking banner — *"Your
   organisation updated Pia's settings. Restart to apply."* — with a Restart button, and apply on the next
   natural start regardless. Lowest risk; latency becomes "whenever the user says yes".
3. **Automatic restart at a safe moment.** Same detection, but Pia restarts itself once §6's unsafe-state
   list is all clear (and perhaps only when the window is not focused, or after an idle threshold).
   Fastest without the notification work; needs the safety enumeration to be right and needs the §5
   mechanism hardened.
4. **Partial application.** Apply `enforce` values immediately but leave locks until restart, or the
   reverse. Probably the worst of both — listed so it can be explicitly rejected with a reason.
5. **Do the notification work only for the locks that exist, and force restart for the rest.** A hybrid
   worth pricing, because §8's "most settings enforce invisibly" gap means the lock surface is already
   partial.

## 8. Pre-existing gaps this interacts with

Both are already out of scope in the shipped spec's §8, but they change the calculus here:

- **Most enforced settings enforce invisibly.** Only controls bound to `Policy[...]` or an `Is…Enforced`
  property grey out. `enforce` accepts any `AppSettings` key, so anything else silently snaps back on the
  next save with no explanation. Option 1's notification work is only as good as this binding coverage.
- **No "managed by your organisation" affordance.** A locked control just greys out. The in-repo pattern to
  copy is the managed-persona badge plus `Msg_Settings_CannotEditManagedPersona`. Needs `.resx` entries in
  en/de/fr.

## 9. Questions the analysis must answer

1. Does the server bump `catalogVersion` on a policy write? (§4 — blocking.)
2. What is the actual target? "Within one sync cycle" and "within seconds" imply very different work.
3. Is an *unprompted* restart acceptable to the owner, or must the user always consent?
4. What is the complete unsafe-to-restart list, and how is each state queried?
5. If the user declines a prompted restart indefinitely, what happens? Does the policy ever force itself?
6. Does the answer differ for `enforce` (a compliance control an admin may need applied now) versus
   `defaults` (a suggestion, where waiting costs nothing)?
7. Does this change the logout path, where `ClearServerPolicyAsync` currently relies on the same
   restart-only assumption?

## 10. Explicitly out of scope

- Re-opening the merge semantics, the precedence chain, or the denied-key split. Those shipped in
  `0e0054a9` and are settled.
- Any server change.
- Widening enforce-lock binding coverage as a goal in itself (§8) — note where it blocks an option, but it
  is its own piece of work.
