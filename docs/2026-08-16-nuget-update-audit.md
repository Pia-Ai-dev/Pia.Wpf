# NuGet update audit — 2026-08-16

Source data: `dotnet list package --outdated` (+ `--include-prerelease`), `--vulnerable --include-transitive`,
`--deprecated`, all three projects.

**Vulnerable packages: none** — and, unlike before, none would appear without the `SQLitePCLRaw` pin either.
A throwaway project referencing only `Microsoft.Data.Sqlite 10.0.11` probes clean, so the 2.1.12 it now resolves
already carries the GHSA-2m69-gcr7-jv3q fix. See #3.

**Deprecated packages: one.** `WPF-UI 4.2.0` is flagged `CriticalBugs` on nuget.org. `WPF-UI.Tray 4.2.0` is not.

Baseline before any change: Debug rebuild `0 Warning(s) / 0 Error(s)`, `dotnet test` → total 4085, failed 0,
skipped 53. (`AssistantChatConcurrencyTests.DeleteAllAsync_WithAnotherConnectionCommittingThroughout_Completes`
is flaky under full-suite SQLite contention — it failed once and passed on re-run of both the class and the full
suite.) Release was rebuilt after the changes, which is what the zero-warning policy actually asks for.

---

## List A — priority, highly recommended → not required

| # | Package(s) | Current → Latest | Why |
|---|---|---|---|
| 1 | **WPF-UI**, **WPF-UI.Tray** | 4.2.0 → 4.3.0 | **The only vendor-declared problem in the tree.** 4.2.0 is deprecated `CriticalBugs`. 4.2.1 and 4.3.0 are both clean, so 4.2.1 is the minimal escape and 4.3.0 the full one. |
| 2 | **Microsoft.Extensions.{DependencyInjection,Hosting,Http,Logging,Logging.Debug}**, **Microsoft.Data.Sqlite** | 10.0.9 → 10.0.11 | First-party .NET 10 servicing band. Fixes only, no API surface change. Staying on the current servicing patch is the cheapest security posture you get. |
| 3 | **SQLitePCLRaw.bundle_e_sqlite3** | 3.0.3 → 3.0.5 | This is the native SQLite the app actually runs on. **The pin's original justification has lapsed** — it was there to override a vulnerable transitive 2.1.11, and `Microsoft.Data.Sqlite 10.0.11` now resolves a patched 2.1.12. Dropping the pin is therefore a valid option; it was kept because 3.0.5 is the newer native line, and the csproj comment was rewritten to say that instead of citing the dead advisory. |
| 4 | **Microsoft.Extensions.AI**, **Microsoft.Extensions.AI.OpenAI** | 10.6.0 → 10.9.0 | The abstraction the entire chat/agent/provider stack sits on (~60 files). Fast-moving; three minors of drift compounds, and later `Microsoft.Agents.AI` / MCP bumps will want it current. |
| 5 | **Microsoft.Agents.AI** | 1.15.0 → 1.17.0 | Version-coupled to #4. Single consumer (`AgentContextCompactor`). |
| 6 | **Nerdbank.GitVersioning** | 3.10.85 → 3.10.91 | Build-only patch. No runtime surface. |
| 7 | **NReco.Logging.File** | 1.3.1 → 1.4.0 | One consumption point (`Bootstrapper`), but log-file behaviour is user-visible — support attachments come from this sink. |
| 8 | **PiperSharp** | 1.0.6 → 1.0.7 | Patch, one file (TTS). |
| 9 | **SharpCompress** | 0.48.0 → 0.50.4 | One consumer (model-archive extraction). |
| 10 | **ModelContextProtocol** | 1.2.0 → 2.2.0 | Worth having for spec coverage eventually; nothing is broken today. One consumer file. |
| 11 | **Microsoft.Playwright** | 1.61.0 → 1.62.0 | Meeting-attendee only. No pull unless a Teams DOM/driver issue forces it. |
| 12 | **Microsoft.ML.OnnxRuntime** · **org.k2fsa.sherpa.onnx** | 1.24.4 → 1.29.0 · 1.12.40 → 1.13.5 | No functional gain unless newer models are wanted. Pure downside risk otherwise. |
| 13 | **NAudio** | 2.3.0 → 3.0.0 | Current version works. A major bump across the whole capture stack buys nothing today. |
| 14 | **YamlDotNet** | `16.*` → 18.1.0 | The *version bump* is not required. The **floating `16.*` wildcard is worth fixing regardless** — it makes restores non-reproducible; pin it to `16.3.0`. |
| 15 | **Velopack** | 0.0.1589-ga2c5a97 → 1.2.0 (latest stable; 1.2.110-ge826545 is newer but prerelease) | Genuinely wanted (you are on a *prerelease* of the updater; it has since gone 1.x) but see List B #17 — it cannot be verified from a build. |
| 16 | **xunit.v3** + **Microsoft.Testing.Extensions.CodeCoverage** + **NSubstitute** | 3.2.2 → 4.0.0 · 18.0.6 → 18.10.0 · 5.3.0 → 6.2.0 | CLAUDE.md's pin note says "revisit when xunit.v3 4.0 ships" — it has. But this is a session of its own, not a line item. See List B #16. |

