using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Operators;
using Xunit;

namespace Pia.Tests.Services;

public class AutocompleteServiceTests
{
    private readonly IMemoryService _memory = Substitute.For<IMemoryService>();
    private readonly ITodoService _todo = Substitute.For<ITodoService>();
    private readonly IReminderService _reminder = Substitute.For<IReminderService>();
    private readonly IScheduledJobService _scheduledJobs = Substitute.For<IScheduledJobService>();
    // Default IsAvailable == false keeps the Files domain out of the existing tier-1 tests.
    private readonly IFilesToolHandler _files = Substitute.For<IFilesToolHandler>();
    private readonly IAssignmentApiClient _assignmentApi = Substitute.For<IAssignmentApiClient>();

    // The REAL cache over a substituted client: a substituted cache would answer instantly and hide the
    // per-keystroke HTTP cost this domain is the first tier-2 source to have. Unrefreshed it is Hidden, which
    // keeps the Assignment domain out of the existing tier-1 tests.
    private readonly AssignmentSurfaceCache _assignmentSurface;

    public AutocompleteServiceTests()
    {
        _assignmentSurface = new AssignmentSurfaceCache(
            _assignmentApi, TimeProvider.System, NullLogger<AssignmentSurfaceCache>.Instance);
    }

    private AutocompleteService CreateService() =>
        new(_memory, _todo, _reminder, _scheduledJobs, _files, _assignmentSurface);

    private async Task SurfaceIsAvailableAsync()
    {
        _assignmentApi.GetSurfaceAsync(Arg.Any<CancellationToken>()).Returns(
            new AssignmentSurface(true, [new AssignmentSkill("digest", "Digest", "Research", [])]));
        await _assignmentSurface.RefreshAsync(TestContext.Current.CancellationToken);
    }

    private static AssignmentDto Run(string skillName, string status) => new(
        Guid.NewGuid(), skillName, "Research", status, 1, 0, 0, DateTime.UtcNow, DateTime.UtcNow,
        null, null, null, null, null);

    private static string Label(AssignmentDto run) =>
        $"{run.SkillName} — {run.Status} ({run.Id.ToString("N")[..8]})";

    [Fact]
    public async Task Tier1_FilterRou_ReturnsRoutineOnly()
    {
        var service = CreateService();

        var results = await service.GetSuggestionsAsync(domain: null, filter: "Rou");

        Assert.Single(results);
        Assert.Equal("Routine", results[0].DisplayText);
        Assert.Equal(AtCommandDomain.Routine, results[0].Domain);
        Assert.True(results[0].IsTier1);
    }

    [Fact]
    public async Task Tier1_NoFilter_IncludesRoutine()
    {
        var service = CreateService();

        var results = await service.GetSuggestionsAsync(domain: null, filter: null);

        Assert.Contains(results, s => s.Domain == AtCommandDomain.Routine && s.DisplayText == "Routine");
    }

    [Fact]
    public async Task Tier1_NeverOffersTheResearchAlias()
    {
        var service = CreateService();

        var results = await service.GetSuggestionsAsync(domain: null, filter: null);

        Assert.DoesNotContain(results, s => s.DisplayText == "Research");
    }

