# Credit status card on the Account page: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status:** Planned. Blocked on the server endpoint: `GET /api/ai/credits` must be on the target server
before Task 6's live check (the unit tests do not need it).
**Owner:** Marco Altmann
**Written:** 2026-09-26
**Origin:** Owner request, 2026-09-26: clients should see their credit usage, the free pools and the top-ups
left, shown only when the group does not set every credit setting to 0. Phase 1 (server) is Azure DevOps
work item #7654 in the Pia repo, planned in `docs/plans/2026-09-26-client-credit-status.md` there. This doc is
phase 2.
**Checklist:** [2026-09-26-client-credit-status-checklist.md](2026-09-26-client-credit-status-checklist.md)

**Goal:** A signed-in user whose group has any credit limit sees a *Credits* card on Settings → Account with
their usage per window, the group's free weekly pool and the group's remaining top-up credits.

**Architecture:** A small `CreditStatusService` reads `GET /api/ai/credits` in the style of
`AssignmentApiClient` and answers null for every "nothing to show" case. A pure `CreditMeterBuilder` turns
the response into display rows, and `AccountSettingsViewModel` fetches on each Settings visit and on sign-in.
`AccountView.xaml` renders the rows in a `PiaSettingsCardStyle` card, hidden unless there is something to show.

**Tech Stack:** WPF, CommunityToolkit.Mvvm, `IHttpClientFactory` + `System.Net.Http.Json`, xunit.v3 +
NSubstitute. Tests run from the built exe (CLAUDE.md, *Test Gate*).

## What the user sees

```
Credits
┌──────────────────────────────────────────────────────────────┐
│ This week                                   620 of 1,000 used │
│ ██████████████░░░░░░░░   Resets 28.09.2026 00:00              │
│ Free team pool this week                   1,800 of 3,000 left│
│ ████████░░░░░░░░░░░░░░   Resets 28.09.2026 00:00              │
│ Top-up credits                           16,700 of 25,000 left│
│ ███████░░░░░░░░░░░░░░░                                        │
│ Last 24 hours                                 41 of 200 used  │
│ ████░░░░░░░░░░░░░░░░░░                                        │
│ Last hour                                       3 of 50 used  │
│ █░░░░░░░░░░░░░░░░░░░░░                                        │
└──────────────────────────────────────────────────────────────┘
```

A bar always fills with consumption. The caption says *used* for a limit and *left* for a pool or a top-up,
because "left" is what a pool is for.

## The server contract (copied from the Pia plan, so this doc stands alone)

`GET {ServerUrl}/api/ai/credits`, `Authorization: Bearer <access token>`. `200`, whole credits. The server
drops nulls, so a section that does not apply is **absent, not zero**.

```json
{ "limited": false }
```

```json
{
  "limited": true,
  "suspended": true,
  "hourly": { "limit": 50, "used": 3 },
  "daily":  { "limit": 200, "used": 41 },
  "weekly": { "limit": 1000, "used": 620, "resetsAt": "2026-09-27T22:00:00Z" },
  "pool":   { "total": 3000, "used": 1200, "remaining": 1800, "resetsAt": "2026-09-27T22:00:00Z" },
  "topUp":  { "granted": 25000, "consumed": 8300, "remaining": 16700, "isActive": true },
  "groupCap": { "daily":  { "limit": 5000, "used": 900 },
                "weekly": { "limit": 20000, "used": 7400, "resetsAt": "2026-09-27T22:00:00Z" } }
}
```

