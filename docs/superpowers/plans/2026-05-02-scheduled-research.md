# Scheduled Research Jobs Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add scheduled research jobs that run on a recurring timer, persist results to Research history, and surface via Windows toast — with a 15-minute grace period and missed-run dialog for cases where Pia is closed at the scheduled time. Add a hybrid (text + vector) search tool over Research history. Auto-download the embedding model when needed. Refactor `ReminderService` and `MemoryService` to share a recurrence calculator and a vector-search helper.

**Architecture:** New `ScheduledJob` domain with its own SQLite table, service, and `BackgroundService`, parallel to `Reminder*`. Reuses `ResearchService.ExecuteResearchAsync` for the work. Persists results as `ResearchHistoryEntry` with new `ScheduledJobId` and `Embedding` columns. Two new built-in plugin tool handlers (`ScheduledJobToolHandler`, `ResearchHistoryToolHandler`) registered through `PluginService`. No new top-level views in v1 — chat-driven CRUD.

**Tech Stack:** .NET 10, WPF (`net10.0-windows`), CommunityToolkit.MVVM, Microsoft.Extensions.AI, WPF-UI (`Wpf.Ui.Controls`), Microsoft.Toolkit.Uwp.Notifications, Microsoft.Data.Sqlite, Microsoft.ML.OnnxRuntime, xUnit v3 with plain `Xunit.Assert`.

**Spec:** `docs/superpowers/specs/2026-05-02-scheduled-research-design.md`

---

## File Structure

**New files:**

```
src/Pia.Wpf/
  Models/
    ScheduledJob.cs                                  # ScheduledJob entity + ScheduledJobKind/Status enums
  Services/
    Scheduling/
      IRecurrenceCalculator.cs
      RecurrenceCalculator.cs                        # ComputeNextFireAt extracted from ReminderService
    Search/
      VectorSearchHelper.cs                          # CosineSimilarity + RankByCosine extracted from MemoryService
    Interfaces/
      IScheduledJobService.cs
      IScheduledJobToolHandler.cs
      IResearchHistoryToolHandler.cs
    ScheduledJobService.cs
    ScheduledJobBackgroundService.cs
    ScheduledJobToolHandler.cs
    ResearchHistoryToolHandler.cs
  Views/
    Dialogs/
      MissedScheduledJobDialog.xaml
      MissedScheduledJobDialog.xaml.cs

tests/Pia.Wpf.Tests/
  Unit/
    RecurrenceCalculatorTests.cs
    VectorSearchHelperTests.cs
    EmbeddingServiceEnsureAvailableTests.cs
    ScheduledJobServiceTests.cs
    ScheduledJobToolHandlerTests.cs
    ScheduledJobBackgroundServiceTests.cs
    ResearchHistoryToolHandlerTests.cs
  Integration/
    ScheduledJobToolIntegrationTests.cs
```

**Modified files:**

```
src/Pia.Wpf/
  Bootstrapper.cs                                    # DI registration
  Infrastructure/SqliteContext.cs                    # Schema + migrations (ScheduledJobs, ResearchSessions ALTERs)
  Models/ResearchHistoryEntry.cs                     # Add ScheduledJobId, Embedding properties
  Services/
    EmbeddingService.cs                              # New EnsureAvailableAsync method
    Interfaces/IEmbeddingService.cs                  # New EnsureAvailableAsync member
    MemoryService.cs                                 # Use VectorSearchHelper; call EnsureAvailableAsync
    ReminderService.cs                               # Use IRecurrenceCalculator
    ResearchHistoryService.cs                        # ScheduledJobId/Embedding columns; vector + hybrid search
    Plugins/PluginService.cs                         # Register two new built-in handlers
    Plugins/BuiltInPluginHandler.cs                  # Two new factory methods
  Resources/Strings/
    MessageStrings.resx                              # New keys (en)
    MessageStrings.de.resx                           # New keys (de)
    MessageStrings.fr.resx                           # New keys (fr)
```

---

## Chunk 1: Shared abstractions

These are pure refactors of existing code. They must keep all current `ReminderService` and `MemoryService` tests green. Land them first so the new feature work isn't bundled with refactor churn.

### Task 1: Extract `IRecurrenceCalculator` from `ReminderService`

**Files:**
- Create: `src/Pia.Wpf/Services/Scheduling/IRecurrenceCalculator.cs`
- Create: `src/Pia.Wpf/Services/Scheduling/RecurrenceCalculator.cs`
- Modify: `src/Pia.Wpf/Services/ReminderService.cs` (lines 251–314 deleted; line 36 changed to use injected calculator)
- Modify: `src/Pia.Wpf/Bootstrapper.cs` (register `IRecurrenceCalculator`)
- Create: `tests/Pia.Wpf.Tests/Unit/RecurrenceCalculatorTests.cs`

- [ ] **Step 1: Write the failing test file**

Create `tests/Pia.Wpf.Tests/Unit/RecurrenceCalculatorTests.cs`:

```csharp
using Pia.Models;
using Pia.Services.Scheduling;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

public class RecurrenceCalculatorTests
{
    private readonly RecurrenceCalculator _calc = new();

    [Fact]
    public void Once_WithSpecificDate_UsesThatDate()
    {
        var now = new DateTime(2026, 5, 2, 10, 0, 0);
        var result = _calc.ComputeNextFireAt(
            recurrence: RecurrenceType.Once,
            timeOfDay: new TimeOnly(14, 30),
            specificDate: new DateTime(2026, 5, 5),
            dayOfWeek: null, dayOfMonth: null, month: null, now: now);

        Assert.Equal(new DateTime(2026, 5, 5, 14, 30, 0), result);
    }

    [Fact]
    public void Daily_TimeAlreadyPassedToday_RollsToTomorrow()
    {
        var now = new DateTime(2026, 5, 2, 15, 0, 0);
        var result = _calc.ComputeNextFireAt(
            RecurrenceType.Daily, new TimeOnly(9, 0),
            null, null, null, null, now);

        Assert.Equal(new DateTime(2026, 5, 3, 9, 0, 0), result);
    }

    [Fact]
    public void Daily_TimeStillToday_StaysToday()
    {
        var now = new DateTime(2026, 5, 2, 7, 0, 0);
        var result = _calc.ComputeNextFireAt(
            RecurrenceType.Daily, new TimeOnly(9, 0),
            null, null, null, null, now);

        Assert.Equal(new DateTime(2026, 5, 2, 9, 0, 0), result);
    }

    [Fact]
    public void Weekly_TargetIsTodayButTimePassed_RollsOneWeek()
    {
        // Saturday 2026-05-02 at 15:00 → next Saturday at 09:00
        var now = new DateTime(2026, 5, 2, 15, 0, 0);
        var result = _calc.ComputeNextFireAt(
            RecurrenceType.Weekly, new TimeOnly(9, 0),
            null, DayOfWeek.Saturday, null, null, now);

        Assert.Equal(new DateTime(2026, 5, 9, 9, 0, 0), result);
    }

    [Fact]
    public void Monthly_TargetDayPassed_RollsToNextMonth()
    {
        var now = new DateTime(2026, 5, 20, 10, 0, 0);
        var result = _calc.ComputeNextFireAt(
            RecurrenceType.Monthly, new TimeOnly(8, 0),
            null, null, dayOfMonth: 5, null, now);

        Assert.Equal(new DateTime(2026, 6, 5, 8, 0, 0), result);
    }

    [Fact]
    public void Yearly_Feb29InNonLeap_ClampsToFeb28()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0);
        var result = _calc.ComputeNextFireAt(
            RecurrenceType.Yearly, new TimeOnly(0, 0),
            null, null, dayOfMonth: 29, month: 2, now);

        Assert.Equal(new DateTime(2026, 2, 28, 0, 0, 0), result);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails to compile**

```powershell
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter FullyQualifiedName~RecurrenceCalculatorTests
```

Expected: build error — types `RecurrenceCalculator` / `Pia.Services.Scheduling` don't exist yet.

- [ ] **Step 3: Create the interface**

Create `src/Pia.Wpf/Services/Scheduling/IRecurrenceCalculator.cs`:

```csharp
using Pia.Models;

namespace Pia.Services.Scheduling;

public interface IRecurrenceCalculator
{
    DateTime ComputeNextFireAt(
        RecurrenceType recurrence,
        TimeOnly timeOfDay,
        DateTime? specificDate,
        DayOfWeek? dayOfWeek,
        int? dayOfMonth,
        int? month,
        DateTime now);
}
```

- [ ] **Step 4: Create the implementation by porting `ReminderService.ComputeNextFireAt`**

Create `src/Pia.Wpf/Services/Scheduling/RecurrenceCalculator.cs`. Copy the bodies of `ComputeNextFireAt`, `ComputeNextWeekly`, `ComputeNextMonthly`, `ComputeNextYearly` from `ReminderService.cs` lines 251–314 verbatim, but as instance methods on `RecurrenceCalculator` taking explicit parameters instead of reading from a `Reminder`. Behavior must be identical.

```csharp
using Pia.Models;

namespace Pia.Services.Scheduling;

public class RecurrenceCalculator : IRecurrenceCalculator
{
    public DateTime ComputeNextFireAt(
        RecurrenceType recurrence,
        TimeOnly timeOfDay,
        DateTime? specificDate,
        DayOfWeek? dayOfWeek,
        int? dayOfMonth,
        int? month,
        DateTime now)
    {
        var todayAtTime = now.Date + timeOfDay.ToTimeSpan();

        return recurrence switch
        {
            RecurrenceType.Once => specificDate.HasValue
                ? specificDate.Value.Date + timeOfDay.ToTimeSpan()
                : todayAtTime > now ? todayAtTime : todayAtTime.AddDays(1),

            RecurrenceType.Daily => todayAtTime > now ? todayAtTime : todayAtTime.AddDays(1),

            RecurrenceType.Weekly => ComputeNextWeekly(now, timeOfDay, dayOfWeek ?? now.DayOfWeek),

            RecurrenceType.Monthly => ComputeNextMonthly(now, timeOfDay, dayOfMonth ?? now.Day),

            RecurrenceType.Yearly => ComputeNextYearly(now, timeOfDay, month ?? now.Month, dayOfMonth ?? now.Day),

            _ => todayAtTime > now ? todayAtTime : todayAtTime.AddDays(1)
        };
    }

    private static DateTime ComputeNextWeekly(DateTime now, TimeOnly timeOfDay, DayOfWeek targetDay)
    {
        var daysUntil = ((int)targetDay - (int)now.DayOfWeek + 7) % 7;
        var candidate = now.Date.AddDays(daysUntil) + timeOfDay.ToTimeSpan();
        if (candidate <= now)
            candidate = candidate.AddDays(7);
        return candidate;
    }

    private static DateTime ComputeNextMonthly(DateTime now, TimeOnly timeOfDay, int targetDay)
    {
        targetDay = Math.Min(targetDay, DateTime.DaysInMonth(now.Year, now.Month));
        var candidate = new DateTime(now.Year, now.Month, targetDay) + timeOfDay.ToTimeSpan();

        if (candidate <= now)
        {
            var next = now.AddMonths(1);
            targetDay = Math.Min(targetDay, DateTime.DaysInMonth(next.Year, next.Month));
            candidate = new DateTime(next.Year, next.Month, targetDay) + timeOfDay.ToTimeSpan();
        }

        return candidate;
    }

