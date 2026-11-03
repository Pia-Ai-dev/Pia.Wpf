# VC++ runtime in the package, recall without embeddings, MSI persona art — checklist

**Status:** In progress. Steps 2–6 are done. The step 7 bitmap has landed but its MSI check is open.
Step 1 is deferred with G1.
**Owner:** Marco Altmann
**Written:** 2026-11-03
**Origin:** [2026-11-03-installer-runtime-and-recall-plan.md](2026-11-03-installer-runtime-and-recall-plan.md),
from the 1.4.355 support log in which ONNX Runtime failed to load on a machine without the Visual C++
runtime.

Tick a box in the commit that lands its step.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new
surface · `L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler`
little standalone value, unblocks a High.

## Decision gates

| Gate | Question | Blocks | Decision |
|---|---|---|---|
| G1 | Does the runtime ship app-local next to the exe, or does `vpk pack --framework vcredist145-x64` install it system-wide? Velopack documents the framework install for Setup and updates (with a user prompt), not for the MSI Pia ships. | 1 | Open, and deferred. The `--framework` spike (plan, G1) failed: silent installs skip the runtime install, and its version check is below 14.44. The owner moved runtime delivery to a separate branch. This branch ships 2–7 without it. |

## Steps

- [ ] **1. Stage the VC++ runtime in every package.**
  `scripts/Copy-VcRuntime.ps1` copies the four VC++ DLLs ONNX Runtime imports, version 14.44 or newer
  and Microsoft-signed, from Visual Studio's redistributable folder into the publish output. Both
  `build-and-release.yml` and `build-velopack.ps1` call it before packing.
  Deferred to a separate branch with G1. The app-local version is commit `5aa58e35`, reverted on this
  branch.
  *Deps:* G1 · *Effort:* S · *Value:* High
- [x] **2. Turn a runtime load failure into one typed, latched exception.**
  `EmbeddingService` throws `EmbeddingUnavailableException` without retrying ONNX and logs the Win32
  load error once, so the next support log names the cause.
  *Deps:* none · *Effort:* S · *Value:* Enabler
- [x] **3. Let recall answer without the vector tier.**
  `MemoryService.RecallAsync` skips the vector tier when embeddings are unavailable instead of failing
  the assistant's reply, and its FTS warning stops logging query text.
  *Deps:* 2 · *Effort:* XS · *Value:* High
- [x] **4. Index text even when the embedding fails.**
  `VaultIndexer` writes the chunk and its FTS row with a null embedding, and embeds it on a later pass
  once embeddings work.
  *Deps:* 2 · *Effort:* XS · *Value:* High
- [x] **5. Fall back to lexical scoring in `remember` and wiki lint.**
  `SectionUpsertService` scores sections by heading similarity alone, and `LintService` skips the
  semantic check, when embeddings are unavailable.
  *Deps:* 2 · *Effort:* XS · *Value:* Med
- [x] **6. Quote FTS prefix terms.**
  One `BuildFtsQuery` serves recall and the legacy full-text search and quotes every prefix term, so a
  query word with punctuation no longer breaks text search.
  *Deps:* 3 (its recall test runs without vectors) · *Effort:* XS · *Value:* Med
- [ ] **7. Put the edge persona on the MSI dialog background.**
  `Resources/Installer/banner.bmp` is regenerated from `Pia_Persona_edge.png`, in the old persona's box.
  The bitmap has landed. The MSI welcome-dialog check (plan Task 7 Step 2) is still open, and now needs
  no VS component.
  *Deps:* none (its MSI check uses 1's local build) · *Effort:* XS · *Value:* Med
- [ ] **8. Verify the branch.**
  Zero-warning rebuilds and the test gate. A local package holding the four DLLs. Recall working on a
  machine without the runtime. After merge, the `main` release run's staging step.
  *Deps:* 1–7 · *Effort:* XS · *Value:* High

## Suggested order

G1, then 1: it removes the failure for every install with one packaging change. Then 2 → 3 → 4, the
slice that keeps memory working wherever the runtime still cannot load. Then 6 and 5. 7 is independent
and can land any time before 8.

## Not yet planned

- A guard in `AiClientService.DispatchToolCallsAsync` that answers any non-cancellation tool exception
  with an error result instead of failing the whole reply. It changes behavior for every tool, so it
  needs its own decision.
- An in-app notice when ONNX Runtime cannot load (speech settings, memory view). This is moot once step 1
  ships, unless policy or antivirus blocks the DLL.
- *Regenerate embeddings* in the vault view logs one warning per memory when embeddings are unavailable.
  It could stop at the first `EmbeddingUnavailableException`.
- A support probe script under `scripts/` that reports the runtime DLLs and the Win32 load error on a
  customer machine.