| Key | Present when |
|---|---|
| `limited` | always; false when the group sets hourly, daily, weekly, group-daily and group-weekly all to 0 |
| `suspended` | only when true: the server's free tier is paused, and every request is refused |
| `hourly`, `daily` | that per-member limit is above 0; rolling windows, so no `resetsAt` |
| `weekly` | the weekly allowance is above 0; `used` is the member's **own** use, pool and top-up draws excluded |
| `pool` | the group has a free weekly pool this week (last week's unused allowance, shared first-come) |
| `topUp` | the group has ever bought top-up credits; drawn only after the member's own weekly allowance is gone |
| `groupCap.daily`, `groupCap.weekly` | the group caps what the whole team may spend |

`resetsAt` is a UTC instant. `401` means the token is bad; `403 feature_not_licensed` means the server has no
AI proxy; `404` means the server predates the endpoint.

## Decisions

**C1: Placement.** One card on Settings → Account, directly below the signed-in block (`Sync_Account`) and
inside the same `IsSyncLoggedIn` panel. No status-bar or chat-view indicator; that is listed under
*not yet planned* in the checklist.

**C2: When to fetch.** On every `AccountSettingsViewModel.InitializeAsync` (Settings is re-initialized on each
visit) and when `LoginStateChanged` raises `true`. Off the initialization path, fire-and-forget, like
`RefreshBusinessProfileStateAsync`. Sign-out clears the card. No timer.

**C3: Hidden unless there is something to show.** The card appears only for a `200` with `limited: true`.
Signed out, no server URL, `401`/`403`/`404`/`5xx`, a network error or unreadable JSON all hide it, and are
logged, not surfaced. A credit view is informational; it must never raise a dialog.

**C4: Client-local DTOs.** The records live in `src/Pia.Wpf/Services/Credits/`, not in `Pia.Shared`. The server
keeps its own records (the same way `/api/group/team` does), so a shared copy would be a second definition
nobody compiles against. Lift them into `Pia.Shared` only if a second consumer appears.

**C5: Formatting.** Whole credits with `N0` in the current UI culture. `resetsAt` shown in local time with the
culture's short date and time (`"g"`). Rolling windows are labelled "Last hour" / "Last 24 hours", never
"today", because they are not calendar days.

**C6: Suspended.** When `suspended` is true the card shows one warning line above the meters, and the meters
stay visible.

## Global Constraints

- Zero warnings in Debug **and** Release, checked with `-t:Rebuild` (CLAUDE.md, *Zero-Warning Policy*).
- Tests run from the exe: `tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe > test.log 2>&1`, then `grep -E "TEST EXECUTION SUMMARY" -A 2 test.log`. Never pipe a test run.
- Namespaces start with `Pia.`, never `Pia.Wpf.`. 4-space C#, 2-space XAML. `_camelCase` fields.
- No comments by default; one line WHY at most; no history, no ticket IDs (CLAUDE.md, comments section).
- Server URLs in logs go through `SafeUrl.Format`; response bodies only through `SensitiveDebug`.
- Every string in `ViewStrings.resx`, `ViewStrings.de.resx` and `ViewStrings.fr.resx`, same key in all three.
- Feature branch `feature/credit-status`; never push `main` without the `help-corpus` skill (a push to `main`
  cuts a release).

## Review Focus

1. **An older server (404) or one without the AI proxy (403):** the card must stay hidden with no dialog,
   snackbar or exception (Task 1 tests).
2. **Sign-out while the card is shown, then sign-in as a user in an unlimited group:** the previous account's
   figures must not survive (Task 3, `SigningOut_ClearsTheCard`, `ALaterUnlimitedAnswer_HidesTheCard`).
3. **A member living entirely on the pool:** `weekly.used` is own use, so "This week" can read "0 of 1,000
   used" while the pool bar moves. The pool row must render, or the user thinks nothing is being counted
   (Task 2, `Build_FullResponse_OrdersAndCaptionsEveryRow`).
4. **`limit` of 0 can never arrive for a present window**, but a `total` of 0 can (a frozen pool with nothing
   unused). A zero maximum must not divide by zero or render a full bar (Task 2, `Build_EmptyPool_…`).
5. **A culture switch to German or French** while Settings is open: captions are built at fetch time, so the
   next visit shows the new language. Accepted, not tested; call it out in the PR.

---

## File structure

| File | Change | Responsibility |
|---|---|---|
| `src/Pia.Wpf/Services/Credits/CreditStatusResponse.cs` | create | wire DTOs |
| `src/Pia.Wpf/Services/Credits/ICreditStatusService.cs`, `CreditStatusService.cs` | create | the HTTP read |
| `src/Pia.Wpf/ViewModels/CreditMeter.cs` | create | display row + `CreditMeterBuilder` |
| `src/Pia.Wpf/ViewModels/AccountSettingsViewModel.cs` | modify | fetch, hold, clear |
| `src/Pia.Wpf/ViewModels/SettingsViewModel.cs` | modify | pass the service through |
| `src/Pia.Wpf/Bootstrapper.cs` | modify | DI registration |
| `src/Pia.Wpf/Views/SettingsViews/AccountView.xaml` | modify | the card |
| `src/Pia.Wpf/Resources/Strings/ViewStrings{,.de,.fr}.resx` | modify | labels |
| `tests/Pia.Wpf.Tests/Services/CreditStatusServiceTests.cs` | create | HTTP outcomes |
| `tests/Pia.Wpf.Tests/ViewModels/CreditMeterBuilderTests.cs` | create | rows, order, captions |
| `tests/Pia.Wpf.Tests/ViewModels/AccountSettingsCreditsTests.cs` | create | fetch/clear lifecycle |
| `tests/Pia.Wpf.Tests/ViewModels/AccountSettings{BusinessProfile,AccountData}Tests.cs`, `SettingsPolicyReloadTests.cs` | modify | new constructor argument |
| `docs/release_notes/RELEASE.md` | modify | user-facing note |

---

### Task 1: DTOs and `CreditStatusService`

**Files:**
- Create: `src/Pia.Wpf/Services/Credits/CreditStatusResponse.cs`, `ICreditStatusService.cs`, `CreditStatusService.cs`
- Modify: `src/Pia.Wpf/Bootstrapper.cs` (next to `IAssignmentApiClient`, around line 937)
- Test: `tests/Pia.Wpf.Tests/Services/CreditStatusServiceTests.cs`

**Interfaces:**
- Produces:
  - `record CreditStatusResponse(bool Limited, bool? Suspended, CreditWindowDto? Hourly, CreditWindowDto? Daily, CreditWindowDto? Weekly, CreditPoolDto? Pool, CreditTopUpDto? TopUp, CreditGroupCapDto? GroupCap)`
  - `record CreditWindowDto(long Limit, long Used, DateTime? ResetsAt)`
  - `record CreditPoolDto(long Total, long Used, long Remaining, DateTime ResetsAt)`
  - `record CreditTopUpDto(long Granted, long Consumed, long Remaining)`
  - `record CreditGroupCapDto(CreditWindowDto? Daily, CreditWindowDto? Weekly)`
  - `interface ICreditStatusService { Task<CreditStatusResponse?> GetAsync(CancellationToken ct = default); }`

- [x] **Step 1: DTOs** in `src/Pia.Wpf/Services/Credits/CreditStatusResponse.cs`

```csharp
namespace Pia.Services.Credits;

public sealed record CreditStatusResponse(
    bool Limited,
    bool? Suspended,
    CreditWindowDto? Hourly,
    CreditWindowDto? Daily,
    CreditWindowDto? Weekly,
    CreditPoolDto? Pool,
    CreditTopUpDto? TopUp,
    CreditGroupCapDto? GroupCap);

public sealed record CreditWindowDto(long Limit, long Used, DateTime? ResetsAt);

public sealed record CreditPoolDto(long Total, long Used, long Remaining, DateTime ResetsAt);

public sealed record CreditTopUpDto(long Granted, long Consumed, long Remaining);

public sealed record CreditGroupCapDto(CreditWindowDto? Daily, CreditWindowDto? Weekly);
```

`ICreditStatusService.cs`:

```csharp
namespace Pia.Services.Credits;

public interface ICreditStatusService
{
    Task<CreditStatusResponse?> GetAsync(CancellationToken ct = default);
}
```

- [x] **Step 2: Write the failing tests** in `tests/Pia.Wpf.Tests/Services/CreditStatusServiceTests.cs`

```csharp
namespace Pia.Tests.Services;

using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Credits;
using Pia.Services.Interfaces;
using Xunit;

public class CreditStatusServiceTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();

    public CreditStatusServiceTests()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings { ServerUrl = "https://pia.example/" });
        _auth.GetAccessTokenAsync().Returns("at");
    }

    [Fact]
    public async Task ALimitedAnswer_DeserializesEverySection()
    {
        var (sut, handler) = Create(HttpStatusCode.OK, """
            { "limited": true, "suspended": true,
              "hourly": { "limit": 50, "used": 3 },
              "weekly": { "limit": 1000, "used": 620, "resetsAt": "2026-09-27T22:00:00Z" },
              "pool": { "total": 3000, "used": 1200, "remaining": 1800, "resetsAt": "2026-09-27T22:00:00Z" },
              "topUp": { "granted": 25000, "consumed": 8300, "remaining": 16700, "isActive": true },
              "groupCap": { "weekly": { "limit": 20000, "used": 7400, "resetsAt": "2026-09-27T22:00:00Z" } } }
            """);

        var status = await sut.GetAsync();

        Assert.NotNull(status);
        Assert.True(status.Limited);
        Assert.True(status.Suspended);
        Assert.Equal(new CreditWindowDto(50, 3, null), status.Hourly);
        Assert.Null(status.Daily);
        Assert.Equal(new DateTime(2026, 9, 27, 22, 0, 0, DateTimeKind.Utc), status.Weekly!.ResetsAt);
        Assert.Equal(1800, status.Pool!.Remaining);
        Assert.Equal(new CreditTopUpDto(25000, 8300, 16700), status.TopUp);
        Assert.Null(status.GroupCap!.Daily);
        Assert.Equal("https://pia.example/api/ai/credits", handler.LastUri);
        Assert.Equal("Bearer at", handler.LastAuthorization);
    }

    [Fact]
    public async Task AnUnlimitedAnswer_IsReturnedAsSuch()
    {
        var (sut, _) = Create(HttpStatusCode.OK, """{ "limited": false }""");

        var status = await sut.GetAsync();

        Assert.NotNull(status);
        Assert.False(status.Limited);
        Assert.Null(status.Weekly);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ARefusalOrAnOlderServer_AnswersNull(HttpStatusCode status)
    {
        var (sut, _) = Create(status, """{ "error": "x" }""");

        Assert.Null(await sut.GetAsync());
    }

    [Fact]
    public async Task UnreadableJson_AnswersNull()
    {
        var (sut, _) = Create(HttpStatusCode.OK, "<html>");

        Assert.Null(await sut.GetAsync());
    }

    [Fact]
    public async Task ANetworkFailure_AnswersNull()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new ThrowingHandler(), disposeHandler: false));
        var sut = new CreditStatusService(_settings, _auth, factory, NullLogger<CreditStatusService>.Instance);

        Assert.Null(await sut.GetAsync());
    }

    [Fact]
    public async Task NoServerUrlOrNoToken_AnswersNullWithoutARequest()
    {
        var (sut, handler) = Create(HttpStatusCode.OK, """{ "limited": true }""");
        _auth.GetAccessTokenAsync().Returns((string?)null);

        Assert.Null(await sut.GetAsync());
        Assert.Null(handler.LastUri);
    }

    private (CreditStatusService Sut, ScriptedHandler Handler) Create(HttpStatusCode status, string body)
    {
        var handler = new ScriptedHandler(status, body);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return (new CreditStatusService(_settings, _auth, factory, NullLogger<CreditStatusService>.Instance), handler);
    }

    private sealed class ScriptedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastUri { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri!.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("unreachable");
    }
}
```

`AppSettings.ServerUrl` is a settable `string?`, and `IAuthService.GetAccessTokenAsync(bool forceRefresh = false,
string? staleAccessToken = null)` returns `Task<string?>`, so the parameterless setup matches the service's call.

- [x] **Step 3: Build and run to see them fail**

Run: `dotnet build tests/Pia.Wpf.Tests` then the exe with `-class "Pia.Tests.Services.CreditStatusServiceTests" > test.log 2>&1`
Expected: compile error, `CreditStatusService` does not exist.

- [x] **Step 4: Implement** `src/Pia.Wpf/Services/Credits/CreditStatusService.cs`

```csharp
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services.Credits;

public sealed class CreditStatusService : ICreditStatusService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISettingsService _settings;
    private readonly IAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CreditStatusService> _logger;

    public CreditStatusService(
        ISettingsService settings, IAuthService auth, IHttpClientFactory httpClientFactory,
        ILogger<CreditStatusService> logger)
    {
        _settings = settings;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // Null for every "nothing to show" answer, 403 and 404 included: a server without the AI proxy, or one
    // older than the endpoint, simply has no credit view.
    public async Task<CreditStatusResponse?> GetAsync(CancellationToken ct = default)
    {
        var serverUrl = (await _settings.GetSettingsAsync()).ServerUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(serverUrl)) return null;

        var token = await _auth.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return null;

        using var http = _httpClientFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await http.GetAsync($"{serverUrl}/api/ai/credits", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Credit status from {Url} returned {Status}.", SafeUrl.Format(serverUrl), (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<CreditStatusResponse>(JsonOptions, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Credit status from {Url} could not be read.", SafeUrl.Format(serverUrl));
            return null;
        }
    }
}
```

Register in `Bootstrapper.cs`, beside the `IAssignmentApiClient` line:

```csharp
        services.AddSingleton<Services.Credits.ICreditStatusService, Services.Credits.CreditStatusService>();
```

- [x] **Step 5: Build and run the class again**

Expected: all 9 cases PASS (the theory counts four).

- [x] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Services/Credits src/Pia.Wpf/Bootstrapper.cs tests/Pia.Wpf.Tests/Services/CreditStatusServiceTests.cs
git commit -m "feat(credits): read the caller's credit status from the server"
```

---

### Task 2: `CreditMeterBuilder` and the labels

**Files:**
- Create: `src/Pia.Wpf/ViewModels/CreditMeter.cs`
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.resx`, `ViewStrings.de.resx`, `ViewStrings.fr.resx`
- Test: `tests/Pia.Wpf.Tests/ViewModels/CreditMeterBuilderTests.cs`

**Interfaces:**
- Consumes: Task 1's DTOs; `ILocalizationService` indexer `this[string key]`.
- Produces:
  - `record CreditMeter(string Key, string Label, long Value, long Maximum, string Caption)`
  - `static class CreditMeterBuilder { static IReadOnlyList<CreditMeter> Build(CreditStatusResponse status, ILocalizationService loc, TimeZoneInfo zone, CultureInfo culture); }`
  - Keys, in display order: `weekly`, `pool`, `topUp`, `daily`, `hourly`, `groupWeekly`, `groupDaily`.

- [x] **Step 1: Add the strings**, the same keys in all three files

| Key | en | de | fr |
|---|---|---|---|
| `Settings_Credits_Title` | Credits | Credits | Crédits |
| `Settings_Credits_Weekly` | This week | Diese Woche | Cette semaine |
| `Settings_Credits_Pool` | Free team pool this week | Freier Team-Pool diese Woche | Réserve d'équipe gratuite cette semaine |
| `Settings_Credits_TopUp` | Top-up credits | Zusätzlich gekaufte Credits | Crédits supplémentaires achetés |
| `Settings_Credits_Daily` | Last 24 hours | Letzte 24 Stunden | Dernières 24 heures |
| `Settings_Credits_Hourly` | Last hour | Letzte Stunde | Dernière heure |
| `Settings_Credits_GroupWeekly` | Whole team, this week | Ganzes Team, diese Woche | Toute l'équipe, cette semaine |
| `Settings_Credits_GroupDaily` | Whole team, last 24 hours | Ganzes Team, letzte 24 Stunden | Toute l'équipe, dernières 24 heures |
| `Settings_Credits_UsedOf` | {0} of {1} used | {0} von {1} verbraucht | {0} sur {1} utilisés |
| `Settings_Credits_LeftOf` | {0} of {1} left | {0} von {1} übrig | {0} sur {1} restants |
| `Settings_Credits_Resets` | Resets {0} | Zurückgesetzt am {0} | Réinitialisation le {0} |
| `Settings_Credits_Suspended` | The free tier is paused right now. Please try again later. | Die kostenlose Stufe ist gerade pausiert. Bitte versuchen Sie es später erneut. | L'offre gratuite est suspendue pour le moment. Veuillez réessayer plus tard. |

- [x] **Step 2: Write the failing tests** in `tests/Pia.Wpf.Tests/ViewModels/CreditMeterBuilderTests.cs`

```csharp
namespace Pia.Tests.ViewModels;

using System.Globalization;
using NSubstitute;
using Pia.Services.Credits;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

public class CreditMeterBuilderTests
{
    private static readonly DateTime ResetsUtc = new(2026, 9, 27, 22, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();

    public CreditMeterBuilderTests()
    {
        _loc[Arg.Any<string>()].Returns(call => call.Arg<string>());
        _loc["Settings_Credits_UsedOf"].Returns("{0} of {1} used");
        _loc["Settings_Credits_LeftOf"].Returns("{0} of {1} left");
        _loc["Settings_Credits_Resets"].Returns("Resets {0}");
    }

    [Fact]
    public void Build_FullResponse_OrdersAndCaptionsEveryRow()
    {
        var status = new CreditStatusResponse(
            true, null,
            new CreditWindowDto(50, 3, null),
            new CreditWindowDto(200, 41, null),
            new CreditWindowDto(1000, 620, ResetsUtc),
            new CreditPoolDto(3000, 1200, 1800, ResetsUtc),
            new CreditTopUpDto(25000, 8300, 16700),
            new CreditGroupCapDto(new CreditWindowDto(5000, 900, null), new CreditWindowDto(20000, 7400, ResetsUtc)));

        var meters = CreditMeterBuilder.Build(status, _loc, Berlin, German);

        Assert.Equal(
            new[] { "weekly", "pool", "topUp", "daily", "hourly", "groupWeekly", "groupDaily" },
            meters.Select(m => m.Key));
        Assert.Equal(new CreditMeter("weekly", "Settings_Credits_Weekly", 620, 1000,
            "620 of 1.000 used · Resets 28.09.2026 00:00"), meters[0]);
        Assert.Equal(new CreditMeter("pool", "Settings_Credits_Pool", 1200, 3000,
            "1.800 of 3.000 left · Resets 28.09.2026 00:00"), meters[1]);
        Assert.Equal(new CreditMeter("topUp", "Settings_Credits_TopUp", 8300, 25000,
            "16.700 of 25.000 left"), meters[2]);
        Assert.Equal("3 of 50 used", meters[4].Caption);
    }

    [Fact]
    public void Build_OnlySomeSections_SkipsTheAbsentOnes()
    {
        var status = new CreditStatusResponse(
            true, null, null, new CreditWindowDto(20, 5, null), null, null, null, null);

        var meter = Assert.Single(CreditMeterBuilder.Build(status, _loc, Berlin, German));

        Assert.Equal("daily", meter.Key);
    }

    [Fact]
    public void Build_EmptyPool_KeepsAnEmptyBarRatherThanAFullOne()
    {
        var status = new CreditStatusResponse(
            true, null, null, null, new CreditWindowDto(10, 0, ResetsUtc),
            new CreditPoolDto(0, 0, 0, ResetsUtc), null, null);

        var pool = CreditMeterBuilder.Build(status, _loc, Berlin, German).Single(m => m.Key == "pool");

        Assert.Equal(0, pool.Value);
        Assert.Equal(1, pool.Maximum);
    }
}
```

The `"g"` pattern for `de-DE` is `dd.MM.yyyy HH:mm`; `2026-09-27T22:00Z` is Monday 00:00 in Berlin.

- [x] **Step 3: Build and run to see them fail**

Run: `dotnet build tests/Pia.Wpf.Tests` then the exe with `-class "Pia.Tests.ViewModels.CreditMeterBuilderTests" > test.log 2>&1`
Expected: compile error, `CreditMeterBuilder` does not exist.

- [x] **Step 4: Implement** `src/Pia.Wpf/ViewModels/CreditMeter.cs`

```csharp
using System.Globalization;
using Pia.Services.Credits;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public sealed record CreditMeter(string Key, string Label, long Value, long Maximum, string Caption);

public static class CreditMeterBuilder
{
    public static IReadOnlyList<CreditMeter> Build(
        CreditStatusResponse status, ILocalizationService loc, TimeZoneInfo zone, CultureInfo culture)
    {
        var meters = new List<CreditMeter>();

        string Number(long value) => value.ToString("N0", culture);

        string WithReset(string caption, DateTime? resetsAtUtc) => resetsAtUtc is { } utc
            ? $"{caption} · {string.Format(culture, loc["Settings_Credits_Resets"],
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).ToString("g", culture))}"
            : caption;

        void Used(string key, string labelKey, CreditWindowDto? window)
        {
            if (window is null) return;
            var caption = string.Format(culture, loc["Settings_Credits_UsedOf"], Number(window.Used), Number(window.Limit));
            meters.Add(new CreditMeter(key, loc[labelKey], window.Used, Math.Max(1, window.Limit),
                WithReset(caption, window.ResetsAt)));
        }

        void Left(string key, string labelKey, long used, long total, long remaining, DateTime? resetsAtUtc)
        {
            var caption = string.Format(culture, loc["Settings_Credits_LeftOf"], Number(remaining), Number(total));
            meters.Add(new CreditMeter(key, loc[labelKey], used, Math.Max(1, total), WithReset(caption, resetsAtUtc)));
        }

        Used("weekly", "Settings_Credits_Weekly", status.Weekly);
        if (status.Pool is { } pool)
            Left("pool", "Settings_Credits_Pool", pool.Used, pool.Total, pool.Remaining, pool.ResetsAt);
        if (status.TopUp is { } topUp)
            Left("topUp", "Settings_Credits_TopUp", topUp.Consumed, topUp.Granted, topUp.Remaining, null);
        Used("daily", "Settings_Credits_Daily", status.Daily);
        Used("hourly", "Settings_Credits_Hourly", status.Hourly);
        Used("groupWeekly", "Settings_Credits_GroupWeekly", status.GroupCap?.Weekly);
        Used("groupDaily", "Settings_Credits_GroupDaily", status.GroupCap?.Daily);

        return meters;
    }
}
```

`Math.Max(1, …)` is what keeps a zero pool an empty bar: `ProgressBar` with `Maximum = 0` renders full.

- [x] **Step 5: Build and run the class again.** Expected: 3 PASS.

- [x] **Step 6: Commit**

```bash
git add src/Pia.Wpf/ViewModels/CreditMeter.cs src/Pia.Wpf/Resources/Strings/ViewStrings*.resx tests/Pia.Wpf.Tests/ViewModels/CreditMeterBuilderTests.cs
git commit -m "feat(credits): turn a credit status into ordered, captioned meters"
```

---

### Task 3: Fetch, hold and clear in `AccountSettingsViewModel`

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/AccountSettingsViewModel.cs` (constructor, the `LoginStateChanged` handler, `InitializeAsync`)
- Modify: `src/Pia.Wpf/ViewModels/SettingsViewModel.cs` (constructor, the `new AccountSettingsViewModel(…)` line)
- Modify: `tests/Pia.Wpf.Tests/ViewModels/AccountSettingsBusinessProfileTests.cs`, `AccountSettingsAccountDataTests.cs`, `SettingsPolicyReloadTests.cs`
- Test: `tests/Pia.Wpf.Tests/ViewModels/AccountSettingsCreditsTests.cs`

**Interfaces:**
- Consumes: `ICreditStatusService.GetAsync` (Task 1), `CreditMeterBuilder.Build` (Task 2).
- Produces (bound by Task 4): `ObservableCollection<CreditMeter> CreditMeters`, `bool HasCredits`, `bool IsCreditTierSuspended`.

- [x] **Step 1: Write the failing tests** in `tests/Pia.Wpf.Tests/ViewModels/AccountSettingsCreditsTests.cs`.
  The scaffold is the one `AccountSettingsBusinessProfileTests` uses, plus `_credits` as the last argument.

```csharp
namespace Pia.Tests.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Credits;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

public class AccountSettingsCreditsTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly ISyncClientService _sync = Substitute.For<ISyncClientService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IDeviceManagementService _deviceMgmt = Substitute.For<IDeviceManagementService>();
    private readonly IDeviceKeyService _deviceKeys = Substitute.For<IDeviceKeyService>();
    private readonly IMemoryService _memory = Substitute.For<IMemoryService>();
    private readonly IPolicyService _policy = Substitute.For<IPolicyService>();
    private readonly ICreditStatusService _credits = Substitute.For<ICreditStatusService>();

    public AccountSettingsCreditsTests()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings());
        _loc[Arg.Any<string>()].Returns("{0} {1}");
    }

    private AccountSettingsViewModel CreateSut()
    {
        // AccountSettingsViewModel demands a captured context; inline keeps the assertions synchronous.
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());

        return new AccountSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, _settings, Substitute.For<IDialogService>(),
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), _auth, _sync, _loc, _deviceMgmt,
            _deviceKeys, _memory, _policy,
            new E2EEOnboardingViewModel(
                _deviceMgmt, _deviceKeys, Substitute.For<IE2EEService>(), _sync, _settings,
                NullLogger<E2EEOnboardingViewModel>.Instance),
            Substitute.For<IAccountDataService>(), Substitute.For<IFileDialogService>(), _credits);
    }

    private static CreditStatusResponse Limited(bool? suspended = null) => new(
        true, suspended, null, null, new CreditWindowDto(100, 40, DateTime.UtcNow.AddDays(3)), null, null, null);

    [Fact]
    public async Task ALimitedAnswer_ShowsTheCard()
    {
        _credits.GetAsync(Arg.Any<CancellationToken>()).Returns(Limited());
        var sut = CreateSut();

        await sut.RefreshCreditsAsync();

        Assert.True(sut.HasCredits);
        Assert.Equal("weekly", Assert.Single(sut.CreditMeters).Key);
        Assert.False(sut.IsCreditTierSuspended);
    }

    [Fact]
    public async Task ASuspendedAnswer_RaisesTheWarning()
    {
        _credits.GetAsync(Arg.Any<CancellationToken>()).Returns(Limited(suspended: true));
        var sut = CreateSut();

        await sut.RefreshCreditsAsync();

        Assert.True(sut.IsCreditTierSuspended);
    }

    [Fact]
    public async Task ALaterUnlimitedAnswer_HidesTheCard()
    {
        _credits.GetAsync(Arg.Any<CancellationToken>()).Returns(Limited(), new CreditStatusResponse(
            false, null, null, null, null, null, null, null));
        var sut = CreateSut();

        await sut.RefreshCreditsAsync();
        await sut.RefreshCreditsAsync();

        Assert.False(sut.HasCredits);
        Assert.Empty(sut.CreditMeters);
    }

    [Fact]
    public async Task NoAnswer_HidesTheCard()
    {
        _credits.GetAsync(Arg.Any<CancellationToken>()).Returns((CreditStatusResponse?)null);
        var sut = CreateSut();

        await sut.RefreshCreditsAsync();

        Assert.False(sut.HasCredits);
    }

    [Fact]
    public async Task SigningOut_ClearsTheCard()
    {
        _credits.GetAsync(Arg.Any<CancellationToken>()).Returns(Limited(suspended: true));
        var sut = CreateSut();
        await sut.RefreshCreditsAsync();

        _auth.LoginStateChanged += Raise.Event<EventHandler<bool>>(_auth, false);

        Assert.False(sut.HasCredits);
        Assert.False(sut.IsCreditTierSuspended);
        Assert.Empty(sut.CreditMeters);
    }

    [Fact]
    public async Task SigningIn_FetchesTheCard()
    {
        _credits.GetAsync(Arg.Any<CancellationToken>()).Returns(Limited());
        var sut = CreateSut();

        _auth.LoginStateChanged += Raise.Event<EventHandler<bool>>(_auth, true);

        await _credits.Received(1).GetAsync(Arg.Any<CancellationToken>());
    }
}
```

- [x] **Step 2: Build and run to see them fail.** Expected: compile errors (`RefreshCreditsAsync`, `HasCredits`,
  the extra constructor argument).

- [x] **Step 3: Implement.** In `AccountSettingsViewModel`:
  - Add `ICreditStatusService creditStatus` as the **last** constructor parameter, stored in `_creditStatus`,
    and the usings `System.Collections.ObjectModel`, `System.Globalization` and `Pia.Services.Credits`.
  - Add the state:

```csharp
    public ObservableCollection<CreditMeter> CreditMeters { get; } = [];

    [ObservableProperty]
    private bool _hasCredits;

    [ObservableProperty]
    private bool _isCreditTierSuspended;

    internal async Task RefreshCreditsAsync()
    {
        var status = await _creditStatus.GetAsync();
        IReadOnlyList<CreditMeter> meters = status is { Limited: true }
            ? CreditMeterBuilder.Build(status, _localizationService, TimeZoneInfo.Local, CultureInfo.CurrentUICulture)
            : [];

        Post(() =>
        {
            CreditMeters.Clear();
            foreach (var meter in meters) CreditMeters.Add(meter);
            IsCreditTierSuspended = status is { Limited: true, Suspended: true };
            HasCredits = meters.Count > 0;
        });
    }

    private void ClearCredits()
    {
        CreditMeters.Clear();
        IsCreditTierSuspended = false;
        HasCredits = false;
    }