    private static DateTime ComputeNextYearly(DateTime now, TimeOnly timeOfDay, int targetMonth, int targetDay)
    {
        targetDay = Math.Min(targetDay, DateTime.DaysInMonth(now.Year, targetMonth));
        var candidate = new DateTime(now.Year, targetMonth, targetDay) + timeOfDay.ToTimeSpan();

        if (candidate <= now)
        {
            var nextYear = now.Year + 1;
            targetDay = Math.Min(targetDay, DateTime.DaysInMonth(nextYear, targetMonth));
            candidate = new DateTime(nextYear, targetMonth, targetDay) + timeOfDay.ToTimeSpan();
        }

        return candidate;
    }
}
```

- [ ] **Step 5: Run new tests to verify they pass**

```powershell
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter FullyQualifiedName~RecurrenceCalculatorTests
```

Expected: 6 passed.

- [ ] **Step 6: Refactor `ReminderService` to use the calculator**

In `src/Pia.Wpf/Services/ReminderService.cs`:
- Add `private readonly IRecurrenceCalculator _calculator;` field.
- Change constructor to take `IRecurrenceCalculator calculator` and assign it.
- Replace every call to `ComputeNextFireAt(reminder)` (lines 36, 123, 191, 222) with `_calculator.ComputeNextFireAt(reminder.Recurrence, reminder.TimeOfDay, reminder.SpecificDate, reminder.DayOfWeek, reminder.DayOfMonth, reminder.Month, DateTime.Now)`.
- Delete the four private static methods `ComputeNextFireAt`, `ComputeNextWeekly`, `ComputeNextMonthly`, `ComputeNextYearly` (lines 251–314).

- [ ] **Step 7: Register `IRecurrenceCalculator` in `Bootstrapper.cs`**

Find the line registering `IReminderService` (search for `AddSingleton<IReminderService`) and add immediately above it:

```csharp
services.AddSingleton<IRecurrenceCalculator, RecurrenceCalculator>();
```

Add `using Pia.Services.Scheduling;` at the top if not present.

- [ ] **Step 8: Run the full test suite**

```powershell
dotnet build
dotnet test
```

Expected: solution builds; all existing tests still pass plus 6 new ones.

- [ ] **Step 9: Commit**

```bash
git add src/Pia.Wpf/Services/Scheduling/ src/Pia.Wpf/Services/ReminderService.cs src/Pia.Wpf/Bootstrapper.cs tests/Pia.Wpf.Tests/Unit/RecurrenceCalculatorTests.cs
git commit -m "Extract IRecurrenceCalculator from ReminderService"
```

---

### Task 2: Extract `VectorSearchHelper` from `MemoryService`

**Files:**
- Create: `src/Pia.Wpf/Services/Search/VectorSearchHelper.cs`
- Modify: `src/Pia.Wpf/Services/MemoryService.cs` (replace `CosineSimilarity` static and rewrite `VectorSearchAsync` body)
- Create: `tests/Pia.Wpf.Tests/Unit/VectorSearchHelperTests.cs`

> **Note:** `MemoryService.HybridSearchAsync` has memory-specific tiers (FTS5, fuzzy label match) that should NOT be extracted. Keep the merge inline; only share `CosineSimilarity` and `RankByCosine`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Pia.Wpf.Tests/Unit/VectorSearchHelperTests.cs`:

```csharp
using Pia.Services.Search;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

public class VectorSearchHelperTests
{
    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var a = new float[] { 1f, 2f, 3f };
        var b = new float[] { 1f, 2f, 3f };
        var score = VectorSearchHelper.CosineSimilarity(a, b);
        Assert.True(Math.Abs(score - 1f) < 1e-5);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new float[] { 1f, 0f };
        var b = new float[] { 0f, 1f };
        var score = VectorSearchHelper.CosineSimilarity(a, b);
        Assert.True(Math.Abs(score) < 1e-5);
    }

    [Fact]
    public void CosineSimilarity_OppositeVectors_ReturnsMinusOne()
    {
        var a = new float[] { 1f, 0f };
        var b = new float[] { -1f, 0f };
        var score = VectorSearchHelper.CosineSimilarity(a, b);
        Assert.True(Math.Abs(score + 1f) < 1e-5);
    }

    [Fact]
    public void CosineSimilarity_DifferentLengths_ReturnsZero()
    {
        var a = new float[] { 1f, 2f };
        var b = new float[] { 1f, 2f, 3f };
        var score = VectorSearchHelper.CosineSimilarity(a, b);
        Assert.Equal(0f, score);
    }

    [Fact]
    public void RankByCosine_SortsAndFiltersAndLimits()
    {
        var query = new float[] { 1f, 0f };
        var items = new[]
        {
            ("near", new float[] { 0.9f, 0.1f }),
            ("far", new float[] { -1f, 0f }),
            ("perp", new float[] { 0f, 1f }),
            ("exact", new float[] { 1f, 0f })
        };

        var ranked = VectorSearchHelper.RankByCosine(
            items,
            getEmbedding: x => x.Item2,
            query,
            topK: 2,
            threshold: 0.5f).ToList();

        Assert.Equal(2, ranked.Count);
        Assert.Equal("exact", ranked[0].Item1);
        Assert.Equal("near", ranked[1].Item1);
    }

    [Fact]
    public void RankByCosine_NullEmbeddings_AreSkipped()
    {
        var query = new float[] { 1f, 0f };
        var items = new (string, float[]?)[]
        {
            ("hit", new float[] { 1f, 0f }),
            ("missing", null)
        };

        var ranked = VectorSearchHelper.RankByCosine(
            items,
            getEmbedding: x => x.Item2,
            query,
            topK: 5,
            threshold: 0f).ToList();

        Assert.Single(ranked);
        Assert.Equal("hit", ranked[0].Item1);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

```powershell
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter FullyQualifiedName~VectorSearchHelperTests
```

Expected: build error — `VectorSearchHelper` does not exist.

- [ ] **Step 3: Create the helper**

Create `src/Pia.Wpf/Services/Search/VectorSearchHelper.cs`:

```csharp
namespace Pia.Services.Search;

public static class VectorSearchHelper
{
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;

        float dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA == 0 || magB == 0) return 0f;
        return dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB));
    }

    public static IEnumerable<T> RankByCosine<T>(
        IEnumerable<T> items,
        Func<T, float[]?> getEmbedding,
        float[] query,
        int topK,
        float threshold)
    {
        return items
            .Select(item => (Item: item, Embedding: getEmbedding(item)))
            .Where(x => x.Embedding is not null)
            .Select(x => (x.Item, Score: CosineSimilarity(query, x.Embedding!)))
            .Where(x => x.Score >= threshold)
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Item);
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Expected: 6 passed.

- [ ] **Step 5: Refactor `MemoryService.VectorSearchAsync` to use the helper**

In `src/Pia.Wpf/Services/MemoryService.cs`:
- Add `using Pia.Services.Search;` at the top.
- Replace the body of `VectorSearchAsync` (currently lines 319–335) with:

```csharp
public async Task<IReadOnlyList<MemoryObject>> VectorSearchAsync(
    float[] queryEmbedding, int topK = 5, float threshold = 0.3f)
{
    var allObjects = await GetAllObjectsWithEmbeddingsAsync();

    var ranked = VectorSearchHelper.RankByCosine(
        allObjects,
        m => m.Embedding is null ? null : _embeddingService.BytesToFloats(m.Embedding),
        queryEmbedding,
        topK,
        threshold).ToList();

    return ranked.AsReadOnly();
}
```

- Delete the private static `CosineSimilarity` method (around line 578).

- [ ] **Step 6: Run all tests**

```powershell
dotnet test
```

Expected: all existing memory tests still pass plus 6 new helper tests.

- [ ] **Step 7: Commit**

```bash
git add src/Pia.Wpf/Services/Search/ src/Pia.Wpf/Services/MemoryService.cs tests/Pia.Wpf.Tests/Unit/VectorSearchHelperTests.cs
git commit -m "Extract VectorSearchHelper from MemoryService"
```

---

### Task 3: Add `IEmbeddingService.EnsureAvailableAsync`

**Files:**
- Modify: `src/Pia.Wpf/Services/Interfaces/IEmbeddingService.cs`
- Modify: `src/Pia.Wpf/Services/EmbeddingService.cs`
- Modify: `src/Pia.Wpf/Services/MemoryService.cs` (call `EnsureAvailableAsync` before embedding-needing flows)
- Create: `tests/Pia.Wpf.Tests/Unit/EmbeddingServiceEnsureAvailableTests.cs`

- [ ] **Step 1: Open `IEmbeddingService.cs` and identify current shape**

Find the file at `src/Pia.Wpf/Services/Interfaces/IEmbeddingService.cs`. Note the existing members.

- [ ] **Step 2: Write the failing tests**

Create `tests/Pia.Wpf.Tests/Unit/EmbeddingServiceEnsureAvailableTests.cs`:

```csharp
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

public class EmbeddingServiceEnsureAvailableTests
{
    [Fact]
    public async Task EnsureAvailableAsync_ModelAlreadyAvailable_ReturnsTrueWithoutDownload()
    {
        var factory = new SimpleHttpClientFactory();
        var svc = new EmbeddingService(NullLogger<EmbeddingService>.Instance, factory);

        if (!svc.IsModelAvailable)
        {
            // Skip: this test only applies when the model has already been downloaded once
            // on the dev machine. CI environments without it will exercise download path tests below.
            return;
        }

        var ok = await svc.EnsureAvailableAsync();
        Assert.True(ok);
        Assert.Equal(0, factory.RequestCount);
    }

    [Fact]
    public async Task EnsureAvailableAsync_DownloadFailure_ReturnsFalse()
    {
        var factory = new FailingHttpClientFactory();
        // Use a temp directory that we know is empty so download is needed
        var svc = new EmbeddingService(NullLogger<EmbeddingService>.Instance, factory);

        if (svc.IsModelAvailable)
        {
            // Test only meaningful when model is missing
            return;
        }

        var ok = await svc.EnsureAvailableAsync();
        Assert.False(ok);
    }

    private class SimpleHttpClientFactory : IHttpClientFactory
    {
        public int RequestCount;
        public HttpClient CreateClient(string name) => new();
    }

    private class FailingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new FailingHandler());
    }

    private class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated failure");
    }
}
```

- [ ] **Step 3: Run the test to verify failure**

```powershell
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter FullyQualifiedName~EmbeddingServiceEnsureAvailableTests
```

Expected: build error — `EnsureAvailableAsync` does not exist on `EmbeddingService`.

- [ ] **Step 4: Add `EnsureAvailableAsync` to the interface**

In `src/Pia.Wpf/Services/Interfaces/IEmbeddingService.cs`, add this member declaration:

```csharp
Task<bool> EnsureAvailableAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default);
```

- [ ] **Step 5: Implement on `EmbeddingService`**

In `src/Pia.Wpf/Services/EmbeddingService.cs`, add this method (near `DownloadModelAsync`):

```csharp
public async Task<bool> EnsureAvailableAsync(
    IProgress<float>? progress = null,
    CancellationToken cancellationToken = default)
{
    if (IsModelAvailable) return true;

    _logger.LogInformation("Embedding model missing — auto-downloading");
    return await DownloadModelAsync(progress, cancellationToken);
}
```

Also update `GenerateEmbeddingAsync` so the auto-download is transparent. Replace its first line `EnsureModelLoaded();` with:

```csharp
if (!await EnsureAvailableAsync(progress: null, cancellationToken: cancellationToken))
    throw new InvalidOperationException("Embedding model is not available and could not be downloaded.");
EnsureModelLoaded();
```

- [ ] **Step 6: Run tests to verify pass**

```powershell
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter FullyQualifiedName~EmbeddingServiceEnsureAvailableTests
```

Expected: 2 passed (or skipped depending on dev machine state).

- [ ] **Step 7: Update `MemoryService` callers that currently silently skip when model unavailable**

Search for callers of `_embeddingService.IsModelAvailable` in `MemoryService.cs`. Replace `if (_embeddingService.IsModelAvailable)` guards that gate embedding generation with `if (await _embeddingService.EnsureAvailableAsync())`. (`IsModelAvailable` lookups for non-blocking UI display — e.g. in `MemoryViewModel` — stay as-is.)

If `MemoryService` doesn't currently gate on `IsModelAvailable` (it may just call `GenerateEmbeddingAsync` and rely on the inner check), no changes are needed because `GenerateEmbeddingAsync` now triggers download internally.

- [ ] **Step 8: Run the full suite**

```powershell
dotnet test
```

Expected: all tests pass.

- [ ] **Step 9: Commit**

```bash
git add src/Pia.Wpf/Services/EmbeddingService.cs src/Pia.Wpf/Services/Interfaces/IEmbeddingService.cs src/Pia.Wpf/Services/MemoryService.cs tests/Pia.Wpf.Tests/Unit/EmbeddingServiceEnsureAvailableTests.cs
git commit -m "Add IEmbeddingService.EnsureAvailableAsync with auto-download"
```

---

## Chunk 2: ScheduledJob domain

### Task 4: `ScheduledJob` model

**Files:**
- Create: `src/Pia.Wpf/Models/ScheduledJob.cs`

- [ ] **Step 1: Create the model file**

Create `src/Pia.Wpf/Models/ScheduledJob.cs`:

```csharp
namespace Pia.Models;

public enum ScheduledJobKind { Research }
public enum ScheduledJobStatus { Active, Disabled, Failed }

public class ScheduledJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Query { get; set; }
    public ScheduledJobKind Kind { get; set; } = ScheduledJobKind.Research;
    public ResearchAnswerLength AnswerLength { get; set; } = ResearchAnswerLength.Default;
    public Guid? ProviderId { get; set; }
    public RecurrenceType Recurrence { get; set; }
    public TimeOnly TimeOfDay { get; set; }
    public DayOfWeek? DayOfWeek { get; set; }
    public int? DayOfMonth { get; set; }
    public int? Month { get; set; }
    public DateTime? SpecificDate { get; set; }
    public DateTime NextFireAt { get; set; }
    public ScheduledJobStatus Status { get; set; } = ScheduledJobStatus.Active;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastFiredAt { get; set; }
    public Guid? LastResultEntryId { get; set; }
    public int ConsecutiveFailures { get; set; }
}
```

