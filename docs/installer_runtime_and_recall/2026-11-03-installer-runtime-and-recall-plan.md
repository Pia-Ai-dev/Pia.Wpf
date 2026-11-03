# VC++ runtime in the package, recall without embeddings, MSI persona art — plan

**Status:** Planned
**Owner:** Marco Altmann
**Written:** 2026-11-03
**Origin:** Support log `pia-2026-11-03.log` from a per-machine MSI install of 1.4.355. Assistant memory
and tool calls failed with `TypeInitializationException` for `Microsoft.ML.OnnxRuntime.NativeMethods`
→ `DllNotFoundException: Dll was not found.` The owner confirmed the cause the same day: installing the
Visual C++ x64 runtime on that machine fixed it. The MSI art item is the owner's observation from the
same day.

Tracking: [2026-11-03-installer-runtime-and-recall-checklist.md](2026-11-03-installer-runtime-and-recall-checklist.md).
Tick boxes there, not here.

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans.

**Goal:** the local embedding model and the speech engine load on a machine that has no Visual C++
runtime, memory recall and indexing keep working when they still cannot, and the MSI shows the current
persona.

**Architecture:** the four VC++ DLLs ONNX Runtime imports ship next to `Pia.Wpf.exe` in every package
Velopack builds, so both MSIs and every update carry them. Inside the app, `EmbeddingService` turns a
native load failure into one typed `EmbeddingUnavailableException`, latched for the process, and every
caller treats that as "no vector tier" instead of failing.

**Tech stack:** .NET 10 WPF single-file publish (`IncludeNativeLibrariesForSelfExtract=false`),
Microsoft.ML.OnnxRuntime 1.27.1, org.k2fsa.sherpa.onnx 1.13.5, vpk 1.2.158, SQLite FTS5, xunit v3.

## Findings

1. **The runtime is not shipped.** `onnxruntime.dll` 1.27.1 imports `MSVCP140.dll`, `MSVCP140_1.dll`
   (`_Aligned_get_default_resource`), `VCRUNTIME140.dll` and `VCRUNTIME140_1.dll`
   (`__CxxFrameHandler4`). The `_1` DLLs exist only with a Visual C++ 2019-or-newer x64 runtime. Neither
   `vpk pack` call passes `--framework`, and the publish output carries only WPF's private
   `vcruntime140_cor3.dll`. `sherpa-onnx-c-api.dll` imports `onnxruntime.dll`, so speech fails the same
   way. Both DLLs were linked by MSVC 14.44. The 1.4.355 full nupkg does contain Microsoft's 1.27.1
   `onnxruntime.dll`, byte-identical; only its dependencies are missing.
2. **The single-file host drops the Win32 reason.** ORT's own resolver returns `IntPtr.Zero` on failure;
   the runtime's fallback load then throws a parameterless `DllNotFoundException`. The log cannot tell a
   missing dependency (126) from a blocked one (5, 1260).
3. **Recall fails outright** (`MemoryService.cs:523`). The vector tier calls `GenerateEmbeddingAsync`
   unguarded, so the exception escapes the `recall` tool and `ChatSession` fails the whole reply, though
   the LIKE, FTS and fuzzy tiers already had hits.
4. **New memories are not indexed** (`VaultIndexer.cs:157`, `:178`). The embedding is computed before
   `UpsertChunkAsync` / `RefreshFtsAsync`, so a failure leaves neither a chunk nor an FTS row; text
   search cannot find the memory either.
5. **`remember` into a file that has sections throws** (`SectionUpsertService.cs:39`, `:45`) for the same
   reason. The log only shows the Create band, which embeds nothing.
6. **FTS prefix terms are unquoted** (`MemoryService.cs:1700`; `:316` and `:319` in
   `FullTextSearchAsync`). Any token with punctuation becomes invalid FTS5: `Marco,` → `Marco,*` →
   `fts5: syntax error near ","`. Reproduced; `"Marco,"*` parses and matches.