```

  - In the `LoginStateChanged` handler, replace `if (isLoggedIn) return;` with:

```csharp
            if (isLoggedIn)
            {
                RefreshCreditsAsync().SafeFireAndForget(_logger);
                return;
            }
```

    and call `ClearCredits();` inside the existing sign-out `Post(() => { … })` block.
  - At the end of `InitializeAsync`, after the business-profile probe:

```csharp
        RefreshCreditsAsync().SafeFireAndForget(_logger);
```

  - `RefreshCreditsAsync` stays `internal`: `Pia.Wpf.csproj` already has `InternalsVisibleTo Pia.Wpf.Tests`.

  In `SettingsViewModel`: add `ICreditStatusService creditStatus` to its constructor and pass it as the last
  argument of `new AccountSettingsViewModel(…)`. DI resolves `SettingsViewModel`, so nothing else changes.

  Update the three existing test files: add `Substitute.For<ICreditStatusService>()` as the last
  `AccountSettingsViewModel` argument (and the `SettingsViewModel` argument in `SettingsPolicyReloadTests`).

- [x] **Step 4: Build and run the whole exe gate** (the constructor change touches other test classes).
  Expected: all green, plus the 6 new tests.

- [x] **Step 5: Commit**

```bash
git add src/Pia.Wpf/ViewModels tests/Pia.Wpf.Tests/ViewModels
git commit -m "feat(credits): fetch the credit status on each Account visit and on sign-in"
```

---

### Task 4: The card in `AccountView.xaml`

**Files:**
- Modify: `src/Pia.Wpf/Views/SettingsViews/AccountView.xaml` (inside the `IsSyncLoggedIn` `StackPanel`, directly after the signed-in `Border`, before `<!-- Sync status -->`)

- [ ] **Step 1: Add the card**

```xml
        <StackPanel Margin="0,0,0,12"
                    Visibility="{Binding HasCredits, Converter={StaticResource BooleanToVisibilityConverter}}">
          <TextBlock Text="{loc:Str Settings_Credits_Title}"
                     Style="{StaticResource PiaSettingsSectionLabelStyle}"/>
          <Border Style="{StaticResource PiaSettingsCardStyle}"
                  Padding="16"
                  AutomationProperties.AutomationId="Settings_Account_Credits">
            <StackPanel>
              <TextBlock Text="{loc:Str Settings_Credits_Suspended}"
                         TextWrapping="Wrap"
                         FontSize="12"
                         Foreground="{DynamicResource WarningBrush}"
                         Margin="0,0,0,8"
                         Visibility="{Binding IsCreditTierSuspended, Converter={StaticResource BooleanToVisibilityConverter}}"/>
              <ItemsControl ItemsSource="{Binding CreditMeters}">
                <ItemsControl.ItemTemplate>
                  <DataTemplate>
                    <Grid Margin="0,0,0,10">
                      <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="Auto"/>
                      </Grid.ColumnDefinitions>
                      <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                      </Grid.RowDefinitions>
                      <TextBlock Text="{Binding Label}"
                                 FontSize="13"
                                 Foreground="{DynamicResource TextDefaultBrush}"/>
                      <TextBlock Grid.Column="1"
                                 Text="{Binding Caption}"
                                 FontSize="12"
                                 Foreground="{DynamicResource TextMutedBrush}"/>
                      <ProgressBar Grid.Row="1" Grid.ColumnSpan="2"
                                   Value="{Binding Value, Mode=OneWay}"
                                   Maximum="{Binding Maximum, Mode=OneWay}"
                                   Height="4"
                                   Margin="0,6,0,0"/>
                    </Grid>
                  </DataTemplate>
                </ItemsControl.ItemTemplate>
              </ItemsControl>
            </StackPanel>
          </Border>
        </StackPanel>