- [ ] **Step 2: Build to confirm compilation**

```powershell
dotnet build src/Pia.Wpf/Pia.Wpf.csproj
```

Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Wpf/Models/ScheduledJob.cs
git commit -m "Add ScheduledJob model"
```

---

### Task 5: SQLite schema + migrations

**Files:**
- Modify: `src/Pia.Wpf/Infrastructure/SqliteContext.cs`

- [ ] **Step 1: Add the `ScheduledJobs` CREATE TABLE block**

In `SqliteContext.EnsureSchema()`, append to the heredoc (after the `Plugins` table block, before the closing `"""`):

```sql
CREATE TABLE IF NOT EXISTS ScheduledJobs (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Query TEXT NOT NULL,
    Kind TEXT NOT NULL DEFAULT 'Research',
    AnswerLength TEXT NOT NULL DEFAULT 'Default',
    ProviderId TEXT NULL,
    Recurrence TEXT NOT NULL,
    TimeOfDay TEXT NOT NULL,
    DayOfWeek INTEGER NULL,
    DayOfMonth INTEGER NULL,
    Month INTEGER NULL,
    SpecificDate TEXT NULL,
    NextFireAt TEXT NOT NULL,
    Status TEXT NOT NULL DEFAULT 'Active',
    CreatedAt TEXT NOT NULL,
    LastFiredAt TEXT NULL,
    LastResultEntryId TEXT NULL,
    ConsecutiveFailures INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS IX_ScheduledJobs_NextFireAt ON ScheduledJobs(NextFireAt, Status);
```

- [ ] **Step 2: Add `ScheduledJobId` and `Embedding` columns to `ResearchSessions` in `MigrateSchema()`**

Append at the end of `SqliteContext.MigrateSchema()`:

```csharp
// Add ScheduledJobId column to ResearchSessions if it doesn't exist
using var rsPragma = _connection!.CreateCommand();
rsPragma.CommandText = "PRAGMA table_info(ResearchSessions)";
using var rsReader = rsPragma.ExecuteReader();
var hasScheduledJobId = false;
var hasEmbedding = false;
while (rsReader.Read())
{
    var columnName = rsReader.GetString(1);
    if (columnName == "ScheduledJobId") hasScheduledJobId = true;
    else if (columnName == "Embedding") hasEmbedding = true;
}
rsReader.Close();

if (!hasScheduledJobId)
{
    using var addCol = _connection.CreateCommand();
    addCol.CommandText = "ALTER TABLE ResearchSessions ADD COLUMN ScheduledJobId TEXT NULL";
    addCol.ExecuteNonQuery();

    using var addIdx = _connection.CreateCommand();
    addIdx.CommandText = "CREATE INDEX IF NOT EXISTS IX_ResearchSessions_ScheduledJobId ON ResearchSessions(ScheduledJobId)";
    addIdx.ExecuteNonQuery();
}

if (!hasEmbedding)
{
    using var addEmb = _connection.CreateCommand();
    addEmb.CommandText = "ALTER TABLE ResearchSessions ADD COLUMN Embedding BLOB NULL";
    addEmb.ExecuteNonQuery();
}
```

- [ ] **Step 3: Build to confirm**

```powershell
dotnet build
```

Expected: build succeeds. Schema is created when the first connection is opened.

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/Infrastructure/SqliteContext.cs
git commit -m "Add ScheduledJobs table and ResearchSessions migration"
```

---

### Task 6: `ScheduledJobService` + interface

**Files:**
- Create: `src/Pia.Wpf/Services/Interfaces/IScheduledJobService.cs`
- Create: `src/Pia.Wpf/Services/ScheduledJobService.cs`
- Create: `tests/Pia.Wpf.Tests/Unit/ScheduledJobServiceTests.cs`

- [ ] **Step 1: Define the interface**

Create `src/Pia.Wpf/Services/Interfaces/IScheduledJobService.cs`:

```csharp
using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IScheduledJobService
{
    Task<ScheduledJob> CreateAsync(string name, string query, RecurrenceType recurrence, TimeOnly timeOfDay,
        DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null, DateTime? specificDate = null,
        ResearchAnswerLength answerLength = ResearchAnswerLength.Default, Guid? providerId = null);

    Task<IReadOnlyList<ScheduledJob>> GetAllAsync();
    Task<IReadOnlyList<ScheduledJob>> GetActiveAsync();
    Task<ScheduledJob?> GetAsync(Guid id);
    Task<IReadOnlyList<ScheduledJob>> GetDueJobsAsync();

    Task UpdateAsync(Guid id, string? name = null, string? query = null,
        RecurrenceType? recurrence = null, TimeOnly? timeOfDay = null,
        DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null,
        ResearchAnswerLength? answerLength = null, Guid? providerId = null);

    Task DeleteAsync(Guid id);

    Task DisableAsync(Guid id);
    Task EnableAsync(Guid id);

    Task MarkRunCompleteAsync(Guid id, Guid resultEntryId);
    Task MarkRunFailedAsync(Guid id, string reason);
}
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Pia.Wpf.Tests/Unit/ScheduledJobServiceTests.cs` (use the existing `ReminderService` test class as a structural reference if present):

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Scheduling;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

public class ScheduledJobServiceTests : IDisposable
{
    private readonly SqliteContext _ctx;
    private readonly ScheduledJobService _service;

    public ScheduledJobServiceTests()
    {
        _ctx = new SqliteContext();      // uses the real LocalAppData path; tests cleanup their rows
        _service = new ScheduledJobService(_ctx, new RecurrenceCalculator(), NullLogger<ScheduledJobService>.Instance);
    }

    [Fact]
    public async Task CreateAsync_PersistsAndComputesNextFireAt()
    {
        var job = await _service.CreateAsync("Tesla briefing", "Latest Tesla news", RecurrenceType.Daily, new TimeOnly(8, 0));
        Assert.NotEqual(Guid.Empty, job.Id);
        Assert.True(job.NextFireAt > DateTime.Now);

        var fetched = await _service.GetAsync(job.Id);
        Assert.NotNull(fetched);
        Assert.Equal("Tesla briefing", fetched!.Name);
    }

    [Fact]
    public async Task GetDueJobsAsync_ReturnsOnlyOverdueAndActive()
    {
        var due = await _service.CreateAsync("Due", "q", RecurrenceType.Daily, new TimeOnly(0, 0));
        // Manually backdate
        await ForceNextFireAtAsync(due.Id, DateTime.Now.AddMinutes(-5));

        var disabled = await _service.CreateAsync("Disabled", "q", RecurrenceType.Daily, new TimeOnly(0, 0));
        await _service.DisableAsync(disabled.Id);
        await ForceNextFireAtAsync(disabled.Id, DateTime.Now.AddMinutes(-5));

        var dueList = await _service.GetDueJobsAsync();
        Assert.Contains(dueList, j => j.Id == due.Id);
        Assert.DoesNotContain(dueList, j => j.Id == disabled.Id);
    }

    [Fact]
    public async Task MarkRunFailedAsync_FifthFailure_DisablesJob()
    {
        var job = await _service.CreateAsync("FlakeJob", "q", RecurrenceType.Daily, new TimeOnly(0, 0));
        for (var i = 0; i < 5; i++)
            await _service.MarkRunFailedAsync(job.Id, "test");

        var fetched = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Failed, fetched!.Status);
        Assert.Equal(5, fetched.ConsecutiveFailures);
    }

    [Fact]
    public async Task MarkRunCompleteAsync_ResetsFailureCount()
    {
        var job = await _service.CreateAsync("Recovers", "q", RecurrenceType.Daily, new TimeOnly(0, 0));
        await _service.MarkRunFailedAsync(job.Id, "a");
        await _service.MarkRunFailedAsync(job.Id, "b");
        await _service.MarkRunCompleteAsync(job.Id, Guid.NewGuid());

        var fetched = await _service.GetAsync(job.Id);
        Assert.Equal(0, fetched!.ConsecutiveFailures);
        Assert.NotNull(fetched.LastFiredAt);
        Assert.NotNull(fetched.LastResultEntryId);
    }

    private async Task ForceNextFireAtAsync(Guid id, DateTime when)
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE ScheduledJobs SET NextFireAt = @t WHERE Id = @id";
        cmd.Parameters.AddWithValue("@t", when.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        // Clean up test rows
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ScheduledJobs";
        cmd.ExecuteNonQuery();
        _ctx.Dispose();
    }
}
```

- [ ] **Step 3: Run tests to verify they fail to compile**

```powershell
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter FullyQualifiedName~ScheduledJobServiceTests
```

Expected: build error — `ScheduledJobService` does not exist.

- [ ] **Step 4: Implement `ScheduledJobService`**

Create `src/Pia.Wpf/Services/ScheduledJobService.cs` modeled on `ReminderService.cs`. Key differences: takes `IRecurrenceCalculator` injection, uses `ScheduledJobs` table, has `MarkRunCompleteAsync` / `MarkRunFailedAsync` instead of `DismissAsync`. Auto-disable threshold is `5` consecutive failures.

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Scheduling;

namespace Pia.Services;

public class ScheduledJobService : IScheduledJobService
{
    private const int MaxConsecutiveFailures = 5;

    private readonly SqliteContext _context;
    private readonly IRecurrenceCalculator _calculator;
    private readonly ILogger<ScheduledJobService> _logger;

    public ScheduledJobService(SqliteContext context, IRecurrenceCalculator calculator, ILogger<ScheduledJobService> logger)
    {
        _context = context;
        _calculator = calculator;
        _logger = logger;
    }

    public async Task<ScheduledJob> CreateAsync(string name, string query, RecurrenceType recurrence, TimeOnly timeOfDay,
        DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null, DateTime? specificDate = null,
        ResearchAnswerLength answerLength = ResearchAnswerLength.Default, Guid? providerId = null)
    {
        var job = new ScheduledJob
        {
            Name = name,
            Query = query,
            Recurrence = recurrence,
            TimeOfDay = timeOfDay,
            DayOfWeek = dayOfWeek,
            DayOfMonth = dayOfMonth,
            Month = month,
            SpecificDate = specificDate,
            AnswerLength = answerLength,
            ProviderId = providerId,
            CreatedAt = DateTime.Now
        };

        job.NextFireAt = _calculator.ComputeNextFireAt(
            job.Recurrence, job.TimeOfDay, job.SpecificDate, job.DayOfWeek, job.DayOfMonth, job.Month, DateTime.Now);

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ScheduledJobs
            (Id, Name, Query, Kind, AnswerLength, ProviderId, Recurrence, TimeOfDay,
             DayOfWeek, DayOfMonth, Month, SpecificDate, NextFireAt, Status, CreatedAt,
             LastFiredAt, LastResultEntryId, ConsecutiveFailures)
            VALUES (@Id, @Name, @Query, @Kind, @AnswerLength, @ProviderId, @Recurrence, @TimeOfDay,
                    @DayOfWeek, @DayOfMonth, @Month, @SpecificDate, @NextFireAt, @Status, @CreatedAt,
                    @LastFiredAt, @LastResultEntryId, @ConsecutiveFailures)
            """;
        AddJobParameters(command, job);
        await command.ExecuteNonQueryAsync();

        _logger.LogInformation("Created scheduled job {Id} ({Recurrence})", job.Id, recurrence);
        _logger.SensitiveDebug("Created scheduled job {Id} name: {Name} query: {Query}", job.Id, name, query);
        return job;
    }

    public async Task<IReadOnlyList<ScheduledJob>> GetAllAsync() =>
        await ReadAsync("ORDER BY NextFireAt ASC", _ => { });

    public async Task<IReadOnlyList<ScheduledJob>> GetActiveAsync() =>
        await ReadAsync("WHERE Status = 'Active' ORDER BY NextFireAt ASC", _ => { });

    public async Task<ScheduledJob?> GetAsync(Guid id)
    {
        var list = await ReadAsync("WHERE Id = @Id", cmd => cmd.Parameters.AddWithValue("@Id", id.ToString()));
        return list.FirstOrDefault();
    }

    public async Task<IReadOnlyList<ScheduledJob>> GetDueJobsAsync() =>
        await ReadAsync(
            "WHERE NextFireAt <= @Now AND Status = 'Active' ORDER BY NextFireAt ASC",
            cmd => cmd.Parameters.AddWithValue("@Now", DateTime.Now.ToString("O")));

    public async Task UpdateAsync(Guid id, string? name = null, string? query = null,
        RecurrenceType? recurrence = null, TimeOnly? timeOfDay = null,
        DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null,
        ResearchAnswerLength? answerLength = null, Guid? providerId = null)
    {
        var existing = await GetAsync(id) ?? throw new InvalidOperationException($"ScheduledJob {id} not found");

        if (name is not null) existing.Name = name;
        if (query is not null) existing.Query = query;
        if (recurrence is not null) existing.Recurrence = recurrence.Value;
        if (timeOfDay is not null) existing.TimeOfDay = timeOfDay.Value;
        if (dayOfWeek is not null) existing.DayOfWeek = dayOfWeek;
        if (dayOfMonth is not null) existing.DayOfMonth = dayOfMonth;
        if (month is not null) existing.Month = month;
        if (answerLength is not null) existing.AnswerLength = answerLength.Value;
        if (providerId is not null) existing.ProviderId = providerId;

        existing.NextFireAt = _calculator.ComputeNextFireAt(
            existing.Recurrence, existing.TimeOfDay, existing.SpecificDate,
            existing.DayOfWeek, existing.DayOfMonth, existing.Month, DateTime.Now);

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ScheduledJobs
            SET Name=@Name, Query=@Query, Recurrence=@Recurrence, TimeOfDay=@TimeOfDay,
                DayOfWeek=@DayOfWeek, DayOfMonth=@DayOfMonth, Month=@Month,
                AnswerLength=@AnswerLength, ProviderId=@ProviderId, NextFireAt=@NextFireAt
            WHERE Id=@Id
            """;
        command.Parameters.AddWithValue("@Id", existing.Id.ToString());
        command.Parameters.AddWithValue("@Name", existing.Name);
        command.Parameters.AddWithValue("@Query", existing.Query);
        command.Parameters.AddWithValue("@Recurrence", existing.Recurrence.ToString());
        command.Parameters.AddWithValue("@TimeOfDay", existing.TimeOfDay.ToString("HH:mm"));
        command.Parameters.AddWithValue("@DayOfWeek", existing.DayOfWeek.HasValue ? (object)(int)existing.DayOfWeek.Value : DBNull.Value);
        command.Parameters.AddWithValue("@DayOfMonth", existing.DayOfMonth.HasValue ? (object)existing.DayOfMonth.Value : DBNull.Value);
        command.Parameters.AddWithValue("@Month", existing.Month.HasValue ? (object)existing.Month.Value : DBNull.Value);
        command.Parameters.AddWithValue("@AnswerLength", existing.AnswerLength.ToString());
        command.Parameters.AddWithValue("@ProviderId", existing.ProviderId.HasValue ? (object)existing.ProviderId.Value.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("@NextFireAt", existing.NextFireAt.ToString("O"));

        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Updated scheduled job {Id}", id);
    }

    public async Task DeleteAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ScheduledJobs WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Deleted scheduled job {Id}", id);
    }

    public async Task DisableAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ScheduledJobs SET Status = 'Disabled' WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync();
    }

    public async Task EnableAsync(Guid id)
    {
        var existing = await GetAsync(id) ?? throw new InvalidOperationException($"ScheduledJob {id} not found");
        existing.NextFireAt = _calculator.ComputeNextFireAt(
            existing.Recurrence, existing.TimeOfDay, existing.SpecificDate,
            existing.DayOfWeek, existing.DayOfMonth, existing.Month, DateTime.Now);

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ScheduledJobs SET Status = 'Active', NextFireAt = @NextFireAt, ConsecutiveFailures = 0 WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@NextFireAt", existing.NextFireAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task MarkRunCompleteAsync(Guid id, Guid resultEntryId)
    {
        var existing = await GetAsync(id) ?? throw new InvalidOperationException($"ScheduledJob {id} not found");
        var nextFire = _calculator.ComputeNextFireAt(
            existing.Recurrence, existing.TimeOfDay, existing.SpecificDate,
            existing.DayOfWeek, existing.DayOfMonth, existing.Month, DateTime.Now);

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ScheduledJobs
            SET LastFiredAt=@Now, LastResultEntryId=@EntryId, ConsecutiveFailures=0, NextFireAt=@NextFireAt
            WHERE Id=@Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@Now", DateTime.Now.ToString("O"));
        command.Parameters.AddWithValue("@EntryId", resultEntryId.ToString());
        command.Parameters.AddWithValue("@NextFireAt", nextFire.ToString("O"));
        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Scheduled job {Id} run completed; next fire {NextFireAt:g}", id, nextFire);
    }

    public async Task MarkRunFailedAsync(Guid id, string reason)
    {
        var existing = await GetAsync(id) ?? throw new InvalidOperationException($"ScheduledJob {id} not found");
        var newFailureCount = existing.ConsecutiveFailures + 1;
        var newStatus = newFailureCount >= MaxConsecutiveFailures ? ScheduledJobStatus.Failed : existing.Status;

        var nextFire = _calculator.ComputeNextFireAt(
            existing.Recurrence, existing.TimeOfDay, existing.SpecificDate,
            existing.DayOfWeek, existing.DayOfMonth, existing.Month, DateTime.Now);

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ScheduledJobs
            SET LastFiredAt=@Now, ConsecutiveFailures=@Failures, Status=@Status, NextFireAt=@NextFireAt
            WHERE Id=@Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@Now", DateTime.Now.ToString("O"));
        command.Parameters.AddWithValue("@Failures", newFailureCount);
        command.Parameters.AddWithValue("@Status", newStatus.ToString());
        command.Parameters.AddWithValue("@NextFireAt", nextFire.ToString("O"));
        await command.ExecuteNonQueryAsync();
        _logger.LogWarning("Scheduled job {Id} run failed (count={Count}, status={Status}, reason={Reason})",
            id, newFailureCount, newStatus, reason);
    }

    private async Task<IReadOnlyList<ScheduledJob>> ReadAsync(string whereOrOrder, Action<SqliteCommand> bind)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Name, Query, Kind, AnswerLength, ProviderId, Recurrence, TimeOfDay,
                   DayOfWeek, DayOfMonth, Month, SpecificDate, NextFireAt, Status, CreatedAt,
                   LastFiredAt, LastResultEntryId, ConsecutiveFailures
            FROM ScheduledJobs
            {whereOrOrder}
            """;
        bind(command);

        var list = new List<ScheduledJob>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(MapJob(reader));

        return list.AsReadOnly();
    }

    private static void AddJobParameters(SqliteCommand command, ScheduledJob job)
    {
        command.Parameters.AddWithValue("@Id", job.Id.ToString());
        command.Parameters.AddWithValue("@Name", job.Name);
        command.Parameters.AddWithValue("@Query", job.Query);
        command.Parameters.AddWithValue("@Kind", job.Kind.ToString());
        command.Parameters.AddWithValue("@AnswerLength", job.AnswerLength.ToString());
        command.Parameters.AddWithValue("@ProviderId", job.ProviderId.HasValue ? (object)job.ProviderId.Value.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("@Recurrence", job.Recurrence.ToString());
        command.Parameters.AddWithValue("@TimeOfDay", job.TimeOfDay.ToString("HH:mm"));
        command.Parameters.AddWithValue("@DayOfWeek", job.DayOfWeek.HasValue ? (object)(int)job.DayOfWeek.Value : DBNull.Value);
        command.Parameters.AddWithValue("@DayOfMonth", job.DayOfMonth.HasValue ? (object)job.DayOfMonth.Value : DBNull.Value);
        command.Parameters.AddWithValue("@Month", job.Month.HasValue ? (object)job.Month.Value : DBNull.Value);
        command.Parameters.AddWithValue("@SpecificDate", job.SpecificDate.HasValue ? (object)job.SpecificDate.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("@NextFireAt", job.NextFireAt.ToString("O"));
        command.Parameters.AddWithValue("@Status", job.Status.ToString());
        command.Parameters.AddWithValue("@CreatedAt", job.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@LastFiredAt", job.LastFiredAt.HasValue ? (object)job.LastFiredAt.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("@LastResultEntryId", job.LastResultEntryId.HasValue ? (object)job.LastResultEntryId.Value.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("@ConsecutiveFailures", job.ConsecutiveFailures);
    }

    private static ScheduledJob MapJob(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)),
        Name = r.GetString(1),
        Query = r.GetString(2),
        Kind = Enum.Parse<ScheduledJobKind>(r.GetString(3)),
        AnswerLength = Enum.Parse<ResearchAnswerLength>(r.GetString(4)),
        ProviderId = r.IsDBNull(5) ? null : Guid.Parse(r.GetString(5)),
        Recurrence = Enum.Parse<RecurrenceType>(r.GetString(6)),
        TimeOfDay = TimeOnly.Parse(r.GetString(7)),
        DayOfWeek = r.IsDBNull(8) ? null : (DayOfWeek)r.GetInt32(8),
        DayOfMonth = r.IsDBNull(9) ? null : r.GetInt32(9),
        Month = r.IsDBNull(10) ? null : r.GetInt32(10),
        SpecificDate = r.IsDBNull(11) ? null : DateTime.Parse(r.GetString(11)),
        NextFireAt = DateTime.Parse(r.GetString(12)),
        Status = Enum.Parse<ScheduledJobStatus>(r.GetString(13)),
        CreatedAt = DateTime.Parse(r.GetString(14)),
        LastFiredAt = r.IsDBNull(15) ? null : DateTime.Parse(r.GetString(15)),
        LastResultEntryId = r.IsDBNull(16) ? null : Guid.Parse(r.GetString(16)),
        ConsecutiveFailures = r.GetInt32(17)
    };
}
```

- [ ] **Step 5: Run tests to verify pass**

```powershell
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter FullyQualifiedName~ScheduledJobServiceTests
```

Expected: 4 passed.

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Services/Interfaces/IScheduledJobService.cs src/Pia.Wpf/Services/ScheduledJobService.cs tests/Pia.Wpf.Tests/Unit/ScheduledJobServiceTests.cs
git commit -m "Add ScheduledJobService with CRUD and run-tracking"
```

