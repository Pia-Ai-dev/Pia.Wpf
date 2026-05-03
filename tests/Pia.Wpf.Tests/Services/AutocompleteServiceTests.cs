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

    private AutocompleteService CreateService() => new(_memory, _todo, _reminder, _scheduledJobs);

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
}