7. **Query text reaches the release log** (`MemoryService.cs:483`). The recall FTS warning logs the
   exception, whose message quotes the query fragment. The sibling at `:331` already logs only the code.
8. **The MSI art is stale.** `Resources/Installer/banner.bmp` (the `--msiDialogBackground`, 493×312)
   still shows the smooth persona. The low-poly `Resources/Images/Pia_Persona_edge.png` (636×909) only
   reached the wizard's welcome step. `logo.bmp` (the top banner) already carries the new wordmark.

## Decision gate G1 — how the runtime reaches the machine

**Recommended: app-local (Task 1).** Microsoft supports deploying the DLLs from Visual Studio's
`VC\Redist` folder next to the executable. They then travel inside the nupkg, so the per-machine MSI, the
per-user MSI (no elevation) and every existing install on its next silent update all get them, with no
prompt. Trade-off: Windows Update does not service app-local copies. Each release refreshes them from
the build machine's Visual Studio, and Task 1 enforces a version floor.

**Alternative: `vpk pack --framework vcredist145-x64`** (`vcredist143-x64` for the 2015–2022 line).
Velopack documents that Setup installs frameworks and that an update prompts the user for a missing one
before it applies. It documents nothing for the `--msi` output, which is all Pia ships. A prompt (plus
UAC for per-user installs) on managed machines is the failure mode this would add. Choose it only if a
spike shows the MSI installs the framework silently.

## Global constraints

- `0 Warning(s)` and `0 Error(s)` on `dotnet build -t:Rebuild` in Debug and Release.
- Test gate: the built `Pia.Wpf.Tests.exe`, no filter, `Failed: 0`.
- Logging: no query text, paths or payloads in plain `Log*` calls; those go through `SensitiveDebug`.
- Comment discipline from `CLAUDE.md`: one line, no history, no task IDs.
- Every binary in the package is signed by `neo42 GmbH`, `Microsoft Corporation` or `OpenJS
  Foundation` (`UpdateOptions.DefaultTrustedPublishers`, mirrored in `build-and-release.yml`).
- Runtime floor `14.44`, the MSVC version ORT 1.27.1 and sherpa 1.13.5 were linked with. Raise it when
  either package moves.
- Catch `EmbeddingUnavailableException` only. `OperationCanceledException` keeps propagating.
- No edits to `docs/release_notes/RELEASE.md` on this branch.

## Review Focus

1. **An older runtime already in System32** (this dev box has 14.40): the app-local copy must still be
   the one loaded, because the application directory is searched before System32. Task 8 checks the
   loaded module path.
2. **The runtime is installed while Pia runs:** the latched failure holds until restart, because a failed
   type initializer is permanent for the process. The Task 2 log line says so.
3. **A chunk indexed without an embedding** must not count as unchanged later, or it never gets a vector.
   Task 4 test 2.
4. **A query term containing `"`** must still give valid FTS after sub-token splitting: split the raw
   term, then escape each piece. Task 6 theory.
5. **A random stub vector clearing the 0.2 cosine threshold** can mask a broken FTS tier in a test. Tests
   that prove a text tier run over `UnavailableEmbeddingService`.

## Tasks

### Task 1: Stage the VC++ runtime in every package

**Files:**
- Create: `scripts/Copy-VcRuntime.ps1`
- Modify: `.github/workflows/build-and-release.yml`. New step between "Publish self-contained" and
  "Sign published Pia.Wpf.exe".
- Modify: `scripts/build-velopack.ps1`, after its `dotnet publish`.
- Test: `tests/Pia.Wpf.Tests/Services/Assets/VcRuntimeStagingTests.cs`

**Script contract:** `Copy-VcRuntime.ps1 -Destination <dir> [-MinimumVersion 14.44]`.
- Locate: `vswhere.exe -all -products * -find 'VC\Redist\MSVC\*\x64\Microsoft.VC143.CRT\vcruntime140_1.dll'`.
  Take the highest `MSVC\<version>` folder across all instances.
