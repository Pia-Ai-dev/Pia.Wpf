using NSubstitute;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Integration;

[Trait("Category", "Integration")]
public class ReminderToolIntegrationTests : ToolPipelineTestBase
{
    [Fact]
    public async Task CreateReminder_ShouldCallCreateReminder()
    {
        if (ShouldSkip) return;

        // Arrange
        ReminderService.CreateAsync(
                Arg.Any<string>(), Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(),
                Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>())
            .Returns(callInfo => new Reminder
            {
                Id = Guid.NewGuid(),
                Description = callInfo.ArgAt<string>(0),
                Recurrence = callInfo.ArgAt<RecurrenceType>(1),
                TimeOfDay = callInfo.ArgAt<TimeOnly>(2),
                Status = ReminderStatus.Active,
                NextFireAt = DateTime.UtcNow.AddDays(1),
                CreatedAt = DateTime.UtcNow
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var (response, toolCalls) = await RunToolPipelineAsync(
            "Remind me to call mom tomorrow at 5pm.", cts.Token);

        // Assert
        Assert.Contains(toolCalls, tc => tc.ToolName == "create_reminder");

        var createCall = toolCalls.First(tc => tc.ToolName == "create_reminder");
        var descArg = createCall.Arguments?["description"]?.ToString() ?? "";
        Assert.Contains("call", descArg, StringComparison.OrdinalIgnoreCase);

        var recurrenceArg = createCall.Arguments?["recurrence"]?.ToString() ?? "";
        Assert.Contains("Once", recurrenceArg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryReminders_ShouldCallQueryReminders()
    {
        if (ShouldSkip) return;

        // Arrange
        ReminderService.GetActiveAsync().Returns(new List<Reminder>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Description = "Call mom",
                Recurrence = RecurrenceType.Once,
                TimeOfDay = new TimeOnly(17, 0),
                Status = ReminderStatus.Active,
                NextFireAt = DateTime.UtcNow.AddDays(1),
                CreatedAt = DateTime.UtcNow
            }
        });
        ReminderService.GetAllAsync().Returns(new List<Reminder>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Description = "Call mom",
                Recurrence = RecurrenceType.Once,
                TimeOfDay = new TimeOnly(17, 0),
                Status = ReminderStatus.Active,
                NextFireAt = DateTime.UtcNow.AddDays(1),
                CreatedAt = DateTime.UtcNow
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var (response, toolCalls) = await RunToolPipelineAsync(
            "What reminders do I have?", cts.Token);

        // Assert
        Assert.Contains(toolCalls, tc => tc.ToolName == "query_reminders");
    }
}
