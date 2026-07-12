using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
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

    private AutocompleteService CreateService() => new(_memory, _todo, _reminder, _scheduledJobs, _files);

    [Fact]
    public async Task Tier1_FilterRes_ReturnsResearchOnly()
    {
        var service = CreateService();

        var results = await service.GetSuggestionsAsync(domain: null, filter: "Res");

        Assert.Single(results);
        Assert.Equal("Research", results[0].DisplayText);
        Assert.Equal(AtCommandDomain.Research, results[0].Domain);
        Assert.True(results[0].IsTier1);
    }

    [Fact]
    public async Task Tier1_NoFilter_IncludesResearch()
    {
        var service = CreateService();

        var results = await service.GetSuggestionsAsync(domain: null, filter: null);

        Assert.Contains(results, s => s.Domain == AtCommandDomain.Research && s.DisplayText == "Research");
    }

    [Fact]
    public async Task Tier2_Research_ReturnsActiveJobsByName()
    {
        var alpha = new ScheduledJob { Id = Guid.NewGuid(), Name = "Weekly AI roundup", Query = "ai news" };
        var beta = new ScheduledJob { Id = Guid.NewGuid(), Name = "Daily climate digest", Query = "climate" };
        _scheduledJobs.GetActiveAsync().Returns(new[] { alpha, beta });

        var service = CreateService();

        var results = await service.GetSuggestionsAsync(AtCommandDomain.Research, filter: null);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, s => s.DisplayText == "Weekly AI roundup" && s.ItemId == alpha.Id);
        Assert.Contains(results, s => s.DisplayText == "Daily climate digest" && s.ItemId == beta.Id);
        Assert.All(results, s => Assert.Equal(AtCommandDomain.Research, s.Domain));
        Assert.All(results, s => Assert.False(s.IsTier1));
    }

    [Fact]
    public async Task Tier2_Research_FiltersByNameSubstring()
    {
        var alpha = new ScheduledJob { Id = Guid.NewGuid(), Name = "Weekly AI roundup", Query = "ai news" };
        var beta = new ScheduledJob { Id = Guid.NewGuid(), Name = "Daily climate digest", Query = "climate" };
        _scheduledJobs.GetActiveAsync().Returns(new[] { alpha, beta });

        var service = CreateService();

        var results = await service.GetSuggestionsAsync(AtCommandDomain.Research, filter: "climate");

        Assert.Single(results);
        Assert.Equal("Daily climate digest", results[0].DisplayText);
    }

    [Fact]
    public async Task Tier2_Research_NoActiveJobs_ReturnsEmpty()
    {
        _scheduledJobs.GetActiveAsync().Returns(Array.Empty<ScheduledJob>());

        var service = CreateService();

        var results = await service.GetSuggestionsAsync(AtCommandDomain.Research, filter: null);

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
}