- The search is pinned to `VC143`, the VS 2022 runtime, on purpose. The VS 2026 runtime (`VC145`, 14.5x)
  has a higher minimum Windows version than Pia's `windows10.0.17763.0` target. A dev box with VS 2026
  installed must not pick it up silently. When ORT or sherpa move to a 14.5x toolset, the floor rises
  past what `VC143` offers and the script fails. That failure is the prompt to re-check the OS floor.
- Copy `msvcp140.dll`, `msvcp140_1.dll`, `vcruntime140.dll` and `vcruntime140_1.dll` into
  `-Destination`. Print the source folder and the four file versions.
- `Write-Error` and exit non-zero when:
  - nothing is found (name the Visual Studio C++ x64 redistributable component in the message);
  - any file's `FileVersion` is below the floor;
  - `Get-AuthenticodeSignature` is not `Valid` with signer `O=Microsoft Corporation`.

A dispatched run of this branch cannot prove the step. "Azure login (OIDC)" runs before "Publish
self-contained", and its trust is main-only, so the run stops before publishing. The evidence before
merge is the runner image instead. `windows-latest` (Windows 2025) carries VS 2022 Enterprise 17.14 with
`Microsoft.VisualStudio.Component.VC.Redist.14.Latest`, that is a 14.44 `Microsoft.VC143.CRT` folder,
per `actions/runner-images` `Windows2025-Readme.md`. The first real run is the release run on `main`.
The script fails before `vpk pack`, so the worst case is a blocked release, never a package without the
runtime.

- [ ] **Step 1: Write the failing tests.** Locate the repo root as `RuntimeAssetCatalogTests` does.
  - `Release_workflow_stages_vc_runtime_after_publish_and_before_signing`: in
    `build-and-release.yml`, the index of `scripts/Copy-VcRuntime.ps1 -Destination publish` is greater
    than that of `dotnet publish src/Pia.Wpf/Pia.Wpf.csproj`, and less than both
    `Sign published Pia.Wpf.exe` and the first `vpk pack`.
  - `Local_velopack_build_stages_vc_runtime_before_packing`: in `build-velopack.ps1`, the
    `Copy-VcRuntime.ps1` call sits between `dotnet publish` and `vpk pack`.
- [ ] **Step 2:** `Pia.Wpf.Tests.exe -class "Pia.Tests.Services.Assets.VcRuntimeStagingTests"`. Expected:
  both fail.
- [ ] **Step 3:** write the script and both call sites.
- [ ] **Step 4:** rerun Step 2; expected: both pass. Then run the script into a temp directory. It must
  either copy four DLLs ≥ 14.44, or fail naming the component. This dev box's VS 2022 has
  `VC\Redist\MSVC\14.44.35112` but no x64 CRT folder: add the component and rerun.
- [ ] **Step 5:** commit.

### Task 2: Typed, latched embedding unavailability

**Files:**
- Create: `src/Pia.Wpf/Services/Exceptions/EmbeddingUnavailableException.cs` (namespace
  `Pia.Services.Exceptions`, which `NamingConventionTests` exempts from the service-suffix rule)
- Create: `src/Pia.Wpf/Services/NativeLoadProbe.cs`
- Modify: `src/Pia.Wpf/Services/EmbeddingService.cs`
- Test: `tests/Pia.Wpf.Tests/Services/EmbeddingServiceRuntimeFailureTests.cs`

**Interfaces (produced):**
- `public sealed class EmbeddingUnavailableException : InvalidOperationException`, with ctor
  `(string message, Exception? innerException = null)`.
- `internal static partial class NativeLoadProbe` with `internal static int? GetLoadError(string dllPath)`.
  - It calls `LoadLibraryExW(path, 0, LOAD_WITH_ALTERED_SEARCH_PATH)` through
    `[LibraryImport(..., SetLastError = true)]`.
  - On success it frees the handle and returns `null`; otherwise it returns
    `Marshal.GetLastPInvokeError()`.