Prerelease-only "updates" deliberately excluded: `Azure.AI.OpenAI 2.9.0-beta.1`, `Microsoft.ML.Tokenizers 3.0.0-preview`,
`Microsoft.Extensions.* 11.0.0-preview`, `Microsoft.Data.Sqlite 11.0.0-preview`, `Nerdbank.GitVersioning 3.11.93-beta`.
Also note the repo already carries one prerelease pin by choice: `MdXaml 2.0.0-pre202603081301` (no stable 2.x exists).

---

## List B — effort, easy → unknown pitfalls

Everything here is read through one repo-specific lens: `Directory.Build.props` sets
`TreatWarningsAsErrors=true`, so **any newly `[Obsolete]`-marked API turns a package bump into a build failure**,
and WPF re-runs `src/` warnings through the XAML markup-compile pass.

| # | Package(s) | Risk read |
|---|---|---|
| 1 | Nerdbank.GitVersioning 3.10.91 | Build-only patch. Nothing to break but the build, which tells you immediately. |
| 2 | Microsoft.Extensions.* + Microsoft.Data.Sqlite 10.0.11 | Same-band servicing patch. Must move as **one set** — a split 10.0.9/10.0.11 graph risks NU1605, which is an error here. |
| 3 | SQLitePCLRaw.bundle_e_sqlite3 3.0.5 | Native-only patch, no managed API. |
| 4 | PiperSharp 1.0.7 | One file. |
| 5 | Microsoft.Playwright 1.62.0 | Compiles trivially. The catch is runtime: the driver expects a **matching browser binary**, so it needs a re-install of browsers, and the only consumer is a path no test exercises. |
| 6 | NReco.Logging.File 1.4.0 | Small API. Watch rolling/flush behaviour — the file-sink test has to wait on the background writer. |
| 7 | Microsoft.Extensions.AI (+ .OpenAI) 10.9.0 | Mechanically a minor bump, but ~60 files and a library that deprecates aggressively. Under `TreatWarningsAsErrors` one new `[Obsolete]` on `ChatMessage`/`ChatOptions`/`ChatResponseUpdate` fails the build. Well-covered by tests, so failures surface fast. |
| 8 | Microsoft.Agents.AI 1.17.0 | Same shape as #7, far smaller footprint; may force #7 transitively. |
| 9 | SharpCompress 0.50.4 | `0.x` minor — semver allows breaking changes. Only one consumer, so cheap to fix if it breaks. |
| 10 | **WPF-UI + WPF-UI.Tray 4.3.0** | Minor by number, wide by surface: **95 XAML files / 1072 references** plus 35 `.cs` files. Markup-compile catches renamed types but **not** missing `StaticResource` keys — those fail at view load. Partly covered here: `WpfStaHost` builds the real `Application` and loads App.xaml's merged Wpf.Ui dictionaries, so the view-parse tests do exercise theme-key resolution. Residual risk is visual-only, plus the views that host can't reach. 4.2.1 exists as a lower-risk escape from the deprecation if 4.3.0 misbehaves. |
| 11 | ModelContextProtocol 2.2.0 | Major bump; only `McpPluginToolHandler` consumes it. Compile break is likely, contained, and obvious. Runtime protocol-negotiation changes against real MCP servers are the unknown. |
| 12 | YamlDotNet 18.1.0 | Two majors. `MarkdownVaultParser` round-trips vault frontmatter, and the vault tests are byte-sensitive (CRLF). Serializer default changes here silently rewrite user files. |
| 13 | Microsoft.ML.OnnxRuntime 1.29.0 | Native runtime. Fails at **model load**, not at compile — a green build proves nothing. `EmbeddingService` already has a history of tokenizer/model mismatches producing garbage rather than errors. |
| 14 | org.k2fsa.sherpa.onnx 1.13.5 | Same class of risk, plus a **model-format** dimension: the downloaded Whisper/Parakeet/speaker-embedding models must still be accepted by the new native lib. Verifiable only in a live transcription run. |
| 15 | NAudio 3.0.0 | Major, across 10 files covering WASAPI loopback, per-process loopback, mic capture and resampling. Device- and OS-dependent; a green suite says almost nothing about whether audio still captures. |
| 16 | xunit.v3 4.0.0 (+ CodeCoverage 18.10.0, NSubstitute 6.2.0) | **Takes out the verification gate itself.** CLAUDE.md documents the exact failure: CodeCoverage 18.1.0+ wants `Microsoft.Testing.Platform` 2.x, xunit.v3 3.x is on 1.9.x, and the unification kills *every* `dotnet test` with `TypeLoadException`. Three-way coupled change over 167 test files. Own session, own branch. |
| 17 | Velopack 1.2.0 | **Highest blast radius in the list.** It is the auto-updater: a break does not show up locally, it shows up as shipped clients that can no longer update themselves. Going prerelease → 1.x across a `0.0.x` → `1.x` boundary means the packaging/CLI contract likely moved too. Only a real release round-trip verifies it. |