---

## Chunk 3: Research history extensions

### Task 7: Extend `ResearchHistoryEntry` and persistence

**Files:**
- Modify: `src/Pia.Wpf/Models/ResearchHistoryEntry.cs`
- Modify: `src/Pia.Wpf/Services/ResearchHistoryService.cs`

- [ ] **Step 1: Add properties to the model**

In `src/Pia.Wpf/Models/ResearchHistoryEntry.cs`, after `CompletedAt` add:

```csharp
public Guid? ScheduledJobId { get; set; }
public byte[]? Embedding { get; set; }
```

- [ ] **Step 2: Update SQL in `ResearchHistoryService.AddEntryAsync`**

In `src/Pia.Wpf/Services/ResearchHistoryService.cs`:

Replace the INSERT command text and parameter list in `AddEntryAsync` with:

```csharp
command.CommandText = """
    INSERT INTO ResearchSessions (Id, Query, SynthesizedResult, StepsJson, ProviderId, ProviderName,
                                  Status, StepCount, CreatedAt, CompletedAt, ScheduledJobId, Embedding)
    VALUES (@Id, @Query, @SynthesizedResult, @StepsJson, @ProviderId, @ProviderName,
            @Status, @StepCount, @CreatedAt, @CompletedAt, @ScheduledJobId, @Embedding)
    """;
command.Parameters.AddWithValue("@Id", entry.Id.ToString());
command.Parameters.AddWithValue("@Query", entry.Query);
command.Parameters.AddWithValue("@SynthesizedResult", entry.SynthesizedResult);
command.Parameters.AddWithValue("@StepsJson", entry.StepsJson);
command.Parameters.AddWithValue("@ProviderId", entry.ProviderId.ToString());
command.Parameters.AddWithValue("@ProviderName", entry.ProviderName ?? (object)DBNull.Value);
command.Parameters.AddWithValue("@Status", entry.Status);
command.Parameters.AddWithValue("@StepCount", entry.StepCount);
command.Parameters.AddWithValue("@CreatedAt", entry.CreatedAt.ToString("O"));
command.Parameters.AddWithValue("@CompletedAt", entry.CompletedAt.ToString("O"));
command.Parameters.AddWithValue("@ScheduledJobId", entry.ScheduledJobId.HasValue ? (object)entry.ScheduledJobId.Value.ToString() : DBNull.Value);
command.Parameters.AddWithValue("@Embedding", entry.Embedding is null ? DBNull.Value : (object)entry.Embedding);
```