- `internal EmbeddingService(ILogger<EmbeddingService> logger, IAssetDownloader downloader, string modelDirectory, Func<string, InferenceSession>? sessionFactory = null)`.
  - The public ctor chains to it with `Path.Combine(PiaPaths.ModelsDirectory, "Embeddings")`.
  - The default factory creates its own `SessionOptions` (`ORT_ENABLE_ALL`) and the session. In
    production the throw comes from `new SessionOptions()` itself, so the factory call is what the latch
    wraps. Never build options outside the factory.

**Behavior:**
- `GenerateEmbeddingAsync` throws `EmbeddingUnavailableException` when the model cannot be downloaded
  (today a plain `InvalidOperationException`) and when the runtime failed to load.
- `EnsureModelLoaded` order:
  1. Call the session factory first, into a local.
  2. Load the vocabulary and the tokenizer.
  3. Assign `_session` last. Dispose the local if step 2 throws.
- A `TypeInitializationException` or `DllNotFoundException` from step 1 is stored in a field. That call
  and every later one throw `EmbeddingUnavailableException(..., stored)` without touching ONNX again.
- The latch logs once, at `LogError`: the Win32 code from
  `NativeLoadProbe.GetLoadError(Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll"))`, and that
  semantic recall and speech stay off until Pia restarts. No paths in the message.

- [ ] **Step 1: Write the failing tests.**
  - Fixture: a temp model directory holding three empty files named after `ModelFileName`,
    `TokenizerFileName` and `SentencePieceFileName`. A factory that throws
    `new TypeInitializationException("Microsoft.ML.OnnxRuntime.NativeMethods", new DllNotFoundException())`
    and counts its calls.
  - `Runtime_load_failure_surfaces_as_EmbeddingUnavailable`:
    `Assert.ThrowsAsync<EmbeddingUnavailableException>`, and `InnerException` is
    `TypeInitializationException`.
  - `Runtime_load_failure_is_not_retried`: two calls, both throw, and the factory ran once.
  - `NativeLoadProbe_reports_a_missing_file`: `GetLoadError(<temp>\absent.dll) == 126`.
- [ ] **Step 2:** run the class. Expected: compile failure, because the types are missing.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run the new class plus `EmbeddingServiceEnsureAvailableTests` and
  `EmbeddingServiceSemanticTests`, one `-class` per run. Expected: pass.
- [ ] **Step 5:** commit.

### Task 3: Recall answers without the vector tier

**Files:**
- Modify: `src/Pia.Wpf/Services/MemoryService.cs`. `RecallAsync` tier 3 at `:522-534`, FTS catch at
  `:480-484`.
- Create: `tests/Pia.Wpf.Tests/TestInfrastructure/UnavailableEmbeddingService.cs`.
  - It implements `IEmbeddingService` with `IsModelAvailable == true`.
  - `GenerateEmbeddingAsync` throws `EmbeddingUnavailableException`.
  - Byte conversion is as in `StubEmbeddingService`.
- Test: `tests/Pia.Wpf.Tests/Vault/RecallTests.cs`

**Interfaces:** consumes `EmbeddingUnavailableException` (Task 2).

**Behavior:**
- Catch `EmbeddingUnavailableException` around the query embedding only. On it, skip tier 3 and
  `LogWarning("Vault recall ran without the vector tier: embeddings unavailable")`.
- The FTS catch becomes `LogWarning("Vault FTS recall failed (SQLite error {Code}); falling back to
  other tiers", ex.SqliteErrorCode)`, plus `SensitiveDebug` carrying the message, as at `:331`.

- [ ] **Step 1: Write the failing test.** `Recall_returns_text_hits_when_embeddings_are_unavailable`:
  - Index a file with heading `Coffee preferences` using the normal stub.
  - Build `MemoryService` over `UnavailableEmbeddingService`.
  - `RecallAsync("Coffee")` returns at least one hit whose `Heading == "Coffee preferences"`.