---

## Applied in this pass

Top of List A (#1) and top of List B (#1–#3):

- `WPF-UI` 4.2.0 → 4.3.0, `WPF-UI.Tray` 4.2.0 → 4.3.0 — clears the only deprecation.
- `Microsoft.Extensions.DependencyInjection` / `.Hosting` / `.Http` / `.Logging` / `.Logging.Debug` and
  `Microsoft.Data.Sqlite` 10.0.9 → 10.0.11 — moved as one set.
- `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 → 3.0.5, pin retained.
- `Nerdbank.GitVersioning` 3.10.85 → 3.10.91.

Left alone deliberately: everything in List B #7 and below, and the `YamlDotNet` wildcard (flagged above, not
changed — pinning it is a separate call).

**Still needs a human.** The gate covers more of the WPF-UI bump than a UI-library bump usually gets — `WpfStaHost`
constructs the real `Application`, so App.xaml's merged Wpf.Ui dictionaries load and the view-parse tests would
catch a theme key that disappeared in 4.3.0. What it does not cover, in priority order:

1. **`FirstRunWizardWindow`** — the one view the parse host can't load (pack-URI construction), and a heavy WPF-UI
   consumer: 17 XAML references plus two `using Wpf.Ui` lines in code-behind.
2. **Tray icon** — `WPF-UI.Tray` moved too, `TrayIconService` is its only consumer, and no test touches it.
3. **Visual regression generally** — shell chrome, navigation sidebar, snackbars/flow toasts, and at least one
   `ContentDialog` (provider edit / todo edit). A resolved resource key is not the same as a correct layout.