```

`WarningBrush` is defined in `Resources/Themes/Light.xaml` and `Dark.xaml` and already used by
`AssistantView.xaml`; do not add a new brush. The card has no interactive control, so no
`ViewAutomationIdTests` row; the `AutomationId` on the `Border` is for WinWright walkthroughs.

- [ ] **Step 2: Zero-warning build.** Run `dotnet build -t:Rebuild` and `dotnet build -c Release -t:Rebuild`.
  Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Wpf/Views/SettingsViews/AccountView.xaml
git commit -m "feat(credits): show the credit meters on the Account page"
```

---

### Task 5: Release note and desktop guide

- [ ] **Step 1: `docs/release_notes/RELEASE.md`.** Follow `docs/release_notes/README.md` (plain Markdown, 80
  columns, one-level bullets). Add under the new-features heading:

```markdown
- Settings → Account now shows your credits when your server limits them:
  what you used this week, today and in the last hour, your team's free
  weekly pool and any top-up credits left.
```

- [ ] **Step 2: Desktop guide.** The guide lives in the Pia repo (`src/Pia.Docs`). Add a short *Credits*
  paragraph to the Account settings page there, quoting the labels from `ViewStrings.resx` exactly (never
  free-translate a UI label; take the de/fr text from the matching resx). Then run the `help-corpus` skill in
  this repo before the next push to `main`.

- [ ] **Step 3: Commit** the release note (the guide is a separate Pia PR).

```bash
git add docs/release_notes/RELEASE.md
git commit -m "docs(credits): release note for the Account credit card"
```

---

### Task 6: Verify against a live server

- [ ] **Step 1: Exe gate** (Global Constraints). Expected: green.
- [ ] **Step 2: Live walkthrough.** Against a server carrying `GET /api/ai/credits` (the Pia repo's
  `pwsh deploy/local/pia-stack.ps1 up`, or a deployment that has it), sign in:
  - as a Free user: the card shows *This week*, *Last 24 hours* and *Last hour*;
  - as a user in a group with every credit setting at 0: no card;
  - against a server without the endpoint: no card and no error.
  Use `docs/ui_automation/ui-automation-playbook.md` and the `Settings_Account_Credits` id if driving it with
  WinWright.
- [ ] **Step 3: PR** from `feature/credit-status` into `main` on GitHub, after the server change is deployed
  where the release will point.