- [ ] **Step 2:** `-class "Pia.Tests.Vault.RecallTests"`. Expected: the new test fails with
  `EmbeddingUnavailableException`.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** rerun Step 2. Expected: pass.
- [ ] **Step 5:** commit.

### Task 4: Index text even when the embedding fails, embed it later

**Files:**
- Modify: `src/Pia.Wpf/Services/VaultIndexer.cs`.
  - `IndexFileAsync` at `:157` and `:178`.
  - `UpsertChunkAsync` at `:228` takes `byte[]? embeddingBytes` and writes `DBNull.Value` for null.
  - `IsContentHashUnchangedAsync` at `:217`.
- Test: `tests/Pia.Wpf.Tests/Vault/VaultIndexerTests.cs`

**Behavior:**
- Per section and for the preamble chunk, try the embedding. On `EmbeddingUnavailableException`, upsert
  the chunk with a null embedding and still refresh its FTS row.
- `IsContentHashUnchangedAsync` is true only when the hash matches **and** `Embedding IS NOT NULL`.

- [ ] **Step 1: Write the failing tests.**
  - `Indexing_without_embeddings_still_writes_chunk_and_fts_row`: index a one-section file whose body
    contains `espresso`, over `UnavailableEmbeddingService`.
    - Its `Chunks` row exists with `Embedding IS NULL`.
    - `SELECT rowid FROM ChunksFts WHERE ChunksFts MATCH '"espresso"'` returns one row.
  - `Chunk_indexed_without_embedding_is_embedded_once_available`: then reindex the unchanged file with
    the counting stub. Embed count is 1, and `Embedding` is not null.
- [ ] **Step 2:** `-class "Pia.Tests.Vault.VaultIndexerTests"`. Expected: both new tests fail.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** rerun Step 2 and `RecallTests`. Expected: pass, including the existing
  `Reindexing_unchanged_file_does_not_reembed` and `Reconcile_over_unchanged_vault_does_not_reembed`.
- [ ] **Step 5:** commit.

### Task 5: `remember` and wiki lint fall back to lexical scoring

**Files:**
- Modify: `src/Pia.Wpf/Services/SectionUpsertService.cs:39-49`
- Modify: `src/Pia.Wpf/Services/Wiki/LintService.cs:490` (`GetEmbeddingAsync`)
- Test: `tests/Pia.Wpf.Tests/Vault/SectionUpsertServiceTests.cs`, `tests/Pia.Wpf.Tests/Wiki/LintServiceTests.cs`

**Behavior:**
- `ResolveAsync`: when the subject embedding throws `EmbeddingUnavailableException`, score each section
  by `JaroWinkler.Similarity(subject, heading)` alone, with the same thresholds (Edit 0.85, Ambiguous
  0.60).
- Lint: `GetEmbeddingAsync` returns `null` on `EmbeddingUnavailableException`; its caller already skips
  null.

- [ ] **Step 1: Write the failing tests.**
  - `Resolve_uses_lexical_score_when_embeddings_are_unavailable`: a document with sections
    `Coffee preferences` and `Travel`. `ResolveAsync(doc, "Coffee preferences", "oat milk")` over
    `UnavailableEmbeddingService` returns `UpsertBand.Edit` with the `Coffee preferences` slug.
  - `Lint_completes_when_embeddings_are_unavailable`: a vault with one topic page that has no `Chunks`
    row, linted over `UnavailableEmbeddingService`. It completes without throwing.
- [ ] **Step 2:** run both classes. Expected: both new tests fail.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** rerun. Expected: pass.
- [ ] **Step 5:** commit.

### Task 6: Quote FTS prefix terms

**Files:**
- Modify: `src/Pia.Wpf/Services/MemoryService.cs`. `BuildFtsQuery` at `:1690`, `FullTextSearchAsync` at
  `:308-321`.
