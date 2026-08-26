using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class StarterSuggestionServiceTests
{
    private readonly IMemoryService _memory = Substitute.For<IMemoryService>();
    private readonly ITodoService _todos = Substitute.For<ITodoService>();
    private readonly IReminderService _reminders = Substitute.For<IReminderService>();
    private readonly IScheduledJobService _jobs = Substitute.For<IScheduledJobService>();
    private readonly IAssistantChatService _chats = Substitute.For<IAssistantChatService>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();

    /// <summary>Echoes the key so an assertion can name the phrasing set a draw came from.</summary>
    private StarterSuggestionService CreateSut()
    {
        _localization[Arg.Any<string>()].Returns(call => call.Arg<string>());
        return new StarterSuggestionService(
            _memory, _todos, _reminders, _jobs, _chats, _localization,
            NullLogger<StarterSuggestionService>.Instance);
    }

    private void GiveEverything()
    {
        _memory.GetObjectCountAsync().Returns(4);
        _todos.GetPendingCountAsync().Returns(3);
        _reminders.GetActiveAsync().Returns(new List<Reminder>
        {
            new() { Description = "water the plants" }
        });
        _jobs.GetAllAsync().Returns(new List<ScheduledJob>
        {
            new() { Name = "morning briefing", Query = "what changed overnight" }
        });
        _chats.CountAsync().ReturnsForAnyArgs(7);
    }

    private void GiveNothing()
    {
        _memory.GetObjectCountAsync().Returns(0);
        _todos.GetPendingCountAsync().Returns(0);
        _reminders.GetActiveAsync().Returns(new List<Reminder>());
        _jobs.GetAllAsync().Returns(new List<ScheduledJob>());
        _chats.CountAsync().ReturnsForAnyArgs(0);
    }

    [Fact]
    public async Task AnEmptyProfileOnlyEverDrawsTheGettingStartedPhrasings()
    {
        GiveNothing();
        var sut = CreateSut();

        // One draw is a sample of 3 of 7 groups; the loop is what makes the claim about all of them.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            foreach (var chip in await sut.DrawAsync(3, TestContext.Current.CancellationToken))
                Assert.DoesNotContain("_Grow", chip.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AGroupWithNoStartPhrasingIsWithheldUntilItsDataExists()
    {
        GiveNothing();
        var sut = CreateSut();

        for (var attempt = 0; attempt < 40; attempt++)
            Assert.DoesNotContain(await sut.DrawAsync(3, TestContext.Current.CancellationToken), c => c.Id == "Chats");
    }

    [Fact]
    public async Task AProfileWithDataDrawsTheFollowUpPhrasingForEveryGroupThatHasAnyone()
    {
        GiveEverything();
        var sut = CreateSut();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 60; attempt++)
            foreach (var chip in await sut.DrawAsync(3, TestContext.Current.CancellationToken))
                seen.Add(chip.Id);

        // Plan is the one group with no data axis, so it keeps drawing from its single set.
        Assert.Equal(
            StarterSuggestionService.GroupIds.OrderBy(id => id, StringComparer.Ordinal),
            seen.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Memory")]
    [InlineData("Recall")]
    [InlineData("Todo")]
    [InlineData("Reminder")]
    [InlineData("Routine")]
    [InlineData("Chats")]
    public async Task EveryDataBackedGroupSwitchesPhrasingOnceItsStoreIsNotEmpty(string groupId)
    {
        GiveEverything();
        var sut = CreateSut();

        var texts = new List<string>();
        for (var attempt = 0; attempt < 60; attempt++)
            texts.AddRange((await sut.DrawAsync(3, TestContext.Current.CancellationToken)).Where(c => c.Id == groupId).Select(c => c.Text));

        Assert.NotEmpty(texts);
        Assert.All(texts, t => Assert.Contains("_Grow", t, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailingProbeStillFillsTheChipRow()
    {
        GiveNothing();
        _todos.GetPendingCountAsync().Throws(new InvalidOperationException("database is locked"));
        var sut = CreateSut();

        var drawn = await sut.DrawAsync(3, TestContext.Current.CancellationToken);

        Assert.Equal(3, drawn.Count);
        Assert.All(drawn, c => Assert.DoesNotContain("_Grow", c.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADrawNeverRepeatsAGroup()
    {
        GiveEverything();
        var sut = CreateSut();

        for (var attempt = 0; attempt < 40; attempt++)
        {
            var drawn = await sut.DrawAsync(3, TestContext.Current.CancellationToken);
            Assert.Equal(3, drawn.Count);
            Assert.Equal(3, drawn.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public async Task AskingForNoneProbesNothing()
    {
        GiveEverything();
        var sut = CreateSut();

        Assert.Empty(await sut.DrawAsync(0, TestContext.Current.CancellationToken));
        await _todos.DidNotReceive().GetPendingCountAsync();
    }
}