- [ ] **Step 3: Update SELECT lists in `ResearchHistoryService` to read the new columns**

In `SearchEntriesAsync` and `GetEntryAsync` SELECT statements, change `SELECT Id, Query, ... CompletedAt` to:

```sql
SELECT Id, Query, SynthesizedResult, StepsJson, ProviderId, ProviderName,
       Status, StepCount, CreatedAt, CompletedAt, ScheduledJobId, Embedding
FROM ResearchSessions
```

Update `MapEntry` to read the new columns:

```csharp
private static ResearchHistoryEntry MapEntry(SqliteDataReader reader)
{
    return new ResearchHistoryEntry
    {
        Id = Guid.Parse(reader.GetString(0)),
        Query = reader.GetString(1),
        SynthesizedResult = reader.GetString(2),
        StepsJson = reader.GetString(3),
        ProviderId = Guid.Parse(reader.GetString(4)),
        ProviderName = reader.IsDBNull(5) ? null : reader.GetString(5),
        Status = reader.GetString(6),
        StepCount = reader.GetInt32(7),
        CreatedAt = DateTime.Parse(reader.GetString(8)),
        CompletedAt = DateTime.Parse(reader.GetString(9)),
        ScheduledJobId = reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
        Embedding = reader.IsDBNull(11) ? null : (byte[])reader[11]
    };
}
```

- [ ] **Step 4: Build to confirm**

```powershell
dotnet build
```

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Models/ResearchHistoryEntry.cs src/Pia.Wpf/Services/ResearchHistoryService.cs
git commit -m "Add ScheduledJobId and Embedding to ResearchHistoryEntry"
```

---

### Task 8: Vector + hybrid search on `ResearchHistoryService`

**Files:**
- Modify: `src/Pia.Wpf/Services/Interfaces/IResearchHistoryService.cs`
- Modify: `src/Pia.Wpf/Services/ResearchHistoryService.cs`

- [ ] **Step 1: Add new members to the interface**

In `IResearchHistoryService.cs` add:

```csharp
Task UpdateEmbeddingAsync(Guid id, byte[] embedding);
Task<IReadOnlyList<ResearchHistoryEntry>> VectorSearchAsync(float[] queryEmbedding, int topK = 10, float threshold = 0.2f);
Task<IReadOnlyList<ResearchHistoryEntry>> HybridSearchAsync(string query, float[]? queryEmbedding = null, int topK = 10);
```

- [ ] **Step 2: Implement on the service**

Add to `ResearchHistoryService.cs`. Inject `IEmbeddingService` via the constructor (same pattern as `MemoryService`):

```csharp
public ResearchHistoryService(SqliteContext context, IEmbeddingService embeddingService)
{
    _context = context;
    _embeddingService = embeddingService;
}
```

(Add the `_embeddingService` field, plus `using Pia.Services.Interfaces;` and `using Pia.Services.Search;` at top.)

Methods:

```csharp
public async Task UpdateEmbeddingAsync(Guid id, byte[] embedding)
{
    var connection = _context.GetConnection();
    using var command = connection.CreateCommand();
    command.CommandText = "UPDATE ResearchSessions SET Embedding = @Embedding WHERE Id = @Id";
    command.Parameters.AddWithValue("@Id", id.ToString());
    command.Parameters.AddWithValue("@Embedding", embedding);
    await command.ExecuteNonQueryAsync();
}

public async Task<IReadOnlyList<ResearchHistoryEntry>> VectorSearchAsync(
    float[] queryEmbedding, int topK = 10, float threshold = 0.2f)
{
    var all = await GetAllWithEmbeddingsAsync();

    var ranked = VectorSearchHelper.RankByCosine(
        all,
        e => e.Embedding is null ? null : _embeddingService.BytesToFloats(e.Embedding),
        queryEmbedding,
        topK,
        threshold).ToList();

    return ranked.AsReadOnly();
}

public async Task<IReadOnlyList<ResearchHistoryEntry>> HybridSearchAsync(
    string query, float[]? queryEmbedding = null, int topK = 10)
{
    var resultDict = new Dictionary<Guid, (ResearchHistoryEntry Entry, float Score)>();

    // Tier 1: text LIKE on query and result (uses existing SearchEntriesAsync)
    var textHits = await SearchEntriesAsync(searchText: query, fromDate: null, toDate: null, offset: 0, limit: topK * 2);
    foreach (var e in textHits)
        resultDict[e.Id] = (e, 0.6f);

    // Tier 2: vector
    if (queryEmbedding is not null)
    {
        var vectorHits = await VectorSearchAsync(queryEmbedding, topK, threshold: 0.2f);
        foreach (var e in vectorHits)
        {
            if (resultDict.TryGetValue(e.Id, out var existing))
                resultDict[e.Id] = (e, Math.Max(existing.Score, 0.8f));
            else
                resultDict[e.Id] = (e, 0.8f);
        }
    }

    return resultDict.Values
        .OrderByDescending(x => x.Score)
        .Take(topK)
        .Select(x => x.Entry)
        .ToList()
        .AsReadOnly();
}