- Test: `tests/Pia.Wpf.Tests/Vault/FtsQueryTests.cs` (new) and `tests/Pia.Wpf.Tests/Vault/RecallTests.cs`

**Interfaces (produced):** `internal static string BuildFtsQuery(string query, bool includeSubTokenPrefixes = false)`.
- Every term gives `"t"`, and also `"t"*` when it is at least 3 characters long.
- With `includeSubTokenPrefixes`, sub-token prefixes are cut from the **raw** term, then escaped, then
  emitted as `"p"*`.
- `FullTextSearchAsync` passes `true`; `RecallAsync` keeps the default.

- [ ] **Step 1: Write the failing tests.**
  - `BuildFtsQuery_quotes_every_prefix_term`: `BuildFtsQuery("Marco, neo42")` ==
    `"Marco," OR "Marco,"* OR "neo42" OR "neo42"*`.
  - `BuildFtsQuery_output_is_valid_fts5`, a theory over:
    - `Marco,`
    - `foo-bar`
    - `say "hi"`
    - `Kaffeemaschinen-Einstellung`
    - `ab"cdefghij`

    With `includeSubTokenPrefixes: true`, a `MATCH` against an in-memory FTS5 table, opened through the
    same SQLite provider the app uses, throws no `SqliteException`.
  - In `RecallTests`, `Recall_with_punctuated_query_matches_body_text_via_fts`:
    - Section heading `Drinks`, body `Marco likes espresso`.
    - Over `UnavailableEmbeddingService`, `RecallAsync("espresso,")` returns that section. LIKE and
      fuzzy match headings only, so FTS is the only tier that can find it.
- [ ] **Step 2:** run `FtsQueryTests`, then `RecallTests`. Expected: the new tests fail.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** rerun both. Expected: pass.
- [ ] **Step 5:** commit.

### Task 7: MSI dialog background with the edge persona

**Files:** `src/Pia.Wpf/Resources/Installer/banner.bmp`

**Geometry:**
- 493×312 pixels, 24-bpp RGB, white background.
- `Pia_Persona_edge.png` is alpha-composited onto it, scaled bicubic to 141×202 at (8, 49).
- The old persona's bounding box is x 4–153, y 49–250. The new art therefore keeps its height and centre,
  and the dialog's text column stays white.
- Use a one-off `System.Drawing` snippet and record the geometry in the commit message.

- [ ] **Step 1:** generate the bitmap and view it next to the old one.
- [ ] **Step 2:** build both MSIs locally with `scripts/build-velopack.ps1`; this needs Task 1's
  component. Run `msiexec /i` on the per-user MSI, then screenshot the welcome dialog: the art must not
  clip the dialog text. Cancel.
- [ ] **Step 3:** commit.

### Task 8: Branch verification

- [ ] Rebuild Debug and Release (`dotnet build -t:Rebuild -v:n`, then again with `-c Release`).
  Expected: `0 Warning(s)`, `0 Error(s)`.
- [ ] Test gate: `Failed: 0`.
- [ ] Check the nupkg from `build-velopack.ps1`: its `lib/app/` holds the four runtime DLLs, each
  ≥ 14.44 and signed by Microsoft.
- [ ] Install on a machine where `Test-Path $env:WINDIR\System32\vcruntime140_1.dll` is `False` (a clean
  Windows VM). Then confirm:
  - recall works;
  - the log has `Embedding model loaded successfully`;
  - `(Get-Process Pia.Wpf).Modules | ? ModuleName -like 'vcruntime140*'` resolves under `current\`.

  Repeat the module check on this dev box, whose System32 runtime is 14.40 (Review Focus 1).
- [ ] After the release PR merges, confirm the `main` run's staging step printed four 14.44+ versions
  from a `Microsoft.VC143.CRT` folder. A dispatched branch run cannot reach that step (see Task 1).
- [ ] Open the PR through `release-flow pr`. The body states that no client→server call changed.