    [Fact]
    public async Task Tier2_Routine_ReturnsActiveJobsByName()
    {
        var alpha = new ScheduledJob { Id = Guid.NewGuid(), Name = "Weekly AI roundup", Query = "ai news" };
        var beta = new ScheduledJob { Id = Guid.NewGuid(), Name = "Daily climate digest", Query = "climate" };
        _scheduledJobs.GetActiveAsync().Returns(new[] { alpha, beta });

        var service = CreateService();

        var results = await service.GetSuggestionsAsync(AtCommandDomain.Routine, filter: null);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, s => s.DisplayText == "Weekly AI roundup" && s.ItemId == alpha.Id);
        Assert.Contains(results, s => s.DisplayText == "Daily climate digest" && s.ItemId == beta.Id);
        Assert.All(results, s => Assert.Equal(AtCommandDomain.Routine, s.Domain));
        Assert.All(results, s => Assert.False(s.IsTier1));
    }

    [Fact]
    public async Task Tier2_Routine_FiltersByNameSubstring()
    {
        var alpha = new ScheduledJob { Id = Guid.NewGuid(), Name = "Weekly AI roundup", Query = "ai news" };
        var beta = new ScheduledJob { Id = Guid.NewGuid(), Name = "Daily climate digest", Query = "climate" };
        _scheduledJobs.GetActiveAsync().Returns(new[] { alpha, beta });

        var service = CreateService();

        var results = await service.GetSuggestionsAsync(AtCommandDomain.Routine, filter: "climate");

        Assert.Single(results);
        Assert.Equal("Daily climate digest", results[0].DisplayText);
    }

    [Fact]
    public async Task Tier2_Routine_NoActiveJobs_ReturnsEmpty()
    {
        _scheduledJobs.GetActiveAsync().Returns(Array.Empty<ScheduledJob>());

        var service = CreateService();

        var results = await service.GetSuggestionsAsync(AtCommandDomain.Routine, filter: null);

        Assert.Empty(results);
    }

    // --- Files domain ---

    [Fact]
    public async Task Tier1_FilesUnavailable_ExcludesFiles()
    {
        // Default substitute: IsAvailable == false.
        var service = CreateService();

        var results = await service.GetSuggestionsAsync(domain: null, filter: null);

        Assert.DoesNotContain(results, s => s.Domain == AtCommandDomain.Files);
    }

    [Fact]
    public async Task Tier1_FilesAvailable_IncludesFiles()
    {
        _files.IsAvailable.Returns(true);

        var service = CreateService();

        var results = await service.GetSuggestionsAsync(domain: null, filter: null);

        Assert.Contains(results, s => s.Domain == AtCommandDomain.Files && s.DisplayText == "Files" && s.IsTier1);
    }

    [Fact]
    public async Task Tier1_FilterFil_ReturnsFilesOnly_WhenAvailable()
    {
        _files.IsAvailable.Returns(true);

        var service = CreateService();

        var results = await service.GetSuggestionsAsync(domain: null, filter: "Fil");

        Assert.Single(results);
        Assert.Equal("Files", results[0].DisplayText);
        Assert.Equal(AtCommandDomain.Files, results[0].Domain);
        Assert.True(results[0].IsTier1);
    }

    [Fact]
    public async Task Tier2_Files_ReturnsRelativePathsFromHandler()
    {
        _files.ListRelativeFiles(null, Arg.Any<int>())
            .Returns(new[] { "notes/todo.md", "src/app/main.cs" });

        var service = CreateService();

        var results = await service.GetSuggestionsAsync(AtCommandDomain.Files, filter: null);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, s => s.DisplayText == "notes/todo.md");
        Assert.Contains(results, s => s.DisplayText == "src/app/main.cs");
        Assert.All(results, s => Assert.Equal(AtCommandDomain.Files, s.Domain));
        Assert.All(results, s => Assert.False(s.IsTier1));
        // Files carry the path in DisplayText, not a Guid ItemId.
        Assert.All(results, s => Assert.Null(s.ItemId));
    }

    [Fact]
    public async Task Tier2_Files_PassesFilterToHandler()
    {
        _files.ListRelativeFiles("main", Arg.Any<int>())
            .Returns(new[] { "src/app/main.cs" });

        var service = CreateService();

        var results = await service.GetSuggestionsAsync(AtCommandDomain.Files, filter: "main");

        Assert.Single(results);
        Assert.Equal("src/app/main.cs", results[0].DisplayText);
        _files.Received(1).ListRelativeFiles("main", Arg.Any<int>());
    }

    [Fact]
    public async Task Tier2_Files_NotClampedToPreviewCount()
    {
        // Unlike the other tier-2 domains (capped at 8), @Files surfaces every match so the user
        // can arrow-key through the whole list. The handler owns the only hard cap (500), so the
        // service must ask it for well beyond the 8-item preview count and pass the results through
        // untruncated. (The other tests use Arg.Any<int>(), so they wouldn't catch a regression to
        // the old 8-item clamp.)
        var paths = Enumerable.Range(0, 20).Select(i => $"dir/file{i:D2}.md").ToArray();
        _files.ListRelativeFiles(null, Arg.Any<int>()).Returns(paths);

        var service = CreateService();

        var results = await service.GetSuggestionsAsync(AtCommandDomain.Files, filter: null);

        Assert.Equal(20, results.Count);
        _files.Received(1).ListRelativeFiles(null, Arg.Is<int>(max => max >= 500));
    }

    // --- Assignment domain ---

    [Fact]
    public async Task Tier1_OmitsAssignment_WhenTheSurfaceIsHidden()
    {
        // No refresh, so the cache is still Hidden — the state of a local-only profile.
        var service = CreateService();

        var results = await service.GetSuggestionsAsync(domain: null, filter: null);

        Assert.DoesNotContain(results, s => s.Domain == AtCommandDomain.Assignment);
    }

    [Fact]
    public async Task Tier1_IncludesAssignment_WhenAvailable()
    {
        await SurfaceIsAvailableAsync();
        var service = CreateService();

        var results = await service.GetSuggestionsAsync(domain: null, filter: null);

        Assert.Contains(results, s =>
            s.Domain == AtCommandDomain.Assignment && s.DisplayText == "Assignment" && s.IsTier1);
    }

    [Fact]
    public async Task Tier1_GatesAssignmentWithoutProbingTheServer()
    {
        var service = CreateService();

        await service.GetSuggestionsAsync(domain: null, filter: null);

        await _assignmentApi.DidNotReceive().GetSurfaceAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tier2_Assignment_ListsRunsFromTheCache()
    {
        var running = Run("weekly-digest", "Running");
        var done = Run("inbox-triage", "Completed");
        IReadOnlyList<AssignmentDto> rows = [running, done];
        _assignmentApi.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(rows);
        var service = CreateService();

        var results = await service.GetSuggestionsAsync(AtCommandDomain.Assignment, filter: null);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, s => s.DisplayText == Label(running) && s.ItemId == running.Id);
        Assert.Contains(results, s => s.DisplayText == Label(done) && s.ItemId == done.Id);
        Assert.All(results, s => Assert.Equal(AtCommandDomain.Assignment, s.Domain));
        Assert.All(results, s => Assert.False(s.IsTier1));
    }

    [Fact]
    public async Task Tier2_Assignment_FiltersOnTheRenderedLabel()
    {
        var done = Run("inbox-triage", "Completed");
        IReadOnlyList<AssignmentDto> rows = [Run("weekly-digest", "Running"), done];
        _assignmentApi.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(rows);
        var service = CreateService();

        var results = await service.GetSuggestionsAsync(AtCommandDomain.Assignment, filter: "inbox");

        Assert.Single(results);
        Assert.Equal(Label(done), results[0].DisplayText);
    }

    /// <summary>Only DisplayText is inserted into the message — ItemId is dropped on the way — so two runs of
    /// one skill that render alike leave the model choosing between them at random.</summary>
    [Fact]
    public async Task Tier2_Assignment_TwoRunsOfOneSkillAndStatus_RenderDistinctly()
    {
        IReadOnlyList<AssignmentDto> rows =
            [Run("weekly-digest", "Completed"), Run("weekly-digest", "Completed")];
        _assignmentApi.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(rows);
        var service = CreateService();

        var results = await service.GetSuggestionsAsync(AtCommandDomain.Assignment, filter: null);

        Assert.Equal(2, results.Count);
        Assert.NotEqual(results[0].DisplayText, results[1].DisplayText);
        Assert.All(results, s =>
            Assert.StartsWith("weekly-digest — Completed (", s.DisplayText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tier2_Assignment_TransportFailure_ReturnsEmpty()
    {
        _assignmentApi.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AssignmentDto>?)null);
        var service = CreateService();

        var results = await service.GetSuggestionsAsync(AtCommandDomain.Assignment, filter: null);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Tier2_Assignment_RepeatedFragments_MakeNoExtraApiCalls()
    {
        // Every distinct fragment is one lookup; this is the only tier-2 source behind the network, so the
        // TTL is what keeps "@Assignment:proj" from being four HTTP round trips.
        IReadOnlyList<AssignmentDto> rows = [Run("project-brief", "Running")];
        _assignmentApi.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(rows);
        var service = CreateService();

        foreach (var fragment in new[] { "", "p", "pr", "pro", "proj" })
            await service.GetSuggestionsAsync(AtCommandDomain.Assignment, fragment);

        await _assignmentApi.Received(1).ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EverySuggestionIconIsInTheBasicMultilingualPlane()
    {
        // Roughly a third of SymbolRegular's members sit above U+FFFF and render as a garbage letter with
        // zero compiler warnings, so nothing but launching the app would otherwise catch a bad pick.
        _files.IsAvailable.Returns(true);
        await SurfaceIsAvailableAsync();
        IReadOnlyList<AssignmentDto> rows = [Run("weekly-digest", "Running")];
        _assignmentApi.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(rows);
        var service = CreateService();

        var shown = (await service.GetSuggestionsAsync(domain: null, filter: null))
            .Concat(await service.GetSuggestionsAsync(AtCommandDomain.Assignment, filter: null))
            .ToArray();

        Assert.NotEmpty(shown);
        Assert.All(shown, s => Assert.True((int)s.Icon <= 0xFFFF, $"{s.DisplayText} uses {s.Icon}"));
    }
}