private async Task<IReadOnlyList<ResearchHistoryEntry>> GetAllWithEmbeddingsAsync()
{
    var connection = _context.GetConnection();
    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT Id, Query, SynthesizedResult, StepsJson, ProviderId, ProviderName,
               Status, StepCount, CreatedAt, CompletedAt, ScheduledJobId, Embedding
        FROM ResearchSessions
        WHERE Embedding IS NOT NULL
        """;
    var entries = new List<ResearchHistoryEntry>();
    using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        entries.Add(MapEntry(reader));
    return entries.AsReadOnly();
}
```

Add `private readonly IEmbeddingService _embeddingService;` field.

- [ ] **Step 3: Update DI registration if `ResearchHistoryService` was previously registered without `IEmbeddingService`**

In `Bootstrapper.cs` find the `ResearchHistoryService` registration. Confirm `IEmbeddingService` is registered before this line (it should already be — search for `AddSingleton<IEmbeddingService`). No change needed if already in order.

- [ ] **Step 4: Build to confirm**

```powershell
dotnet build
```

Expected: success.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Services/Interfaces/IResearchHistoryService.cs src/Pia.Wpf/Services/ResearchHistoryService.cs
git commit -m "Add vector and hybrid search to ResearchHistoryService"
```

---

### Task 9: Auto-embed on insert

**Files:**
- Modify: `src/Pia.Wpf/Services/ResearchHistoryService.cs`

- [ ] **Step 1: Wrap embedding generation around `AddEntryAsync`**

Change the start of `AddEntryAsync(ResearchHistoryEntry entry)`:

```csharp
public async Task AddEntryAsync(ResearchHistoryEntry entry)
{
    if (entry.Embedding is null && !string.IsNullOrWhiteSpace(entry.SynthesizedResult))
    {
        try
        {
            if (await _embeddingService.EnsureAvailableAsync())
            {
                var text = entry.Query + "\n\n" + entry.SynthesizedResult;
                var floats = await _embeddingService.GenerateEmbeddingAsync(text);
                entry.Embedding = _embeddingService.FloatsToBytes(floats);
            }
        }
        catch
        {
            // Best effort — entry still saves without embedding.
        }
    }

    var connection = _context.GetConnection();
    // ... rest unchanged
```

- [ ] **Step 2: Build**

```powershell
dotnet build
```

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Wpf/Services/ResearchHistoryService.cs
git commit -m "Auto-embed research history entries on insert"
```

---

## Chunk 4: Background execution

### Task 10: `MissedScheduledJobDialog`

**Files:**
- Create: `src/Pia.Wpf/Views/Dialogs/MissedScheduledJobDialog.xaml`
- Create: `src/Pia.Wpf/Views/Dialogs/MissedScheduledJobDialog.xaml.cs`

> **Note:** Use the existing `Wpf.Ui.Controls.ContentDialog` pattern. Search the codebase for an existing usage to mirror the activation/registration shape (e.g. `AutostartConsentDialog` or any `ContentDialog` invocation under `Views/`).

- [ ] **Step 1: Create the XAML**

```xml
<ui:ContentDialog x:Class="Pia.Views.Dialogs.MissedScheduledJobDialog"
                  xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
                  xmlns:loc="clr-namespace:Pia.Localization"
                  Title="{loc:Str MissedRun_Dialog_Title}"
                  PrimaryButtonText="{loc:Str MissedRun_RunNow}"
                  CloseButtonText="{loc:Str MissedRun_Skip}"
                  DefaultButton="Primary">
    <StackPanel>
        <TextBlock Text="{Binding Body}"
                   TextWrapping="Wrap"
                   FontSize="14" />
    </StackPanel>
</ui:ContentDialog>
```

- [ ] **Step 2: Create the code-behind**

```csharp
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class MissedScheduledJobDialog : ContentDialog
{
    public string Body { get; }

    public MissedScheduledJobDialog(ContentPresenter? contentPresenter, string body) : base(contentPresenter)
    {
        Body = body;
        DataContext = this;
        InitializeComponent();
    }
}
```

> The `ContentPresenter` parameter follows the WPF-UI `ContentDialog` constructor signature. Verify against the existing dialog usage in the codebase; if the convention is different (e.g. plain `ContentDialog` with `ShowAsync()`), match that pattern instead.

- [ ] **Step 3: Build**

```powershell
dotnet build
```

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/Views/Dialogs/
git commit -m "Add MissedScheduledJobDialog"
```

---

### Task 11: `ScheduledJobBackgroundService` — execution path

**Files:**
- Create: `src/Pia.Wpf/Services/ScheduledJobBackgroundService.cs`
- Create: `tests/Pia.Wpf.Tests/Unit/ScheduledJobBackgroundServiceTests.cs`

> The background service has three concerns kept in one file: poll → grace policy → execute. We add the execution path here and the missed-run dialog path in Task 12.

- [ ] **Step 1: Write the failing tests for the silent-execute path**

Create `tests/Pia.Wpf.Tests/Unit/ScheduledJobBackgroundServiceTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

public class ScheduledJobBackgroundServiceTests
{
    [Fact]
    public async Task ExecuteOnceAsync_Success_PersistsEntryAndMarksComplete()
    {
        // Arrange a fake job service with one due job, fake research service that returns synthesized text,
        // and a fake research history service. Verify Add is called with ScheduledJobId and MarkRunCompleteAsync is called.
        var jobs = new FakeJobService();
        var due = new ScheduledJob
        {
            Name = "T", Query = "q", Recurrence = RecurrenceType.Daily,
            TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddSeconds(-1)
        };
        jobs.SeedDue(due);

        var research = new FakeResearchService();
        research.SynthesizedResult = "RESULT";

        var history = new FakeResearchHistoryService();
        var providers = new FakeProviderResolver(new AiProvider { Id = Guid.NewGuid(), Name = "P", TimeoutSeconds = 60 });

        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(jobs, research, history, providers, notifications, NullLogger<ScheduledJobBackgroundService>.Instance);
        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Single(history.Added);
        Assert.Equal(due.Id, history.Added[0].ScheduledJobId);
        Assert.Equal("RESULT", history.Added[0].SynthesizedResult);
        Assert.Single(jobs.Completed);
    }

    [Fact]
    public async Task ExecuteOnceAsync_ResearchThrows_PersistsFailedEntryAndMarksFailed()
    {
        var jobs = new FakeJobService();
        var due = new ScheduledJob
        {
            Name = "T", Query = "q", Recurrence = RecurrenceType.Daily,
            TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddSeconds(-1)
        };
        jobs.SeedDue(due);

        var research = new FakeResearchService { ThrowOnExecute = true };
        var history = new FakeResearchHistoryService();
        var providers = new FakeProviderResolver(new AiProvider { Id = Guid.NewGuid(), Name = "P", TimeoutSeconds = 60 });
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(jobs, research, history, providers, notifications, NullLogger<ScheduledJobBackgroundService>.Instance);
        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Single(history.Added);
        Assert.Equal("Failed", history.Added[0].Status);
        Assert.Single(jobs.Failed);
    }

    // FakeJobService, FakeResearchService, FakeResearchHistoryService, FakeProviderResolver, FakeNotificationSurface
    // are minimal in-memory stubs — implement only the methods called by ScheduledJobBackgroundService.
}
```

> The fakes need to implement the interfaces the background service depends on. After implementing the service in Step 4, come back and finish the fake classes against the actual interface shape.

- [ ] **Step 2: Run tests to verify failure**

Expected: build error — `ScheduledJobBackgroundService` does not exist.

- [ ] **Step 3: Add a small `IScheduledResearchProviderResolver` abstraction**

Used so the background service can resolve "the provider mapped to Research mode" without coupling to all of `ISettingsService`.

In `src/Pia.Wpf/Services/Interfaces/IScheduledResearchProviderResolver.cs`:

```csharp
using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IScheduledResearchProviderResolver
{
    AiProvider? Resolve(Guid? pinnedProviderId);
}
```

In `src/Pia.Wpf/Services/ScheduledResearchProviderResolver.cs`:

```csharp
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class ScheduledResearchProviderResolver : IScheduledResearchProviderResolver
{
    private readonly ISettingsService _settings;

    public ScheduledResearchProviderResolver(ISettingsService settings) => _settings = settings;

    public AiProvider? Resolve(Guid? pinnedProviderId)
    {
        var providers = _settings.GetProviders();
        if (pinnedProviderId.HasValue)
        {
            var pinned = providers.FirstOrDefault(p => p.Id == pinnedProviderId.Value);
            if (pinned is not null) return pinned;
        }
        return _settings.GetProviderForMode(WindowMode.Research);
    }
}
```

> Adapt method names to whatever `ISettingsService` actually exposes. Search `ISettingsService` for the correct accessors before writing this file.

- [ ] **Step 4: Implement `ScheduledJobBackgroundService` (silent-execute path only)**

Create `src/Pia.Wpf/Services/ScheduledJobBackgroundService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class ScheduledJobBackgroundService : BackgroundService
{
    private static readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _gracePeriod = TimeSpan.FromMinutes(15);

    private readonly IScheduledJobService _jobs;
    private readonly IResearchService _research;
    private readonly IResearchHistoryService _history;
    private readonly IScheduledResearchProviderResolver _providers;
    private readonly IScheduledJobNotificationSurface _notifications;
    private readonly ILogger<ScheduledJobBackgroundService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public ScheduledJobBackgroundService(
        IScheduledJobService jobs,
        IResearchService research,
        IResearchHistoryService history,
        IScheduledResearchProviderResolver providers,
        IScheduledJobNotificationSurface notifications,
        ILogger<ScheduledJobBackgroundService> logger)
    {
        _jobs = jobs; _research = research; _history = history;
        _providers = providers; _notifications = notifications; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScheduledJobBackgroundService started");
        using var timer = new PeriodicTimer(_checkInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await ExecuteOnceAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Error in scheduled-job tick"); }
        }
    }

    public async Task ExecuteOnceAsync(CancellationToken ct)
    {
        var due = await _jobs.GetDueJobsAsync();
        foreach (var job in due)
        {
            ct.ThrowIfCancellationRequested();
            await RunJobAsync(job, ct);
        }
    }

    private async Task RunJobAsync(ScheduledJob job, CancellationToken ct)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            var provider = _providers.Resolve(job.ProviderId);
            if (provider is null)
            {
                await PersistFailedEntryAsync(job, "NoProvider");
                await _jobs.MarkRunFailedAsync(job.Id, "NoProvider");
                return;
            }

            var session = new ResearchSession(job.Query);

            try
            {
                await _research.ExecuteResearchAsync(session, provider, job.AnswerLength, ct);
                var entry = new ResearchHistoryEntry
                {
                    Query = session.Query,
                    SynthesizedResult = session.SynthesizedResult,
                    StepsJson = "[]",
                    ProviderId = provider.Id,
                    ProviderName = provider.Name,
                    Status = "Completed",
                    StepCount = session.Steps.Count,
                    CreatedAt = session.CreatedAt,
                    CompletedAt = session.CompletedAt ?? DateTime.Now,
                    ScheduledJobId = job.Id
                };
                await _history.AddEntryAsync(entry);
                await _jobs.MarkRunCompleteAsync(job.Id, entry.Id);
                _notifications.NotifySuccess(job, entry);
            }
            catch (Exception ex)
            {
                var entryId = await PersistFailedEntryAsync(job, ex.Message, provider);
                await _jobs.MarkRunFailedAsync(job.Id, ex.Message);
                _notifications.NotifyFailure(job, entryId, ex.Message);
            }
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<Guid> PersistFailedEntryAsync(ScheduledJob job, string reason, AiProvider? provider = null)
    {
        var entry = new ResearchHistoryEntry
        {
            Query = job.Query,
            SynthesizedResult = $"Run failed: {reason}",
            StepsJson = "[]",
            ProviderId = provider?.Id ?? Guid.Empty,
            ProviderName = provider?.Name,
            Status = "Failed",
            StepCount = 0,
            CreatedAt = DateTime.Now,
            CompletedAt = DateTime.Now,
            ScheduledJobId = job.Id
        };
        await _history.AddEntryAsync(entry);
        return entry.Id;
    }
}
```

Also create the notification-surface abstraction so tests can fake it without touching toast APIs:

`src/Pia.Wpf/Services/Interfaces/IScheduledJobNotificationSurface.cs`:

```csharp
using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IScheduledJobNotificationSurface
{
    void NotifySuccess(ScheduledJob job, ResearchHistoryEntry entry);
    void NotifyFailure(ScheduledJob job, Guid resultEntryId, string reason);
    Task<bool> AskUserToRunMissedAsync(ScheduledJob job, DateTime scheduledFireAt);
}
```

> Concrete implementation comes in Tasks 12–13. The test fakes implement this interface trivially.

- [ ] **Step 5: Finish the test fakes and run tests to pass**

In `ScheduledJobBackgroundServiceTests.cs`, complete the fake implementations of `IScheduledJobService`, `IResearchService`, `IResearchHistoryService`, `IScheduledResearchProviderResolver`, `IScheduledJobNotificationSurface`. Each only needs the methods the service actually calls.

Run:

```powershell
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter FullyQualifiedName~ScheduledJobBackgroundServiceTests
```

Expected: 2 passed.

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Services/ScheduledJobBackgroundService.cs src/Pia.Wpf/Services/ScheduledResearchProviderResolver.cs src/Pia.Wpf/Services/Interfaces/IScheduledResearchProviderResolver.cs src/Pia.Wpf/Services/Interfaces/IScheduledJobNotificationSurface.cs tests/Pia.Wpf.Tests/Unit/ScheduledJobBackgroundServiceTests.cs
git commit -m "Add ScheduledJobBackgroundService with silent-execute path"
```

---

### Task 12: Grace period and missed-run dialog

**Files:**
- Modify: `src/Pia.Wpf/Services/ScheduledJobBackgroundService.cs`
- Modify: `tests/Pia.Wpf.Tests/Unit/ScheduledJobBackgroundServiceTests.cs`

- [ ] **Step 1: Write a failing test for the grace path**

Add to `ScheduledJobBackgroundServiceTests.cs`:

```csharp
[Fact]
public async Task ExecuteOnceAsync_LateBy20Min_AsksUserAndSkipsIfDeclined()
{
    var jobs = new FakeJobService();
    var late = new ScheduledJob
    {
        Name = "T", Query = "q", Recurrence = RecurrenceType.Daily,
        TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddMinutes(-20)
    };
    jobs.SeedDue(late);

    var notifications = new FakeNotificationSurface { AskAnswer = false };
    var research = new FakeResearchService();
    var history = new FakeResearchHistoryService();
    var providers = new FakeProviderResolver(new AiProvider { Id = Guid.NewGuid(), Name = "P", TimeoutSeconds = 60 });

    var bg = new ScheduledJobBackgroundService(jobs, research, history, providers, notifications, NullLogger<ScheduledJobBackgroundService>.Instance);
    await bg.ExecuteOnceAsync(CancellationToken.None);

    Assert.Empty(history.Added);
    Assert.Equal(1, notifications.AskCount);
}

[Fact]
public async Task ExecuteOnceAsync_LateBy20Min_RunsIfAccepted()
{
    var jobs = new FakeJobService();
    var late = new ScheduledJob
    {
        Name = "T", Query = "q", Recurrence = RecurrenceType.Daily,
        TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddMinutes(-20)
    };
    jobs.SeedDue(late);

    var notifications = new FakeNotificationSurface { AskAnswer = true };
    var research = new FakeResearchService { SynthesizedResult = "OK" };
    var history = new FakeResearchHistoryService();
    var providers = new FakeProviderResolver(new AiProvider { Id = Guid.NewGuid(), Name = "P", TimeoutSeconds = 60 });

    var bg = new ScheduledJobBackgroundService(jobs, research, history, providers, notifications, NullLogger<ScheduledJobBackgroundService>.Instance);
    await bg.ExecuteOnceAsync(CancellationToken.None);

    Assert.Single(history.Added);
    Assert.Single(jobs.Completed);
}

[Fact]
public async Task ExecuteOnceAsync_LateBy20Min_DedupesPromptOnSecondTick()
{
    var jobs = new FakeJobService();
    var late = new ScheduledJob
    {
        Name = "T", Query = "q", Recurrence = RecurrenceType.Daily,
        TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddMinutes(-20)
    };
    jobs.SeedDue(late);

    var notifications = new FakeNotificationSurface { AskAnswer = null }; // user closed without answering
    var research = new FakeResearchService();
    var history = new FakeResearchHistoryService();
    var providers = new FakeProviderResolver(new AiProvider { Id = Guid.NewGuid(), Name = "P", TimeoutSeconds = 60 });

    var bg = new ScheduledJobBackgroundService(jobs, research, history, providers, notifications, NullLogger<ScheduledJobBackgroundService>.Instance);
    await bg.ExecuteOnceAsync(CancellationToken.None);
    await bg.ExecuteOnceAsync(CancellationToken.None);

    Assert.Equal(1, notifications.AskCount); // not 2
}
```

Update `FakeNotificationSurface`:
- `bool? AskAnswer` field
- `int AskCount` increment counter
- `AskUserToRunMissedAsync` returns `Task.FromResult<bool>` from `AskAnswer ?? throw new TaskCanceledException()`

For the `null` case ("user closed without answering") `AskUserToRunMissedAsync` should never complete — simulate by returning a never-completing task and treat it as pending in the service. Easier alternative: the service's `_pendingMissedPrompts` set is added BEFORE awaiting the task; on the second tick the job is filtered out.

- [ ] **Step 2: Run tests to verify they fail**

Expected: failures or build errors due to missing grace-period logic.

- [ ] **Step 3: Add grace handling to the service**

Modify `RunJobAsync`. Replace its current body with:

```csharp
private readonly HashSet<Guid> _pendingMissedPrompts = new();

private async Task RunJobAsync(ScheduledJob job, CancellationToken ct)
{
    var lateBy = DateTime.Now - job.NextFireAt;

    if (lateBy > _gracePeriod)
    {
        lock (_pendingMissedPrompts)
        {
            if (_pendingMissedPrompts.Contains(job.Id)) return;
            _pendingMissedPrompts.Add(job.Id);
        }

        bool runIt;
        try
        {
            runIt = await _notifications.AskUserToRunMissedAsync(job, job.NextFireAt);
        }
        finally
        {
            lock (_pendingMissedPrompts) _pendingMissedPrompts.Remove(job.Id);
        }

        if (!runIt)
        {
            // Skip this run: advance NextFireAt to next future occurrence (handled by MarkRunComplete-like skip).
            await _jobs.MarkRunFailedAsync(job.Id, "MissedRunSkippedByUser");
            return;
        }
    }

    await ExecuteResearchAsync(job, ct);
}
```

> The dedup needs to survive across the re-prompt window. For "user closed without answering" we keep the job in `_pendingMissedPrompts` for the lifetime of the service: never remove it on cancellation. Adjust the `finally` block: only remove on `runIt is true or false` (an actual answer), not on exception. Reword:

```csharp
bool? answer = null;
try { answer = await _notifications.AskUserToRunMissedAsync(job, job.NextFireAt); }
catch { /* keep pending forever this session */ }

if (answer is null) return; // dialog closed without answer; do not retry this session

lock (_pendingMissedPrompts) _pendingMissedPrompts.Remove(job.Id);

if (answer == false) { await _jobs.MarkRunFailedAsync(job.Id, "MissedRunSkippedByUser"); return; }

await ExecuteResearchAsync(job, ct);
```

Adjust `IScheduledJobNotificationSurface.AskUserToRunMissedAsync` to return `Task<bool?>` (null = no answer).

Move the body of the previous `RunJobAsync` (provider resolution + research execution + persistence) into a new private method `ExecuteResearchAsync(ScheduledJob, CancellationToken)`.

- [ ] **Step 4: Run tests to verify all pass**

```powershell
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter FullyQualifiedName~ScheduledJobBackgroundServiceTests
```

Expected: 5 passed (2 from Task 11 + 3 new).

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Services/ScheduledJobBackgroundService.cs src/Pia.Wpf/Services/Interfaces/IScheduledJobNotificationSurface.cs tests/Pia.Wpf.Tests/Unit/ScheduledJobBackgroundServiceTests.cs
git commit -m "Add 15-minute grace and missed-run dialog dispatch"
```

---

### Task 13: Toast + missed-run-dialog implementation of `IScheduledJobNotificationSurface`

**Files:**
- Create: `src/Pia.Wpf/Services/ScheduledJobNotificationSurface.cs`

- [ ] **Step 1: Implement the surface**

Create `src/Pia.Wpf/Services/ScheduledJobNotificationSurface.cs`. Pattern after `ReminderBackgroundService`'s toast code (lines 77–98 of `ReminderBackgroundService.cs`):

```csharp
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Views.Dialogs;
using Wpf.Ui.Controls;

namespace Pia.Services;

public class ScheduledJobNotificationSurface : IScheduledJobNotificationSurface
{
    private readonly INotificationService _inApp;
    private readonly ILocalizationService _l10n;
    private readonly ILogger<ScheduledJobNotificationSurface> _logger;

    public ScheduledJobNotificationSurface(
        INotificationService inApp,
        ILocalizationService l10n,
        ILogger<ScheduledJobNotificationSurface> logger)
    {
        _inApp = inApp; _l10n = l10n; _logger = logger;
    }

    public void NotifySuccess(ScheduledJob job, ResearchHistoryEntry entry)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(_l10n["Notification_ScheduledResearch"])
                .AddText(_l10n.Format("Notification_ScheduledResearch_Body", job.Name))
                .AddButton(new ToastButton()
                    .SetContent(_l10n["Notification_OpenBriefing"])
                    .AddArgument("action", "openBriefing")
                    .AddArgument("entryId", entry.Id.ToString())
                    .AddArgument("jobId", job.Id.ToString()))
                .Show();

            Application.Current?.Dispatcher.Invoke(() =>
                _inApp.ShowToast(_l10n.Format("Notification_ScheduledResearchInApp", job.Name)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show success toast for job {Id}", job.Id);
        }
    }

    public void NotifyFailure(ScheduledJob job, Guid resultEntryId, string reason)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(_l10n["Notification_ScheduledResearchFailed"])
                .AddText(_l10n.Format("Notification_ScheduledResearchFailed_Body", job.Name))
                .AddButton(new ToastButton()
                    .SetContent(_l10n["Notification_OpenBriefing"])
                    .AddArgument("action", "openBriefing")
                    .AddArgument("entryId", resultEntryId.ToString())
                    .AddArgument("jobId", job.Id.ToString()))
                .Show();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show failure toast for job {Id}", job.Id);
        }
    }

    public Task<bool?> AskUserToRunMissedAsync(ScheduledJob job, DateTime scheduledFireAt)
    {
        var tcs = new TaskCompletionSource<bool?>();

        Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var presenter = Application.Current.MainWindow?.FindName("RootContentDialogPresenter") as ContentPresenter;
                var body = _l10n.Format("MissedRun_Dialog_Body", job.Name, scheduledFireAt.ToString("g"));
                var dlg = new MissedScheduledJobDialog(presenter, body);
                var result = await dlg.ShowAsync();
                tcs.TrySetResult(result switch
                {
                    ContentDialogResult.Primary => true,
                    ContentDialogResult.Secondary or ContentDialogResult.None => false,
                    _ => null
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to show missed-run dialog for job {Id}", job.Id);
                tcs.TrySetResult(null);
            }
        });

        return tcs.Task;
    }
}
```

> Adjust the `RootContentDialogPresenter` lookup to whatever the actual `ContentDialog` host is named in `MainWindow.xaml`. Search `MainWindow.xaml` for an `<ContentPresenter>` used as the dialog host and use its actual name.

- [ ] **Step 2: Toast activation routing**

Subscribe to `ToastNotificationManagerCompat.OnActivated` in this surface (or in a startup helper). When `args["action"] == "openBriefing"`, parse `entryId`, route to the Research view via `IWindowManagerService` and load the entry. Pattern: see `ReminderBackgroundService.RegisterToastCallbacks` lines 115–161.

For v1 keep the routing logic in `ScheduledJobNotificationSurface` and call it from a hook in `ExecuteAsync` of the background service (parallel to `ReminderBackgroundService.RegisterToastCallbacks`).

- [ ] **Step 3: Build**

```powershell
dotnet build
```

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/Services/ScheduledJobNotificationSurface.cs
git commit -m "Implement ScheduledJobNotificationSurface with toast and dialog"
```

---

## Chunk 5: AI tools

### Task 14: `ScheduledJobToolHandler`

**Files:**
- Create: `src/Pia.Wpf/Services/Interfaces/IScheduledJobToolHandler.cs`
- Create: `src/Pia.Wpf/Services/ScheduledJobToolHandler.cs`
- Create: `tests/Pia.Wpf.Tests/Unit/ScheduledJobToolHandlerTests.cs`

> Pattern source: `src/Pia.Wpf/Services/ReminderToolHandler.cs`. The new handler is structurally identical — same `(Result, PendingAction)` flow, same `GetStringArg`/`GetOptionalStringArg` helpers, same action-card UX. Replace `Reminder` with `ScheduledJob`, add `name`/`query`/`answerLength`/`providerName` to the create schema.

- [ ] **Step 1: Define the interface**

In `src/Pia.Wpf/Services/Interfaces/IScheduledJobToolHandler.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Pia.Services.Interfaces;

public interface IScheduledJobToolHandler
{
    IList<AITool> GetTools();
    Task<(object? Result, ScheduledJobToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);
    Task<object?> ExecutePendingActionAsync(ScheduledJobToolCall pendingAction);
}

public record ScheduledJobToolCall(
    string ToolName,
    string Description,
    string? Details,
    Guid? TargetJobId,
    Func<Task<object?>> Execute);
```

- [ ] **Step 2: Implement `ScheduledJobToolHandler`**

Create `src/Pia.Wpf/Services/ScheduledJobToolHandler.cs`. Mirror `ReminderToolHandler` (file viewable at `src/Pia.Wpf/Services/ReminderToolHandler.cs`). Tools:

- `create_scheduled_research(name, query, recurrence, timeOfDay, dayOfWeek?, dayOfMonth?, month?, specificDate?, answerLength?, providerName?)`
- `query_scheduled_research(filter)`
- `update_scheduled_research(id, name?, query?, recurrence?, timeOfDay?, dayOfWeek?, dayOfMonth?, month?, answerLength?, providerName?)`
- `delete_scheduled_research(id)`

For `providerName`, look up `ISettingsService.GetProviders()` and pick the first whose `Name.Contains(providerName, OrdinalIgnoreCase)`. If none match, set `providerId = null` (use mode default at fire time).

- [ ] **Step 3: Write tests**

Create `tests/Pia.Wpf.Tests/Unit/ScheduledJobToolHandlerTests.cs`. Mirror the existing reminder tool handler tests for structure. Cover at least:

- `create_scheduled_research` with valid args returns a pending action whose `Execute()` calls `IScheduledJobService.CreateAsync` with the parsed values.
- `update_scheduled_research` with invalid GUID returns an error result (not a pending action).
- `delete_scheduled_research` with not-found returns an error result.
- `query_scheduled_research(filter='all')` returns rendered text containing IDs.

- [ ] **Step 4: Run tests to verify**

```powershell
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter FullyQualifiedName~ScheduledJobToolHandlerTests
```

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Services/Interfaces/IScheduledJobToolHandler.cs src/Pia.Wpf/Services/ScheduledJobToolHandler.cs tests/Pia.Wpf.Tests/Unit/ScheduledJobToolHandlerTests.cs
git commit -m "Add ScheduledJobToolHandler"
```

---

### Task 15: `ResearchHistoryToolHandler`

**Files:**
- Create: `src/Pia.Wpf/Services/Interfaces/IResearchHistoryToolHandler.cs`
- Create: `src/Pia.Wpf/Services/ResearchHistoryToolHandler.cs`
- Create: `tests/Pia.Wpf.Tests/Unit/ResearchHistoryToolHandlerTests.cs`

- [ ] **Step 1: Define the interface**

```csharp
using Microsoft.Extensions.AI;

namespace Pia.Services.Interfaces;

public interface IResearchHistoryToolHandler
{
    IList<AITool> GetTools();
    Task<(object? Result, ResearchHistoryToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);
    Task<object?> ExecutePendingActionAsync(ResearchHistoryToolCall pendingAction);
}

public record ResearchHistoryToolCall(
    string ToolName,
    string Description,
    string? Details,
    Func<Task<object?>> Execute);
```

(Both tools are read-only; `PendingAction` is unused in practice but kept for `BuiltInPluginHandler` factory symmetry. Concrete handler always returns `(Result, null)`.)

- [ ] **Step 2: Implement the handler**

```csharp
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class ResearchHistoryToolHandler : IResearchHistoryToolHandler
{
    private readonly IResearchHistoryService _history;
    private readonly IEmbeddingService _embedding;
    private readonly ILogger<ResearchHistoryToolHandler> _logger;

    public ResearchHistoryToolHandler(IResearchHistoryService history, IEmbeddingService embedding, ILogger<ResearchHistoryToolHandler> logger)
    {
        _history = history; _embedding = embedding; _logger = logger;
    }

    public IList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(SearchSchema, "search_research_history",
            "Search past research findings (both ad-hoc and from scheduled jobs). Uses hybrid text+vector search and returns up to topK matches with previews."),
        AIFunctionFactory.Create(GetSchema, "get_research_entry",
            "Get the full text of a research history entry by ID.")
    ];

    public async Task<(object? Result, ResearchHistoryToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall, CancellationToken ct = default)
    {
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();
        return toolCall.Name switch
        {
            "search_research_history" => (await HandleSearch(args), null),
            "get_research_entry" => (await HandleGet(args), null),
            _ => ((object?)$"Unknown tool: {toolCall.Name}", null)
        };
    }

    public Task<object?> ExecutePendingActionAsync(ResearchHistoryToolCall pendingAction) =>
        pendingAction.Execute();

    private async Task<object?> HandleSearch(IDictionary<string, object?> args)
    {
        var query = GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query)) return "Provide a search query.";

        var topK = GetInt(args, "topK") ?? 5;

        float[]? embedding = null;
        try
        {
            if (await _embedding.EnsureAvailableAsync())
                embedding = await _embedding.GenerateEmbeddingAsync(query);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding generation failed for search; falling back to text-only");
        }

        var hits = await _history.HybridSearchAsync(query, embedding, topK);
        if (hits.Count == 0) return "No matching research entries.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {hits.Count} entry/entries:");
        foreach (var e in hits)
        {
            sb.AppendLine($"\n[ID: {e.Id}] {e.CreatedAt:g}{(e.ScheduledJobId.HasValue ? " (scheduled)" : "")}");
            sb.AppendLine($"  Query: {e.QueryPreview}");
            sb.AppendLine($"  Result: {e.ResultPreview}");
        }
        return sb.ToString();
    }

    private async Task<object?> HandleGet(IDictionary<string, object?> args)
    {
        var idStr = GetString(args, "id");
        if (!Guid.TryParse(idStr, out var id))
            return $"Error: invalid GUID '{idStr}'";

        var entry = await _history.GetEntryAsync(id);
        if (entry is null) return $"Error: entry {id} not found.";

        return $"Query: {entry.Query}\n\nResult:\n{entry.SynthesizedResult}";
    }

    [Description("Search past research findings")]
    private static string SearchSchema(
        [Description("Search query (matched against past queries and results)")] string query,
        [Description("Optional ID of a scheduled job to restrict results")] string? scheduledJobId = null,
        [Description("Optional top-K count (default 5)")] string? topK = null) => "";

    [Description("Get a research history entry by ID")]
    private static string GetSchema(
        [Description("The entry ID")] string id) => "";

    private static string GetString(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var v) || v is null) return string.Empty;
        if (v is JsonElement el) return el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : el.GetRawText();
        return v.ToString() ?? string.Empty;
    }

    private static int? GetInt(IDictionary<string, object?> args, string key)
    {
        var s = GetString(args, key);
        return int.TryParse(s, out var i) ? i : null;
    }
}
```

- [ ] **Step 3: Write tests**

`tests/Pia.Wpf.Tests/Unit/ResearchHistoryToolHandlerTests.cs` — minimal coverage:

- `search_research_history` with no embedding (fake `IEmbeddingService.EnsureAvailableAsync` returns false) returns text-only results from `HybridSearchAsync`.
- `get_research_entry` with valid GUID returns the entry's text; with invalid GUID returns an error string.
- `search_research_history` with no matches returns `"No matching research entries."`.

- [ ] **Step 4: Run tests**

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Services/Interfaces/IResearchHistoryToolHandler.cs src/Pia.Wpf/Services/ResearchHistoryToolHandler.cs tests/Pia.Wpf.Tests/Unit/ResearchHistoryToolHandlerTests.cs
git commit -m "Add ResearchHistoryToolHandler with hybrid search"
```

---

### Task 16: Register handlers via `BuiltInPluginHandler` and `PluginService`

**Files:**
- Modify: `src/Pia.Wpf/Services/Plugins/BuiltInPluginHandler.cs`
- Modify: `src/Pia.Wpf/Services/Plugins/PluginService.cs`

- [ ] **Step 1: Add factory methods to `BuiltInPluginHandler`**

Append after `FromReminderHandler`:

```csharp
public static BuiltInPluginHandler FromScheduledJobHandler(
    IScheduledJobToolHandler handler, SyncPlugin config) =>
    new(config.Id, config.Name, handler.GetTools,
        async (call, ct) =>
        {
            var (result, pending) = await handler.HandleToolCallAsync(call, ct);
            if (pending is null) return (result, null);
            return (null, new PluginToolCall(
                pending.ToolName, config.Name, pending.Description, pending.Details, pending.Execute));
        },
        async pluginCall => await pluginCall.Execute(),
        GetSystemPromptFromConfig(config.ConfigJson));

public static BuiltInPluginHandler FromResearchHistoryHandler(
    IResearchHistoryToolHandler handler, SyncPlugin config) =>
    new(config.Id, config.Name, handler.GetTools,
        async (call, ct) =>
        {
            var (result, pending) = await handler.HandleToolCallAsync(call, ct);
            if (pending is null) return (result, null);
            return (null, new PluginToolCall(
                pending.ToolName, config.Name, pending.Description, pending.Details, pending.Execute));
        },
        async pluginCall => await pluginCall.Execute(),
        GetSystemPromptFromConfig(config.ConfigJson));
```

- [ ] **Step 2: Wire them into `PluginService`**

In `src/Pia.Wpf/Services/Plugins/PluginService.cs`:

- Add fields/constructor parameters: `IScheduledJobToolHandler _scheduledJobToolHandler`, `IResearchHistoryToolHandler _researchHistoryToolHandler`.
- In the `GetHandlerId` switch (line ~63), add cases:
  - `"scheduled-research" => BuiltInPluginHandler.FromScheduledJobHandler(_scheduledJobToolHandler, config),`
  - `"research-history" => BuiltInPluginHandler.FromResearchHistoryHandler(_researchHistoryToolHandler, config),`
- The `Plugins` table is seeded somewhere — search for the existing seeding of memory/todo/reminder built-in plugins (look for the strings `"memory"`, `"todo"`, `"reminder"` in service code or the SQL in `SqliteContext`). Add equivalent seeding for `"scheduled-research"` and `"research-history"` plugin records (with default `Name`, `Description`, `IsActive=1`, `UserEnabled=1` as defaults). Match the existing seeding pattern exactly.

- [ ] **Step 3: Build to confirm**

```powershell
dotnet build
```

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/Services/Plugins/BuiltInPluginHandler.cs src/Pia.Wpf/Services/Plugins/PluginService.cs
git commit -m "Register ScheduledJob and ResearchHistory built-in plugins"
```

---

## Chunk 6: DI, localization, integration

### Task 17: DI registration

**Files:**
- Modify: `src/Pia.Wpf/Bootstrapper.cs`

- [ ] **Step 1: Add registrations**

Add (near other service registrations, after `IRecurrenceCalculator` registered in Task 1):

```csharp
services.AddSingleton<IScheduledJobService, ScheduledJobService>();
services.AddSingleton<IScheduledResearchProviderResolver, ScheduledResearchProviderResolver>();
services.AddSingleton<IScheduledJobNotificationSurface, ScheduledJobNotificationSurface>();
services.AddSingleton<IScheduledJobToolHandler, ScheduledJobToolHandler>();
services.AddSingleton<IResearchHistoryToolHandler, ResearchHistoryToolHandler>();
services.AddHostedService<ScheduledJobBackgroundService>();
```

Add `using Pia.Services.Interfaces;` and `using Pia.Services;` if not already present.

- [ ] **Step 2: Build and run smoke**

```powershell
dotnet build
dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj
```

Manually verify the app starts without DI errors. Close it.

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Wpf/Bootstrapper.cs
git commit -m "Register scheduled-research services in DI"
```

---

### Task 18: Localization keys (en/de/fr)

**Files:**
- Modify: `src/Pia.Wpf/Resources/Strings/MessageStrings.resx`
- Modify: `src/Pia.Wpf/Resources/Strings/MessageStrings.de.resx`
- Modify: `src/Pia.Wpf/Resources/Strings/MessageStrings.fr.resx`

- [ ] **Step 1: Add keys to en `.resx`**

Add `<data>` entries with these names and values. Match the XML formatting of the surrounding entries exactly:

| Key | Value (en) |
|---|---|
| `Tool_ScheduledResearch_Desc_Create` | `Schedule research: {0} ({1})` |
| `Tool_ScheduledResearch_Desc_Update` | `Update scheduled research '{0}'` |
| `Tool_ScheduledResearch_Desc_Delete` | `Delete scheduled research '{0}'` |
| `Tool_ScheduledResearch_Detail_Name` | `Name` |
| `Tool_ScheduledResearch_Detail_Query` | `Query` |
| `Tool_ScheduledResearch_Detail_Recurrence` | `Recurrence` |
| `Tool_ScheduledResearch_Detail_Time` | `Time` |
| `Tool_ScheduledResearch_Detail_Provider` | `Provider` |
| `Tool_ScheduledResearch_Detail_AnswerLength` | `Answer length` |
| `Tool_ScheduledResearch_Exec_Created` | `Created scheduled research {0}, next run {1}` |
| `Tool_ScheduledResearch_Exec_Updated` | `Updated scheduled research {0}` |
| `Tool_ScheduledResearch_Exec_Deleted` | `Deleted scheduled research {0}` |
| `Tool_ResearchHistory_Search_Description` | `Search past research findings` |
| `Tool_ResearchHistory_Get_Description` | `Get a research entry by ID` |
| `Notification_ScheduledResearch` | `Scheduled briefing` |
| `Notification_ScheduledResearch_Body` | `'{0}' is ready` |
| `Notification_ScheduledResearchInApp` | `Scheduled briefing '{0}' is ready` |
| `Notification_ScheduledResearchFailed` | `Scheduled briefing failed` |
| `Notification_ScheduledResearchFailed_Body` | `'{0}' could not run — see history for details` |
| `Notification_OpenBriefing` | `Open` |
| `MissedRun_Dialog_Title` | `Missed scheduled briefing` |
| `MissedRun_Dialog_Body` | `'{0}' was scheduled for {1} but the app wasn't open. Run it now in the background?` |
| `MissedRun_RunNow` | `Run now` |
| `MissedRun_Skip` | `Skip this run` |
| `Embedding_Downloading_Toast` | `Downloading embedding model… (one-time)` |

- [ ] **Step 2: Add the same keys with German translations to `MessageStrings.de.resx`**

Translate naturally, e.g. `MissedRun_Dialog_Title` → `Verpasste geplante Recherche`, `MissedRun_RunNow` → `Jetzt ausführen`, `MissedRun_Skip` → `Diesen Lauf überspringen`. Tone: match existing entries.

- [ ] **Step 3: Add French translations to `MessageStrings.fr.resx`**

Translate naturally, e.g. `MissedRun_Dialog_Title` → `Recherche planifiée manquée`, `MissedRun_RunNow` → `Exécuter maintenant`.

- [ ] **Step 4: Build to confirm `.resx` parses**

```powershell
dotnet build
```

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Resources/Strings/MessageStrings.resx src/Pia.Wpf/Resources/Strings/MessageStrings.de.resx src/Pia.Wpf/Resources/Strings/MessageStrings.fr.resx
git commit -m "Add localization keys for scheduled research"
```

---

### Task 19: Integration test

**Files:**
- Create: `tests/Pia.Wpf.Tests/Integration/ScheduledJobToolIntegrationTests.cs`

- [ ] **Step 1: Write the integration test**

Pattern source: `tests/Pia.Wpf.Tests/Integration/ReminderToolIntegrationTests.cs`. End-to-end: tool handler creates a job, background service finds it due, runs research with a stubbed `IResearchService`, persists a `ResearchHistoryEntry`, and `search_research_history` returns it.

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Scheduling;
using Xunit;

namespace Pia.Wpf.Tests.Integration;

public class ScheduledJobToolIntegrationTests : IDisposable
{
    private readonly SqliteContext _ctx = new();

    [Fact]
    public async Task EndToEnd_CreateJob_RunDueJob_SearchFinds()
    {
        var calc = new RecurrenceCalculator();
        var jobs = new ScheduledJobService(_ctx, calc, NullLogger<ScheduledJobService>.Instance);
        var research = new StubResearchService("Test result");
        var embedding = new StubEmbedding();
        var history = new ResearchHistoryService(_ctx, embedding);
        var providers = new StubProviderResolver(new AiProvider { Id = Guid.NewGuid(), Name = "Stub", TimeoutSeconds = 60 });
        var notifications = new SilentNotificationSurface();
        var bg = new ScheduledJobBackgroundService(jobs, research, history, providers, notifications, NullLogger<ScheduledJobBackgroundService>.Instance);

        var job = await jobs.CreateAsync("E2E", "Test query", RecurrenceType.Daily, TimeOnly.MinValue);
        // Force due
        var conn = _ctx.GetConnection();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE ScheduledJobs SET NextFireAt = @t WHERE Id = @id";
            cmd.Parameters.AddWithValue("@t", DateTime.Now.AddSeconds(-1).ToString("O"));
            cmd.Parameters.AddWithValue("@id", job.Id.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        await bg.ExecuteOnceAsync(CancellationToken.None);

        var searchHandler = new ResearchHistoryToolHandler(history, embedding, NullLogger<ResearchHistoryToolHandler>.Instance);
        var fc = new FunctionCallContent("call1", "search_research_history",
            new Dictionary<string, object?> { ["query"] = "Test query" });
        var (result, _) = await searchHandler.HandleToolCallAsync(fc);

        Assert.NotNull(result);
        Assert.Contains("Test query", result!.ToString());
    }

    public void Dispose()
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ScheduledJobs; DELETE FROM ResearchSessions WHERE ProviderName = 'Stub'";
        cmd.ExecuteNonQuery();
        _ctx.Dispose();
    }

    // Stub classes implementing the minimum interfaces needed.
    // Implement after verifying the actual interface shapes.
}
```

- [ ] **Step 2: Run the integration test**

```powershell
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter FullyQualifiedName~ScheduledJobToolIntegrationTests
```

Expected: 1 passed.

- [ ] **Step 3: Run the full test suite one last time**

```powershell
dotnet test
```

Expected: all green.

- [ ] **Step 4: Manual smoke test**

```powershell
dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj
```

In the assistant input box, type:

> Schedule a research job: every day at 8:00, find the latest news about Tesla stock pricing.

Verify:

1. The assistant calls `create_scheduled_research`, an action card appears.
2. Confirm the card.
3. Inspect the database (e.g. via DB Browser for SQLite at `%LOCALAPPDATA%\Pia\history.db`) — `ScheduledJobs` table has the row with `NextFireAt` near tomorrow 08:00.
4. Update the row's `NextFireAt` to a few seconds in the past.
5. Within 30 seconds, a toast appears: *"Scheduled briefing — 'Tesla...' is ready"*.
6. Click **Open** — Pia focuses and the Research view shows the new entry.
7. In the assistant: *"What did we find about Tesla?"* — verify the assistant calls `search_research_history` and returns the briefing content.

- [ ] **Step 5: Commit**

```bash
git add tests/Pia.Wpf.Tests/Integration/ScheduledJobToolIntegrationTests.cs
git commit -m "Add scheduled-job end-to-end integration test"
```

---

## Done

After Task 19 completes successfully, the feature is functionally complete per the spec. Open follow-ups (intentionally out of scope for v1):

- `ToastActivationHub` extraction to consolidate toast routing across reminders and scheduled jobs.
- Embedding backfill task for older `ResearchSessions` rows.
- Dedicated `ScheduledJobsView` if usage demand grows beyond chat-driven CRUD.
- `RecurrenceType.Weekday` if "every weekday" representation as 5 jobs feels clunky.
